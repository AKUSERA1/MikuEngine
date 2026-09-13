// 6DOF spring + contact constraint solver. Sequential-impulse projected
// Gauss-Seidel: per axis, target a relative velocity (limit correction +
// spring), apply the impulse needed to reach it. Friction is two Coulomb
// rows per contact, normal is push-only.
//
// Two passes per substep:
//   1. SETUP — for each constraint and contact, compute every quantity that
//      doesn't depend on lv/av (world axes, lever arms, Jacobian denominators,
//      target velocities, friction tangent bases, restitution reference).
//      These are constant during solve since pos/ori/inertia don't change.
//   2. ITERATE — `iterations` passes that read the cache and apply impulses
//      based on the current lv/av. ~2× faster than recomputing per iter.
//
// reze 是 JS number（f64 中间计算 + f32 存储）；本移植统一 float32，
// 仅保留 reze 明确设为 f64通道的 SolverCache.D（geodesic 行标量）为 double[]）

using MikuEngine.Core.Math;

namespace MikuEngine.Physics;

/// <summary>
/// Flat solver cache (SoA)
/// One typed-array block instead of a dozen small arrays per constraint:
/// the 10-iteration hot loop walks memory linearly off a single base pointer
/// instead of pointer-chasing ~1000 scattered objects. Layout below mirrors the
/// reze cache fields one-to-one.
/// </summary>
public sealed class SolverCache
{
    public const int FStride = 108;
    public const int IStride = 16;

    // F layout offsets（+长度，float32）
    public const int LeverA = 0;       // 3
    public const int LeverB = 3;       // 3
    public const int LinAxes = 6;      // 9
    public const int LinCA = 15;       // 9
    public const int LinCB = 24;       // 9
    public const int LinJac = 33;      // 3
    public const int LinTgt = 36;      // 3
    public const int LinLimitImp = 39; // 3
    public const int LinSprTgt = 42;   // 3
    public const int LinSprMax = 45;   // 3
    public const int LinSprImp = 48;   // 3
    public const int AngAxes = 51;     // 9
    public const int AngTgt = 60;      // 3
    public const int AngJac = 63;      // 3
    public const int AngSprMax = 66;   // 3
    public const int AngSprImp = 69;   // 3
    public const int AngWA = 72;       // 9
    public const int AngWB = 81;       // 9
    public const int AngLimAxis = 90;  // 3
    public const int AngLimWA = 93;    // 3
    public const int AngLimWB = 96;    // 3
    public const int AngPaTgt = 99;    // 3
    public const int AngPaImp = 102;   // 3

    // I layout offsets（int32）
    public const int IBodyA = 0;
    public const int IBodyB = 1;
    public const int ISkip = 2;
    public const int ILinAct = 3;    // 3
    public const int ILinSprAct = 6; // 3
    public const int IAngAct = 9;    // 3
    public const int IAngPaAct = 12; // 3
    public const int IAngLimAct = 15;

    public readonly float[] F;
    public readonly int[] I;

    /// <summary>f64 lane for the geodesic row's scalars — they were plain number
    /// fields in reze, and demoting them to f32 would break bit-identical results.</summary>
    public readonly double[] D;

    public SolverCache(SixDofSpringConstraint[] constraints)
    {
        int n = constraints.Length;
        F = new float[n * FStride];
        I = new int[n * IStride];
        D = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            I[i * IStride + IBodyA] = constraints[i].BodyA;
            I[i * IStride + IBodyB] = constraints[i].BodyB;
        }
    }
}

public static class ConstraintSolver
{
    private const float BounceThreshold = 2.0f;

    // Contact error-reduction parameter — btContactSolverInfo::m_erp in Bullet
    // 2.75, the build MMD's own physics runs, at its stock 0.2. PMX rigs are
    // authored against exactly this response.
    //
    // Bullet folds penetration recovery into the CONTACT VELOCITY ROW rather than
    // translating positions (m_splitImpulse defaults to false in 2.75):
    //
    //   penetration     = cp.getDistance() + m_linearSlop      // < 0 when overlapping
    //   positionalError = -penetration * m_erp / m_timeStep
    //   velocityError   = restitution - rel_vel
    //   m_rhs           = (positionalError + velocityError) * m_jacDiagABInv
    //
    // Our `depth` is the negation of Bullet's distance, so positionalError is just
    // depth·ERP/dt — one expression covering both cases. Penetrating (depth > 0) it
    // pushes apart; separated (depth < 0, a speculative row) it ALLOWS approach at
    // the rate that closes the gap, which is what keeps those rows inert until the
    // body would really arrive.
    private const float ContactErp = 0.2f;

    // btContactSolverInfo's SOLVER_RANDMIZE_ORDER. Gauss-Seidel is order-biased —
    // rows solved first win — which on a cross-linked lattice shows up as a lean or
    // a period-2 chatter. Bullet ships the flag off; it is here to be measured.
    private const bool RandomizeOrder = true;

    // btSequentialImpulseConstraintSolver::btRand2, so the shuffle is reproducible
    // and a take replays identically.
    private static uint _seed;

    private static uint Rand2()
    {
        unchecked { _seed = 1664525u * _seed + 1013904223u; }
        return _seed;
    }

    private static int[] _order = Array.Empty<int>();

    // btContactSolverInfo::m_warmstartingFactor. Seeded impulses are scaled by
    // this so a stale cache decays instead of compounding.
    private const float WarmstartingFactor = 0.85f;

    // btContactSolverInfo::m_restingContactRestitutionThreshold — past this many
    // substeps a contact is "resting" and stops bouncing.
    private const int RestingContactAge = 2;

    // btContactSolverInfo::m_linearSlop is 0 in 2.75 — no allowance, the row aims
    // at exactly touching. Kept named so the divergence is visible if it ever moves.
    private const float LinearSlop = 0f;

    // btContactSolverInfo::m_splitImpulse. False in 2.75 (only the demos turn it
    // on), but TRUE by default from Bullet 2.8x onward — which is what babylon-mmd
    // runs today. MMD is closed-source so its own setting cannot be read, but it is
    // widely reported to enable the flag, and our own measurements agree
    // independently: turning it on lowered the velocity-reversal rate on every rig
    // tested (伊邪那美 0.118 → 0.098, 诗蔻蒂 0.098 → 0.074, 托特 0.054 → 0.028).
    // With it on, the penetration term is moved out of the real
    // velocity row into a pseudo-velocity ("push") channel that is solved
    // separately and integrated straight into the transform, so recovering overlap
    // never adds momentum the joint springs can hand back.
    private const bool SplitImpulse = true;

    // btContactSolverInfo::m_splitImpulsePenetrationThreshold. Bullet's sign: only
    // contacts DEEPER than this go to the split channel; shallower ones keep the
    // bias in the real row. Our depth is the negation, hence the flipped compare.
    private const float SplitPenetrationThreshold = 0.02f;

    // Ceilings on limit-correction velocity. In normal operation limit errors are
    // tiny; a large error only appears after a discontinuity (teleport, stall,
    // deep penetration), and feeding err·ERP/dt to the solver unclamped then
    // injects explosion-scale impulses into the chain.
    private const float MaxLinearCorrectionVel = 120f;   // units/s
    private const float MaxAngularCorrectionVel = 30f;   // rad/s

    // Bullet's limit-motor softness defaults (0.7 translational, 0.5 rotational):
    // scale each iteration's limit impulse so the stop engages progressively
    // instead of as a hard velocity snap.
    private const float LimitSoftnessLinear = 0.7f;
    private const float LimitSoftnessAngular = 0.5f;

    // Spring rows are Bullet MOTOR rows (see setup for the form):
    //   velFactor      = fps · damping / numIterations
    //   targetVelocity = velFactor · force          (force = err · k)
    //   maxMotorForce  = |force| / fps
    // m_springDamping defaults to 1.0 and PMX carries no damping field, so every
    // MMD spring runs at exactly that — which is what these rigs were authored
    // against.
    private const float SpringDamping = 1.0f;

    // btConstraintInfo2::erp, from infoGlobal.m_erp — used only by GetMotorFactor.
    private const float InfoErp = 0.2f;

    // ERP scale for loop-closing constraints (see ConstraintBuilder: joints that
    // close a cycle in the joint graph, e.g. the horizontal ring welds of
    // cross-linked skirt lattices). A loop over-determines positions — when
    // contacts push the lattice, the ring's errors cannot all reach zero, and
    // full-rate corrections chase each other around the cycle as violent
    // chatter. Loop edges keep shape at a fraction of the correction rate while
    // the spanning-tree chains stay stiff.
    private const float LoopErpScale = 1.0f;

    // Angular limit violations below this switch to per-axis euler rows; above
    // it, the single geodesic row takes over (see SetupConstraint).
    private const float GeodesicThreshold = 0.5f; // rad

    // Module-level scratch (no per-iter allocations).
    private static readonly float[] TA = new float[16];
    private static readonly float[] TB = new float[16];
    private static readonly float[] BodyMatA = new float[16];
    private static readonly float[] BodyMatB = new float[16];
    private static readonly float[] AngDiffScratch = new float[3];
    private static readonly float[] QuatScratchA = new float[4];
    private static readonly float[] QuatScratchB = new float[4];
    private static readonly float[] MfA = new float[3];
    private static readonly float[] MfB = new float[3];

    // Pseudo-velocity accumulators for the split channel, zeroed each substep.
    private static float[] _pushLin = Array.Empty<float>();
    private static float[] _pushAng = Array.Empty<float>();

    public static void SolveConstraints(
        RigidBodyStore store,
        SixDofSpringConstraint[] constraints,
        SolverCache? cache,
        ContactPool contacts,
        float dt,
        int iterations,
        ManifoldCache? manifolds = null)
    {
        if (dt <= 0) return;
        if (constraints.Length == 0 && contacts.Count == 0) return;

        float invDt = 1f / dt;
        float[] lv = store.LinearVelocities;
        float[] av = store.AngularVelocities;
        float[] invMass = store.InvMass;

        // World-space inverse inertia tensors for this substep's poses.
        store.UpdateInvInertiaWorld();
        float[] w = store.InvInertiaWorld;

        for (int c = 0; c < constraints.Length; c++)
            SetupConstraint(constraints[c], c, cache, store, dt, invDt, iterations);
        for (int ci = 0; ci < contacts.Count; ci++)
            SetupContactRow(contacts.Get(ci), lv, av, invMass, w, invDt);

        // Warm start: seed each row from what the same point converged to last
        // substep and APPLY it before iterating, exactly as Bullet does at setup.
        if (manifolds != null)
        {
            for (int ci = 0; ci < contacts.Count; ci++)
                WarmStartContact(contacts.Get(ci), store, manifolds, lv, av, invMass);
        }

        // Bullet 2.75 solves each iteration in three separate passes, in this order:
        // all joint rows, then all contact normal rows, then all friction rows. The
        // friction bound is read from the contact's accumulated impulse AS IT STANDS
        // after the normal pass, so friction tightens as the normal converges. Doing
        // friction inline per contact (our previous shape) bounds it against a normal
        // impulse that half the manifold has not contributed to yet.
        if (RandomizeOrder)
        {
            // Reset per substep. Bullet keeps the seed on the solver instance; ours is
            // static, so without this two worlds in one process share a stream
            // and the same scene stops replaying identically — which the determinism
            // test catches. Re-seeding costs nothing: the point is breaking the order
            // bias BETWEEN iterations, not being unpredictable across frames.
            _seed = 0;
            if (_order.Length < contacts.Count) _order = new int[contacts.Count];
            for (int i = 0; i < contacts.Count; i++) _order[i] = i;
        }
        for (int iter = 0; iter < iterations; iter++)
        {
            if (RandomizeOrder)
            {
                // Bullet reshuffles between iterations, not once per substep.
                for (int i = 1; i < contacts.Count; i++)
                {
                    int j = (int)(Rand2() % (uint)(i + 1));
                    (_order[i], _order[j]) = (_order[j], _order[i]);
                }
            }
            for (int c = 0; c < constraints.Length; c++)
                IterateConstraint(c, cache, lv, av, invMass);
            for (int ci = 0; ci < contacts.Count; ci++)
                IterateContactNormal(contacts.Get(RandomizeOrder ? _order[ci] : ci), lv, av, invMass);
            for (int ci = 0; ci < contacts.Count; ci++)
                IterateContactFriction(contacts.Get(ci), lv, av, invMass);
        }

        // Split-impulse pass: its OWN full set of iterations over the penetration
        // channel only, exactly as Bullet runs it after the main loop.
        if (SplitImpulse)
        {
            int n3 = store.Count * 3;
            if (_pushLin.Length < n3) { _pushLin = new float[n3]; _pushAng = new float[n3]; }
            else { Array.Clear(_pushLin, 0, n3); Array.Clear(_pushAng, 0, n3); }
            bool any = false;
            for (int ci = 0; ci < contacts.Count; ci++)
            {
                if (contacts.Get(ci).RhsPenetration != 0) { any = true; break; }
            }
            if (any)
            {
                if (RandomizeOrder)
                {
                    // Reset per substep（同上；注意 reze 原样：这里洗牌只为消费同一
                    // RNG 流保持逐位一致，resolve 循环本身按索引序遍历）。
                    _seed = 0;
                    if (_order.Length < contacts.Count) _order = new int[contacts.Count];
                    for (int i = 0; i < contacts.Count; i++) _order[i] = i;
                }
                for (int iter = 0; iter < iterations; iter++)
                {
                    if (RandomizeOrder)
                    {
                        for (int i = 1; i < contacts.Count; i++)
                        {
                            int j = (int)(Rand2() % (uint)(i + 1));
                            (_order[i], _order[j]) = (_order[j], _order[i]);
                        }
                    }
                    for (int ci = 0; ci < contacts.Count; ci++)
                        ResolveSplitPenetration(contacts.Get(ci), invMass);
                }
            }
        }
    }

    /// <summary>
    /// Integrate the accumulated push/turn velocity straight into the transform —
    /// btSolverBody::writebackVelocity(timeStep). Never touches real momentum.
    /// </summary>
    public static void ApplySplitImpulsePush(RigidBodyStore store, float dt)
    {
        if (!SplitImpulse || _pushLin.Length == 0) return;
        float[] pos = store.Positions;
        float[] ori = store.Orientations;
        float[] invMass = store.InvMass;
        for (int i = 0; i < store.Count; i++)
        {
            if (invMass[i] <= 0) continue;
            int i3 = i * 3, i4 = i * 4;
            float px = _pushLin[i3 + 0], py = _pushLin[i3 + 1], pz = _pushLin[i3 + 2];
            float wx = _pushAng[i3 + 0], wy = _pushAng[i3 + 1], wz = _pushAng[i3 + 2];
            if (px == 0 && py == 0 && pz == 0 && wx == 0 && wy == 0 && wz == 0) continue;
            pos[i3 + 0] += px * dt;
            pos[i3 + 1] += py * dt;
            pos[i3 + 2] += pz * dt;
            if (wx != 0 || wy != 0 || wz != 0)
            {
                float qx = ori[i4 + 0], qy = ori[i4 + 1], qz = ori[i4 + 2], qw = ori[i4 + 3];
                float dx = qw * wx + wy * qz - wz * qy;
                float dy = qw * wy + wz * qx - wx * qz;
                float dz = qw * wz + wx * qy - wy * qx;
                float dw = -(wx * qx + wy * qy + wz * qz);
                float h = 0.5f * dt;
                float nx2 = qx + dx * h, ny2 = qy + dy * h, nz2 = qz + dz * h, nw2 = qw + dw * h;
                float l2 = nx2 * nx2 + ny2 * ny2 + nz2 * nz2 + nw2 * nw2;
                if (l2 > 0)
                {
                    float inv = 1f / MathF.Sqrt(l2);
                    ori[i4 + 0] = nx2 * inv;
                    ori[i4 + 1] = ny2 * inv;
                    ori[i4 + 2] = nz2 * inv;
                    ori[i4 + 3] = nw2 * inv;
                }
            }
        }
    }

    /// <summary>Store every solved row back into the manifold for the next substep.</summary>
    public static void SaveContactImpulses(
        RigidBodyStore store,
        ContactPool contacts,
        ManifoldCache manifolds)
    {
        for (int ci = 0; ci < contacts.Count; ci++)
        {
            Contact c = contacts.Get(ci);
            WorldToLocal(store, c.BodyA, c.RAx, c.RAy, c.RAz, MfA);
            WorldToLocal(store, c.BodyB, c.RBx, c.RBy, c.RBz, MfB);
            manifolds.Store(
                c.BodyA, c.BodyB,
                MfA[0], MfA[1], MfA[2],
                MfB[0], MfB[1], MfB[2],
                c.AppliedNormalImpulse, c.AppliedFrictionImpulse1, c.AppliedFrictionImpulse2,
                c.Age);
        }
        manifolds.EndStep();
    }

    // ── SETUP ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// SETUP: compute everything that doesn't depend on velocities. Caller
    /// guarantees pos/ori don't change between this and the iter loop.
    /// </summary>
    private static void SetupConstraint(
        SixDofSpringConstraint con,
        int ci,
        SolverCache cache,
        RigidBodyStore store,
        float dt,
        float invDt,
        int iterations)
    {
        float[] F = cache.F;
        int[] I = cache.I;
        double[] D = cache.D;
        int baseI = ci * SolverCache.FStride;
        int ib = ci * SolverCache.IStride;
        int db = ci * 3;
        int a = con.BodyA;
        int b = con.BodyB;
        float[] pos = store.Positions;
        float[] invMass = store.InvMass;
        float imA = invMass[a];
        float imB = invMass[b];
        float[] W = store.InvInertiaWorld;
        int a9 = a * 9;
        int b9 = b * 9;

        bool skip = imA == 0 && imB == 0;
        I[ib + SolverCache.ISkip] = skip ? 1 : 0;
        if (skip) return;

        float erpScale = con.IsLoop ? LoopErpScale : 1.0f;

        BuildBodyMat(store, a, BodyMatA);
        BuildBodyMat(store, b, BodyMatB);
        Mat4.MultiplyArrays(BodyMatA, 0, con.FrameA, 0, TA, 0);
        Mat4.MultiplyArrays(BodyMatB, 0, con.FrameB, 0, TB, 0);

        // Per-body pivots at each body's own joint-frame origin (Spring2-style).
        // Bullet 2.7x's shared mass-weighted anchor (m_AnchorPos) degenerates when
        // the joint is violated by a large distance: the midpoint sits far from
        // both bodies, the lever arms grow with the separation, the Jacobian
        // denominator blows up as err²·invInertia, and the row applies torque
        // instead of closing velocity — the joint "breaks" and the error runs
        // away. Per-body pivots keep the levers bounded by the frame offsets, so
        // the row stays effective no matter how large the violation is.
        int ai = a * 3;
        int bi = b * 3;
        float rAx = TA[12] - pos[ai + 0];
        float rAy = TA[13] - pos[ai + 1];
        float rAz = TA[14] - pos[ai + 2];
        float rBx = TB[12] - pos[bi + 0];
        float rBy = TB[13] - pos[bi + 1];
        float rBz = TB[14] - pos[bi + 2];
        int lA = baseI + SolverCache.LeverA;
        int lB = baseI + SolverCache.LeverB;
        F[lA + 0] = rAx; F[lA + 1] = rAy; F[lA + 2] = rAz;
        F[lB + 0] = rBx; F[lB + 1] = rBy; F[lB + 2] = rBz;

        // linearDiff = TA.basis^T · (TB.origin − TA.origin); axes = TA columns 0/1/2.
        float dxw = TB[12] - TA[12];
        float dyw = TB[13] - TA[13];
        float dzw = TB[14] - TA[14];
        float linDiff0 = TA[0] * dxw + TA[1] * dyw + TA[2] * dzw;
        float linDiff1 = TA[4] * dxw + TA[5] * dyw + TA[6] * dzw;
        float linDiff2 = TA[8] * dxw + TA[9] * dyw + TA[10] * dzw;

        int axes = baseI + SolverCache.LinAxes;
        int cA = baseI + SolverCache.LinCA;
        int cB = baseI + SolverCache.LinCB;
        int jac = baseI + SolverCache.LinJac;
        int tgt = baseI + SolverCache.LinTgt;
        int act = ib + SolverCache.ILinAct;

        for (int i = 0; i < 3; i++)
        {
            int o = i * 3;
            float axx = i == 0 ? TA[0] : i == 1 ? TA[4] : TA[8];
            float axy = i == 0 ? TA[1] : i == 1 ? TA[5] : TA[9];
            float axz = i == 0 ? TA[2] : i == 1 ? TA[6] : TA[10];
            F[axes + o + 0] = axx;
            F[axes + o + 1] = axy;
            F[axes + o + 2] = axz;

            float cAx = rAy * axz - rAz * axy;
            float cAy = rAz * axx - rAx * axz;
            float cAz = rAx * axy - rAy * axx;
            float cBx = rBy * axz - rBz * axy;
            float cBy = rBz * axx - rBx * axz;
            float cBz = rBx * axy - rBy * axx;
            // Tensor-multiplied lever crosses: cache I⁻¹·(r×ax) for application;
            // denominator = (r×ax)ᵀ·I⁻¹·(r×ax).
            float wAx = W[a9 + 0] * cAx + W[a9 + 1] * cAy + W[a9 + 2] * cAz;
            float wAy = W[a9 + 3] * cAx + W[a9 + 4] * cAy + W[a9 + 5] * cAz;
            float wAz = W[a9 + 6] * cAx + W[a9 + 7] * cAy + W[a9 + 8] * cAz;
            float wBx = W[b9 + 0] * cBx + W[b9 + 1] * cBy + W[b9 + 2] * cBz;
            float wBy = W[b9 + 3] * cBx + W[b9 + 4] * cBy + W[b9 + 5] * cBz;
            float wBz = W[b9 + 6] * cBx + W[b9 + 7] * cBy + W[b9 + 8] * cBz;
            F[cA + o + 0] = wAx; F[cA + o + 1] = wAy; F[cA + o + 2] = wAz;
            F[cB + o + 0] = wBx; F[cB + o + 1] = wBy; F[cB + o + 2] = wBz;

            float denom = imA + imB +
                (cAx * wAx + cAy * wAy + cAz * wAz) +
                (cBx * wBx + cBy * wBy + cBz * wBz);
            F[jac + i] = denom > 0 ? 1f / denom : 0;

            float lo = con.LinearMin[i];
            float hi = con.LinearMax[i];
            float curr = i == 0 ? linDiff0 : i == 1 ? linDiff1 : linDiff2;
            // active: 1 = bilateral equality (locked axis — a joint, always on),
            //         2 = unilateral stop (ranged axis in violation).
            float target = 0;
            int active = 0;
            if (lo <= hi)
            {
                float err = 0;
                if (curr < lo) err = curr - lo;
                else if (curr > hi) err = curr - hi;
                if (lo == hi) active = 1;
                else if (err != 0) active = 2;
                if (err != 0)
                {
                    target = -err * ConstraintBuilder.StopErp * erpScale * invDt;
                    if (target > MaxLinearCorrectionVel) target = MaxLinearCorrectionVel;
                    else if (target < -MaxLinearCorrectionVel) target = -MaxLinearCorrectionVel;
                }
            }
            F[tgt + i] = target;
            I[act + i] = denom > 0 ? active : 0;
            F[baseI + SolverCache.LinLimitImp + i] = 0;

            // A spring on a locked axis is redundant — the bilateral limit row
            // already welds the DOF, and driving it twice overshoots every
            // iteration (PMX rigs routinely put k=100000 springs on locked axes,
            // which turned welded weight-bodies into energy pumps).
            if (con.SpringEnabled[i] != 0 && denom > 0 && lo != hi)
            {
                // Bullet motor form. Our relVel is (vB − vA)·axis where Bullet's is the
                // negation, so the target carries the opposite sign to Bullet's — which
                // is the same sign the old spring-damper produced, so behaviour keeps its
                // direction and only its magnitude law changes.
                float k = con.SpringStiffness[i];
                float serr = curr - con.EquilibriumPoint[i];
                float force = serr * k;
                float velFactor = invDt * SpringDamping / iterations;
                float springTarget = -velFactor * force;

                // GetMotorFactor's `vel` is Bullet's tag_vel: −targetVelocity for a
                // linear axis, which in our sign convention is the target itself.
                float motFact = GetMotorFactor(curr, lo, hi, springTarget, invDt * InfoErp);
                F[baseI + SolverCache.LinSprTgt + i] = motFact * springTarget;
                F[baseI + SolverCache.LinSprMax + i] = MathF.Abs(force) * dt;
                F[baseI + SolverCache.LinSprImp + i] = 0;
                I[ib + SolverCache.ILinSprAct + i] = 1;
            }
            else if (!(lo == hi && con.IsLoop))
            {
                // locked loop 轴保持上一步的值（首步为 0，之后也不会被
                // 置 1——置 1 分支要求 lo != hi，故语义上恒为 0）。
                I[ib + SolverCache.ILinSprAct + i] = 0;
            }
        }

        // Angular: TA^T · TB → Euler XYZ; axes from TA.col2 × TB.col0.
        float r00 = TA[0] * TB[0] + TA[1] * TB[1] + TA[2] * TB[2];
        float r01 = TA[0] * TB[4] + TA[1] * TB[5] + TA[2] * TB[6];
        float r10 = TA[4] * TB[0] + TA[5] * TB[1] + TA[6] * TB[2];
        float r11 = TA[4] * TB[4] + TA[5] * TB[5] + TA[6] * TB[6];
        float r20 = TA[8] * TB[0] + TA[9] * TB[1] + TA[10] * TB[2];
        float r21 = TA[8] * TB[4] + TA[9] * TB[5] + TA[10] * TB[6];
        float r22 = TA[8] * TB[8] + TA[9] * TB[9] + TA[10] * TB[10];
        MatrixToEulerXYZ(r00, r01, r10, r11, r20, r21, r22, AngDiffScratch);

        float a2x = TA[8], a2y = TA[9], a2z = TA[10];
        float b0x = TB[0], b0y = TB[1], b0z = TB[2];
        float yx = a2y * b0z - a2z * b0y;
        float yy = a2z * b0x - a2x * b0z;
        float yz = a2x * b0y - a2y * b0x;
        float len = MathF.Sqrt(yx * yx + yy * yy + yz * yz);
        if (len > 1e-8f) { float inv = 1f / len; yx *= inv; yy *= inv; yz *= inv; }
        float xx = yy * a2z - yz * a2y;
        float xy = yz * a2x - yx * a2z;
        float xz = yx * a2y - yy * a2x;
        len = MathF.Sqrt(xx * xx + xy * xy + xz * xz);
        if (len > 1e-8f) { float inv = 1f / len; xx *= inv; xy *= inv; xz *= inv; }
        float zx = b0y * yz - b0z * yy;
        float zy = b0z * yx - b0x * yz;
        float zz = b0x * yy - b0y * yx;
        len = MathF.Sqrt(zx * zx + zy * zy + zz * zz);
        if (len > 1e-8f) { float inv = 1f / len; zx *= inv; zy *= inv; zz *= inv; }

        int angAxes = baseI + SolverCache.AngAxes;
        F[angAxes + 0] = xx; F[angAxes + 1] = xy; F[angAxes + 2] = xz;
        F[angAxes + 3] = yx; F[angAxes + 4] = yy; F[angAxes + 5] = yz;
        F[angAxes + 6] = zx; F[angAxes + 7] = zy; F[angAxes + 8] = zz;

        // Per-axis angular Jacobians with the full tensors: cache I⁻¹·axis per
        // body plus 1/(axᵀ(I⁻¹A+I⁻¹B)ax) per axis.
        int angJac = baseI + SolverCache.AngJac;
        int angWAs = baseI + SolverCache.AngWA;
        int angWBs = baseI + SolverCache.AngWB;
        for (int i = 0; i < 3; i++)
        {
            int o = i * 3;
            float axx = F[angAxes + o + 0], axy = F[angAxes + o + 1], axz = F[angAxes + o + 2];
            float wAx = W[a9 + 0] * axx + W[a9 + 1] * axy + W[a9 + 2] * axz;
            float wAy = W[a9 + 3] * axx + W[a9 + 4] * axy + W[a9 + 5] * axz;
            float wAz = W[a9 + 6] * axx + W[a9 + 7] * axy + W[a9 + 8] * axz;
            float wBx = W[b9 + 0] * axx + W[b9 + 1] * axy + W[b9 + 2] * axz;
            float wBy = W[b9 + 3] * axx + W[b9 + 4] * axy + W[b9 + 5] * axz;
            float wBz = W[b9 + 6] * axx + W[b9 + 7] * axy + W[b9 + 8] * axz;
            F[angWAs + o + 0] = wAx; F[angWAs + o + 1] = wAy; F[angWAs + o + 2] = wAz;
            F[angWBs + o + 0] = wBx; F[angWBs + o + 1] = wBy; F[angWBs + o + 2] = wBz;
            float denom = axx * (wAx + wBx) + axy * (wAy + wBy) + axz * (wAz + wBz);
            F[angJac + i] = denom > 0 ? 1f / denom : 0;
        }

        // Per-axis rows carry only the springs. Sign flip vs linear:
        // d(angDiff)/dt = −(ω_B − ω_A)·ax.
        int angTgt = baseI + SolverCache.AngTgt;
        int angAct = ib + SolverCache.IAngAct;
        for (int i = 0; i < 3; i++)
        {
            int idx = i + 3;
            // Springs on locked axes are skipped — the limit row welds those, and
            // double-driving a DOF overshoots every iteration (see the linear loop).
            if (con.SpringEnabled[idx] != 0 && F[angJac + i] > 0 && con.AngularMin[i] != con.AngularMax[i])
            {
                // Bullet motor form, angular flavour. Bullet's angular force is −err·k
                // and its rel_vel is the negation of our relAv, so the two flips cancel
                // and our target stays positive for positive error, as before.
                float k = con.SpringStiffness[idx];
                float serr = AngDiffScratch[i] - con.EquilibriumPoint[idx];
                float force = -serr * k;
                float velFactor = invDt * SpringDamping / iterations;
                float target = -velFactor * force;

                // tag_vel for a rotational axis is Bullet's targetVelocity, i.e. −ours.
                float motFact = GetMotorFactor(AngDiffScratch[i], con.AngularMin[i], con.AngularMax[i], -target, invDt * InfoErp);
                F[angTgt + i] = motFact * target;
                F[baseI + SolverCache.AngSprMax + i] = MathF.Abs(force) * dt;
                I[angAct + i] = 1;
            }
            else
            {
                F[angTgt + i] = 0;
                I[angAct + i] = 0;
            }
            F[baseI + SolverCache.AngSprImp + i] = 0;
        }

        // Angular limit handling is hybrid. Small violations (the resting-cloth
        // regime) use per-axis euler rows — they converge cleanly and keep resting
        // cloth dead still. Large violations switch to a single geodesic row toward
        // the euler-clamped target: per-axis euler rows (the Bullet-2.7x approach
        // this port used) become geometrically inconsistent for large errors — near
        // the asin singularity they chase phantom errors and pump angular velocity
        // into the chain instead of converging.
        I[ib + SolverCache.IAngLimAct] = 0;
        I[ib + SolverCache.IAngPaAct + 0] = 0;
        I[ib + SolverCache.IAngPaAct + 1] = 0;
        I[ib + SolverCache.IAngPaAct + 2] = 0;
        if (imA > 0 || imB > 0)
        {
            float ex = AngDiffScratch[0], ey = AngDiffScratch[1], ez = AngDiffScratch[2];
            // Free axes (min > max) follow the current angle, i.e. no correction.
            float tx = ex, ty = ey, tz = ez;
            if (con.AngularMin[0] <= con.AngularMax[0])
                tx = ex < con.AngularMin[0] ? con.AngularMin[0] : ex > con.AngularMax[0] ? con.AngularMax[0] : ex;
            if (con.AngularMin[1] <= con.AngularMax[1])
                ty = ey < con.AngularMin[1] ? con.AngularMin[1] : ey > con.AngularMax[1] ? con.AngularMax[1] : ey;
            if (con.AngularMin[2] <= con.AngularMax[2])
                tz = ez < con.AngularMin[2] ? con.AngularMin[2] : ez > con.AngularMax[2] ? con.AngularMax[2] : ez;
            float errX = ex - tx, errY = ey - ty, errZ = ez - tz;
            float maxErr = Math.Max(Math.Abs(errX), Math.Max(Math.Abs(errY), Math.Abs(errZ)));
            if (maxErr > 0 && maxErr < GeodesicThreshold)
            {
                // Per-axis euler limit rows. Locked axes are bilateral joints; ranged
                // axes are unilateral stops (sign-clamped accumulation) — a bilateral
                // row on a ranged axis brakes natural recovery every substep and
                // pumps energy into swinging cloth, and the pump grows WITH solver
                // convergence (more iterations enforce the brake harder).
                for (int i = 0; i < 3; i++)
                {
                    float err = i == 0 ? errX : i == 1 ? errY : errZ;
                    F[baseI + SolverCache.AngPaImp + i] = 0;
                    if (err == 0)
                    {
                        I[ib + SolverCache.IAngPaAct + i] = 0;
                        continue;
                    }
                    float target = err * ConstraintBuilder.StopErp * erpScale * invDt;
                    if (target > MaxAngularCorrectionVel) target = MaxAngularCorrectionVel;
                    else if (target < -MaxAngularCorrectionVel) target = -MaxAngularCorrectionVel;
                    F[baseI + SolverCache.AngPaTgt + i] = target;
                    I[ib + SolverCache.IAngPaAct + i] = con.AngularMin[i] == con.AngularMax[i] ? 1 : 2;
                }
            }
            else if (maxErr > 0)
            {
                // Bilateral (equality) if any violated axis is locked — a locked axis
                // is a joint, not a stop. Unilateral otherwise.
                bool bilateral =
                    (tx != ex && con.AngularMin[0] == con.AngularMax[0]) ||
                    (ty != ey && con.AngularMin[1] == con.AngularMax[1]) ||
                    (tz != ez && con.AngularMin[2] == con.AngularMax[2]);

                // The decomposition above satisfies R_rel^T = Rx(x)·Ry(y)·Rz(z), so
                // u = qx·qy·qz is conj(q_rel) and the error rotation (current →
                // clamped target, expressed in TA's frame) is q_E = conj(u_t) ⊗ u.
                EulerXyzQuatInto(ex, ey, ez, QuatScratchA);
                EulerXyzQuatInto(tx, ty, tz, QuatScratchB);
                float ux = QuatScratchA[0], uy = QuatScratchA[1], uz = QuatScratchA[2], uw = QuatScratchA[3];
                float vx = QuatScratchB[0], vy = QuatScratchB[1], vz = QuatScratchB[2], vw = QuatScratchB[3];
                // q_E = conj(v) ⊗ u
                float qex = vw * ux - vx * uw - vy * uz + vz * uy;
                float qey = vw * uy + vx * uz - vy * uw - vz * ux;
                float qez = vw * uz - vx * uy + vy * ux - vz * uw;
                float qew = vw * uw + vx * ux + vy * uy + vz * uz;
                if (qew < 0) { qex = -qex; qey = -qey; qez = -qez; qew = -qew; }
                double sinHalf = Math.Sqrt((double)qex * qex + (double)qey * qey + (double)qez * qez);
                if (sinHalf > 1e-6)
                {
                    double angle = 2 * Math.Atan2(sinHalf, qew);
                    double invS = 1 / sinHalf;
                    float axx = (float)(qex * invS), axy = (float)(qey * invS), axz = (float)(qez * invS);

                    // Axis lives in TA's frame; TA's basis columns map it to world.
                    int lim = baseI + SolverCache.AngLimAxis;
                    F[lim + 0] = TA[0] * axx + TA[4] * axy + TA[8] * axz;
                    F[lim + 1] = TA[1] * axx + TA[5] * axy + TA[9] * axz;
                    F[lim + 2] = TA[2] * axx + TA[6] * axy + TA[10] * axz;
                    int gWA = baseI + SolverCache.AngLimWA;
                    int gWB = baseI + SolverCache.AngLimWB;
                    F[gWA + 0] = W[a9 + 0] * F[lim + 0] + W[a9 + 1] * F[lim + 1] + W[a9 + 2] * F[lim + 2];
                    F[gWA + 1] = W[a9 + 3] * F[lim + 0] + W[a9 + 4] * F[lim + 1] + W[a9 + 5] * F[lim + 2];
                    F[gWA + 2] = W[a9 + 6] * F[lim + 0] + W[a9 + 7] * F[lim + 1] + W[a9 + 8] * F[lim + 2];
                    F[gWB + 0] = W[b9 + 0] * F[lim + 0] + W[b9 + 1] * F[lim + 1] + W[b9 + 2] * F[lim + 2];
                    F[gWB + 1] = W[b9 + 3] * F[lim + 0] + W[b9 + 4] * F[lim + 1] + W[b9 + 5] * F[lim + 2];
                    F[gWB + 2] = W[b9 + 6] * F[lim + 0] + W[b9 + 7] * F[lim + 1] + W[b9 + 8] * F[lim + 2];
                    double gDenom =
                        (double)F[lim + 0] * ((double)F[gWA + 0] + F[gWB + 0]) +
                        (double)F[lim + 1] * ((double)F[gWA + 1] + F[gWB + 1]) +
                        (double)F[lim + 2] * ((double)F[gWA + 2] + F[gWB + 2]);
                    D[db + 0] = gDenom > 0 ? 1 / gDenom : 0;
                    double target = angle * ConstraintBuilder.StopErp * erpScale * invDt;
                    if (target > MaxAngularCorrectionVel) target = MaxAngularCorrectionVel;
                    D[db + 1] = target;
                    I[ib + SolverCache.IAngLimAct] = bilateral ? 1 : 2;
                }
            }
        }
        D[db + 2] = 0;
    }

    /// <summary>
    /// ITER: read cache, compute relVel from current lv/av, apply impulse.
    /// </summary>
    private static void IterateConstraint(
        int ci,
        SolverCache cache,
        float[] lv,
        float[] av,
        float[] invMass)
    {
        float[] F = cache.F;
        int[] I = cache.I;
        double[] D = cache.D;
        int baseI = ci * SolverCache.FStride;
        int ib = ci * SolverCache.IStride;
        int db = ci * 3;
        if (I[ib + SolverCache.ISkip] == 1) return;
        int a = I[ib + SolverCache.IBodyA];
        int b = I[ib + SolverCache.IBodyB];
        int ai = a * 3;
        int bi = b * 3;
        float imA = invMass[a];
        float imB = invMass[b];

        // Linear axes — relVel at the offset point: v_pivot = v_CG + ω × r.
        int lA = baseI + SolverCache.LeverA;
        int lB = baseI + SolverCache.LeverB;
        float rAx = F[lA + 0], rAy = F[lA + 1], rAz = F[lA + 2];
        float rBx = F[lB + 0], rBy = F[lB + 1], rBz = F[lB + 2];
        int axes = baseI + SolverCache.LinAxes;
        int cA = baseI + SolverCache.LinCA;
        int cB = baseI + SolverCache.LinCB;
        int jac = baseI + SolverCache.LinJac;
        int tgt = baseI + SolverCache.LinTgt;
        int act = ib + SolverCache.ILinAct;

        float vAx = lv[ai + 0] + av[ai + 1] * rAz - av[ai + 2] * rAy;
        float vAy = lv[ai + 1] + av[ai + 2] * rAx - av[ai + 0] * rAz;
        float vAz = lv[ai + 2] + av[ai + 0] * rAy - av[ai + 1] * rAx;
        float vBx = lv[bi + 0] + av[bi + 1] * rBz - av[bi + 2] * rBy;
        float vBy = lv[bi + 1] + av[bi + 2] * rBx - av[bi + 0] * rBz;
        float vBz = lv[bi + 2] + av[bi + 0] * rBy - av[bi + 1] * rBx;
        float dvx = vBx - vAx;
        float dvy = vBy - vAy;
        float dvz = vBz - vAz;

        int sprAct = ib + SolverCache.ILinSprAct;
        int sprTgt = baseI + SolverCache.LinSprTgt;
        int sprMax = baseI + SolverCache.LinSprMax;
        int sprImp = baseI + SolverCache.LinSprImp;
        int limImp = baseI + SolverCache.LinLimitImp;
        for (int i = 0; i < 3; i++)
        {
            if (I[act + i] == 0 && I[sprAct + i] == 0) continue;
            int o = i * 3;
            float axx = F[axes + o + 0], axy = F[axes + o + 1], axz = F[axes + o + 2];
            float relVel = dvx * axx + dvy * axy + dvz * axz;
            float j = 0;

            // Limit row. Locked axes (act 1) are bilateral equality joints; ranged
            // axes in violation (act 2) are unilateral stops — accumulated impulse
            // clamped to the corrective sign, so the stop pushes back into range but
            // never pulls deeper or brakes natural recovery (a bilateral stop acts
            // as a motor and pumps energy into swinging cloth).
            if (I[act + i] != 0)
            {
                float target = F[tgt + i];
                float dImp = LimitSoftnessLinear * (target - relVel) * F[jac + i];
                if (I[act + i] == 2)
                {
                    float old = F[limImp + i];
                    float next = old + dImp;
                    if (target > 0 ? next < 0 : next > 0) next = 0;
                    dImp = next - old;
                    F[limImp + i] = next;
                }
                j += dImp;
            }

            // Implicit spring-damper row (soft constraint, see setup): CFM-softened
            // with accumulated λ, dissipative by construction. relVel is refreshed
            // with the limit impulse applied just above (j·denom = j / jac) — driving
            // the spring off the stale value double-corrects the DOF.
            if (I[sprAct + i] != 0)
            {
                float relVelNow = j != 0 ? relVel + j / F[jac + i] : relVel;
                // Motor row: drive toward the target, accumulated impulse clamped to the
                // spring's real force over the step (Bullet's m_lowerLimit/m_upperLimit).
                float dImp = (F[sprTgt + i] - relVelNow) * F[jac + i];
                float maxF = F[sprMax + i];
                float oldS = F[sprImp + i];
                float nextS = oldS + dImp;
                if (nextS < -maxF) nextS = -maxF;
                else if (nextS > maxF) nextS = maxF;
                dImp = nextS - oldS;
                F[sprImp + i] = nextS;
                j += dImp;
            }

            if (j == 0) continue;
            if (imA > 0)
            {
                lv[ai + 0] -= j * imA * axx;
                lv[ai + 1] -= j * imA * axy;
                lv[ai + 2] -= j * imA * axz;
                av[ai + 0] -= j * F[cA + o + 0];
                av[ai + 1] -= j * F[cA + o + 1];
                av[ai + 2] -= j * F[cA + o + 2];
            }
            if (imB > 0)
            {
                lv[bi + 0] += j * imB * axx;
                lv[bi + 1] += j * imB * axy;
                lv[bi + 2] += j * imB * axz;
                av[bi + 0] += j * F[cB + o + 0];
                av[bi + 1] += j * F[cB + o + 1];
                av[bi + 2] += j * F[cB + o + 2];
            }
        }

        // Angular axes — relAv = ω_B − ω_A.
        int angAxes = baseI + SolverCache.AngAxes;
        int angJac = baseI + SolverCache.AngJac;
        int angWAs = baseI + SolverCache.AngWA;
        int angWBs = baseI + SolverCache.AngWB;
        int angTgt = baseI + SolverCache.AngTgt;
        int angAct = ib + SolverCache.IAngAct;
        float dax = av[bi + 0] - av[ai + 0];
        float day = av[bi + 1] - av[ai + 1];
        float daz = av[bi + 2] - av[ai + 2];
        int angSprMax = baseI + SolverCache.AngSprMax;
        int angSprImp = baseI + SolverCache.AngSprImp;
        for (int i = 0; i < 3; i++)
        {
            if (I[angAct + i] == 0) continue;
            int o = i * 3;
            float axx = F[angAxes + o + 0], axy = F[angAxes + o + 1], axz = F[angAxes + o + 2];
            float relAv = dax * axx + day * axy + daz * axz;
            // Implicit spring-damper row (soft constraint, see setup).
            float j = (F[angTgt + i] - relAv) * F[angJac + i];
            float maxF = F[angSprMax + i];
            float oldS = F[angSprImp + i];
            float nextS = oldS + j;
            if (nextS < -maxF) nextS = -maxF;
            else if (nextS > maxF) nextS = maxF;
            j = nextS - oldS;
            F[angSprImp + i] = nextS;
            if (j == 0) continue;
            if (imA > 0)
            {
                av[ai + 0] -= j * F[angWAs + o + 0];
                av[ai + 1] -= j * F[angWAs + o + 1];
                av[ai + 2] -= j * F[angWAs + o + 2];
            }
            if (imB > 0)
            {
                av[bi + 0] += j * F[angWBs + o + 0];
                av[bi + 1] += j * F[angWBs + o + 1];
                av[bi + 2] += j * F[angWBs + o + 2];
            }
        }

        // Per-axis angular limit rows (small-violation regime), on the derived
        // euler axes. Sign convention matches the springs: positive target reduces
        // positive error via d(angDiff)/dt = −(ω_B − ω_A)·ax.
        int paAct = ib + SolverCache.IAngPaAct;
        if (I[paAct + 0] != 0 || I[paAct + 1] != 0 || I[paAct + 2] != 0)
        {
            int paTgt = baseI + SolverCache.AngPaTgt;
            int paImp = baseI + SolverCache.AngPaImp;
            for (int i = 0; i < 3; i++)
            {
                if (I[paAct + i] == 0) continue;
                int o = i * 3;
                float axx = F[angAxes + o + 0], axy = F[angAxes + o + 1], axz = F[angAxes + o + 2];
                float relAv =
                    (av[bi + 0] - av[ai + 0]) * axx +
                    (av[bi + 1] - av[ai + 1]) * axy +
                    (av[bi + 2] - av[ai + 2]) * axz;
                float target = F[paTgt + i];

                // Locked axes (act 1) are welds — full gain, like the 0.16.3 fold;
                // softness only tempers the unilateral stops.
                float soft = I[paAct + i] == 2 ? LimitSoftnessAngular : 1.0f;
                float j = soft * (target - relAv) * F[angJac + i];
                if (I[paAct + i] == 2)
                {
                    float old = F[paImp + i];
                    float next = old + j;
                    if (target > 0 ? next < 0 : next > 0) next = 0;
                    j = next - old;
                    F[paImp + i] = next;
                }
                if (j == 0) continue;
                if (imA > 0)
                {
                    av[ai + 0] -= j * F[angWAs + o + 0];
                    av[ai + 1] -= j * F[angWAs + o + 1];
                    av[ai + 2] -= j * F[angWAs + o + 2];
                }
                if (imB > 0)
                {
                    av[bi + 0] += j * F[angWBs + o + 0];
                    av[bi + 1] += j * F[angWBs + o + 1];
                    av[bi + 2] += j * F[angWBs + o + 2];
                }
            }
        }

        // Geodesic limit row: drive (ω_B − ω_A)·axis toward the correction target.
        // Unilateral — the accumulated impulse can only push toward the legal
        // region (target is always ≥ 0 along the corrective axis).
        if (I[ib + SolverCache.IAngLimAct] != 0)
        {
            int lim = baseI + SolverCache.AngLimAxis;
            float nx = F[lim + 0], ny = F[lim + 1], nz = F[lim + 2];
            // Re-read relAv — the spring rows above may have changed av.
            float relAv =
                (av[bi + 0] - av[ai + 0]) * nx +
                (av[bi + 1] - av[ai + 1]) * ny +
                (av[bi + 2] - av[ai + 2]) * nz;
            // D 是 reze 的 f64 通道：该行乘法全程 double，最后才落 float。
            double jd = LimitSoftnessAngular * (D[db + 1] - (double)relAv) * D[db + 0];
            if (I[ib + SolverCache.IAngLimAct] == 2)
            {
                double old = D[db + 2];
                double next = old + jd;
                if (next < 0) next = 0;
                jd = next - old;
                D[db + 2] = next;
            }
            float j = (float)jd;
            if (j != 0)
            {
                int gWA = baseI + SolverCache.AngLimWA;
                int gWB = baseI + SolverCache.AngLimWB;
                if (imA > 0)
                {
                    av[ai + 0] -= j * F[gWA + 0];
                    av[ai + 1] -= j * F[gWA + 1];
                    av[ai + 2] -= j * F[gWA + 2];
                }
                if (imB > 0)
                {
                    av[bi + 0] += j * F[gWB + 0];
                    av[bi + 1] += j * F[gWB + 1];
                    av[bi + 2] += j * F[gWB + 2];
                }
            }
        }
    }

    /// <summary>
    /// SETUP: pre-compute Jacobians, friction basis, and the bounce reference
    /// from the *initial* closing velocity (Bullet's pattern — captures restitution
    /// before iter 1 zeroes out the approach).
    /// </summary>
    private static void SetupContactRow(
        Contact c,
        float[] lv,
        float[] av,
        float[] invMass,
        float[] W,
        float invDt)
    {
        int ai = c.BodyA * 3;
        int bi = c.BodyB * 3;
        int a9 = c.BodyA * 9;
        int b9 = c.BodyB * 9;
        float imA = invMass[c.BodyA];
        float imB = invMass[c.BodyB];
        float rAx = c.RAx, rAy = c.RAy, rAz = c.RAz;
        float rBx = c.RBx, rBy = c.RBy, rBz = c.RBz;
        float nx = c.Nx, ny = c.Ny, nz = c.Nz;

        // Normal Jacobian. Cached vectors are tensor-multiplied I⁻¹·(r×n).
        float cAxN = rAy * nz - rAz * ny;
        float cAyN = rAz * nx - rAx * nz;
        float cAzN = rAx * ny - rAy * nx;
        float cBxN = rBy * nz - rBz * ny;
        float cByN = rBz * nx - rBx * nz;
        float cBzN = rBx * ny - rBy * nx;
        float wAxN = W[a9 + 0] * cAxN + W[a9 + 1] * cAyN + W[a9 + 2] * cAzN;
        float wAyN = W[a9 + 3] * cAxN + W[a9 + 4] * cAyN + W[a9 + 5] * cAzN;
        float wAzN = W[a9 + 6] * cAxN + W[a9 + 7] * cAyN + W[a9 + 8] * cAzN;
        float wBxN = W[b9 + 0] * cBxN + W[b9 + 1] * cByN + W[b9 + 2] * cBzN;
        float wByN = W[b9 + 3] * cBxN + W[b9 + 4] * cByN + W[b9 + 5] * cBzN;
        float wBzN = W[b9 + 6] * cBxN + W[b9 + 7] * cByN + W[b9 + 8] * cBzN;
        float denomN = imA + imB +
            (cAxN * wAxN + cAyN * wAyN + cAzN * wAzN) +
            (cBxN * wBxN + cByN * wByN + cBzN * wBzN);
        c.CAxN = wAxN; c.CAyN = wAyN; c.CAzN = wAzN;
        c.CBxN = wBxN; c.CByN = wByN; c.CBzN = wBzN;
        c.JacInvN = denomN > 0 ? 1f / denomN : 0;

        // Restitution reference, captured from initial relVelN.
        float vAx = lv[ai + 0] + av[ai + 1] * rAz - av[ai + 2] * rAy;
        float vAy = lv[ai + 1] + av[ai + 2] * rAx - av[ai + 0] * rAz;
        float vAz = lv[ai + 2] + av[ai + 0] * rAy - av[ai + 1] * rAx;
        float vBx = lv[bi + 0] + av[bi + 1] * rBz - av[bi + 2] * rBy;
        float vBy = lv[bi + 1] + av[bi + 2] * rBx - av[bi + 0] * rBz;
        float vBz = lv[bi + 2] + av[bi + 0] * rBy - av[bi + 1] * rBx;
        float dvx = vBx - vAx;
        float dvy = vBy - vAy;
        float dvz = vBz - vAz;
        float relVelN0 = dvx * nx + dvy * ny + dvz * nz;

        // Restitution is switched off once a point has been in contact for more than
        // a couple of substeps (m_restingContactRestitutionThreshold), or resting
        // cloth keeps being handed a little bounce forever.
        c.BounceVel = c.Restitution > 0 && relVelN0 < -BounceThreshold && c.Age <= RestingContactAge
            ? -c.Restitution * relVelN0
            : 0;

        // Speculative rows (depth < 0 — the shapes are inside the margin band but
        // NOT touching) must not brake a body that hasn't arrived yet. Their whole
        // job is to stop it crossing the surface within this substep, so the
        // approach speed they leave alone is exactly the one that closes the
        // remaining gap in dt; only the excess above that is cancelled.
        //
        // Without this the row targets relVelN = 0 like a touching contact and
        // stops approaching bodies dead up to CONTACT_MARGIN away from anything.
        // The push-only clamp does NOT prevent that — it only forbids a negative
        // (pulling) impulse, not a large positive one on a body in mid-air. On a
        // dress rig half of all contact rows are speculative and 88% of them fire,
        // which is the field of invisible brakes the cloth was shaking against.
        float positionalError = (c.Depth - LinearSlop) * ContactErp * invDt;
        if (SplitImpulse && c.Depth > SplitPenetrationThreshold)
        {
            c.BiasVel = 0;
            c.RhsPenetration = positionalError;
        }
        else
        {
            c.BiasVel = positionalError;
            c.RhsPenetration = 0;
        }

        // Friction direction, Bullet 2.75 style. The stock solverMode is
        // SOLVER_USE_WARMSTARTING | SOLVER_SIMD — SOLVER_USE_2_FRICTION_DIRECTIONS is
        // NOT set, so there is exactly ONE friction row, aligned with the direction
        // the contact is actually sliding:
        //
        //   m_lateralFrictionDir1 = vel - normalWorldOnB * rel_vel
        //
        // with vel = vA − vB. Our dv is vB − vA, so that direction is the negation of
        // dv's tangential part; sign is irrelevant to a row with symmetric ±μN bounds.
        // Below SIMD_EPSILON of sliding Bullet falls back to btPlaneSpace1, ported
        // exactly. Direction is recomputed every substep — friction-direction caching
        // is off in the stock mode.
        float t1x = dvx - nx * relVelN0;
        float t1y = dvy - ny * relVelN0;
        float t1z = dvz - nz * relVelN0;
        float t2x, t2y, t2z;
        float lat2 = t1x * t1x + t1y * t1y + t1z * t1z;
        if (lat2 > 1.192092896e-07f)
        {
            float inv = 1f / MathF.Sqrt(lat2);
            t1x *= inv; t1y *= inv; t1z *= inv;
            // Second tangent is unused (single-direction mode) but kept orthonormal so
            // the cached Jacobian slots stay well-defined.
            t2x = ny * t1z - nz * t1y;
            t2y = nz * t1x - nx * t1z;
            t2z = nx * t1y - ny * t1x;
        }
        else
        {
            // btPlaneSpace1, verbatim.
            if (MathF.Abs(nz) > 0.7071067811865475f)
            {
                float a = ny * ny + nz * nz;
                float k = 1f / MathF.Sqrt(a);
                t1x = 0; t1y = -nz * k; t1z = ny * k;
                t2x = a * k; t2y = -nx * t1z; t2z = nx * t1y;
            }
            else
            {
                float a = nx * nx + ny * ny;
                float k = 1f / MathF.Sqrt(a);
                t1x = -ny * k; t1y = nx * k; t1z = 0;
                t2x = -nz * t1y; t2y = nz * t1x; t2z = a * k;
            }
        }
        c.T1x = t1x; c.T1y = t1y; c.T1z = t1z;
        c.T2x = t2x; c.T2y = t2y; c.T2z = t2z;

        // Friction Jacobians.
        float cAxT1 = rAy * t1z - rAz * t1y;
        float cAyT1 = rAz * t1x - rAx * t1z;
        float cAzT1 = rAx * t1y - rAy * t1x;
        float cBxT1 = rBy * t1z - rBz * t1y;
        float cByT1 = rBz * t1x - rBx * t1z;
        float cBzT1 = rBx * t1y - rBy * t1x;
        float wAxT1 = W[a9 + 0] * cAxT1 + W[a9 + 1] * cAyT1 + W[a9 + 2] * cAzT1;
        float wAyT1 = W[a9 + 3] * cAxT1 + W[a9 + 4] * cAyT1 + W[a9 + 5] * cAzT1;
        float wAzT1 = W[a9 + 6] * cAxT1 + W[a9 + 7] * cAyT1 + W[a9 + 8] * cAzT1;
        float wBxT1 = W[b9 + 0] * cBxT1 + W[b9 + 1] * cByT1 + W[b9 + 2] * cBzT1;
        float wByT1 = W[b9 + 3] * cBxT1 + W[b9 + 4] * cByT1 + W[b9 + 5] * cBzT1;
        float wBzT1 = W[b9 + 6] * cBxT1 + W[b9 + 7] * cByT1 + W[b9 + 8] * cBzT1;
        float denomT1 = imA + imB +
            (cAxT1 * wAxT1 + cAyT1 * wAyT1 + cAzT1 * wAzT1) +
            (cBxT1 * wBxT1 + cByT1 * wByT1 + cBzT1 * wBzT1);
        c.CAxT1 = wAxT1; c.CAyT1 = wAyT1; c.CAzT1 = wAzT1;
        c.CBxT1 = wBxT1; c.CByT1 = wByT1; c.CBzT1 = wBzT1;
        c.JacInvT1 = denomT1 > 0 ? 1f / denomT1 : 0;

        float cAxT2 = rAy * t2z - rAz * t2y;
        float cAyT2 = rAz * t2x - rAx * t2z;
        float cAzT2 = rAx * t2y - rAy * t2x;
        float cBxT2 = rBy * t2z - rBz * t2y;
        float cByT2 = rBz * t2x - rBx * t2z;
        float cBzT2 = rBx * t2y - rBy * t2x;
        float wAxT2 = W[a9 + 0] * cAxT2 + W[a9 + 1] * cAyT2 + W[a9 + 2] * cAzT2;
        float wAyT2 = W[a9 + 3] * cAxT2 + W[a9 + 4] * cAyT2 + W[a9 + 5] * cAzT2;
        float wAzT2 = W[a9 + 6] * cAxT2 + W[a9 + 7] * cAyT2 + W[a9 + 8] * cAzT2;
        float wBxT2 = W[b9 + 0] * cBxT2 + W[b9 + 1] * cByT2 + W[b9 + 2] * cBzT2;
        float wByT2 = W[b9 + 3] * cBxT2 + W[b9 + 4] * cByT2 + W[b9 + 5] * cBzT2;
        float wBzT2 = W[b9 + 6] * cBxT2 + W[b9 + 7] * cByT2 + W[b9 + 8] * cBzT2;
        float denomT2 = imA + imB +
            (cAxT2 * wAxT2 + cAyT2 * wAyT2 + cAzT2 * wAzT2) +
            (cBxT2 * wBxT2 + cByT2 * wByT2 + cBzT2 * wBzT2);
        c.CAxT2 = wAxT2; c.CAyT2 = wAyT2; c.CAzT2 = wAzT2;
        c.CBxT2 = wBxT2; c.CByT2 = wByT2; c.CBzT2 = wBzT2;
        c.JacInvT2 = denomT2 > 0 ? 1f / denomT2 : 0;
    }

    /// <summary>
    /// ITER, normal row only — Bullet resolveSingleConstraintRowLowerLimit:
    /// deltaImpulse = (rhs - relVel) * jacDiagABInv, accumulated impulse clamped at
    /// the lower limit of 0 (push-only). `rhs` here is restitution + Baumgarte bias.
    /// </summary>
    private static void IterateContactNormal(
        Contact c,
        float[] lv,
        float[] av,
        float[] invMass)
    {
        float jacInvN = c.JacInvN;
        if (jacInvN <= 0) return;
        float imA = invMass[c.BodyA];
        float imB = invMass[c.BodyB];
        if (imA == 0 && imB == 0) return;
        int ai = c.BodyA * 3, bi = c.BodyB * 3;
        float rAx = c.RAx, rAy = c.RAy, rAz = c.RAz;
        float rBx = c.RBx, rBy = c.RBy, rBz = c.RBz;
        float nx = c.Nx, ny = c.Ny, nz = c.Nz;

        float vAx = lv[ai + 0] + av[ai + 1] * rAz - av[ai + 2] * rAy;
        float vAy = lv[ai + 1] + av[ai + 2] * rAx - av[ai + 0] * rAz;
        float vAz = lv[ai + 2] + av[ai + 0] * rAy - av[ai + 1] * rAx;
        float vBx = lv[bi + 0] + av[bi + 1] * rBz - av[bi + 2] * rBy;
        float vBy = lv[bi + 1] + av[bi + 2] * rBx - av[bi + 0] * rBz;
        float vBz = lv[bi + 2] + av[bi + 0] * rBy - av[bi + 1] * rBx;
        float relVelN = (vBx - vAx) * nx + (vBy - vAy) * ny + (vBz - vAz) * nz;

        float dImpN = (c.BounceVel + c.BiasVel - relVelN) * jacInvN;
        float oldN = c.AppliedNormalImpulse;
        float newN = oldN + dImpN;
        if (newN < 0) { newN = 0; dImpN = -oldN; }
        c.AppliedNormalImpulse = newN;
        if (dImpN == 0) return;
        if (imA > 0)
        {
            lv[ai + 0] -= dImpN * imA * nx;
            lv[ai + 1] -= dImpN * imA * ny;
            lv[ai + 2] -= dImpN * imA * nz;
            av[ai + 0] -= dImpN * c.CAxN;
            av[ai + 1] -= dImpN * c.CAyN;
            av[ai + 2] -= dImpN * c.CAzN;
        }
        if (imB > 0)
        {
            lv[bi + 0] += dImpN * imB * nx;
            lv[bi + 1] += dImpN * imB * ny;
            lv[bi + 2] += dImpN * imB * nz;
            av[bi + 0] += dImpN * c.CBxN;
            av[bi + 1] += dImpN * c.CByN;
            av[bi + 2] += dImpN * c.CBzN;
        }
    }

    /// <summary>
    /// ITER, friction pass. One Coulomb row (stock Bullet has
    /// SOLVER_USE_2_FRICTION_DIRECTIONS off), bounded by ±friction · the normal
    /// impulse accumulated so far. Bullet skips the row entirely while that is
    /// still zero.
    /// </summary>
    private static void IterateContactFriction(
        Contact c,
        float[] lv,
        float[] av,
        float[] invMass)
    {
        float muNormal = c.Friction * c.AppliedNormalImpulse;
        if (muNormal <= 0) return;
        if (c.JacInvT1 <= 0) return;
        float imA = invMass[c.BodyA];
        float imB = invMass[c.BodyB];
        if (imA == 0 && imB == 0) return;
        int ai = c.BodyA * 3, bi = c.BodyB * 3;
        float rAx = c.RAx, rAy = c.RAy, rAz = c.RAz;
        float rBx = c.RBx, rBy = c.RBy, rBz = c.RBz;

        float vAx = lv[ai + 0] + av[ai + 1] * rAz - av[ai + 2] * rAy;
        float vAy = lv[ai + 1] + av[ai + 2] * rAx - av[ai + 0] * rAz;
        float vAz = lv[ai + 2] + av[ai + 0] * rAy - av[ai + 1] * rAx;
        float vBx = lv[bi + 0] + av[bi + 1] * rBz - av[bi + 2] * rBy;
        float vBy = lv[bi + 1] + av[bi + 2] * rBx - av[bi + 0] * rBz;
        float vBz = lv[bi + 2] + av[bi + 0] * rBy - av[bi + 1] * rBx;
        float dvx = vBx - vAx, dvy = vBy - vAy, dvz = vBz - vAz;

        float relVel = dvx * c.T1x + dvy * c.T1y + dvz * c.T1z;
        float dImp = -relVel * c.JacInvT1;
        float old = c.AppliedFrictionImpulse1;
        float next = old + dImp;
        if (next < -muNormal) next = -muNormal;
        else if (next > muNormal) next = muNormal;
        dImp = next - old;
        c.AppliedFrictionImpulse1 = next;
        if (dImp == 0) return;
        if (imA > 0)
        {
            lv[ai + 0] -= dImp * imA * c.T1x;
            lv[ai + 1] -= dImp * imA * c.T1y;
            lv[ai + 2] -= dImp * imA * c.T1z;
            av[ai + 0] -= dImp * c.CAxT1;
            av[ai + 1] -= dImp * c.CAyT1;
            av[ai + 2] -= dImp * c.CAzT1;
        }
        if (imB > 0)
        {
            lv[bi + 0] += dImp * imB * c.T1x;
            lv[bi + 1] += dImp * imB * c.T1y;
            lv[bi + 2] += dImp * imB * c.T1z;
            av[bi + 0] += dImp * c.CBxT1;
            av[bi + 1] += dImp * c.CByT1;
            av[bi + 2] += dImp * c.CBzT1;
        }
    }

    /// <summary>
    /// Bullet's resolveSplitPenetrationImpulse: same Jacobian, but reading and
    /// writing the pseudo-velocity channel, with its own push-only accumulator.
    /// </summary>
    private static void ResolveSplitPenetration(Contact c, float[] invMass)
    {
        if (c.RhsPenetration == 0) return;
        float jacInvN = c.JacInvN;
        if (jacInvN <= 0) return;
        float imA = invMass[c.BodyA];
        float imB = invMass[c.BodyB];
        if (imA == 0 && imB == 0) return;
        int ai = c.BodyA * 3, bi = c.BodyB * 3;
        float nx = c.Nx, ny = c.Ny, nz = c.Nz;
        float rAx = c.RAx, rAy = c.RAy, rAz = c.RAz;
        float rBx = c.RBx, rBy = c.RBy, rBz = c.RBz;

        float vAx = _pushLin[ai + 0] + _pushAng[ai + 1] * rAz - _pushAng[ai + 2] * rAy;
        float vAy = _pushLin[ai + 1] + _pushAng[ai + 2] * rAx - _pushAng[ai + 0] * rAz;
        float vAz = _pushLin[ai + 2] + _pushAng[ai + 0] * rAy - _pushAng[ai + 1] * rAx;
        float vBx = _pushLin[bi + 0] + _pushAng[bi + 1] * rBz - _pushAng[bi + 2] * rBy;
        float vBy = _pushLin[bi + 1] + _pushAng[bi + 2] * rBx - _pushAng[bi + 0] * rBz;
        float vBz = _pushLin[bi + 2] + _pushAng[bi + 0] * rBy - _pushAng[bi + 1] * rBx;
        float relVel = (vBx - vAx) * nx + (vBy - vAy) * ny + (vBz - vAz) * nz;

        float d = (c.RhsPenetration - relVel) * jacInvN;
        float old = c.AppliedPushImpulse;
        float sum = old + d;
        if (sum < 0) { sum = 0; d = -old; }
        c.AppliedPushImpulse = sum;
        if (d == 0) return;
        if (imA > 0)
        {
            _pushLin[ai + 0] -= d * imA * nx;
            _pushLin[ai + 1] -= d * imA * ny;
            _pushLin[ai + 2] -= d * imA * nz;
            _pushAng[ai + 0] -= d * c.CAxN;
            _pushAng[ai + 1] -= d * c.CAyN;
            _pushAng[ai + 2] -= d * c.CAzN;
        }
        if (imB > 0)
        {
            _pushLin[bi + 0] += d * imB * nx;
            _pushLin[bi + 1] += d * imB * ny;
            _pushLin[bi + 2] += d * imB * nz;
            _pushAng[bi + 0] += d * c.CBxN;
            _pushAng[bi + 1] += d * c.CByN;
            _pushAng[bi + 2] += d * c.CBzN;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// btTypedConstraint::getMotorFactor. Scales a motor down as it approaches the
    /// limit it is driving toward, so a stiff spring cannot drive straight through
    /// its own stop and then be fought by the limit row. That fight is a candidate
    /// explanation for the period-2 joint chatter seen on bodies carrying NO
    /// contacts at all (qh_15_5: accumulated joint impulse alternating 8.4 / 14.7).
    /// </summary>
    private static float GetMotorFactor(float pos, float lowLim, float uppLim, float vel, float timeFact)
    {
        if (lowLim > uppLim) return 1;
        if (lowLim == uppLim) return 0;
        float deltaMax = vel / timeFact;
        if (deltaMax < 0)
        {
            if (pos >= lowLim && pos < lowLim - deltaMax) return (lowLim - pos) / deltaMax;
            return pos < lowLim ? 0 : 1;
        }
        if (deltaMax > 0)
        {
            if (pos <= uppLim && pos > uppLim - deltaMax) return (uppLim - pos) / deltaMax;
            return pos > uppLim ? 0 : 1;
        }
        return 0;
    }

    private static void BuildBodyMat(RigidBodyStore store, int i, float[] output)
    {
        int i3 = i * 3, i4 = i * 4;
        Mat4.FromPositionRotationInto(
            store.Positions[i3 + 0], store.Positions[i3 + 1], store.Positions[i3 + 2],
            store.Orientations[i4 + 0], store.Orientations[i4 + 1], store.Orientations[i4 + 2], store.Orientations[i4 + 3],
            output, 0);
    }

    /// <summary>Quaternion of qx(x) ⊗ qy(y) ⊗ qz(z) (three.js 'XYZ' order).</summary>
    private static void EulerXyzQuatInto(float x, float y, float z, float[] output)
    {
        float sx = MathF.Sin(x * 0.5f), cx = MathF.Cos(x * 0.5f);
        float sy = MathF.Sin(y * 0.5f), cy = MathF.Cos(y * 0.5f);
        float sz = MathF.Sin(z * 0.5f), cz = MathF.Cos(z * 0.5f);
        output[0] = sx * cy * cz + cx * sy * sz;
        output[1] = cx * sy * cz - sx * cy * sz;
        output[2] = cx * cy * sz + sx * sy * cz;
        output[3] = cx * cy * cz - sx * sy * sz;
    }

    /// <summary>
    /// Euler XYZ from a 3×3 rotation matrix (row-major elements).
    ///
    /// ⚠ TRANSPOSED RELATIVE TO BULLET. Bullet's matrixToEulerXYZ (btGeneric6DofConstraint.cpp)
    /// reads asin(r02) and atan2(-r12, r22); this reads asin(r20) and atan2(-r21, r22).
    /// Every angular DOF here is therefore the NEGATION of Bullet's, and the code
    /// compensates by driving angular rows with +k where Bullet uses −k, and by
    /// treating a low violation as Bullet's high one. The two flips cancel, so
    /// nothing is wrong — but they must be flipped TOGETHER.
    ///
    /// Porting Bullet's sign without also swapping the violation enum makes every
    /// limit drive its joint further out of range: measured mean velocity-reversal
    /// 0.59 with jerk 1912 (an explosion), improving to 0.40 and then 0.137 as each
    /// half was corrected. If you ever port more Bullet joint code, either flip both
    /// or change this function to Bullet's convention and flip every consumer.
    /// </summary>
    private static void MatrixToEulerXYZ(
        float r00, float r01,
        float r10, float r11,
        float r20, float r21, float r22,
        float[] output)
    {
        if (r20 < 1)
        {
            if (r20 > -1)
            {
                output[0] = MathF.Atan2(-r21, r22);
                output[1] = MathF.Asin(r20);
                output[2] = MathF.Atan2(-r10, r00);
            }
            else
            {
                output[0] = -MathF.Atan2(r01, r11);
                output[1] = -MathF.PI * 0.5f;
                output[2] = 0;
            }
        }
        else
        {
            output[0] = MathF.Atan2(r01, r11);
            output[1] = MathF.PI * 0.5f;
            output[2] = 0;
        }
    }

    /// <summary>
    /// Seed a row from the persistent manifold and apply that impulse up front —
    /// Bullet's setup does this inline (m_appliedImpulse = cp.m_appliedImpulse *
    /// m_warmstartingFactor, then applyImpulse). Contact points are matched in body
    /// A's local frame so the identity survives the bodies moving.
    /// </summary>
    private static void WarmStartContact(
        Contact c,
        RigidBodyStore store,
        ManifoldCache manifolds,
        float[] lv,
        float[] av,
        float[] invMass)
    {
        float imA = invMass[c.BodyA];
        float imB = invMass[c.BodyB];
        if (imA == 0 && imB == 0) return;
        WorldToLocal(store, c.BodyA, c.RAx, c.RAy, c.RAz, MfA);
        ManifoldPoint? prev = manifolds.Find(c.BodyA, c.BodyB, MfA[0], MfA[1], MfA[2]);
        if (prev is null)
        {
            c.Age = 0;
            return;
        }
        c.Age = prev.Age + 1;
        float n = prev.NormalImpulse * WarmstartingFactor;
        float f1 = prev.FrictionImpulse1 * WarmstartingFactor;
        float f2 = prev.FrictionImpulse2 * WarmstartingFactor;
        c.AppliedNormalImpulse = n;
        c.AppliedFrictionImpulse1 = f1;
        c.AppliedFrictionImpulse2 = f2;
        ApplyContactImpulse(c, lv, av, imA, imB, c.Nx, c.Ny, c.Nz, c.CAxN, c.CAyN, c.CAzN, c.CBxN, c.CByN, c.CBzN, n);
        if (c.JacInvT1 > 0)
            ApplyContactImpulse(c, lv, av, imA, imB, c.T1x, c.T1y, c.T1z, c.CAxT1, c.CAyT1, c.CAzT1, c.CBxT1, c.CByT1, c.CBzT1, f1);
        if (c.JacInvT2 > 0)
            ApplyContactImpulse(c, lv, av, imA, imB, c.T2x, c.T2y, c.T2z, c.CAxT2, c.CAyT2, c.CAzT2, c.CBxT2, c.CByT2, c.CBzT2, f2);
    }

    private static void ApplyContactImpulse(
        Contact c,
        float[] lv, float[] av,
        float imA, float imB,
        float dx, float dy, float dz,
        float cAx, float cAy, float cAz,
        float cBx, float cBy, float cBz,
        float j)
    {
        if (j == 0) return;
        int ai = c.BodyA * 3, bi = c.BodyB * 3;
        if (imA > 0)
        {
            lv[ai + 0] -= j * imA * dx; lv[ai + 1] -= j * imA * dy; lv[ai + 2] -= j * imA * dz;
            av[ai + 0] -= j * cAx; av[ai + 1] -= j * cAy; av[ai + 2] -= j * cAz;
        }
        if (imB > 0)
        {
            lv[bi + 0] += j * imB * dx; lv[bi + 1] += j * imB * dy; lv[bi + 2] += j * imB * dz;
            av[bi + 0] += j * cBx; av[bi + 1] += j * cBy; av[bi + 2] += j * cBz;
        }
    }

    /// <summary>Rotate a world-space lever arm into the body's local frame (R^T · v).</summary>
    private static void WorldToLocal(RigidBodyStore store, int i, float x, float y, float z, float[] output)
    {
        int i4 = i * 4;
        float qx = store.Orientations[i4 + 0], qy = store.Orientations[i4 + 1];
        float qz = store.Orientations[i4 + 2], qw = store.Orientations[i4 + 3];
        // v' = conj(q) * v * q
        float tx = 2 * (qy * z - qz * y);
        float ty = 2 * (qz * x - qx * z);
        float tz = 2 * (qx * y - qy * x);
        output[0] = x - qw * tx + (qy * tz - qz * ty);
        output[1] = y - qw * ty + (qz * tx - qx * tz);
        output[2] = z - qw * tz + (qx * ty - qy * tx);
    }
}

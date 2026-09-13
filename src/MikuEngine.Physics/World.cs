using System.Numerics;
using MikuEngine.Core.Math;

namespace MikuEngine.Physics;

/// <summary>
/// 全局空气流动。作为加速度与重力并列施加——与 MMD 自家风插件形状相同，且保持
/// 免费：重力和风每子步求和一次，per-body predict 循环无需知道风的存在。
/// 终端速度来自既有的 per-body 阻尼而非额外阻力模型——PMX 作者已经把阻尼调到
/// 他们想要的垂坠感，再加一个隐藏阻力项只会打架。
/// （对照 reze physics/world.ts WindOptions）
/// </summary>
public sealed class WindOptions
{
    /// <summary>空气行进方向。赋值时归一化；零向量 = 关风。</summary>
    public required Vector3 Direction { get; init; }

    /// <summary>沿该方向的加速度，单位与重力一致——内置重力是 98。</summary>
    public float Strength { get; init; }

    /// <summary>
    /// Gust depth, clamped to 0–1. 0 is a steady breeze; 1 swings between still
    /// and double. It is a clamp rather than a free scalar because past 1 the
    /// swing goes negative and the wind blows backwards on every other beat,
    /// which is never what a caller meant by "more turbulent".
    /// </summary>
    public float Turbulence { get; init; }

    /// <summary>Gusts per second。</summary>
    public float Frequency { get; init; } = 0.35f;
}

/// <summary>
/// World step: predict velocities → collide → solve → position correction →
/// integrate. Static and kinematic bodies are skipped during predict and
/// integrate; the parent class syncs them from bones around the step. The
/// solver pass runs on all bodies — kinematic ones have invMass = 0 and
/// act as anchors.
/// （对照 reze physics/world.ts World 逐行移植。注意：reze 本体没有 sleeping——
/// 所有动态体每步全量积分，本移植保持一致。约束求解段（B4）已接入，manifolds
/// 由 World 自持（reze 同）。）
/// </summary>
public sealed class World
{
    public Vector3 Gravity { get; private set; }

    public int SolverIterations = 10;

    // Per-body damping factors pow(1−damping, dt), cached because damping and
    // the fixed dt never change — two Math.pow per body per substep otherwise.
    private float _dampCacheDt = -1;

    /// <summary>
    /// Drop the cached damping factors. The cache is keyed on dt alone, because
    /// authored damping never changed — until a rig asked for softer jiggle (see
    /// RezePhysics.setJiggleDamping), which rewrites the store's values.
    /// </summary>
    public void InvalidateDampingCache()
    {
        _dampCacheDt = -1;
    }

    private float[]? _linDampFactor;
    private float[]? _angDampFactor;

    // Wind, resolved to a direction × strength at construction of the options so
    // the step loop only multiplies by a gust scalar.
    private float _windX;
    private float _windY;
    private float _windZ;
    private float _windTurbulence;
    private float _windFrequency = 0.35f;

    /// <summary>
    /// Advances with simulated time, not wall time, so a scrubbed or exported
    /// take gusts identically to a live one.
    /// </summary>
    private float _windClock;

    /// <summary>Per-pair contact impulse history behind warm starting。</summary>
    private readonly ManifoldCache _manifolds = new();

    // 窄签名（null contacts/constraints）调用时复用的空实例——零分配纪律：
    // 每步新建空池/空数组会在 60fps 热路径上留垃圾。
    private static readonly ContactPool s_emptyContacts = new();
    private static readonly SixDofSpringConstraint[] s_emptyConstraints = Array.Empty<SixDofSpringConstraint>();

    public World(Vector3 gravity)
    {
        Gravity = gravity;
    }

    public void SetGravity(Vector3 g)
    {
        Gravity = g;
    }

    public void SetWind(WindOptions? wind)
    {
        // Shape first, and unconditionally: bailing out early on a zero strength
        // used to leave turbulence and frequency holding whatever a previous call
        // set, so raising the strength again resurrected settings the caller had
        // since replaced.
        _windTurbulence = Math.Clamp(wind?.Turbulence ?? 0, 0f, 1f);
        _windFrequency = MathF.Max(0, wind?.Frequency ?? 0.35f);

        Vector3? d = wind?.Direction;
        float len = d.HasValue
            ? MathF.Sqrt(d.Value.X * d.Value.X + d.Value.Y * d.Value.Y + d.Value.Z * d.Value.Z)
            : 0f;
        if (wind is null || !d.HasValue || len < 1e-9f || wind.Strength == 0)
        {
            _windX = _windY = _windZ = 0;
            return;
        }
        float s = wind.Strength / len;
        _windX = d.Value.X * s;
        _windY = d.Value.Y * s;
        _windZ = d.Value.Z * s;
    }

    public WindOptions? GetWind()
    {
        float strength = MathF.Sqrt(_windX * _windX + _windY * _windY + _windZ * _windZ);
        if (strength == 0) return null;
        return new WindOptions
        {
            Direction = new Vector3(_windX / strength, _windY / strength, _windZ / strength),
            Strength = strength,
            Turbulence = _windTurbulence,
            Frequency = _windFrequency,
        };
    }

    /// <summary>
    /// B4 范围：step 3（Solve joint + contact constraints）已接入。reze step 的
    /// 顺序（predict → collide → solve → integrate）是确定性契约的一部分，不得重排。
    /// <paramref name="constraints"/> 非空时必须同时传 <paramref name="cache"/>
    /// （cache 按 reze 约定由上层按 constraints 构建一次、跨步复用）；B5 起
    /// contacts/constraints/cache 将按 reze step 完整签名由 MMDPhysics 传入。
    /// </summary>
    public void Step(
        RigidBodyStore store,
        float dt,
        ContactPool? contacts = null,
        SixDofSpringConstraint[]? constraints = null,
        SolverCache? cache = null)
    {
        if (dt <= 0) return;

        int n = store.Count;
        byte[] types = store.Type;
        float[] lv = store.LinearVelocities;
        float[] av = store.AngularVelocities;
        float[] pos = store.Positions;
        float[] ori = store.Orientations;
        float[] ldamp = store.LinearDamping;
        float[] adamp = store.AngularDamping;
        float[] invMass = store.InvMass;

        // Gravity and wind are the same kind of term, so they are summed here and
        // the predict loop below never learns wind exists.
        float gx = Gravity.X;
        float gy = Gravity.Y;
        float gz = Gravity.Z;
        if (_windX != 0 || _windY != 0 || _windZ != 0)
        {
            _windClock += dt;
            float gust = 1;
            if (_windTurbulence > 0 && _windFrequency > 0)
            {
                // Two incommensurate sines: a single one is a metronome, and real gusts
                // do not repeat on a bar line. Stays within 1 ± turbulence.
                float t = _windClock * _windFrequency * MathF.PI * 2;
                gust = 1 + _windTurbulence * 0.5f * (MathF.Sin(t) + MathF.Sin(t * 0.37f + 1.3f));
            }
            gx += _windX * gust;
            gy += _windY * gust;
            gz += _windZ * gust;
        }

        // 1. Predict — gravity + damping. The pow form (vs the linear
        //    1−damping·dt approximation) stays stable at high PMX damping
        //    values like 0.99. Factors are cached (damping and dt are constant).
        if (_dampCacheDt != dt || _linDampFactor is null || _linDampFactor.Length != n)
        {
            _dampCacheDt = dt;
            _linDampFactor = new float[n];
            _angDampFactor = new float[n];
            for (int i = 0; i < n; i++)
            {
                _linDampFactor[i] = MathF.Pow(MathF.Max(0, 1 - ldamp[i]), dt);
                _angDampFactor[i] = MathF.Pow(MathF.Max(0, 1 - adamp[i]), dt);
            }
        }
        float[] linDamp = _linDampFactor;
        float[] angDamp = _angDampFactor!;
        for (int i = 0; i < n; i++)
        {
            if (types[i] != (byte)RigidbodyType.Dynamic || invMass[i] <= 0) continue;
            int i3 = i * 3;
            lv[i3 + 0] += gx * dt;
            lv[i3 + 1] += gy * dt;
            lv[i3 + 2] += gz * dt;
            float ld = linDamp[i];
            float ad = angDamp[i];
            lv[i3 + 0] *= ld; lv[i3 + 1] *= ld; lv[i3 + 2] *= ld;
            av[i3 + 0] *= ad; av[i3 + 1] *= ad; av[i3 + 2] *= ad;
        }

        // 2. Collide.
        if (contacts != null)
        {
            contacts.Reset();
            ContactDetection.FindContacts(store, contacts);
        }

        // 3. Solve joint + contact constraints (velocity-only).
        bool hasConstraints = constraints is { Length: > 0 };
        if (hasConstraints && cache == null)
        {
            throw new ArgumentException(
                "constraints requires a matching SolverCache (build once per constraint list, reuse across steps).",
                nameof(cache));
        }
        if (hasConstraints || (contacts != null && contacts.Count > 0))
        {
            // B4 脚手架：reze 的 step 由 MMDPhysics 传入永远非空的 contacts/
            // constraints/cache（可为空数组）；窄签名调用（测试）允许传 null。
            // 归一到共享空实例后求解器端保持统一输入；cache 仅在 constraints
            // 非空时被解引用（reze 同构），故此处为 null 安全。
            SixDofSpringConstraint[] cons = constraints ?? s_emptyConstraints;
            ContactPool pool = contacts ?? s_emptyContacts;
            ConstraintSolver.SolveConstraints(store, cons, cache, pool, dt, SolverIterations, _manifolds);
            ConstraintSolver.ApplySplitImpulsePush(store, dt);
            ConstraintSolver.SaveContactImpulses(store, pool, _manifolds);
        }
        else
        {
            _manifolds.Clear();
        }

        // 4. Penetration recovery happens in the contact velocity row (Baumgarte,
        //    CONTACT_ERP), not here. Bullet 2.75 — the build MMD's physics runs —
        //    defaults m_splitImpulse to false and folds positionalError straight
        //    into m_rhs, so a separate position-translation pass is a divergence
        //    from what PMX rigs are authored against. It also could not be made to
        //    behave: every contact translated the body by its own full share, so a
        //    panel carrying dozens of rows was shoved out dozens of times.
        // 5. Integrate. Cap angular velocity at π/2 per step — a high-impulse
        //    contact spike on a low-inertia body would otherwise spin past π
        //    in one step and trash the quaternion integration. Linear velocity
        //    is capped at 5 units per step for the same reason: an explosion
        //    guard far above what legit cloth motion ever reaches.
        const float MaxAngvelDt = MathF.PI * 0.5f;
        const float MaxLinvelDt = 5;
        for (int i = 0; i < n; i++)
        {
            if (types[i] != (byte)RigidbodyType.Dynamic || invMass[i] <= 0) continue;
            int i3 = i * 3;
            int i4 = i * 4;

            float vx = lv[i3 + 0], vy = lv[i3 + 1], vz = lv[i3 + 2];
            float vmag = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            if (vmag * dt > MaxLinvelDt)
            {
                float scale = MaxLinvelDt / (vmag * dt);
                lv[i3 + 0] = vx * scale;
                lv[i3 + 1] = vy * scale;
                lv[i3 + 2] = vz * scale;
            }

            pos[i3 + 0] += lv[i3 + 0] * dt;
            pos[i3 + 1] += lv[i3 + 1] * dt;
            pos[i3 + 2] += lv[i3 + 2] * dt;

            float wx = av[i3 + 0];
            float wy = av[i3 + 1];
            float wz = av[i3 + 2];
            float wmag = MathF.Sqrt(wx * wx + wy * wy + wz * wz);
            if (wmag * dt > MaxAngvelDt)
            {
                float scale = MaxAngvelDt / (wmag * dt);
                wx *= scale; wy *= scale; wz *= scale;
                av[i3 + 0] = wx; av[i3 + 1] = wy; av[i3 + 2] = wz;
            }
            if (wx != 0 || wy != 0 || wz != 0)
            {
                float qx = ori[i4 + 0];
                float qy = ori[i4 + 1];
                float qz = ori[i4 + 2];
                float qw = ori[i4 + 3];

                float dx = qw * wx + wy * qz - wz * qy;
                float dy = qw * wy + wz * qx - wx * qz;
                float dz = qw * wz + wx * qy - wy * qx;
                float dw = -(wx * qx + wy * qy + wz * qz);

                float half = 0.5f * dt;
                float nx = qx + dx * half;
                float ny = qy + dy * half;
                float nz = qz + dz * half;
                float nw = qw + dw * half;

                float len2 = nx * nx + ny * ny + nz * nz + nw * nw;
                if (len2 > 0)
                {
                    float inv = 1 / MathF.Sqrt(len2);
                    ori[i4 + 0] = nx * inv;
                    ori[i4 + 1] = ny * inv;
                    ori[i4 + 2] = nz * inv;
                    ori[i4 + 3] = nw * inv;
                }
            }
        }
    }
}

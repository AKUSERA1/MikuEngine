namespace MikuEngine.Physics;

/// <summary>
/// Narrowphase contact generation for sphere/box/capsule pairs.
/// （对照 reze physics/contact.ts L144-1488 逐行移植；reze 原行为注释保留）
///
/// Contact convention: normal points from body A toward body B, so a positive
/// normal impulse pushes B away from A. rA / rB are world-space lever arms from
/// each CG to the contact point. Depth is positive when shapes overlap, ≤ 0 for
/// speculative contacts inside the margin band. Box-box is SAT + face clipping
/// (see DetectBoxBox) — MMD dress rigs are built from box panels, and it is the
/// majority of collidable pairs on those models.
///
/// 零分配纪律：所有 scratch 缓冲为模块级 static（单线程假设与 reze 的
/// module-level Float32Array 一致）；接触点经 <see cref="ContactPool"/> 复用。
/// </summary>
public static class ContactDetection
{
    /// <summary>
    /// Speculative contact range. Depth is reported relative to the un-inflated
    /// surface, so values 0 ≥ depth ≥ −CONTACT_MARGIN cover the "near touch but
    /// not overlapping yet" case. They exist so a fast body cannot cross a thin
    /// surface in one substep without ever generating a contact.
    ///
    /// What keeps them inert until the body would actually arrive is the solver's
    /// allowedApproachVel (gap / dt), NOT the push-only impulse clamp — the clamp
    /// only forbids a negative (pulling) impulse and does nothing to stop a large
    /// positive one from stopping a body dead in mid-air. This comment used to
    /// claim otherwise, and the bug it hid was worth 88% of speculative rows firing
    /// on a dress rig. See setupContactRow (B4).
    /// </summary>
    public const float ContactMargin = 0.04f;

    // A face axis has to lose by a real margin before an edge axis wins. Near
    // ties are common between two flat panels lying against each other, and an
    // edge axis there yields one point where a face yields four — the manifold
    // would flicker between them frame to frame and the panel would rock.
    private const float EdgeAxisBias = 1.05f;

    // --- Module-level scratch（单线程假设与 reze 一致；互不踩踏已按 reze 布局分开） ---
    private static readonly float[] CapPoint = new float[3];
    private static readonly float[] CapPointB = new float[3];
    private static readonly float[] CpA = new float[3];
    private static readonly float[] CpB = new float[3];
    private static readonly float[] LocalPt = new float[3];

    // 3×3 row-major rotation matrix for a body (xx = 2·qx·qx etc.).
    private static readonly float[] Rot = new float[9];

    // Box-box scratch, module-level so a frame of narrowphase allocates nothing.
    private static readonly float[] BbBax = new float[9];  // B's axes in A's frame, row j = axis j
    private static readonly float[] BbC = new float[3];    // B's centre in A's frame
    private static readonly float[] BbAxis = new float[3]; // best separating axis, A's frame
    private static readonly float[] BbClip = new float[24];  // clip buffer: up to 8 points
    private static readonly float[] BbClip2 = new float[24];
    private static readonly float[] BbDepth = new float[8];
    private static readonly float[] BbTmp = new float[3];
    // A's axes in A's own frame are the identity; kept as a constant so the
    // reference/incident selection can treat both boxes through one code path
    // without allocating a basis per call.
    private static readonly float[] BbIdent = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    private static readonly float[] BbRefH = new float[3];
    private static readonly float[] BbIncH = new float[3];
    private static readonly float[] BbPA = new float[3];
    private static readonly float[] BbHA = new float[3];
    private static readonly float[] BbHB = new float[3];

    // --- Material combination ---------------------------------------------------

    // Geometric mean for friction, arithmetic for restitution.
    private static void CombineMaterials(RigidBodyStore store, int a, int b, Contact c)
    {
        c.Friction = MathF.Sqrt(store.Friction[a] * store.Friction[b]);
        c.Restitution = (store.Restitution[a] + store.Restitution[b]) * 0.5f;
    }

    // --- AABB overlap (broadphase reuses this) ----------------------------------
    public static bool AabbOverlap(RigidBodyStore store, int a, int b)
    {
        int a3 = a * 3, b3 = b * 3;
        float[] minA = store.AabbMin, maxA = store.AabbMax;
        return
            minA[a3 + 0] <= maxA[b3 + 0] &&
            maxA[a3 + 0] >= minA[b3 + 0] &&
            minA[a3 + 1] <= maxA[b3 + 1] &&
            maxA[a3 + 1] >= minA[b3 + 1] &&
            minA[a3 + 2] <= maxA[b3 + 2] &&
            maxA[a3 + 2] >= minA[b3 + 2];
    }

    // --- Shared helpers ---------------------------------------------------------

    // Returns closest point on capsule's line segment (centered at cBody, axis=R·ŷ,
    // half-height halfH) to the sphere center sx,sy,sz. Out is (cx,cy,cz).
    private static void ClosestPointOnCapsuleSegment(
        float cx, float cy, float cz,
        float ax, float ay, float az,
        float halfH,
        float sx, float sy, float sz,
        float[] output)
    {
        float dx = sx - cx, dy = sy - cy, dz = sz - cz;
        float t = dx * ax + dy * ay + dz * az;
        if (t > halfH) t = halfH;
        else if (t < -halfH) t = -halfH;
        output[0] = cx + ax * t;
        output[1] = cy + ay * t;
        output[2] = cz + az * t;
    }

    // Capsule axis = R · (0,1,0).
    private static void CapsuleAxis(RigidBodyStore store, int i, float[] output)
    {
        int i4 = i * 4;
        float[] ori = store.Orientations;
        float qx = ori[i4 + 0], qy = ori[i4 + 1], qz = ori[i4 + 2], qw = ori[i4 + 3];
        output[0] = 2 * (qx * qy - qw * qz);
        output[1] = 1 - 2 * (qx * qx + qz * qz);
        output[2] = 2 * (qy * qz + qw * qx);
    }

    // 3×3 row-major rotation matrix for body i.
    private static void LoadBodyRot(RigidBodyStore store, int i)
    {
        int i4 = i * 4;
        float[] ori = store.Orientations;
        float qx = ori[i4 + 0], qy = ori[i4 + 1], qz = ori[i4 + 2], qw = ori[i4 + 3];
        float x2 = qx + qx, y2 = qy + qy, z2 = qz + qz;
        float xx = qx * x2, yy = qy * y2, zz = qz * z2;
        float xy = qx * y2, xz = qx * z2, yz = qy * z2;
        float wx = qw * x2, wy = qw * y2, wz = qw * z2;
        Rot[0] = 1 - (yy + zz);
        Rot[1] = xy - wz;
        Rot[2] = xz + wy;
        Rot[3] = xy + wz;
        Rot[4] = 1 - (xx + zz);
        Rot[5] = yz - wx;
        Rot[6] = xz - wy;
        Rot[7] = yz + wx;
        Rot[8] = 1 - (xx + yy);
    }

    // Transform world point into body i's local frame: v_local = R^T · (p − bodyPos).
    private static void WorldToBodyLocal(RigidBodyStore store, int i, float px, float py, float pz, float[] output)
    {
        int i3 = i * 3;
        float dx = px - store.Positions[i3 + 0];
        float dy = py - store.Positions[i3 + 1];
        float dz = pz - store.Positions[i3 + 2];
        LoadBodyRot(store, i);
        // R^T · v = (col k of R) · v.
        output[0] = Rot[0] * dx + Rot[3] * dy + Rot[6] * dz;
        output[1] = Rot[1] * dx + Rot[4] * dy + Rot[7] * dz;
        output[2] = Rot[2] * dx + Rot[5] * dy + Rot[8] * dz;
    }

    // Rotate a body-local direction into world space: v_world = R · v_local.
    private static void BodyLocalToWorldDir(RigidBodyStore store, int i, float lx, float ly, float lz, float[] output)
    {
        LoadBodyRot(store, i);
        output[0] = Rot[0] * lx + Rot[1] * ly + Rot[2] * lz;
        output[1] = Rot[3] * lx + Rot[4] * ly + Rot[5] * lz;
        output[2] = Rot[6] * lx + Rot[7] * ly + Rot[8] * lz;
    }

    private static float Clamp01(float x)
    {
        return x < 0 ? 0 : x > 1 ? 1 : x;
    }

    // Closest pair on two segments. Adapted from Real-Time Collision Detection §5.1.9.
    private static void ClosestPointsTwoSegments(
        float p1x, float p1y, float p1z, float q1x, float q1y, float q1z,
        float p2x, float p2y, float p2z, float q2x, float q2y, float q2z,
        float[] outA, float[] outB)
    {
        float d1x = q1x - p1x, d1y = q1y - p1y, d1z = q1z - p1z;
        float d2x = q2x - p2x, d2y = q2y - p2y, d2z = q2z - p2z;
        float rx = p1x - p2x, ry = p1y - p2y, rz = p1z - p2z;
        float a = d1x * d1x + d1y * d1y + d1z * d1z;
        float e = d2x * d2x + d2y * d2y + d2z * d2z;
        float f = d2x * rx + d2y * ry + d2z * rz;
        float s = 0, t = 0;
        const float Eps = 1e-8f;
        if (a <= Eps && e <= Eps)
        {
            outA[0] = p1x; outA[1] = p1y; outA[2] = p1z;
            outB[0] = p2x; outB[1] = p2y; outB[2] = p2z;
            return;
        }
        if (a <= Eps)
        {
            s = 0;
            t = Clamp01(f / e);
        }
        else
        {
            float c = d1x * rx + d1y * ry + d1z * rz;
            if (e <= Eps)
            {
                t = 0;
                s = Clamp01(-c / a);
            }
            else
            {
                float b = d1x * d2x + d1y * d2y + d1z * d2z;
                float denom = a * e - b * b;
                if (denom != 0) s = Clamp01((b * f - c * e) / denom);
                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Clamp01(-c / a);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Clamp01((b - c) / a);
                }
            }
        }
        outA[0] = p1x + d1x * s;
        outA[1] = p1y + d1y * s;
        outA[2] = p1z + d1z * s;
        outB[0] = p2x + d2x * t;
        outB[1] = p2y + d2y * t;
        outB[2] = p2z + d2z * t;
    }

    // Closest point on segment p1→q1 to a free point (sx,sy,sz). Out gets the
    // projected point clamped to the segment.
    private static void ClosestPointOnSegment(
        float p1x, float p1y, float p1z, float q1x, float q1y, float q1z,
        float sx, float sy, float sz,
        float[] output)
    {
        float dx = q1x - p1x, dy = q1y - p1y, dz = q1z - p1z;
        float segLen2 = dx * dx + dy * dy + dz * dz;
        float t = 0;
        if (segLen2 > 1e-8f)
        {
            t = ((sx - p1x) * dx + (sy - p1y) * dy + (sz - p1z) * dz) / segLen2;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
        }
        output[0] = p1x + dx * t;
        output[1] = p1y + dy * t;
        output[2] = p1z + dz * t;
    }

    // --- Sphere–sphere ----------------------------------------------------------
    private static void DetectSphereSphere(RigidBodyStore store, int a, int b, ContactPool pool)
    {
        int ai = a * 3, bi = b * 3;
        float[] pos = store.Positions, sz = store.Size;
        float dx = pos[bi + 0] - pos[ai + 0];
        float dy = pos[bi + 1] - pos[ai + 1];
        float dz = pos[bi + 2] - pos[ai + 2];
        float rA = sz[ai + 0];
        float rB = sz[bi + 0];
        float sumR = rA + rB;
        float sumExt = sumR + ContactMargin;
        float d2 = dx * dx + dy * dy + dz * dz;
        if (d2 > sumExt * sumExt) return;
        float d = MathF.Sqrt(d2);
        float nx, ny, nz;
        if (d > 1e-6f)
        {
            nx = dx / d;
            ny = dy / d;
            nz = dz / d;
        }
        else
        {
            nx = 0; ny = 1; nz = 0;
        } // arbitrary axis when fully co-located
        Contact c = pool.Acquire();
        c.BodyA = a;
        c.BodyB = b;
        c.Nx = nx; c.Ny = ny; c.Nz = nz;
        c.Depth = sumR - d; // signed: > 0 overlapping, ≤ 0 within margin
        c.RAx = nx * rA;
        c.RAy = ny * rA;
        c.RAz = nz * rA;
        c.RBx = -nx * rB;
        c.RBy = -ny * rB;
        c.RBz = -nz * rB;
        CombineMaterials(store, a, b, c);
    }

    // --- Sphere–capsule (sphere = a, capsule = b) -------------------------------
    private static void DetectSphereCapsule(RigidBodyStore store, int a, int b, ContactPool pool)
    {
        float[] pos = store.Positions, sz = store.Size;
        int ai = a * 3, bi = b * 3;
        float sx = pos[ai + 0], sy = pos[ai + 1], szZ = pos[ai + 2];
        float cx = pos[bi + 0], cy = pos[bi + 1], cz = pos[bi + 2];
        float rA = sz[ai + 0];
        float rB = sz[bi + 0];
        float halfH = sz[bi + 1] * 0.5f;
        float[] axis = CapPoint;
        CapsuleAxis(store, b, axis);
        float[] closest = CapPointB;
        ClosestPointOnCapsuleSegment(cx, cy, cz, axis[0], axis[1], axis[2], halfH, sx, sy, szZ, closest);
        float dx = closest[0] - sx;
        float dy = closest[1] - sy;
        float dz = closest[2] - szZ;
        float sumR = rA + rB;
        float sumExt = sumR + ContactMargin;
        float d2 = dx * dx + dy * dy + dz * dz;
        if (d2 > sumExt * sumExt) return;
        float d = MathF.Sqrt(d2);
        float nx, ny, nz;
        if (d > 1e-6f)
        {
            nx = dx / d;
            ny = dy / d;
            nz = dz / d;
        }
        else
        {
            nx = 0; ny = 1; nz = 0;
        }
        Contact c = pool.Acquire();
        c.BodyA = a;
        c.BodyB = b;
        c.Nx = nx; c.Ny = ny; c.Nz = nz;
        c.Depth = sumR - d;
        // Contact point on A's surface: sphere center + n * rA. Lever arm rA = that
        // offset since A's CG = sphere center.
        c.RAx = nx * rA;
        c.RAy = ny * rA;
        c.RAz = nz * rA;
        // Contact point on B's surface: closest_on_segment − n * rB, lever from B's CG.
        c.RBx = closest[0] - nx * rB - cx;
        c.RBy = closest[1] - ny * rB - cy;
        c.RBz = closest[2] - nz * rB - cz;
        CombineMaterials(store, a, b, c);
    }

    // Emit one capsule-vs-capsule contact given a pair of points (pA on A's
    // segment, pB on B's segment). Skips silently if outside speculative range.
    private static void EmitCapsuleContact(
        RigidBodyStore store, int a, int b, ContactPool pool,
        float pAx, float pAy, float pAz,
        float pBx, float pBy, float pBz,
        float rA, float rB, float sumR, float sumExt,
        float cAx, float cAy, float cAz,
        float cBx, float cBy, float cBz)
    {
        float dx = pBx - pAx, dy = pBy - pAy, dz = pBz - pAz;
        float d2 = dx * dx + dy * dy + dz * dz;
        if (d2 > sumExt * sumExt) return;
        float d = MathF.Sqrt(d2);
        float nx, ny, nz;
        if (d > 1e-6f)
        {
            nx = dx / d;
            ny = dy / d;
            nz = dz / d;
        }
        else
        {
            nx = 0; ny = 1; nz = 0;
        }
        Contact c = pool.Acquire();
        c.BodyA = a;
        c.BodyB = b;
        c.Nx = nx; c.Ny = ny; c.Nz = nz;
        c.Depth = sumR - d;
        c.RAx = pAx + nx * rA - cAx;
        c.RAy = pAy + ny * rA - cAy;
        c.RAz = pAz + nz * rA - cAz;
        c.RBx = pBx - nx * rB - cBx;
        c.RBy = pBy - ny * rB - cBy;
        c.RBz = pBz - nz * rB - cBz;
        CombineMaterials(store, a, b, c);
    }

    // --- Capsule–capsule --------------------------------------------------------
    private static void DetectCapsuleCapsule(RigidBodyStore store, int a, int b, ContactPool pool)
    {
        float[] pos = store.Positions, sz = store.Size;
        int ai = a * 3, bi = b * 3;
        float cAx = pos[ai + 0], cAy = pos[ai + 1], cAz = pos[ai + 2];
        float cBx = pos[bi + 0], cBy = pos[bi + 1], cBz = pos[bi + 2];
        float rA = sz[ai + 0], hA = sz[ai + 1] * 0.5f;
        float rB = sz[bi + 0], hB = sz[bi + 1] * 0.5f;
        float[] aAx = CapPoint;
        float[] aBx = CapPointB;
        CapsuleAxis(store, a, aAx);
        CapsuleAxis(store, b, aBx);
        float p1x = cAx - aAx[0] * hA, p1y = cAy - aAx[1] * hA, p1z = cAz - aAx[2] * hA;
        float q1x = cAx + aAx[0] * hA, q1y = cAy + aAx[1] * hA, q1z = cAz + aAx[2] * hA;
        float p2x = cBx - aBx[0] * hB, p2y = cBy - aBx[1] * hB, p2z = cBz - aBx[2] * hB;
        float q2x = cBx + aBx[0] * hB, q2y = cBy + aBx[1] * hB, q2z = cBz + aBx[2] * hB;

        float sumR = rA + rB;
        float sumExt = sumR + ContactMargin;

        // Primary contact: closest-pair on the two segments.
        ClosestPointsTwoSegments(p1x, p1y, p1z, q1x, q1y, q1z, p2x, p2y, p2z, q2x, q2y, q2z, CpA, CpB);
        EmitCapsuleContact(store, a, b, pool,
            CpA[0], CpA[1], CpA[2], CpB[0], CpB[1], CpB[2],
            rA, rB, sumR, sumExt, cAx, cAy, cAz, cBx, cBy, cBz);

        // For nearly-parallel axes the closest-pair algorithm is degenerate
        // (denom = a·e − b² ≈ 0) and returns one arbitrary point. Sampling A's
        // endpoints adds two contacts that pin both rotation and length-wise push.
        float cosA = MathF.Abs(aAx[0] * aBx[0] + aAx[1] * aBx[1] + aAx[2] * aBx[2]);
        if (cosA > 0.9f)
        {
            ClosestPointOnSegment(p2x, p2y, p2z, q2x, q2y, q2z, p1x, p1y, p1z, CpB);
            EmitCapsuleContact(store, a, b, pool,
                p1x, p1y, p1z, CpB[0], CpB[1], CpB[2],
                rA, rB, sumR, sumExt, cAx, cAy, cAz, cBx, cBy, cBz);
            ClosestPointOnSegment(p2x, p2y, p2z, q2x, q2y, q2z, q1x, q1y, q1z, CpB);
            EmitCapsuleContact(store, a, b, pool,
                q1x, q1y, q1z, CpB[0], CpB[1], CpB[2],
                rA, rB, sumR, sumExt, cAx, cAy, cAz, cBx, cBy, cBz);
        }
    }

    // --- Sphere–box (sphere = a, box = b) ---------------------------------------
    private static void DetectSphereBox(RigidBodyStore store, int a, int b, ContactPool pool)
    {
        int ai = a * 3, bi = b * 3;
        float[] pos = store.Positions, sz = store.Size;
        float sx = pos[ai + 0], sy = pos[ai + 1], szZ = pos[ai + 2];
        float rA = sz[ai + 0];
        float hx = sz[bi + 0], hy = sz[bi + 1], hz = sz[bi + 2];

        // Sphere center in box-local frame.
        WorldToBodyLocal(store, b, sx, sy, szZ, LocalPt);
        float lx = LocalPt[0], ly = LocalPt[1], lz = LocalPt[2];

        // Closest point on box (clamp to half-extents).
        float qx = lx, qy = ly, qz = lz;
        if (qx > hx) qx = hx;
        else if (qx < -hx) qx = -hx;
        if (qy > hy) qy = hy;
        else if (qy < -hy) qy = -hy;
        if (qz > hz) qz = hz;
        else if (qz < -hz) qz = -hz;

        float dx = lx - qx, dy = ly - qy, dz = lz - qz;
        float d2 = dx * dx + dy * dy + dz * dz;

        float nLocalX, nLocalY, nLocalZ;
        float depth;

        float rExt = rA + ContactMargin;
        if (d2 > rExt * rExt) return; // outside speculative range

        if (d2 > 1e-12f)
        {
            float d = MathF.Sqrt(d2);
            nLocalX = dx / d;
            nLocalY = dy / d;
            nLocalZ = dz / d;
            depth = rA - d; // signed: > 0 overlapping, ≤ 0 within margin
        }
        else
        {
            // Sphere center inside box — pick shortest axis to escape.
            float px = hx - MathF.Abs(lx), py = hy - MathF.Abs(ly), pz = hz - MathF.Abs(lz);
            if (px < py && px < pz)
            {
                nLocalX = lx > 0 ? 1 : -1;
                nLocalY = 0;
                nLocalZ = 0;
                depth = rA + px;
                qx = lx > 0 ? hx : -hx;
                qy = ly;
                qz = lz;
            }
            else if (py < pz)
            {
                nLocalX = 0;
                nLocalY = ly > 0 ? 1 : -1;
                nLocalZ = 0;
                depth = rA + py;
                qx = lx;
                qy = ly > 0 ? hy : -hy;
                qz = lz;
            }
            else
            {
                nLocalX = 0;
                nLocalY = 0;
                nLocalZ = lz > 0 ? 1 : -1;
                depth = rA + pz;
                qx = lx;
                qy = ly;
                qz = lz > 0 ? hz : -hz;
            }
        }

        // Rotate local normal back to world. Convention: normal points A→B, but we
        // computed n = (lx − qx) which goes "from box surface toward sphere center"
        // (= B → A). Flip sign.
        float[] output = CapPoint;
        BodyLocalToWorldDir(store, b, -nLocalX, -nLocalY, -nLocalZ, output);
        float nx = output[0], ny = output[1], nz = output[2];

        // Box's contact point in world: rotate (qx,qy,qz) and translate by box pos.
        float[] bp = CapPointB;
        BodyLocalToWorldDir(store, b, qx, qy, qz, bp);
        float bpx = bp[0] + pos[bi + 0];
        float bpy = bp[1] + pos[bi + 1];
        float bpz = bp[2] + pos[bi + 2];

        Contact c = pool.Acquire();
        c.BodyA = a;
        c.BodyB = b;
        c.Nx = nx; c.Ny = ny; c.Nz = nz;
        c.Depth = depth;
        c.RAx = nx * rA;
        c.RAy = ny * rA;
        c.RAz = nz * rA;
        c.RBx = bpx - pos[bi + 0];
        c.RBy = bpy - pos[bi + 1];
        c.RBz = bpz - pos[bi + 2];
        CombineMaterials(store, a, b, c);
    }

    // --- Capsule–box ------------------------------------------------------------
    // Walk the capsule's segment (in box-local space) toward the box, sample
    // sphere-box at the converged parameter plus both endpoints, and keep the
    // DEEPEST sample. Endpoint samples catch caps grazing a face when the
    // closest-point parameter sits at one end of the segment.
    //
    // Deliberately one contact, not a manifold along the touching segment. A line
    // of contacts here does stop a panel pivoting into a leg, and it was tried:
    // penetration fell, but idle jitter on a 688-body rig rose because every extra
    // contact is another shove from the position-correction pass, which the joint
    // springs hand straight back. Cloth that buzzes reads worse than cloth that
    // clips, so this keeps a little 穿模 in exchange for stillness. Box-box is a
    // different case — those panels had NO collision at all, so it is pure gain.
    private static void DetectCapsuleBox(RigidBodyStore store, int a, int b, ContactPool pool)
    {
        float[] pos = store.Positions, sz = store.Size;
        int ai = a * 3, bi = b * 3;
        float cx = pos[ai + 0], cy = pos[ai + 1], cz = pos[ai + 2];
        float rA = sz[ai + 0];
        float hA = sz[ai + 1] * 0.5f;
        float[] ax = CapPoint;
        CapsuleAxis(store, a, ax);

        // Endpoints in world space.
        float p1wx = cx - ax[0] * hA, p1wy = cy - ax[1] * hA, p1wz = cz - ax[2] * hA;
        float p2wx = cx + ax[0] * hA, p2wy = cy + ax[1] * hA, p2wz = cz + ax[2] * hA;

        // Endpoints in box-local space.
        WorldToBodyLocal(store, b, p1wx, p1wy, p1wz, LocalPt);
        float p1lx = LocalPt[0], p1ly = LocalPt[1], p1lz = LocalPt[2];
        WorldToBodyLocal(store, b, p2wx, p2wy, p2wz, LocalPt);
        float p2lx = LocalPt[0], p2ly = LocalPt[1], p2lz = LocalPt[2];

        float hx = sz[bi + 0], hy = sz[bi + 1], hz = sz[bi + 2];

        // Closest point on segment to box (in box-local). Iterate a few times to
        // converge — clamp each component, recompute t, repeat. Two passes is
        // enough for our use case (capsule modestly larger than box).
        float t = 0.5f;
        for (int iter = 0; iter < 4; iter++)
        {
            float px = p1lx + (p2lx - p1lx) * t;
            float py = p1ly + (p2ly - p1ly) * t;
            float pz = p1lz + (p2lz - p1lz) * t;
            float qx = px, qy = py, qz = pz;
            if (qx > hx) qx = hx;
            else if (qx < -hx) qx = -hx;
            if (qy > hy) qy = hy;
            else if (qy < -hy) qy = -hy;
            if (qz > hz) qz = hz;
            else if (qz < -hz) qz = -hz;
            // Project clamped point back onto the segment to refine t.
            float dx = p2lx - p1lx, dy = p2ly - p1ly, dz = p2lz - p1lz;
            float segLen2 = dx * dx + dy * dy + dz * dz;
            if (segLen2 < 1e-8f) break;
            t = ((qx - p1lx) * dx + (qy - p1ly) * dy + (qz - p1lz) * dz) / segLen2;
            if (t < 0)
            {
                t = 0;
                break;
            }
            if (t > 1)
            {
                t = 1;
                break;
            }
        }

        // Sample at the converged t plus both endpoints — endpoints catch capsule
        // caps grazing the box surface where the closest-point loop sits at one
        // segment end.
        float bestDepth = float.NegativeInfinity;
        float bestNX = 0, bestNY = 0, bestNZ = 0;
        float bestRAX = 0, bestRAY = 0, bestRAZ = 0;
        float bestRBX = 0, bestRBY = 0, bestRBZ = 0;
        bool found = false;

        // samples = [t, 0, 1]（顺序保持 reze：同深度先到先得）
        for (int si = 0; si < 3; si++)
        {
            float s = si == 0 ? t : si == 1 ? 0f : 1f;
            float sx = p1wx + (p2wx - p1wx) * s;
            float sy = p1wy + (p2wy - p1wy) * s;
            float szZ = p1wz + (p2wz - p1wz) * s;
            WorldToBodyLocal(store, b, sx, sy, szZ, LocalPt);
            float lx = LocalPt[0], ly = LocalPt[1], lz = LocalPt[2];
            float qx = lx, qy = ly, qz = lz;
            if (qx > hx) qx = hx;
            else if (qx < -hx) qx = -hx;
            if (qy > hy) qy = hy;
            else if (qy < -hy) qy = -hy;
            if (qz > hz) qz = hz;
            else if (qz < -hz) qz = -hz;
            float dx = lx - qx, dy = ly - qy, dz = lz - qz;
            float d2 = dx * dx + dy * dy + dz * dz;
            float rExt = rA + ContactMargin;
            if (d2 > rExt * rExt) continue;
            float nLocalX = 0, nLocalY = 0, nLocalZ = 0;
            float depth;
            if (d2 > 1e-12f)
            {
                float d = MathF.Sqrt(d2);
                nLocalX = dx / d;
                nLocalY = dy / d;
                nLocalZ = dz / d;
                depth = rA - d; // signed: > 0 overlapping, ≤ 0 within margin
            }
            else
            {
                float px = hx - MathF.Abs(lx), py = hy - MathF.Abs(ly), pz = hz - MathF.Abs(lz);
                if (px < py && px < pz)
                {
                    nLocalX = lx > 0 ? 1 : -1;
                    depth = rA + px;
                    qx = lx > 0 ? hx : -hx;
                    qy = ly;
                    qz = lz;
                }
                else if (py < pz)
                {
                    nLocalY = ly > 0 ? 1 : -1;
                    depth = rA + py;
                    qx = lx;
                    qy = ly > 0 ? hy : -hy;
                    qz = lz;
                }
                else
                {
                    nLocalZ = lz > 0 ? 1 : -1;
                    depth = rA + pz;
                    qx = lx;
                    qy = ly;
                    qz = lz > 0 ? hz : -hz;
                }
            }
            if (depth <= bestDepth) continue;
            bestDepth = depth;
            found = true;
            float[] dirOut = LocalPt;
            BodyLocalToWorldDir(store, b, -nLocalX, -nLocalY, -nLocalZ, dirOut);
            bestNX = dirOut[0];
            bestNY = dirOut[1];
            bestNZ = dirOut[2];
            float[] bpOut = LocalPt;
            BodyLocalToWorldDir(store, b, qx, qy, qz, bpOut);
            float bpx = bpOut[0] + pos[bi + 0];
            float bpy = bpOut[1] + pos[bi + 1];
            float bpz = bpOut[2] + pos[bi + 2];
            bestRAX = sx + bestNX * rA - cx;
            bestRAY = sy + bestNY * rA - cy;
            bestRAZ = szZ + bestNZ * rA - cz;
            bestRBX = bpx - pos[bi + 0];
            bestRBY = bpy - pos[bi + 1];
            bestRBZ = bpz - pos[bi + 2];
        }

        if (!found) return;
        Contact c = pool.Acquire();
        c.BodyA = a;
        c.BodyB = b;
        c.Nx = bestNX; c.Ny = bestNY; c.Nz = bestNZ;
        c.Depth = bestDepth;
        c.RAx = bestRAX;
        c.RAy = bestRAY;
        c.RAz = bestRAZ;
        c.RBx = bestRBX;
        c.RBy = bestRBY;
        c.RBz = bestRBZ;
        CombineMaterials(store, a, b, c);
    }

    // --- Box–box ---------------------------------------------------------------
    // SAT over the 15 candidate axes, then face clipping for a multi-point
    // manifold. Everything below happens in A's local frame: B's centre and axes
    // are transformed in once, so the 15 tests and the clipping all read as plain
    // vector maths instead of repeated quaternion work.
    //
    // Why this exists at all: MMD dress rigs are built from flat box PANELS, and
    // panel-against-panel is the collision that keeps skirt layers out of each
    // other. Measured across seven shipped models, box-box is 45–70% of every
    // collidable pair on models whose riggers left skirt self-collision enabled —
    // all of it silently dropped before this. MMD's own physics is Bullet, which
    // dispatches box-box through btBoxBoxDetector (ODE's dBoxBox: this same
    // 15-axis SAT plus face clipping), so rigs are authored assuming it works.
    //
    // The pair count is not the frame cost: the AABB pass upstream filters first,
    // and in a rest pose only ~220 of 诗蔻蒂's 60k box-box candidates reach here.
    private static void DetectBoxBox(RigidBodyStore store, int a, int b, ContactPool pool)
    {
        int ai = a * 3, bi = b * 3;
        float[] sz = store.Size;
        float hAx = sz[ai + 0], hAy = sz[ai + 1], hAz = sz[ai + 2];
        float hBx = sz[bi + 0], hBy = sz[bi + 1], hBz = sz[bi + 2];

        // B's centre, in A's frame. The body index here is A, not B — transforming
        // B's own centre by B's own transform yields the origin every time, which
        // makes every SAT distance zero and every pair maximally overlapping.
        WorldToBodyLocal(store, a, store.Positions[bi + 0], store.Positions[bi + 1], store.Positions[bi + 2], BbC);
        float cx = BbC[0], cy = BbC[1], cz = BbC[2];

        // B's three axes, in A's frame: RAᵀ · RB. LoadBodyRot writes one body at a
        // time, so read B's columns out before loading A over the top of them.
        LoadBodyRot(store, b);
        float b00 = Rot[0], b01 = Rot[1], b02 = Rot[2];
        float b10 = Rot[3], b11 = Rot[4], b12 = Rot[5];
        float b20 = Rot[6], b21 = Rot[7], b22 = Rot[8];
        LoadBodyRot(store, a);
        float a00 = Rot[0], a01 = Rot[1], a02 = Rot[2];
        float a10 = Rot[3], a11 = Rot[4], a12 = Rot[5];
        float a20 = Rot[6], a21 = Rot[7], a22 = Rot[8];
        // Column j of RB is B's axis j in world; RAᵀ · that is it in A's frame.
        for (int j = 0; j < 3; j++)
        {
            float wx = j == 0 ? b00 : j == 1 ? b01 : b02;
            float wy = j == 0 ? b10 : j == 1 ? b11 : b12;
            float wz = j == 0 ? b20 : j == 1 ? b21 : b22;
            BbBax[j * 3 + 0] = a00 * wx + a10 * wy + a20 * wz;
            BbBax[j * 3 + 1] = a01 * wx + a11 * wy + a21 * wz;
            BbBax[j * 3 + 2] = a02 * wx + a12 * wy + a22 * wz;
        }

        // --- SAT. Track the axis of MINIMUM overlap; that is the shallowest way out
        //     and therefore the contact normal.
        float bestOverlap = float.PositiveInfinity;
        int bestAxis = -1; // 0-2 = A's faces, 3-5 = B's faces, 6-14 = edge crosses
        float bestNx = 0, bestNy = 0, bestNz = 0;

        // `scale` lets the edge axes be judged slightly harder — see EdgeAxisBias.
        // Local closure over the frame locals; a static local function would need
        // too many parameters to stay readable. Does not allocate.
        bool Test(float nx, float ny, float nz, int id, float scale)
        {
            float len2 = nx * nx + ny * ny + nz * nz;
            // Degenerate cross product: the two edges are parallel, so this axis adds
            // nothing that the face axes have not already covered.
            if (len2 < 1e-12f) return true;
            float inv = 1 / MathF.Sqrt(len2);
            float ux = nx * inv, uy = ny * inv, uz = nz * inv;
            float projA = hAx * MathF.Abs(ux) + hAy * MathF.Abs(uy) + hAz * MathF.Abs(uz);
            float projB =
                hBx * MathF.Abs(ux * BbBax[0] + uy * BbBax[1] + uz * BbBax[2]) +
                hBy * MathF.Abs(ux * BbBax[3] + uy * BbBax[4] + uz * BbBax[5]) +
                hBz * MathF.Abs(ux * BbBax[6] + uy * BbBax[7] + uz * BbBax[8]);
            float dist = MathF.Abs(ux * cx + uy * cy + uz * cz);
            float overlap = projA + projB - dist;
            // A gap wider than the speculative band: no contact, and no need to test
            // the rest — one separating axis is proof.
            if (overlap < -ContactMargin) return false;
            if (overlap * scale < bestOverlap)
            {
                bestOverlap = overlap * scale;
                bestAxis = id;
                bestNx = ux; bestNy = uy; bestNz = uz;
            }
            return true;
        }

        if (!Test(1, 0, 0, 0, 1)) return;
        if (!Test(0, 1, 0, 1, 1)) return;
        if (!Test(0, 0, 1, 2, 1)) return;
        for (int j = 0; j < 3; j++)
        {
            if (!Test(BbBax[j * 3 + 0], BbBax[j * 3 + 1], BbBax[j * 3 + 2], 3 + j, 1)) return;
        }
        for (int i = 0; i < 3; i++)
        {
            float axi = i == 0 ? 1 : 0, ayi = i == 1 ? 1 : 0, azi = i == 2 ? 1 : 0;
            for (int j = 0; j < 3; j++)
            {
                float bx = BbBax[j * 3 + 0], by = BbBax[j * 3 + 1], bz = BbBax[j * 3 + 2];
                if (!Test(ayi * bz - azi * by, azi * bx - axi * bz, axi * by - ayi * bx, 6 + i * 3 + j, EdgeAxisBias)) return;
            }
        }
        if (bestAxis < 0) return;

        // Orient the normal A → B, matching the contact convention.
        if (bestNx * cx + bestNy * cy + bestNz * cz < 0)
        {
            bestNx = -bestNx; bestNy = -bestNy; bestNz = -bestNz;
        }
        BbAxis[0] = bestNx; BbAxis[1] = bestNy; BbAxis[2] = bestNz;

        if (bestAxis >= 6)
        {
            EmitBoxEdgeContact(store, a, b, bestOverlap / EdgeAxisBias, (bestAxis - 6) / 3, (bestAxis - 6) % 3,
                hAx, hAy, hAz, hBx, hBy, hBz, cx, cy, cz, pool);
            return;
        }
        EmitBoxFaceManifold(store, a, b, bestAxis, hAx, hAy, hAz, hBx, hBy, hBz, cx, cy, cz, pool);
    }

    // Write one contact from a point given in A's LOCAL frame, with the manifold's
    // shared world normal. Both lever arms come from the same world point: the
    // clipped point sits on the incident face, within CONTACT_MARGIN of the
    // reference face, so splitting them would be false precision.
    private static void EmitBoxContact(
        RigidBodyStore store, int a, int b,
        float lx, float ly, float lz,
        float depth,
        ContactPool pool)
    {
        int ai = a * 3, bi = b * 3;
        float[] pos = store.Positions;
        BodyLocalToWorldDir(store, a, lx, ly, lz, BbTmp);
        float wx = BbTmp[0] + pos[ai + 0];
        float wy = BbTmp[1] + pos[ai + 1];
        float wz = BbTmp[2] + pos[ai + 2];
        BodyLocalToWorldDir(store, a, BbAxis[0], BbAxis[1], BbAxis[2], BbTmp);
        Contact c = pool.Acquire();
        c.BodyA = a;
        c.BodyB = b;
        c.Nx = BbTmp[0]; c.Ny = BbTmp[1]; c.Nz = BbTmp[2];
        c.Depth = depth;
        c.RAx = wx - pos[ai + 0];
        c.RAy = wy - pos[ai + 1];
        c.RAz = wz - pos[ai + 2];
        c.RBx = wx - pos[bi + 0];
        c.RBy = wy - pos[bi + 1];
        c.RBz = wz - pos[bi + 2];
        CombineMaterials(store, a, b, c);
    }

    // Clip a polygon against the plane dot(p, t) ≤ offset (Sutherland–Hodgman).
    // Points are xyz triples packed into `src`; returns the new count.
    private static int ClipPolyByPlane(
        float[] src, int n,
        float tx, float ty, float tz, float offset,
        float[] dst)
    {
        int output = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            float px = src[i * 3], py = src[i * 3 + 1], pz = src[i * 3 + 2];
            float qx = src[j * 3], qy = src[j * 3 + 1], qz = src[j * 3 + 2];
            float dp = px * tx + py * ty + pz * tz - offset;
            float dq = qx * tx + qy * ty + qz * tz - offset;
            if (dp <= 0)
            {
                dst[output * 3] = px; dst[output * 3 + 1] = py; dst[output * 3 + 2] = pz;
                output++;
            }
            // Sign change: the edge crosses the plane, so the crossing point joins the
            // polygon. Guard the divide — a denominator this small means the edge lies
            // in the plane, and both endpoints are already handled by the tests above.
            if ((dp < 0 && dq > 0) || (dp > 0 && dq < 0))
            {
                float den = dp - dq;
                if (MathF.Abs(den) > 1e-12f && output < 8)
                {
                    float s = dp / den;
                    dst[output * 3] = px + (qx - px) * s;
                    dst[output * 3 + 1] = py + (qy - py) * s;
                    dst[output * 3 + 2] = pz + (qz - pz) * s;
                    output++;
                }
            }
            if (output >= 8) break;
        }
        return output;
    }

    // Face-vs-face: clip the incident face against the reference face's four side
    // planes, then keep whatever is at or below the reference plane. This is what
    // yields a multi-point manifold — the reason a flat panel resting on another
    // stops pivoting about a single point.
    private static void EmitBoxFaceManifold(
        RigidBodyStore store, int a, int b,
        int bestAxis,
        float hAx, float hAy, float hAz,
        float hBx, float hBy, float hBz,
        float cx, float cy, float cz,
        ContactPool pool)
    {
        bool refIsA = bestAxis < 3;
        int refAxisIdx = refIsA ? bestAxis : bestAxis - 3;
        // Reference basis, half extents and centre — all in A's frame. When A is the
        // reference its axes ARE the frame, hence the identity rows.
        float[] refAx = refIsA ? BbIdent : BbBax;
        float[] incAx = refIsA ? BbBax : BbIdent;
        float[] refH = BbRefH, incH = BbIncH;
        refH[0] = refIsA ? hAx : hBx; refH[1] = refIsA ? hAy : hBy; refH[2] = refIsA ? hAz : hBz;
        incH[0] = refIsA ? hBx : hAx; incH[1] = refIsA ? hBy : hAy; incH[2] = refIsA ? hBz : hAz;
        float refCx = refIsA ? 0 : cx, refCy = refIsA ? 0 : cy, refCz = refIsA ? 0 : cz;
        float incCx = refIsA ? cx : 0, incCy = refIsA ? cy : 0, incCz = refIsA ? cz : 0;

        // Outward normal of the reference face, pointing at the incident box.
        // BbAxis runs A → B, so B-as-reference faces the other way.
        float sgn = refIsA ? 1 : -1;
        float nx = BbAxis[0] * sgn, ny = BbAxis[1] * sgn, nz = BbAxis[2] * sgn;

        // Incident face: the one whose outward normal is most opposed to n.
        int incIdx = 0;
        float incDot = float.PositiveInfinity, incSign = 1;
        for (int k = 0; k < 3; k++)
        {
            float d = incAx[k * 3] * nx + incAx[k * 3 + 1] * ny + incAx[k * 3 + 2] * nz;
            float s = d > 0 ? -1 : 1;
            float dv = d * s;
            if (dv < incDot) { incDot = dv; incIdx = k; incSign = s; }
        }

        // Its four corners, from the face centre along the two remaining axes.
        int u = (incIdx + 1) % 3, v = (incIdx + 2) % 3;
        float fx = incCx + incAx[incIdx * 3] * incSign * incH[incIdx];
        float fy = incCy + incAx[incIdx * 3 + 1] * incSign * incH[incIdx];
        float fz = incCz + incAx[incIdx * 3 + 2] * incSign * incH[incIdx];
        int n0 = 0;
        for (int iu = 0; iu < 2; iu++)
        {
            float su = iu == 0 ? 1 : -1;
            for (int iv = 0; iv < 2; iv++)
            {
                float sv = iv == 0 ? 1 : -1;
                // Wound consistently (++, +−, −−, −+) so the clip walks a real quad.
                float s2 = su == 1 ? sv : -sv;
                BbClip[n0 * 3] = fx + incAx[u * 3] * incH[u] * su + incAx[v * 3] * incH[v] * s2;
                BbClip[n0 * 3 + 1] = fy + incAx[u * 3 + 1] * incH[u] * su + incAx[v * 3 + 1] * incH[v] * s2;
                BbClip[n0 * 3 + 2] = fz + incAx[u * 3 + 2] * incH[u] * su + incAx[v * 3 + 2] * incH[v] * s2;
                n0++;
            }
        }

        // Clip against the reference face's four side planes.
        int ru = (refAxisIdx + 1) % 3, rv = (refAxisIdx + 2) % 3;
        float[] src = BbClip, dst = BbClip2;
        int cnt = n0;
        for (int plane = 0; plane < 4; plane++)
        {
            int axis = plane < 2 ? ru : rv;
            float sgn2 = plane % 2 == 0 ? 1 : -1;
            float tx = refAx[axis * 3] * sgn2, ty = refAx[axis * 3 + 1] * sgn2, tz = refAx[axis * 3 + 2] * sgn2;
            float offset = refCx * tx + refCy * ty + refCz * tz + refH[axis];
            cnt = ClipPolyByPlane(src, cnt, tx, ty, tz, offset, dst);
            (src, dst) = (dst, src);
            if (cnt == 0) return;
        }

        // Keep what is at or below the reference face plane.
        float planeD = (refCx + nx * refH[refAxisIdx]) * nx + (refCy + ny * refH[refAxisIdx]) * ny +
                       (refCz + nz * refH[refAxisIdx]) * nz;
        int kept = 0;
        for (int i = 0; i < cnt; i++)
        {
            float sep = src[i * 3] * nx + src[i * 3 + 1] * ny + src[i * 3 + 2] * nz - planeD;
            if (sep > ContactMargin) continue;
            src[kept * 3] = src[i * 3];
            src[kept * 3 + 1] = src[i * 3 + 1];
            src[kept * 3 + 2] = src[i * 3 + 2];
            BbDepth[kept] = -sep;
            kept++;
        }
        if (kept == 0) return;

        // Cap the manifold at Bullet's four. Clipping a quad by four planes can
        // reach eight points, and every extra one is another solver row for a
        // patch the deepest four already describe. Deepest-first so the points
        // that matter survive the cut.
        if (kept > 4)
        {
            for (int i = 1; i < kept; i++)
            {
                float d = BbDepth[i];
                float px = src[i * 3], py = src[i * 3 + 1], pz = src[i * 3 + 2];
                int j = i - 1;
                while (j >= 0 && BbDepth[j] < d)
                {
                    BbDepth[j + 1] = BbDepth[j];
                    src[(j + 1) * 3] = src[j * 3];
                    src[(j + 1) * 3 + 1] = src[j * 3 + 1];
                    src[(j + 1) * 3 + 2] = src[j * 3 + 2];
                    j--;
                }
                BbDepth[j + 1] = d;
                src[(j + 1) * 3] = px; src[(j + 1) * 3 + 1] = py; src[(j + 1) * 3 + 2] = pz;
            }
            kept = 4;
        }
        for (int i = 0; i < kept; i++)
        {
            EmitBoxContact(store, a, b, src[i * 3], src[i * 3 + 1], src[i * 3 + 2], BbDepth[i], pool);
        }
    }

    // Edge-vs-edge: one point, at the midpoint of the closest approach between the
    // two supporting edges. Single-point is correct here — two crossed edges touch
    // at a point, unlike two faces.
    private static void EmitBoxEdgeContact(
        RigidBodyStore store, int a, int b,
        float depth,
        int i, int j,
        float hAx, float hAy, float hAz,
        float hBx, float hBy, float hBz,
        float cx, float cy, float cz,
        ContactPool pool)
    {
        float[] hA = BbHA, hB = BbHB;
        hA[0] = hAx; hA[1] = hAy; hA[2] = hAz;
        hB[0] = hBx; hB[1] = hBy; hB[2] = hBz;
        float nx = BbAxis[0], ny = BbAxis[1], nz = BbAxis[2];

        // A's supporting edge: offset along the two axes that are NOT the edge
        // direction, each toward B.
        float[] pA = BbPA;
        pA[0] = 0; pA[1] = 0; pA[2] = 0;
        for (int k = 0; k < 3; k++)
        {
            if (k == i) continue;
            float d = k == 0 ? nx : k == 1 ? ny : nz;
            pA[k] = hA[k] * (d >= 0 ? 1 : -1);
        }
        float dAx = i == 0 ? 1 : 0, dAy = i == 1 ? 1 : 0, dAz = i == 2 ? 1 : 0;

        // B's, offset the other way — its edge faces back toward A.
        float pBx = cx, pBy = cy, pBz = cz;
        for (int k = 0; k < 3; k++)
        {
            if (k == j) continue;
            float ax = BbBax[k * 3], ay = BbBax[k * 3 + 1], az = BbBax[k * 3 + 2];
            float s = ax * nx + ay * ny + az * nz >= 0 ? -1 : 1;
            pBx += ax * hB[k] * s; pBy += ay * hB[k] * s; pBz += az * hB[k] * s;
        }
        float dBx = BbBax[j * 3], dBy = BbBax[j * 3 + 1], dBz = BbBax[j * 3 + 2];

        ClosestPointsTwoSegments(
            pA[0] - dAx * hA[i], pA[1] - dAy * hA[i], pA[2] - dAz * hA[i],
            pA[0] + dAx * hA[i], pA[1] + dAy * hA[i], pA[2] + dAz * hA[i],
            pBx - dBx * hB[j], pBy - dBy * hB[j], pBz - dBz * hB[j],
            pBx + dBx * hB[j], pBy + dBy * hB[j], pBz + dBz * hB[j],
            CpA, CpB);
        EmitBoxContact(store, a, b,
            (CpA[0] + CpB[0]) * 0.5f, (CpA[1] + CpB[1]) * 0.5f, (CpA[2] + CpB[2]) * 0.5f,
            depth, pool);
    }

    // --- Dispatch & sweep -------------------------------------------------------

    // Dispatch a pair to the matching narrowphase. Caller has already done
    // broadphase + group/mask filtering. Some shape pairs (sphere-A capsule-B
    // etc.) reuse a canonical implementation via swap + flipLastNormal.
    public static void GenerateContacts(RigidBodyStore store, int a, int b, ContactPool pool)
    {
        int sA = store.Shape[a];
        int sB = store.Shape[b];
        if (sA == (int)RigidbodyShape.Sphere && sB == (int)RigidbodyShape.Sphere)
        {
            DetectSphereSphere(store, a, b, pool);
            return;
        }
        if (sA == (int)RigidbodyShape.Sphere && sB == (int)RigidbodyShape.Capsule)
        {
            DetectSphereCapsule(store, a, b, pool);
            return;
        }
        if (sA == (int)RigidbodyShape.Capsule && sB == (int)RigidbodyShape.Sphere)
        {
            // Only flip a contact this call actually produced. The detector returns
            // without emitting whenever the shapes are out of range, and flipping then
            // reverses the normal of whatever unrelated contact happens to sit at the
            // end of the pool — pushing those two bodies together instead of apart.
            int before = pool.Count;
            DetectSphereCapsule(store, b, a, pool);
            FlipNormalsFrom(pool, before);
            return;
        }
        if (sA == (int)RigidbodyShape.Capsule && sB == (int)RigidbodyShape.Capsule)
        {
            DetectCapsuleCapsule(store, a, b, pool);
            return;
        }
        if (sA == (int)RigidbodyShape.Sphere && sB == (int)RigidbodyShape.Box)
        {
            DetectSphereBox(store, a, b, pool);
            return;
        }
        if (sA == (int)RigidbodyShape.Box && sB == (int)RigidbodyShape.Sphere)
        {
            int before = pool.Count;
            DetectSphereBox(store, b, a, pool);
            FlipNormalsFrom(pool, before);
            return;
        }
        if (sA == (int)RigidbodyShape.Capsule && sB == (int)RigidbodyShape.Box)
        {
            DetectCapsuleBox(store, a, b, pool);
            return;
        }
        if (sA == (int)RigidbodyShape.Box && sB == (int)RigidbodyShape.Capsule)
        {
            int before = pool.Count;
            DetectCapsuleBox(store, b, a, pool);
            FlipNormalsFrom(pool, before);
            return;
        }
        if (sA == (int)RigidbodyShape.Box && sB == (int)RigidbodyShape.Box)
        {
            DetectBoxBox(store, a, b, pool);
        }
    }

    // After a swapped detect* call, the produced contacts' normals point the wrong
    // way and lever arms are mismatched. Flip and re-anchor EVERY contact the call
    // emitted, not just the last: capsule-box now returns up to three, and flipping
    // one of them would leave the others pulling the pair together instead of
    // pushing it apart.
    private static void FlipNormalsFrom(ContactPool pool, int from)
    {
        for (int i = from; i < pool.Count; i++)
        {
            FlipOneNormal(pool.Get(i));
        }
    }

    private static void FlipOneNormal(Contact c)
    {
        (c.BodyA, c.BodyB) = (c.BodyB, c.BodyA);
        (c.RAx, c.RBx) = (c.RBx, c.RAx);
        (c.RAy, c.RBy) = (c.RBy, c.RAy);
        (c.RAz, c.RBz) = (c.RBz, c.RAz);
        c.Nx = -c.Nx;
        c.Ny = -c.Ny;
        c.Nz = -c.Nz;
    }

    // Iterate the prebuilt candidate-pair list and AABB-test each pair. The
    // static-static and group/mask filters were applied once at construction —
    // see RigidBodyStore.GetCollisionPairs. SAP / dynamic AABB tree pay off
    // above ~500 bodies; below that this flat sweep wins on cache locality.
    public static void FindContacts(RigidBodyStore store, ContactPool pool)
    {
        store.UpdateAabbs();
        ushort[] pairs = store.GetCollisionPairs();
        for (int p = 0; p < pairs.Length; p += 2)
        {
            int i = pairs[p];
            int j = pairs[p + 1];
            if (!AabbOverlap(store, i, j)) continue;
            GenerateContacts(store, i, j, pool);
        }
        // Built-in floor: a plane pass against every dynamic body. Cheaper and more
        // complete than routing the huge ground box through the pair machinery —
        // one y-test per body, and box hems collide too (there is no generic
        // box-box narrowphase to lean on).
        int g = store.GroundIndex;
        if (g >= 0)
        {
            float[] minA = store.AabbMin;
            float[] invMass = store.InvMass;
            for (int i = 0; i < store.Count; i++)
            {
                if (invMass[i] <= 0) continue;
                if (minA[i * 3 + 1] > ContactMargin) continue;
                DetectFloor(store, i, g, pool);
            }
        }
    }

    // The floor's top face is the model-space plane y = 0; its body (`g`) exists so
    // contact rows have a static B side. Normal convention matches the detectors:
    // A→B, so pointing DOWN into the floor; depth > 0 = penetrating.
    private static void DetectFloor(RigidBodyStore store, int a, int g, ContactPool pool)
    {
        int ai = a * 3;
        float[] pos = store.Positions;
        float gx = pos[g * 3 + 0];
        float gy = pos[g * 3 + 1];
        float gz = pos[g * 3 + 2];
        float cx = pos[ai + 0];
        float cy = pos[ai + 1];
        float cz = pos[ai + 2];

        void Emit(float px, float py, float pz, float depth)
        {
            Contact c = pool.Acquire();
            c.BodyA = a;
            c.BodyB = g;
            c.Nx = 0;
            c.Ny = -1;
            c.Nz = 0;
            c.Depth = depth;
            c.RAx = px - pos[ai + 0];
            c.RAy = py - pos[ai + 1];
            c.RAz = pz - pos[ai + 2];
            c.RBx = px - gx;
            c.RBy = py - gy;
            c.RBz = pz - gz;
            CombineMaterials(store, a, g, c);
        }

        switch ((RigidbodyShape)store.Shape[a])
        {
            case RigidbodyShape.Sphere:
            {
                float r = store.Size[ai + 0];
                float low = cy - r;
                if (low <= ContactMargin) Emit(cx, low, cz, -low);
                break;
            }
            case RigidbodyShape.Capsule:
            {
                float r = store.Size[ai + 0];
                float h = store.Size[ai + 1] * 0.5f;
                float[] ax = CapPoint;
                CapsuleAxis(store, a, ax);
                for (int sign = 0; sign < 2; sign++)
                {
                    float sgn = sign == 0 ? -1f : 1f;
                    float ex = cx + ax[0] * h * sgn;
                    float ey = cy + ax[1] * h * sgn;
                    float ez = cz + ax[2] * h * sgn;
                    float low = ey - r;
                    if (low <= ContactMargin) Emit(ex, low, ez, -low);
                }
                break;
            }
            case RigidbodyShape.Box:
            {
                float hx = store.Size[ai + 0];
                float hy = store.Size[ai + 1];
                float hz = store.Size[ai + 2];
                float[] output = CapPointB;
                for (int k = 0; k < 8; k++)
                {
                    float lx = (k & 1) != 0 ? hx : -hx;
                    float ly = (k & 2) != 0 ? hy : -hy;
                    float lz = (k & 4) != 0 ? hz : -hz;
                    BodyLocalToWorldDir(store, a, lx, ly, lz, output);
                    float wy = cy + output[1];
                    if (wy <= ContactMargin) Emit(cx + output[0], wy, cz + output[2], -wy);
                }
                break;
            }
        }
    }
}

using System.Numerics;
using MikuEngine.Core.Math;

namespace MikuEngine.Physics;

/// <summary>
/// SoA storage for all rigid bodies. Per-body state, constants, bone-coupling
/// matrices, and a per-step AABB.（对照 reze physics/body.ts RigidBodyStore 逐行移植）
///
/// 零分配纪律：构造之后每帧路径（UpdateInvInertiaWorld / UpdateAabbs /
/// ComputeBoneOffsets）不分配；<see cref="GetCollisionPairs"/> 惰性构建一次后缓存
/// （reze 同）。scratch 缓冲为模块级 static（单线程假设与 reze 一致）。
/// </summary>
public sealed class RigidBodyStore
{
    public readonly int Count;

    public readonly float[] Positions;            // 3*N
    public readonly float[] Orientations;         // 4*N (xyzw)
    public readonly float[] LinearVelocities;     // 3*N
    public readonly float[] AngularVelocities;    // 3*N

    public readonly float[] InvMass;              // N (0 for static / kinematic)

    // Full anisotropic inertia. Local diagonal (body frame, Bullet's shape
    // formulas) plus the per-substep world tensor I⁻¹ = R·diag·Rᵀ (9 floats
    // row-major, symmetric). The old scalar approximation deposited constraint
    // impulses into rotational modes at the wrong rates on elongated capsules
    // (5:1 skirt/hair bodies are ~25× anisotropic), leaving cloth with several
    // times the kinetic energy real Bullet retains — visible as perpetual boil.
    public readonly float[] InvInertiaLocal;      // 3*N
    public readonly float[] InvInertiaWorld;      // 9*N

    public readonly float[] LinearDamping;
    public readonly float[] AngularDamping;
    public readonly byte[] Type;
    // PMX mode-2 bodies: dynamic, but the bone takes rotation only and the
    // body position re-pins to the animated bone each frame.
    public readonly byte[] Aligned;
    public readonly int[] BoneIndex;
    public readonly float[] Friction;
    public readonly float[] Restitution;

    // PMX has 16 collision groups. collisionGroup[i] is a single-bit set;
    // willCollideMask[i] is the 16-bit set of groups body i collides with.
    public readonly ushort[] CollisionGroup;
    public readonly ushort[] WillCollideMask;

    public readonly byte[] Shape;
    public readonly float[] Size;                 // 3*N (semantics depend on shape)

    public readonly float[] AabbMin;              // 3*N
    public readonly float[] AabbMax;              // 3*N

    // bodyOffsetMatrix[i] = boneInverseBind · shapeWorldBind.
    // bodyWorld = boneWorld · bodyOffsetMatrix; boneWorld = bodyWorld · bodyOffsetInverse.
    public readonly float[] BodyOffsetMatrix;     // 16*N column-major
    public readonly float[] BodyOffsetInverse;    // 16*N column-major
    private bool _boneOffsetsReady;

    // Flat list of (i, j) pairs that survive the static-static + group/mask
    // filter. None of those inputs change after construction, so building this
    // once collapses 60k pair tests/step (349 bodies) down to a few thousand.
    // Built lazily on first access.
    private ushort[]? _collisionPairs;

    /// <summary>
    /// Index of the built-in floor body (see MMDPhysics constructor), -1 if none.
    /// Excluded from the pair list; findContacts gives it a dedicated plane pass.
    /// 这是 setFloor 开关位（S2 的 P1 接出点），Stage 1 保留字段不接出 API。
    /// </summary>
    public int GroundIndex = -1;

    public RigidBodyStore(IReadOnlyList<RigidBodyDef> rigidbodies)
    {
        int n = rigidbodies.Count;
        Count = n;

        Positions = new float[n * 3];
        Orientations = new float[n * 4];
        LinearVelocities = new float[n * 3];
        AngularVelocities = new float[n * 3];
        InvMass = new float[n];
        InvInertiaLocal = new float[n * 3];
        InvInertiaWorld = new float[n * 9];
        LinearDamping = new float[n];
        AngularDamping = new float[n];
        Type = new byte[n];
        Aligned = new byte[n];
        BoneIndex = new int[n];
        BodyOffsetMatrix = new float[n * 16];
        BodyOffsetInverse = new float[n * 16];
        Friction = new float[n];
        Restitution = new float[n];
        CollisionGroup = new ushort[n];
        WillCollideMask = new ushort[n];
        Shape = new byte[n];
        Size = new float[n * 3];
        AabbMin = new float[n * 3];
        AabbMax = new float[n * 3];

        for (int i = 0; i < n; i++)
        {
            RigidBodyDef rb = rigidbodies[i];
            int i3 = i * 3;
            int i4 = i * 4;

            Positions[i3 + 0] = rb.ShapePosition.X;
            Positions[i3 + 1] = rb.ShapePosition.Y;
            Positions[i3 + 2] = rb.ShapePosition.Z;

            Quaternion q = QuatMath.FromEuler(rb.ShapeRotation.X, rb.ShapeRotation.Y, rb.ShapeRotation.Z);
            Orientations[i4 + 0] = q.X;
            Orientations[i4 + 1] = q.Y;
            Orientations[i4 + 2] = q.Z;
            Orientations[i4 + 3] = q.W;

            bool dynamic = rb.Type == RigidbodyType.Dynamic && rb.Mass > 0;
            InvMass[i] = dynamic ? 1f / rb.Mass : 0f;
            if (dynamic) ComputeLocalInvInertia(rb, InvInertiaLocal, i * 3);
            LinearDamping[i] = rb.LinearDamping;
            AngularDamping[i] = rb.AngularDamping;
            Type[i] = (byte)rb.Type;
            Aligned[i] = rb.Aligned ? (byte)1 : (byte)0;
            BoneIndex[i] = rb.BoneIndex;
            Friction[i] = rb.Friction;
            Restitution[i] = rb.Restitution;
            CollisionGroup[i] = (ushort)(1 << (rb.Group & 0xf));
            WillCollideMask[i] = (ushort)(rb.CollisionMask & 0xffff);
            Shape[i] = (byte)rb.Shape;
            Size[i * 3 + 0] = rb.Size.X;
            Size[i * 3 + 1] = rb.Size.Y;
            Size[i * 3 + 2] = rb.Size.Z;
        }
    }

    // Refresh I⁻¹_world = R·diag(invInertiaLocal)·Rᵀ for every dynamic body.
    // Called once per substep before constraint setup (orientations are
    // constant during a solve).
    public void UpdateInvInertiaWorld()
    {
        int n = Count;
        float[] ori = Orientations;
        float[] local = InvInertiaLocal;
        float[] w = InvInertiaWorld;
        float[] invMass = InvMass;

        for (int i = 0; i < n; i++)
        {
            if (invMass[i] <= 0) continue;
            int i3 = i * 3;
            int i4 = i * 4;
            int i9 = i * 9;
            float qx = ori[i4 + 0], qy = ori[i4 + 1], qz = ori[i4 + 2], qw = ori[i4 + 3];
            float x2 = qx + qx, y2 = qy + qy, z2 = qz + qz;
            float xx = qx * x2, yy = qy * y2, zz = qz * z2;
            float xy = qx * y2, xz = qx * z2, yz = qy * z2;
            float wx = qw * x2, wy = qw * y2, wz = qw * z2;
            // R columns (column-major rotation matrix)
            float r00 = 1 - (yy + zz), r01 = xy - wz, r02 = xz + wy;
            float r10 = xy + wz, r11 = 1 - (xx + zz), r12 = yz - wx;
            float r20 = xz - wy, r21 = yz + wx, r22 = 1 - (xx + yy);
            float d0 = local[i3 + 0], d1 = local[i3 + 1], d2 = local[i3 + 2];
            // W = R·diag·Rᵀ (symmetric)
            float a0 = r00 * d0, a1 = r01 * d1, a2 = r02 * d2;
            float b0 = r10 * d0, b1 = r11 * d1, b2 = r12 * d2;
            float c0 = r20 * d0, c1 = r21 * d1, c2 = r22 * d2;
            w[i9 + 0] = a0 * r00 + a1 * r01 + a2 * r02;
            w[i9 + 1] = a0 * r10 + a1 * r11 + a2 * r12;
            w[i9 + 2] = a0 * r20 + a1 * r21 + a2 * r22;
            w[i9 + 3] = w[i9 + 1];
            w[i9 + 4] = b0 * r10 + b1 * r11 + b2 * r12;
            w[i9 + 5] = b0 * r20 + b1 * r21 + b2 * r22;
            w[i9 + 6] = w[i9 + 2];
            w[i9 + 7] = w[i9 + 5];
            w[i9 + 8] = c0 * r20 + c1 * r21 + c2 * r22;
        }
    }

    // World-space AABBs for every body. Inflated by margin so contacts stay
    // paired across small velocity jitter without recomputing per iteration.
    public void UpdateAabbs(float margin = 0.5f)
    {
        int n = Count;
        float[] pos = Positions;
        float[] ori = Orientations;
        byte[] shapes = Shape;
        float[] sz = Size;
        float[] minA = AabbMin;
        float[] maxA = AabbMax;

        for (int i = 0; i < n; i++)
        {
            int i3 = i * 3;
            int i4 = i * 4;
            float px = pos[i3 + 0], py = pos[i3 + 1], pz = pos[i3 + 2];
            float hx = 0, hy = 0, hz = 0;

            switch ((RigidbodyShape)shapes[i])
            {
                case RigidbodyShape.Sphere:
                {
                    float r = sz[i3 + 0];
                    hx = hy = hz = r;
                    break;
                }
                case RigidbodyShape.Box:
                {
                    // OBB AABB: half-extents projected by |R|·size.
                    float qx = ori[i4 + 0], qy = ori[i4 + 1], qz = ori[i4 + 2], qw = ori[i4 + 3];
                    float x2 = qx + qx, y2 = qy + qy, z2 = qz + qz;
                    float xx = qx * x2, yy = qy * y2, zz = qz * z2;
                    float xy = qx * y2, xz = qx * z2, yz = qy * z2;
                    float wx = qw * x2, wy = qw * y2, wz = qw * z2;
                    float m00 = MathF.Abs(1 - (yy + zz)), m01 = MathF.Abs(xy + wz), m02 = MathF.Abs(xz - wy);
                    float m10 = MathF.Abs(xy - wz), m11 = MathF.Abs(1 - (xx + zz)), m12 = MathF.Abs(yz + wx);
                    float m20 = MathF.Abs(xz + wy), m21 = MathF.Abs(yz - wx), m22 = MathF.Abs(1 - (xx + yy));
                    float sx = sz[i3 + 0], sy = sz[i3 + 1], szz = sz[i3 + 2];
                    hx = m00 * sx + m01 * sy + m02 * szz;
                    hy = m10 * sx + m11 * sy + m12 * szz;
                    hz = m20 * sx + m21 * sy + m22 * szz;
                    break;
                }
                case RigidbodyShape.Capsule:
                {
                    // After rotation, cap offsets are ±halfH · R·ŷ, so AABB half-
                    // extents = |R·ŷ|·halfH + radius.
                    float r = sz[i3 + 0];
                    float halfH = sz[i3 + 1] * 0.5f;
                    float qx = ori[i4 + 0], qy = ori[i4 + 1], qz = ori[i4 + 2], qw = ori[i4 + 3];
                    // R · (0,1,0) = (2(xy − wz), 1 − 2(xx + zz), 2(yz + wx))
                    float rx = 2 * (qx * qy - qw * qz);
                    float ry = 1 - 2 * (qx * qx + qz * qz);
                    float rz = 2 * (qy * qz + qw * qx);
                    hx = MathF.Abs(rx) * halfH + r;
                    hy = MathF.Abs(ry) * halfH + r;
                    hz = MathF.Abs(rz) * halfH + r;
                    break;
                }
            }

            minA[i3 + 0] = px - hx - margin;
            minA[i3 + 1] = py - hy - margin;
            minA[i3 + 2] = pz - hz - margin;
            maxA[i3 + 0] = px + hx + margin;
            maxA[i3 + 1] = py + hy + margin;
            maxA[i3 + 2] = pz + hz + margin;
        }
    }

    // Compute bone-coupling matrices once, on the first step. Bodies with
    // boneIndex < 0 get identity offsets.
    public void ComputeBoneOffsets(float[] boneInverseBindMatrices)
    {
        int n = Count;
        float[] offsets = BodyOffsetMatrix;
        float[] inverses = BodyOffsetInverse;
        float[] ori = Orientations;
        float[] pos = Positions;
        int[] boneIdx = BoneIndex;
        int totalBones = boneInverseBindMatrices.Length / 16;

        float[] shapeWorldBind = ScratchA;
        float[] offsetMat = ScratchB;

        for (int i = 0; i < n; i++)
        {
            int dst = i * 16;
            int b = boneIdx[i];

            if (b < 0 || b >= totalBones)
            {
                Mat4.SetIdentity(offsets, dst);
                Mat4.SetIdentity(inverses, dst);
                continue;
            }

            // shapeWorldBind = T(shapePosition) · R(shapeRotation)
            int i3 = i * 3;
            int i4 = i * 4;
            Mat4.FromPositionRotationInto(
                pos[i3 + 0], pos[i3 + 1], pos[i3 + 2],
                ori[i4 + 0], ori[i4 + 1], ori[i4 + 2], ori[i4 + 3],
                shapeWorldBind, 0);

            // bodyOffset = boneInverseBind × shapeWorldBind
            Mat4.MultiplyArrays(boneInverseBindMatrices, b * 16, shapeWorldBind, 0, offsetMat, 0);

            // Copy into offsets[dst] and invert into inverses[dst].
            Array.Copy(offsetMat, 0, offsets, dst, 16);
            if (Mat4.InverseInto(offsetMat, ScratchC))
            {
                Array.Copy(ScratchC, 0, inverses, dst, 16);
            }
            else
            {
                Mat4.SetIdentity(inverses, dst);
            }
        }

        _boneOffsetsReady = true;
    }

    public bool IsBoneOffsetsReady() => _boneOffsetsReady;

    // Pair-filter inputs (invMass, group, mask) are immutable post-construction,
    // so build the candidate-pair list once and reuse every step.
    public ushort[] GetCollisionPairs()
    {
        if (_collisionPairs != null) return _collisionPairs;
        int n = Count;
        float[] invMass = InvMass;
        ushort[] group = CollisionGroup;
        ushort[] mask = WillCollideMask;
        // ushort 索引对（体数 &lt; 65536，MMD 模型刚体量级 ~ 数百）
        List<ushort> buf = new();
        for (int i = 0; i < n; i++)
        {
            ushort gi = group[i];
            ushort mi = mask[i];
            bool dynA = invMass[i] > 0;
            for (int j = i + 1; j < n; j++)
            {
                if (!dynA && invMass[j] == 0) continue;
                if ((mi & group[j]) == 0 || (mask[j] & gi) == 0) continue;
                buf.Add((ushort)i);
                buf.Add((ushort)j);
            }
        }
        _collisionPairs = buf.ToArray();
        return _collisionPairs;
    }

    // Module-private scratch（对照 reze body.ts 的 _scratchA/B/C；模块级互不踩踏）
    private static readonly float[] ScratchA = new float[16];
    private static readonly float[] ScratchB = new float[16];
    private static readonly float[] ScratchC = new float[16];

    // Diagonal local inverse inertia, matching Bullet's calculateLocalInertia
    // per shape (so behavior tracks Ammo-based MMD engines):
    //   Sphere:  I = (2/5)·m·r² on every axis
    //   Box:     Ix = m/12·(ly²+lz²), … with l = full extents (2·half)
    //   Capsule: Bullet's bounding-box approximation of the Y-axis capsule
    //            (lx = lz = 2r, ly = h + 2r)
    private static void ComputeLocalInvInertia(RigidBodyDef rb, float[] output, int o)
    {
        float m = rb.Mass;
        if (m <= 0) return;
        float ix, iy, iz;
        switch (rb.Shape)
        {
            case RigidbodyShape.Sphere:
            {
                float inertia = 0.4f * m * rb.Size.X * rb.Size.X;
                ix = inertia; iy = inertia; iz = inertia;
                break;
            }
            case RigidbodyShape.Box:
            {
                float lx2 = 4 * rb.Size.X * rb.Size.X;
                float ly2 = 4 * rb.Size.Y * rb.Size.Y;
                float lz2 = 4 * rb.Size.Z * rb.Size.Z;
                ix = (m / 12) * (ly2 + lz2);
                iy = (m / 12) * (lx2 + lz2);
                iz = (m / 12) * (lx2 + ly2);
                break;
            }
            case RigidbodyShape.Capsule:
            {
                float lx = 2 * rb.Size.X;
                float ly = rb.Size.Y + 2 * rb.Size.X;
                float lx2 = lx * lx;
                float ly2 = ly * ly;
                ix = (m / 12) * (ly2 + lx2);
                iy = (m / 12) * (lx2 + lx2);
                iz = (m / 12) * (lx2 + ly2);
                break;
            }
            default:
            {
                ix = m; iy = m; iz = m;
                break;
            }
        }
        output[o + 0] = ix > 0 ? 1f / ix : 0;
        output[o + 1] = iy > 0 ? 1f / iy : 0;
        output[o + 2] = iz > 0 ? 1f / iz : 0;
    }
}

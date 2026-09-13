using System.Numerics;
using MikuEngine.Core.Math;
using MikuEngine.Physics;

namespace MikuEngine.Physics.Tests;

/// <summary>
/// M-STORE-1：store 布局。构造 5 体 + 内置地面（模拟 B5 内核构造的接法：
/// 追加地面 def → 置零其组掩码 → GroundIndex 指向它），
/// 断言 groundIndex=N、各 SoA 字段与 def 一致、pair 过滤、骨骼偏移矩阵。
/// </summary>
public class RigidBodyStoreTests
{
    private static RigidBodyDef Def(string name, RigidbodyType type, RigidbodyShape shape, Vector3 size,
        Vector3 pos, Vector3 rot, int boneIndex, int group, ushort mask, float mass = 1f,
        float linDamp = 0.1f, float angDamp = 0.2f, float restitution = 0.3f, float friction = 0.4f,
        bool aligned = false)
    {
        return new RigidBodyDef
        {
            Name = name,
            EnglishName = name,
            Type = type,
            Aligned = aligned,
            BoneIndex = boneIndex,
            Group = group,
            CollisionMask = mask,
            Shape = shape,
            Size = size,
            ShapePosition = pos,
            ShapeRotation = rot,
            Mass = mass,
            LinearDamping = linDamp,
            AngularDamping = angDamp,
            Restitution = restitution,
            Friction = friction,
        };
    }

    /// <summary>5 体 + 地面，模拟内核构造时的地面接法（reze RezePhysics ctor）。</summary>
    private static (RigidBodyStore Store, RigidBodyDef[] Defs) BuildFivePlusGround()
    {
        var defs = new List<RigidBodyDef>
        {
            Def("static_box", RigidbodyType.Static, RigidbodyShape.Box,
                new Vector3(0.2f, 0.3f, 0.4f), new Vector3(1, 1, 0), new Vector3(0.1f, 0, 0),
                boneIndex: 0, group: 0, mask: 0xffff),
            Def("dyn_sphere", RigidbodyType.Dynamic, RigidbodyShape.Sphere,
                new Vector3(0.5f, 0, 0), new Vector3(2, 3, 4), new Vector3(0, 0.2f, 0),
                boneIndex: 1, group: 1, mask: 0xffff, mass: 1.5f,
                linDamp: 0.11f, angDamp: 0.22f, restitution: 0.33f, friction: 0.44f),
            Def("dyn_capsule_mode2", RigidbodyType.Dynamic, RigidbodyShape.Capsule,
                new Vector3(0.3f, 0.8f, 0), new Vector3(0, 5, 0), new Vector3(0, 0, 0.3f),
                boneIndex: 1, group: 2, mask: 0xffff, mass: 2f, aligned: true),
            Def("static_boneless", RigidbodyType.Static, RigidbodyShape.Sphere,
                new Vector3(0.25f, 0, 0), new Vector3(9, 9, 9), Vector3.Zero,
                boneIndex: -1, group: 4, mask: 0x0004),
            Def("dyn_box", RigidbodyType.Dynamic, RigidbodyShape.Box,
                new Vector3(0.4f, 0.6f, 0.8f), new Vector3(-1, 2, -3), new Vector3(0.2f, 0.3f, 0.4f),
                boneIndex: -1, group: 3, mask: 0x0002, mass: 2f),
        };
        // 内置地面：追加 + 掩码置零（脱离通用 pair 列表）+ groundIndex 开关位
        defs.Add(RigidBodyDef.CreateGround());
        var store = new RigidBodyStore(defs);
        int gi = store.Count - 1;
        store.CollisionGroup[gi] = 0;
        store.WillCollideMask[gi] = 0;
        store.GroundIndex = gi;
        return (store, defs.ToArray());
    }

    [Fact]
    public void Store_Layout_And_GroundIndex()
    {
        var (store, defs) = BuildFivePlusGround();

        Assert.Equal(6, store.Count);
        Assert.Equal(5, store.GroundIndex);
        // 地面体组掩码置零：脱离通用 pair 列表，走 findContacts 专用平面 pass
        Assert.Equal(0, store.CollisionGroup[5]);
        Assert.Equal(0, store.WillCollideMask[5]);

        for (int i = 0; i < 6; i++)
        {
            var d = defs[i];
            int i3 = i * 3, i4 = i * 4;
            Assert.Equal(d.ShapePosition.X, store.Positions[i3 + 0]);
            Assert.Equal(d.ShapePosition.Y, store.Positions[i3 + 1]);
            Assert.Equal(d.ShapePosition.Z, store.Positions[i3 + 2]);

            var q = QuatMath.FromEuler(d.ShapeRotation.X, d.ShapeRotation.Y, d.ShapeRotation.Z);
            Assert.Equal(q.X, store.Orientations[i4 + 0]);
            Assert.Equal(q.Y, store.Orientations[i4 + 1]);
            Assert.Equal(q.Z, store.Orientations[i4 + 2]);
            Assert.Equal(q.W, store.Orientations[i4 + 3]);

            Assert.Equal(d.LinearDamping, store.LinearDamping[i]);
            Assert.Equal(d.AngularDamping, store.AngularDamping[i]);
            Assert.Equal((byte)d.Type, store.Type[i]);
            Assert.Equal(d.Aligned ? 1 : 0, store.Aligned[i]);
            Assert.Equal(d.BoneIndex, store.BoneIndex[i]);
            Assert.Equal(d.Friction, store.Friction[i]);
            Assert.Equal(d.Restitution, store.Restitution[i]);
            // 组掩码：地面体（i=5）在构造后被内核置零以脱离通用 pair 列表，跳过
            if (i < 5)
            {
                Assert.Equal((ushort)(1 << (d.Group & 0xf)), store.CollisionGroup[i]);
                Assert.Equal(d.CollisionMask, store.WillCollideMask[i]);
            }
            Assert.Equal((byte)d.Shape, store.Shape[i]);
            Assert.Equal(d.Size.X, store.Size[i3 + 0]);
            Assert.Equal(d.Size.Y, store.Size[i3 + 1]);
            Assert.Equal(d.Size.Z, store.Size[i3 + 2]);

            // invMass：Dynamic 且 mass>0 → 1/mass，否则 0
            bool dynamic = d.Type == RigidbodyType.Dynamic && d.Mass > 0;
            Assert.Equal(dynamic ? 1f / d.Mass : 0f, store.InvMass[i]);
        }
    }

    /// <summary>形状惯量对 Bullet calculateLocalInertia 公式（球/盒/胶囊）。</summary>
    [Fact]
    public void Store_LocalInvInertia_PerShape()
    {
        var (store, defs) = BuildFivePlusGround();

        // body1 球：I = 0.4·m·r²
        {
            float i = 0.4f * 1.5f * 0.5f * 0.5f;
            Assert.Equal(1f / i, store.InvInertiaLocal[1 * 3 + 0], 4);
            Assert.Equal(1f / i, store.InvInertiaLocal[1 * 3 + 1], 4);
            Assert.Equal(1f / i, store.InvInertiaLocal[1 * 3 + 2], 4);
        }
        // body2 胶囊（r=0.3, h=0.8）：lx=lz=2r=0.6, ly=h+2r=1.4（Bullet 包围盒近似）
        {
            float m12 = 2f / 12f;
            float lx2 = 0.36f, ly2 = 1.96f;
            Assert.Equal(1f / (m12 * (ly2 + lx2)), store.InvInertiaLocal[2 * 3 + 0], 4);
            Assert.Equal(1f / (m12 * (lx2 + lx2)), store.InvInertiaLocal[2 * 3 + 1], 4);
            Assert.Equal(1f / (m12 * (lx2 + ly2)), store.InvInertiaLocal[2 * 3 + 2], 4);
        }
        // body4 盒：I = m/12·(l²+l²)，l = 全长（2·半长）
        {
            float m12 = 2f / 12f;
            float lx2 = 0.64f, ly2 = 1.44f, lz2 = 2.56f;
            Assert.Equal(1f / (m12 * (ly2 + lz2)), store.InvInertiaLocal[4 * 3 + 0], 4);
            Assert.Equal(1f / (m12 * (lx2 + lz2)), store.InvInertiaLocal[4 * 3 + 1], 4);
            Assert.Equal(1f / (m12 * (lx2 + ly2)), store.InvInertiaLocal[4 * 3 + 2], 4);
        }
        // 静态体惯量为 0（构造时不填）
        Assert.Equal(0f, store.InvInertiaLocal[0 * 3 + 0]);
    }

    /// <summary>pair 过滤：static-static 剔除、组掩码双向过滤、地面（掩码 0）全剔除。</summary>
    [Fact]
    public void Store_CollisionPairs_Filter()
    {
        var (store, _) = BuildFivePlusGround();
        // 预期：双方掩码互通的 static-dynamic 与 dynamic-dynamic 对
        // (0,1)(0,2)：0 的 mask 0xffff 放行、对方 mask 回看 group0 放行
        // (1,2)：双动态互通；(1,4)：4 的 mask=group1；(2,3)：3 的 mask=group2
        // 其余：单向掩码不通（0,4)(1,3)(2,4)(3,4)；地面掩码 0（1,5)(2,5)(4,5)；
        //       static-static（0,3)(0,5)(3,5）被剔除
        var expected = new ushort[] { 0, 1, 0, 2, 1, 2, 1, 4, 2, 3 };
        var pairs = store.GetCollisionPairs();
        Assert.Equal(expected, pairs);
        // 缓存：二次获取返回同一实例
        Assert.Same(pairs, store.GetCollisionPairs());
    }

    /// <summary>骨骼偏移矩阵：offset = boneInvBind·shapeWorldBind；inverse 可还原；无骨体 identity。</summary>
    [Fact]
    public void Store_ComputeBoneOffsets()
    {
        var (store, defs) = BuildFivePlusGround();
        Assert.False(store.IsBoneOffsetsReady());

        // 2 根骨骼：bone0 = T(1,2,3)，bone1 = identity
        float[] boneInvBind = new float[32];
        Mat4.FromPositionRotationInto(-1, -2, -3, 0, 0, 0, 1, boneInvBind, 0); // T(1,2,3)⁻¹
        Mat4.SetIdentity(boneInvBind, 16);

        store.ComputeBoneOffsets(boneInvBind);
        Assert.True(store.IsBoneOffsetsReady());

        float[] shapeWorldBind = new float[16];
        float[] expected = new float[16];
        for (int i = 0; i < 6; i++)
        {
            var d = defs[i];
            int dst = i * 16;
            if (d.BoneIndex < 0)
            {
                // 无骨体：identity（reze 语义）
                Assert.Equal(1f, store.BodyOffsetMatrix[dst]);
                Assert.Equal(1f, store.BodyOffsetInverse[dst]);
                Assert.Equal(0f, store.BodyOffsetMatrix[dst + 1]);
                continue;
            }
            var q = QuatMath.FromEuler(d.ShapeRotation.X, d.ShapeRotation.Y, d.ShapeRotation.Z);
            Mat4.FromPositionRotationInto(
                d.ShapePosition.X, d.ShapePosition.Y, d.ShapePosition.Z,
                q.X, q.Y, q.Z, q.W,
                shapeWorldBind, 0);
            Mat4.MultiplyArrays(boneInvBind, d.BoneIndex * 16, shapeWorldBind, 0, expected, 0);
            for (int k = 0; k < 16; k++)
                Assert.True(MathF.Abs(expected[k] - store.BodyOffsetMatrix[dst + k]) <= 1e-5f,
                    $"body{i} offset[{k}]");

            // offset·offsetInverse ≈ I
            float[] prod = new float[16];
            float[] inv = new float[16];
            Array.Copy(store.BodyOffsetInverse, dst, inv, 0, 16);
            Mat4.MultiplyArrays(expected, 0, inv, 0, prod, 0);
            float[] identity = new float[16];
            Mat4.SetIdentity(identity, 0);
            for (int k = 0; k < 16; k++)
                Assert.True(MathF.Abs(identity[k] - prod[k]) <= 1e-4f, $"body{i} offset·inv[{k}]");
        }
    }

    /// <summary>PMX 模式映射：0→Static、1→Dynamic、2→Dynamic+Aligned（reze loader 决策）。</summary>
    [Fact]
    public void FromPmx_MapsModes()
    {
        var rb = new MikuEngine.Core.Models.PmxRigidBody
        {
            Name = "skirt", EnglishName = "skirt",
            BoneIndex = 3, CollisionGroup = 2, CollisionMask = 0x00ff,
            ShapeType = MikuEngine.Core.Models.PmxRigidBodyShapeType.Capsule,
            ShapeSize = new Vector3(0.3f, 0.8f, 0),
            ShapePosition = new Vector3(0, 1, 2),
            ShapeRotation = new Vector3(0.1f, 0.2f, 0.3f),
            Mass = 1.2f, LinearDamping = 0.5f, AngularDamping = 0.6f,
            Repulsion = 0.7f, Friction = 0.8f,
            PhysicsMode = MikuEngine.Core.Models.PmxRigidBodyMode.PhysicsWithBone,
        };
        var def = RigidBodyDef.FromPmx(rb);
        Assert.Equal(RigidbodyType.Dynamic, def.Type);
        Assert.True(def.Aligned);
        Assert.Equal(RigidbodyShape.Capsule, def.Shape);
        Assert.Equal(0.7f, def.Restitution); // PMX 反发 → Restitution
        Assert.Equal(2, def.Group);
        Assert.Equal((ushort)0x00ff, def.CollisionMask);
        Assert.Equal(0, def.ModelGroupId); // S5 预留位默认 0

        var m0 = RigidBodyDef.FromPmx(rb with { PhysicsMode = MikuEngine.Core.Models.PmxRigidBodyMode.FollowBone });
        Assert.Equal(RigidbodyType.Static, m0.Type);
        Assert.False(m0.Aligned);
        var m1 = RigidBodyDef.FromPmx(rb with { PhysicsMode = MikuEngine.Core.Models.PmxRigidBodyMode.Physics });
        Assert.Equal(RigidbodyType.Dynamic, m1.Type);
        Assert.False(m1.Aligned);
    }

    /// <summary>内置地面 def 与 reze 构造函数里的参数逐项一致。</summary>
    [Fact]
    public void CreateGround_MatchesReze()
    {
        var g = RigidBodyDef.CreateGround();
        Assert.Equal("__ground__", g.Name);
        Assert.Equal(-1, g.BoneIndex);
        Assert.Equal(RigidbodyShape.Box, g.Shape);
        Assert.Equal(new Vector3(500, 1, 500), g.Size);
        Assert.Equal(new Vector3(0, -1, 0), g.ShapePosition); // 顶面 = 模型空间 y=0
        Assert.Equal(0f, g.Mass);
        Assert.Equal(0.6f, g.Friction);
        Assert.Equal(RigidbodyType.Static, g.Type);
    }
}

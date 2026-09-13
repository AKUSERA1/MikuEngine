using System.Numerics;
using MikuEngine.Physics;

namespace MikuEngine.Physics.Tests;

/// <summary>
/// B3 碰撞检测单测：M-CONTACT-1/2/3、M-FLOOR-1（方案 §5）。
/// 断言全部为解析几何值（穿透深度、法线方向、杠杆臂），无外部黄金值——
/// reze 的行为规格由逐行移植保证，几何定义在断言中显式复算。
/// </summary>
public class ContactDetectionTests
{
    private static RigidBodyDef Def(
        RigidbodyShape shape, Vector3 pos, Vector3 size,
        RigidbodyType type = RigidbodyType.Dynamic, float mass = 1f,
        float friction = 0.5f, float restitution = 0.2f,
        Vector3? rot = null, int group = 1, ushort mask = 0xffff, string? name = null)
    {
        return new RigidBodyDef
        {
            Name = name ?? "b",
            EnglishName = name ?? "b",
            BoneIndex = -1,
            Group = group,
            CollisionMask = mask,
            Shape = shape,
            Size = size,
            ShapePosition = pos,
            ShapeRotation = rot ?? Vector3.Zero,
            Mass = type == RigidbodyType.Dynamic ? mass : 0f,
            LinearDamping = 0,
            AngularDamping = 0,
            Restitution = restitution,
            Friction = friction,
            Type = type,
        };
    }

    private static RigidBodyStore Store(params RigidBodyDef[] defs) => new(defs);

    /// <summary>
    /// 内置地面挂载，对照 reze physics.ts 构造（L107-132）：ground def 追加到列表
    /// 末尾、组掩码置零（脱离通用 pair 列表，走专用平面 pass）、记录 groundIndex。
    /// B5 的 MMDPhysics 构造将承担同一职责。
    /// </summary>
    private static RigidBodyStore StoreWithGround(params RigidBodyDef[] defs)
    {
        List<RigidBodyDef> all = new List<RigidBodyDef>(defs) { RigidBodyDef.CreateGround() };
        RigidBodyStore store = new(all);
        int gi = store.Count - 1;
        store.CollisionGroup[gi] = 0;
        store.WillCollideMask[gi] = 0;
        store.GroundIndex = gi;
        return store;
    }

    private static ContactPool Detect(RigidBodyStore store, int a, int b)
    {
        ContactPool pool = new();
        pool.Reset();
        ContactDetection.GenerateContacts(store, a, b, pool);
        return pool;
    }

    private static void AssertVec(string label, float actual, float expected, float tol = 1e-5f)
    {
        Assert.True(MathF.Abs(actual - expected) <= tol, $"{label}: actual={actual} expected={expected}");
    }

    // =========================================================================
    // M-CONTACT-1 球-球穿透（+ 球-胶囊 / 胶囊-胶囊 形状对）
    // 输入：两球强制重叠；断言：法线方向、深度、接触点对齐几何定义。
    // =========================================================================

    [Fact]
    public void MContact1_SphereSphere_OverlapGeometry()
    {
        RigidBodyStore store = Store(
            Def(RigidbodyShape.Sphere, new Vector3(0, 0, 0), new Vector3(1, 0, 0), friction: 0.3f, restitution: 0.2f, name: "A"),
            Def(RigidbodyShape.Sphere, new Vector3(1.5f, 0, 0), new Vector3(1, 0, 0), friction: 0.7f, restitution: 0.4f, name: "B"));
        ContactPool pool = Detect(store, 0, 1);

        Assert.Equal(1, pool.Count);
        Contact c = pool.Get(0);
        Assert.Equal(0, c.BodyA);
        Assert.Equal(1, c.BodyB);

        // depth = rA + rB − d = 2 − 1.5
        AssertVec("depth", c.Depth, 0.5f);
        // n 单位、A→B（+x）
        AssertVec("nx", c.Nx, 1f);
        AssertVec("ny", c.Ny, 0f);
        AssertVec("nz", c.Nz, 0f);
        // 接触点 = 各自表面：rA = n·rA（A 表面），rB = −n·rB（B 表面）
        AssertVec("rAx", c.RAx, 1f);
        AssertVec("rAy", c.RAy, 0f);
        AssertVec("rAz", c.RAz, 0f);
        AssertVec("rBx", c.RBx, -1f);
        AssertVec("rBy", c.RBy, 0f);
        AssertVec("rBz", c.RBz, 0f);
        // 材质组合：摩擦几何均值、恢复系数算术均值
        AssertVec("friction", c.Friction, MathF.Sqrt(0.3f * 0.7f));
        AssertVec("restitution", c.Restitution, 0.3f);
    }

    [Fact]
    public void MContact1_SphereSphere_SeparatedAndMarginBand()
    {
        // 分离超 margin：无接触
        RigidBodyStore far = Store(
            Def(RigidbodyShape.Sphere, new Vector3(0, 0, 0), new Vector3(1, 0, 0)),
            Def(RigidbodyShape.Sphere, new Vector3(2.06f, 0, 0), new Vector3(1, 0, 0)));
        Assert.Equal(0, Detect(far, 0, 1).Count);

        // margin 带内：speculative 接触，深度为负
        RigidBodyStore band = Store(
            Def(RigidbodyShape.Sphere, new Vector3(0, 0, 0), new Vector3(1, 0, 0)),
            Def(RigidbodyShape.Sphere, new Vector3(2.02f, 0, 0), new Vector3(1, 0, 0)));
        ContactPool pool = Detect(band, 0, 1);
        Assert.Equal(1, pool.Count);
        AssertVec("spec depth", pool.Get(0).Depth, -0.02f);

        // 完全重合：任意轴取 (0,1,0)
        RigidBodyStore same = Store(
            Def(RigidbodyShape.Sphere, new Vector3(0, 0, 0), new Vector3(1, 0, 0)),
            Def(RigidbodyShape.Sphere, new Vector3(0, 0, 0), new Vector3(1, 0, 0)));
        ContactPool colocated = Detect(same, 0, 1);
        Assert.Equal(1, colocated.Count);
        AssertVec("co nx", colocated.Get(0).Nx, 0f);
        AssertVec("co ny", colocated.Get(0).Ny, 1f);
    }

    [Fact]
    public void MContact1_SphereCapsule_GeometryAndSwap()
    {
        // 球在胶囊段端点正上方：closest=(0,1,0)，d=0.6，depth = 1.0 − 0.6 = 0.4。
        // n 从球心指向胶囊最近点（= A→B，向下）。
        RigidBodyStore store = Store(
            Def(RigidbodyShape.Sphere, new Vector3(0, 1.6f, 0), new Vector3(0.5f, 0, 0), name: "sphere"),
            Def(RigidbodyShape.Capsule, new Vector3(0, 0, 0), new Vector3(0.5f, 2, 0.5f), name: "cap"));
        ContactPool pool = Detect(store, 0, 1);
        Assert.Equal(1, pool.Count);
        Contact c = pool.Get(0);
        AssertVec("depth", c.Depth, 0.4f);
        AssertVec("nx", c.Nx, 0f);
        AssertVec("ny", c.Ny, -1f);
        // rA = n·rA（球表面）= (0,−0.5,0)；rB = closest − n·rB − capCenter = (0,1,0)−(0,−0.5,0)
        AssertVec("rAy", c.RAy, -0.5f);
        AssertVec("rBy", c.RBy, 1.5f);

        // 反序派发（capsule, sphere）走 swap+flip：约定仍 A→B（向上）
        ContactPool swapped = Detect(store, 1, 0);
        Assert.Equal(1, swapped.Count);
        Contact f = swapped.Get(0);
        Assert.Equal(1, f.BodyA);
        Assert.Equal(0, f.BodyB);
        AssertVec("swap depth", f.Depth, 0.4f);
        AssertVec("swap ny", f.Ny, 1f);     // A→B = capsule→sphere = +y
        AssertVec("swap rAy", f.RAy, 1.5f);  // 原 rB（capsule 侧）
        AssertVec("swap rBy", f.RBy, -0.5f); // 原 rA（sphere 侧）
    }

    [Fact]
    public void MContact1_CapsuleCapsule_ParallelEmitsThree()
    {
        // 近平行轴（cos=1 > 0.9）：primary + A 段两端点采样 = 3 接触
        RigidBodyStore store = Store(
            Def(RigidbodyShape.Capsule, new Vector3(0, 0, 0), new Vector3(0.5f, 2, 0.5f), name: "A"),
            Def(RigidbodyShape.Capsule, new Vector3(0.5f, 0, 0), new Vector3(0.5f, 2, 0.5f), name: "B"));
        ContactPool pool = Detect(store, 0, 1);
        Assert.Equal(3, pool.Count);
        for (int i = 0; i < 3; i++)
        {
            Contact c = pool.Get(i);
            AssertVec($"c{i} depth", c.Depth, 0.5f);
            AssertVec($"c{i} nx", c.Nx, 1f);
            AssertVec($"c{i} ny", c.Ny, 0f);
        }
    }

    // =========================================================================
    // M-CONTACT-2 盒-盒（旋转盒对，分离轴无漏判）
    // =========================================================================

    [Fact]
    public void MContact2_BoxBox_FaceManifoldFourPoints()
    {
        // 轴对齐重叠：A 的 −x 面与 B 的 +x 面，面裁剪得 4 点 manifold
        RigidBodyStore store = Store(
            Def(RigidbodyShape.Box, new Vector3(0, 0, 0), new Vector3(1, 1, 1), name: "A"),
            Def(RigidbodyShape.Box, new Vector3(1.5f, 0, 0), new Vector3(1, 1, 1), name: "B"));
        ContactPool pool = Detect(store, 0, 1);

        Assert.Equal(4, pool.Count);
        for (int i = 0; i < 4; i++)
        {
            Contact c = pool.Get(i);
            AssertVec($"c{i} nx", c.Nx, 1f);
            AssertVec($"c{i} ny", c.Ny, 0f);
            AssertVec($"c{i} depth", c.Depth, 0.5f); // 1 + 1 − 1.5
            // 接触点在 B 的 +x 面：posB + rB = (0.5, ±1, ±1)，rB.x = 0.5 − 1.5 = −1
            AssertVec($"c{i} rBx", c.RBx, -1f);
        }
    }

    [Fact]
    public void MContact2_BoxBox_RotationalSweepNeverMisses()
    {
        // 凸对采样表：B 扫欧拉角组合 3³ = 27 组，中心距固定重叠（overlap ≥ 0.3
        // 且任何单位轴上 projA+projB−dist ≥ 0）——SAT 无漏判：每次必须至少 1
        // 接触，法线单位长且指向 A→B。
        float[] angles = { 0f, MathF.PI / 6f, MathF.PI / 3f };
        foreach (float ex in angles)
        foreach (float ey in angles)
        foreach (float ez in angles)
        {
            Vector3 euler = new(ex, ey, ez);
            RigidBodyStore store = Store(
                Def(RigidbodyShape.Box, new Vector3(0, 0, 0), new Vector3(1, 1, 1), name: "A"),
                Def(RigidbodyShape.Box, new Vector3(1.4f, 0, 0), new Vector3(0.7f, 0.7f, 0.7f), rot: euler, name: "B"));
            ContactPool pool = Detect(store, 0, 1);
            Assert.True(pool.Count >= 1,
                $"euler={euler}: expected >=1 contact, got 0");

            for (int i = 0; i < pool.Count; i++)
            {
                Contact c = pool.Get(i);
                float len = MathF.Sqrt(c.Nx * c.Nx + c.Ny * c.Ny + c.Nz * c.Nz);
                AssertVec($"euler={euler} c{i} |n|", len, 1f, 1e-4f);
                Assert.True(c.Nx > 0,
                    $"euler={euler} c{i}: normal must point A→B (B is at +x)");
            }
        }
    }

    [Fact]
    public void MContact2_BoxBox_SeparatingAxisYieldsNone()
    {
        // AABB 重叠但 SAT 分离（45° 薄板斜插空隙）：不得产生接触。
        // A 的 y 轴投影：projA+projB−dist = 0.05 + 0.7425 − 0.9 < −margin。
        RigidBodyStore store = Store(
            Def(RigidbodyShape.Box, new Vector3(0, 0, 0), new Vector3(1, 0.05f, 1), name: "A"),
            Def(RigidbodyShape.Box, new Vector3(0, 0.9f, 0), new Vector3(1, 0.05f, 1),
                rot: new Vector3(0, 0, MathF.PI / 4f), name: "B"));
        store.UpdateAabbs();
        Assert.True(ContactDetection.AabbOverlap(store, 0, 1), "test precondition: AABBs must overlap");
        Assert.Equal(0, Detect(store, 0, 1).Count);
    }

    // =========================================================================
    // M-CONTACT-3 地面平面 pass（球/胶囊/盒各压地，均产生接触，顶面 y=0）
    // =========================================================================

    [Fact]
    public void MContact3_FloorPassCoversAllShapes()
    {
        // 三个体在 x 上互相分离（AABB 不交），保证接触全部来自地面 pass
        RigidBodyStore store = StoreWithGround(
            Def(RigidbodyShape.Sphere, new Vector3(-3, 0.3f, 0), new Vector3(0.5f, 0, 0), name: "sphere"),
            Def(RigidbodyShape.Capsule, new Vector3(0, 0, 0), new Vector3(0.5f, 2, 0.5f), name: "capsule"),
            Def(RigidbodyShape.Box, new Vector3(3, 0, 0), new Vector3(1, 0.5f, 1), name: "box"));

        ContactPool pool = new();
        ContactDetection.FindContacts(store, pool);

        // 球 1（最低点）+ 胶囊 1（下端帽；上端 low=0.5 出带）+ 盒 4（底面四角）= 6
        Assert.Equal(6, pool.Count);

        int sphere = 0, capsule = 0, box = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            Contact c = pool.Get(i);
            Assert.Equal(3, c.BodyB);
            AssertVec($"c{i} nx", c.Nx, 0f);
            AssertVec($"c{i} ny", c.Ny, -1f);
            AssertVec($"c{i} nz", c.Nz, 0f);

            // 接触点 = 体表面最低处（球最低点 / 胶囊下端帽最低点 / 盒底角）
            float contactY = c.RAy + store.Positions[c.BodyA * 3 + 1];
            switch (c.BodyA)
            {
                case 0: // 球：low = 0.3 − 0.5 = −0.2，depth = 0.2
                    sphere++;
                    AssertVec("sphere depth", c.Depth, 0.2f);
                    AssertVec("sphere contact y", contactY, -0.2f);
                    break;
                case 1: // 胶囊：下端帽 ey=−1，low=−1.5，depth=1.5
                    capsule++;
                    AssertVec("capsule depth", c.Depth, 1.5f);
                    AssertVec("capsule contact y", contactY, -1.5f);
                    break;
                case 2: // 盒：四个底角 wy=−0.5，depth=0.5
                    box++;
                    AssertVec("box depth", c.Depth, 0.5f);
                    AssertVec("box contact y", contactY, -0.5f);
                    break;
            }
            // 地面材质：friction = sqrt(0.5 × 0.6)，restitution = (0.2 + 0)/2 = 0.1
            AssertVec($"c{i} friction", c.Friction, MathF.Sqrt(0.5f * 0.6f));
            AssertVec($"c{i} restitution", c.Restitution, 0.1f);
        }
        Assert.Equal(1, sphere);
        Assert.Equal(1, capsule);
        Assert.Equal(4, box);
    }

    // =========================================================================
    // M-FLOOR-1 地面细节：margin 带、过滤条件、旋转盒角
    // =========================================================================

    [Fact]
    public void MFloor1_SpeculativeMarginBandAndFilters()
    {
        // margin 带内：speculative 负深度
        RigidBodyStore band = StoreWithGround(
            Def(RigidbodyShape.Sphere, new Vector3(0, 0.52f, 0), new Vector3(0.5f, 0, 0)));
        ContactPool pool = new();
        ContactDetection.FindContacts(band, pool);
        Assert.Equal(1, pool.Count);
        AssertVec("spec depth", pool.Get(0).Depth, -0.02f);

        // 远离地面（AABB 下沿 > margin）：无接触
        RigidBodyStore high = StoreWithGround(
            Def(RigidbodyShape.Sphere, new Vector3(0, 1.2f, 0), new Vector3(0.5f, 0, 0)));
        ContactPool empty = new();
        ContactDetection.FindContacts(high, empty);
        Assert.Equal(0, empty.Count);

        // 静态体压地：不产生接触（floor pass 只对 invMass>0）
        RigidBodyStore stat = StoreWithGround(
            Def(RigidbodyShape.Sphere, new Vector3(0, 0.3f, 0), new Vector3(0.5f, 0, 0), type: RigidbodyType.Static));
        ContactPool staticPool = new();
        ContactDetection.FindContacts(stat, staticPool);
        Assert.Equal(0, staticPool.Count);

        // setFloor(false) 语义（groundIndex=-1）：地面体仍在 store，但不走平面 pass
        RigidBodyStore noGround = StoreWithGround(
            Def(RigidbodyShape.Sphere, new Vector3(0, 0.3f, 0), new Vector3(0.5f, 0, 0)));
        noGround.GroundIndex = -1;
        ContactPool none = new();
        ContactDetection.FindContacts(noGround, none);
        Assert.Equal(0, none.Count);
    }

    [Fact]
    public void MFloor1_RotatedBoxRestsOnCorners()
    {
        // 盒绕 z 转 45°：最低角 wy = 0.7071(ly−lx)（详断言）
        RigidBodyStore store = StoreWithGround(
            Def(RigidbodyShape.Box, new Vector3(0, 0, 0), new Vector3(0.5f, 0.5f, 0.5f),
                rot: new Vector3(0, 0, MathF.PI / 4f)));
        ContactPool pool = new();
        ContactDetection.FindContacts(store, pool);
        // 8 角 wy ∈ {0×4, ±0.7071×2}；wy ≤ margin（含 4 个 wy=0 的 depth=0
        // speculative 角）都 emit → 2 深 + 4 零深 = 6
        Assert.Equal(6, pool.Count);
        int deep = 0, touching = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            Contact c = pool.Get(i);
            AssertVec($"c{i} ny", c.Ny, -1f);
            if (c.Depth > 0.1f)
            {
                deep++;
                AssertVec($"c{i} depth", c.Depth, 0.70710678f, 1e-4f);
            }
            else
            {
                touching++;
                AssertVec($"c{i} depth", c.Depth, 0f, 1e-5f);
            }
        }
        Assert.Equal(2, deep);
        Assert.Equal(4, touching);
    }

    [Fact]
    public void MFloor1_PairAndFloorContactsCoexist()
    {
        // pair sweep 与地面 pass 共存：A-B 互撞且都在压地
        RigidBodyStore store = StoreWithGround(
            Def(RigidbodyShape.Sphere, new Vector3(0, 0.3f, 0), new Vector3(0.5f, 0, 0), name: "A"),
            Def(RigidbodyShape.Sphere, new Vector3(0.7f, 0.3f, 0), new Vector3(0.5f, 0, 0), name: "B"));
        ContactPool pool = new();
        ContactDetection.FindContacts(store, pool);

        // A-B pair 1 + A 地面 1 + B 地面 1（low = −0.2）
        Assert.Equal(3, pool.Count);
        int pair = 0, floorA = 0, floorB = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            Contact c = pool.Get(i);
            if (c.BodyB == 2)
            {
                if (c.BodyA == 0) floorA++; else floorB++;
            }
            else
            {
                pair++;
                Assert.Equal(0, c.BodyA);
                Assert.Equal(1, c.BodyB);
            }
        }
        Assert.Equal(1, pair);
        Assert.Equal(1, floorA);
        Assert.Equal(1, floorB);
    }
}

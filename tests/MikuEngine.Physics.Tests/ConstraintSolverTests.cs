using System.Numerics;
using MikuEngine.Core.Math;
using MikuEngine.Physics;
using Xunit;

// 物理内核沿用 reze 的模块级 static scratch（单线程假设，reze 同），测试间
// 禁止交错执行——否则 ContactDetection/ConstraintSolver 的 static 缓冲会被
// 并行测试类踩踏（xUnit 默认按类并行）。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MikuEngine.Physics.Tests;

/// <summary>
/// B4 约束求解器单测：M-CHAIN-1（关节摆锤收敛）、M-JOINT-1（旋转限位钳制）、
/// M-FRIC-1（摩擦静止），另含 B4 checklist 的 8 节链长链误差验证与确定性 smoke。
/// 断言为解析几何/能量判据，无外部黄金值——reze 行为规格由逐行移植保证。
/// </summary>
public class ConstraintSolverTests
{
    private const float Dt = 1f / 60f;

    private static RigidBodyDef Def(
        RigidbodyShape shape, Vector3 pos, Vector3 size,
        RigidbodyType type = RigidbodyType.Dynamic, float mass = 1f,
        float friction = 0.5f, float restitution = 0.2f,
        float linearDamping = 0f, float angularDamping = 0f,
        int group = 1, ushort mask = 0xffff, string? name = null)
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
            ShapeRotation = Vector3.Zero,
            Mass = type == RigidbodyType.Dynamic ? mass : 0f,
            LinearDamping = linearDamping,
            AngularDamping = angularDamping,
            Restitution = restitution,
            Friction = friction,
            Type = type,
        };
    }

    private static JointDef Joint(int a, int b, Vector3 position,
        Vector3? linearMin = null, Vector3? linearMax = null,
        Vector3? angularMin = null, Vector3? angularMax = null, string? name = null)
    {
        // 默认：线性全锁（min==max==0，焊接），角度全自由（min>max，Bullet free 约定）。
        linearMin ??= Vector3.Zero;
        linearMax ??= Vector3.Zero;
        angularMin ??= new Vector3(1, 1, 1);
        angularMax ??= new Vector3(-1, -1, -1);
        return new JointDef
        {
            Name = name ?? "j",
            EnglishName = name ?? "j",
            Type = 0,
            RigidbodyIndexA = a,
            RigidbodyIndexB = b,
            Position = position,
            Rotation = Vector3.Zero,
            PositionMin = linearMin.Value,
            PositionMax = linearMax.Value,
            RotationMin = angularMin.Value,
            RotationMax = angularMax.Value,
            SpringPosition = Vector3.Zero,
            SpringRotation = Vector3.Zero,
        };
    }

    /// <summary>相对欧拉 X（对照 solver.ts matrixToEulerXYZ 的转置约定）：
    /// R = TAᵀ·TB（此处两 frame 均 identity、A 无旋转 → R 即 B 的旋转矩阵），
    /// 列主序 m[col*4+row]，ex = atan2(−r21, r22)。</summary>
    private static float RelEulerX(RigidBodyStore store, int i)
    {
        int i4 = i * 4;
        float[] m = new float[16];
        Mat4.FromQuatInto(
            store.Orientations[i4 + 0], store.Orientations[i4 + 1],
            store.Orientations[i4 + 2], store.Orientations[i4 + 3], m, 0);
        return MathF.Atan2(-m[1 * 4 + 2], m[2 * 4 + 2]);
    }

    /// <summary>
    /// M-CHAIN-1：双体 6DOF 摆锤（锚点静态 + 动态球，线性轴焊接、角度轴自由），
    /// 初角速度释放，500 tick。
    /// 断言：能量单调不增（允许求解器噪声）、杆长不漂移、静止后角度稳定。
    /// </summary>
    [Fact]
    public void MChain1_PendulumEnergyMonotonicAndSettles()
    {
        List<RigidBodyDef> defs =
        [
            // 碰撞掩码置 0：摆锤测的是关节，不是碰撞。
            Def(RigidbodyShape.Sphere, new Vector3(0, 10, 0), new Vector3(0.3f, 0, 0),
                RigidbodyType.Static, group: 1, mask: 0, name: "anchor"),
            Def(RigidbodyShape.Sphere, new Vector3(0, 8, 0), new Vector3(0.3f, 0, 0),
                mass: 1f, linearDamping: 0.4f, angularDamping: 0.4f,
                group: 1, mask: 0, name: "bob"),
        ];
        JointDef joint = Joint(0, 1, new Vector3(0, 10, 0), name: "swing");
        SixDofSpringConstraint[] constraints = ConstraintBuilder.BuildConstraints(defs, [joint]);
        Assert.Single(constraints);
        Assert.False(constraints[0].IsLoop);

        RigidBodyStore store = new(defs);
        World world = new(new Vector3(0, -98, 0));
        SolverCache cache = new(constraints);

        // 初角速度绕 Z → 在 XY 平面摆动。ω=3, L=2 → KE₀ = ½·(ωL)² = 18。
        store.AngularVelocities[1 * 3 + 2] = 3f;

        const float g = 98f;
        float e0 = 0;
        float prevE = 0;
        float maxJump = 0;

        for (int tick = 0; tick < 500; tick++)
        {
            world.Step(store, Dt, null, constraints, cache);

            float vy = store.LinearVelocities[1 * 3 + 1];
            float ke = 0.5f * (store.LinearVelocities[1 * 3] * store.LinearVelocities[1 * 3]
                + vy * vy
                + store.LinearVelocities[1 * 3 + 2] * store.LinearVelocities[1 * 3 + 2]);
            float pe = g * (store.Positions[1 * 3 + 1] - 8f);
            float e = ke + pe;
            if (tick == 0) e0 = e;
            if (tick > 0) maxJump = MathF.Max(maxJump, e - prevE);
            prevE = e;

            // 杆长不漂移（焊接轴 + Baumgarte 允许的微小弹性）。
            float dx = store.Positions[0] - store.Positions[1 * 3];
            float dy = store.Positions[1] - store.Positions[1 * 3 + 1];
            float dz = store.Positions[2] - store.Positions[1 * 3 + 2];
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            Assert.True(MathF.Abs(dist - 2f) < 0.1f, $"tick {tick}: rod length {dist}");
        }

        Assert.True(maxJump <= 0.02f, $"max single-tick energy jump {maxJump}");
        Assert.True(prevE < 2f, $"final energy {prevE} (E0={e0}) not dissipated");

        // 静止后角度稳定：再走 50 tick，位置变化可忽略且垂在锚点正下方。
        float[] before = (float[])store.Positions.Clone();
        for (int tick = 0; tick < 50; tick++) world.Step(store, Dt, null, constraints, cache);
        for (int k = 0; k < 3; k++)
        {
            Assert.True(MathF.Abs(store.Positions[1 * 3 + k] - before[1 * 3 + k]) < 0.02f,
                $"rest drift axis {k}");
        }
        Assert.True(MathF.Abs(store.Positions[1 * 3 + 0]) < 0.05f, $"rest x {store.Positions[3]}");
        Assert.True(MathF.Abs(store.Positions[1 * 3 + 1] - 8f) < 0.05f, $"rest y {store.Positions[4]}");
        Assert.True(MathF.Abs(store.Positions[1 * 3 + 2]) < 0.05f, $"rest z {store.Positions[5]}");
    }

    /// <summary>
    /// B4 checklist：8 节裙摆链式长链误差验证（纯 float32 精度压力测试）。
    /// 8 个动态体锁轴链式连接，末端踢一脚后自由摆动衰减。
    /// 断言：无 NaN、节距保持、链体垂到解析静止位、全部速度衰减（无爆炸）。
    /// </summary>
    [Fact]
    public void MChain2_EightNodeChainStaysBounded()
    {
        const float link = 0.5f;
        int nodeCount = 8;
        List<RigidBodyDef> defs =
        [
            Def(RigidbodyShape.Sphere, new Vector3(0, 10, 0), new Vector3(0.15f, 0, 0),
                RigidbodyType.Static, group: 1, mask: 0, name: "root"),
        ];
        for (int i = 1; i <= nodeCount; i++)
        {
            defs.Add(Def(RigidbodyShape.Sphere, new Vector3(0, 10 - i * link, 0), new Vector3(0.15f, 0, 0),
                mass: 1f, linearDamping: 0.5f, angularDamping: 0.5f,
                group: 1, mask: 0, name: $"n{i}"));
        }
        List<JointDef> joints = new();
        for (int i = 0; i < nodeCount; i++)
        {
            // 关节位于子节点中心（PMX 链惯例）：frameA = (0,−link,0)，frameB = identity。
            joints.Add(Joint(i, i + 1, new Vector3(0, 10 - (i + 1) * link, 0), name: $"j{i}"));
        }

        SixDofSpringConstraint[] constraints = ConstraintBuilder.BuildConstraints(defs, joints);
        Assert.Equal(nodeCount, constraints.Length);

        RigidBodyStore store = new(defs);
        World world = new(new Vector3(0, -98, 0));
        SolverCache cache = new(constraints);

        // 末端踢一脚，链做多节摆后回落。
        store.LinearVelocities[nodeCount * 3 + 0] = 2f;

        for (int tick = 0; tick < 400; tick++)
            world.Step(store, Dt, null, constraints, cache);

        float[] pos = store.Positions;
        float[] lv = store.LinearVelocities;
        for (int i = 0; i < store.Count * 3; i++)
        {
            Assert.False(float.IsNaN(pos[i]), $"NaN in position[{i}]");
            Assert.False(float.IsInfinity(pos[i]), $"Inf in position[{i}]");
        }

        // 节距保持。PGS 的稳态残差随子链载荷单调放大（对照 reze solver.ts L526
        // 的 target = −err·STOP_ERP·invDt，STOP_ERP=0.45：Baumgarte 每 tick 收敛
        // 45% 误差，10 次迭代的 Gauss-Seidel 残差在连续重力载荷下留出平衡拉伸）。
        // 实测稳态（ kicking 后 400 tick）：L1=0.563（驮 7 节）… L7=0.505（驮 1 节），
        // 这是 reze 本体的忠实行为，容差按其上界 + 余量设定，不得收紧去"修"它。
        for (int i = 0; i < nodeCount; i++)
        {
            float dx = pos[(i + 1) * 3 + 0] - pos[i * 3 + 0];
            float dy = pos[(i + 1) * 3 + 1] - pos[i * 3 + 1];
            float dz = pos[(i + 1) * 3 + 2] - pos[i * 3 + 2];
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            Assert.True(MathF.Abs(dist - link) < 0.08f, $"link {i}: dist {dist}");
        }

        // 链垂正下方：总长 8·link = 4，稳态各节累积拉伸 ≈ 0.27（见上），末端
        // 实测 y ≈ 5.73，容差 0.4 覆盖；x/z 回中。
        float endY = pos[nodeCount * 3 + 1];
        Assert.True(MathF.Abs(endY - (10f - nodeCount * link)) < 0.4f, $"end y {endY}");
        Assert.True(MathF.Abs(pos[nodeCount * 3 + 0]) < 0.3f, $"end x {pos[nodeCount * 3 + 0]}");
        Assert.True(MathF.Abs(pos[nodeCount * 3 + 2]) < 0.3f, $"end z {pos[nodeCount * 3 + 2]}");

        // 全部速度衰减（无能量泵入）。
        for (int i = 1; i <= nodeCount; i++)
        {
            float speed = MathF.Sqrt(
                lv[i * 3] * lv[i * 3] + lv[i * 3 + 1] * lv[i * 3 + 1] + lv[i * 3 + 2] * lv[i * 3 + 2]);
            Assert.True(speed < 0.2f, $"node {i} speed {speed}");
        }
    }

    /// <summary>
    /// M-JOINT-1：强制越限输入（B 绕 X 转 0.5 rad，限位 ±0.2），求解后被钳在限位内。
    /// </summary>
    [Fact]
    public void MJoint1_AngularLimitClampsViolation()
    {
        const float limit = 0.2f;
        List<RigidBodyDef> defs =
        [
            Def(RigidbodyShape.Sphere, new Vector3(0, 5, 0), new Vector3(0.2f, 0, 0),
                RigidbodyType.Static, group: 1, mask: 0, name: "a"),
            Def(RigidbodyShape.Sphere, new Vector3(0, 5, 0), new Vector3(0.2f, 0, 0),
                mass: 1f, group: 1, mask: 0, name: "b"),
        ];
        // X 轴限位 ±0.2，Y/Z 自由。
        JointDef joint = Joint(0, 1, new Vector3(0, 5, 0),
            angularMin: new Vector3(-limit, 1, 1),
            angularMax: new Vector3(limit, -1, -1),
            name: "hinge");
        SixDofSpringConstraint[] constraints = ConstraintBuilder.BuildConstraints(defs, [joint]);

        RigidBodyStore store = new(defs);
        World world = new(new Vector3(0, -98, 0));
        SolverCache cache = new(constraints);

        // 越限输入：绕 X 转 0.5 rad（readout 约定下 ex = −0.5）。
        Quaternion over = QuatMath.FromAxisAngle(1, 0, 0, 0.5f);
        store.Orientations[1 * 4 + 0] = over.X;
        store.Orientations[1 * 4 + 1] = over.Y;
        store.Orientations[1 * 4 + 2] = over.Z;
        store.Orientations[1 * 4 + 3] = over.W;

        float ex0 = RelEulerX(store, 1);
        Assert.True(MathF.Abs(ex0) > limit + 0.2f, $"setup violated angle {ex0}");

        for (int tick = 0; tick < 60; tick++)
            world.Step(store, Dt, null, constraints, cache);

        float ex = RelEulerX(store, 1);
        Assert.True(MathF.Abs(ex) <= limit + 0.03f, $"limit not enforced: {ex}");
        // 确实收敛（不是原地不动）。
        Assert.True(MathF.Abs(ex) <= MathF.Abs(ex0) - 0.2f, $"did not converge: {ex0} -> {ex}");
    }

    /// <summary>
    /// M-FRIC-1：盒置于地面给侧向速度，库仑摩擦减速。μ = √(μ_box·μ_ground)，
    /// 解析停止距离 d = v²/(2μg)，断言实际滑行距离同量级且最终静止、不下陷。
    /// </summary>
    [Fact]
    public void MFric1_BoxStopsWithinCoulombDistance()
    {
        const float v0 = 2f;
        const float boxFriction = 0.25f;
        List<RigidBodyDef> defs =
        [
            Def(RigidbodyShape.Box, new Vector3(0, 0.25f, 0), new Vector3(0.5f, 0.25f, 0.5f),
                mass: 1f, friction: boxFriction, restitution: 0, name: "box"),
            RigidBodyDef.CreateGround(),
        ];
        RigidBodyStore store = new(defs);
        store.GroundIndex = 1;
        World world = new(new Vector3(0, -98, 0));

        store.LinearVelocities[0] = v0;

        for (int tick = 0; tick < 120; tick++)
            world.Step(store, Dt, new ContactPool());

        float mu = MathF.Sqrt(boxFriction * 0.6f); // 地面摩擦 0.6（CreateGround）
        float analyticDistance = v0 * v0 / (2f * mu * 98f);

        float finalVx = store.LinearVelocities[0];
        float x = store.Positions[0];
        Assert.True(MathF.Abs(finalVx) < 0.02f, $"box still moving: vx={finalVx}");
        Assert.True(x > 0.02f, $"box slid too little: {x} (analytic {analyticDistance})");
        Assert.True(x < analyticDistance * 4f, $"box overshot: {x} (analytic {analyticDistance})");
        // 没有下陷/弹飞。
        Assert.True(MathF.Abs(store.Positions[1] - 0.25f) < 0.06f, $"box y {store.Positions[1]}");
    }

    /// <summary>
    /// 确定性 smoke：同配置双跑摆锤 200 tick，位置逐 tick 位相等。
    /// 锁定 rand2 种子每子步重置（reze 语义）——这是 DET-1 的 B4 级前置。
    /// </summary>
    [Fact]
    public void PendulumDoubleRun_BitwiseIdentical()
    {
        float[][] Run()
        {
            List<RigidBodyDef> defs =
            [
                Def(RigidbodyShape.Sphere, new Vector3(0, 10, 0), new Vector3(0.3f, 0, 0),
                    RigidbodyType.Static, group: 1, mask: 0, name: "anchor"),
                Def(RigidbodyShape.Sphere, new Vector3(0, 8, 0), new Vector3(0.3f, 0, 0),
                    mass: 1f, linearDamping: 0.1f, angularDamping: 0.1f, group: 1, mask: 0, name: "bob"),
            ];
            JointDef joint = Joint(0, 1, new Vector3(0, 10, 0), name: "swing");
            SixDofSpringConstraint[] constraints = ConstraintBuilder.BuildConstraints(defs, [joint]);
            RigidBodyStore store = new(defs);
            World world = new(new Vector3(0, -98, 0));
            SolverCache cache = new(constraints);
            store.AngularVelocities[1 * 3 + 2] = 4f;

            float[] trace = new float[200 * 3];
            for (int tick = 0; tick < 200; tick++)
            {
                world.Step(store, Dt, null, constraints, cache);
                trace[tick * 3 + 0] = store.Positions[1 * 3 + 0];
                trace[tick * 3 + 1] = store.Positions[1 * 3 + 1];
                trace[tick * 3 + 2] = store.Positions[1 * 3 + 2];
            }
            return [trace];
        }

        float[][] a = Run();
        float[][] b = Run();
        Assert.Equal(a[0].Length, b[0].Length);
        for (int i = 0; i < a[0].Length; i++)
        {
            Assert.Equal(a[0][i], b[0][i]); // float 位相等
        }
    }
}

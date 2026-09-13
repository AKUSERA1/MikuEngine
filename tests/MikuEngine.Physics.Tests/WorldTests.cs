using System.Numerics;
using MikuEngine.Core.Math;
using MikuEngine.Physics;

namespace MikuEngine.Physics.Tests;

/// <summary>
/// B2 积分层单测：M-FALL-1（自由落体）、M-DAMP-1（阻尼收敛）。
/// reze 的积分器是半隐式欧拉（先 v += g·dt，再 p += v·dt），对连续解析解
/// y(t)=y₀-½gt² 存在已知的 O(g·dt·t) 偏差（v 先更新的阶差），因此
/// M-FALL-1 的"解析解"断言对<strong>离散递推的闭式解</strong>给出——这才是
/// 锁定积分行为的严格形式（重力缩放、积分顺序、dt 处理全部被锁住）。
/// </summary>
public class WorldTests
{
    private const float Dt = 1f / 60f;

    private static RigidBodyStore SingleDynamicSphere(float linearDamping = 0f, float angularDamping = 0f)
    {
        var defs = new List<RigidBodyDef>
        {
            new()
            {
                Name = "ball", EnglishName = "ball",
                Type = RigidbodyType.Dynamic,
                BoneIndex = -1, Group = 0, CollisionMask = 0xffff,
                Shape = RigidbodyShape.Sphere,
                Size = new Vector3(0.5f, 0, 0),
                ShapePosition = new Vector3(0, 10, 0),
                ShapeRotation = Vector3.Zero,
                Mass = 1f,
                LinearDamping = linearDamping,
                AngularDamping = angularDamping,
            },
        };
        return new RigidBodyStore(defs); // 无地面（GroundIndex=-1），B3 接入后也不会产生接触
    }

    /// <summary>
    /// M-FALL-1：单球、重力 (0,-98,0)、无接触、60 tick。
    /// 半隐式欧拉闭式解：v_n = g·dt·n，y_n = y₀ + g·dt²·n(n+1)/2。
    /// 并断言与连续解析解的偏差落在积分器已知偏差界 ½·g·dt·t 之内。
    /// </summary>
    [Fact]
    public void FreeFall_MatchesSemiImplicitClosedForm()
    {
        var store = SingleDynamicSphere();
        var world = new World(new Vector3(0, -98, 0));
        const float g = -98f;
        const int steps = 60;

        for (int i = 0; i < steps; i++)
            world.Step(store, Dt);

        float t = steps * Dt;
        float expectedVy = g * Dt * steps;
        float expectedY = 10f + g * Dt * Dt * (steps * (steps + 1) / 2f);

        float vy = store.LinearVelocities[1];
        float y = store.Positions[1];

        Assert.True(MathF.Abs(vy - expectedVy) <= 1e-3f, $"vy: {vy} vs {expectedVy}");
        Assert.True(MathF.Abs(y - expectedY) <= 1e-3f, $"y: {y} vs {expectedY}");

        // 与连续解析解 y₀+½gt² 的偏差 = ½·g·dt·t ≈ 0.817（半隐式欧拉的阶差，非 bug）
        float continuousY = 10f + 0.5f * g * t * t;
        float deviation = MathF.Abs(y - continuousY);
        float bound = 0.5f * MathF.Abs(g) * Dt * t + 1e-3f;
        Assert.True(deviation <= bound, $"deviation {deviation} > bound {bound}");

        // x/z 无水平受力，保持原位
        Assert.Equal(0f, store.Positions[0]);
        Assert.Equal(0f, store.Positions[2]);
        // 逐帧闭式解抽查（第 1、2 步的递推形态）
        var store2 = SingleDynamicSphere();
        var world2 = new World(new Vector3(0, -98, 0));
        world2.Step(store2, Dt);
        Assert.Equal(g * Dt, store2.LinearVelocities[1], 5);
        Assert.Equal(10f + g * Dt * Dt, store2.Positions[1], 5);
        world2.Step(store2, Dt);
        Assert.Equal(2f * g * Dt, store2.LinearVelocities[1], 5);
        Assert.Equal(10f + g * Dt * Dt * 3f, store2.Positions[1], 5);
    }

    /// <summary>
    /// M-DAMP-1：阻尼收敛。线性/角阻尼因子 = pow(max(0,1-damping), dt) 每步乘上，
    /// n 步后速度 = v₀·pow(1-d, n·dt)。零重力隔离重力项。
    /// </summary>
    [Fact]
    public void Damping_VelocityDecayFollowsStoreParameters()
    {
        var store = SingleDynamicSphere(linearDamping: 0.1f, angularDamping: 0.5f);
        store.LinearVelocities[0] = 3f;   // 水平初速，避开重力方向
        store.AngularVelocities[1] = 2f;

        var world = new World(Vector3.Zero);
        const int steps = 120;

        for (int i = 0; i < steps; i++)
            world.Step(store, Dt);

        float linFactor = MathF.Pow(1f - 0.1f, steps * Dt);
        float angFactor = MathF.Pow(1f - 0.5f, steps * Dt);

        float expectedVx = 3f * linFactor;
        float expectedWy = 2f * angFactor;
        Assert.True(MathF.Abs(store.LinearVelocities[0] - expectedVx) <= 1e-4f,
            $"vx: {store.LinearVelocities[0]} vs {expectedVx}");
        Assert.True(MathF.Abs(store.AngularVelocities[1] - expectedWy) <= 1e-4f,
            $"wy: {store.AngularVelocities[1]} vs {expectedWy}");

        // 收敛方向：速度单调衰减
        Assert.True(MathF.Abs(store.LinearVelocities[0]) < 3f);
        Assert.True(MathF.Abs(store.AngularVelocities[1]) < 2f);
    }

    /// <summary>静态体不参与预测/积分；速度上限护栏（5 units/step）与角速度上限（π/2/step）。</summary>
    [Fact]
    public void Step_SkipsStatic_And_CapsVelocities()
    {
        var defs = new List<RigidBodyDef>
        {
            new()
            {
                Name = "anchor", EnglishName = "anchor",
                Type = RigidbodyType.Static, BoneIndex = -1, Group = 0, CollisionMask = 0xffff,
                Shape = RigidbodyShape.Box, Size = new Vector3(0.5f, 0.5f, 0.5f),
                ShapePosition = new Vector3(0, 0, 0), ShapeRotation = Vector3.Zero, Mass = 0f,
            },
            new()
            {
                Name = "ball", EnglishName = "ball",
                Type = RigidbodyType.Dynamic, BoneIndex = -1, Group = 0, CollisionMask = 0xffff,
                Shape = RigidbodyShape.Sphere, Size = new Vector3(0.5f, 0, 0),
                ShapePosition = new Vector3(100, 0, 0), ShapeRotation = Vector3.Zero, Mass = 1f,
            },
        };
        var store = new RigidBodyStore(defs);
        store.LinearVelocities[1 * 3 + 0] = 10000f; // 远超 5 units/step 护栏
        store.AngularVelocities[1 * 3 + 1] = 1000f; // 远超 π/2/step 护栏

        var world = new World(Vector3.Zero);
        world.Step(store, Dt);

        // 静态体不动
        Assert.Equal(0f, store.Positions[0]);
        Assert.Equal(0f, store.LinearVelocities[0]);

        // 动态体位置增量被钳到 5 units/step
        Assert.Equal(100f + 5f, store.Positions[1 * 3 + 0], 3);
        // 角速度被钳到 π/2/dt 以内
        float wy = store.AngularVelocities[1 * 3 + 1];
        Assert.True(wy * Dt <= MathF.PI * 0.5f + 1e-4f, $"wy*dt={wy * Dt}");
    }
}

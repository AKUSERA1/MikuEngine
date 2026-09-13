using System.Numerics;
using MikuEngine.Core.Math;
using MikuEngine.Physics;
using Xunit;

namespace MikuEngine.Physics.Tests;

/// <summary>
/// B5 骨骼同步层单测：M-KIN-1（kinematic 跟随）、M-TEL-1（teleport carry 与
/// 阈值语义）、M-RESET-1（reset 三件事）、M-NAN-1（NaN gate + restore backstop）。
/// 断言为解析几何/状态判据，无外部黄金值——reze 行为规格由逐行移植保证。
/// 每帧驱动顺序与方案 B6 一致：SetKinematicTargets → Step(dt) → WriteBack。
/// </summary>
public class SyncLayerTests
{
    private const float Dt = 1f / 60f;

    /// <summary>
    /// 两骨摆锤 rig：bone0 挂静态体 A（shapePos (0,5,0)），bone1 挂动态体 B
    /// （shapePos (0,3,0)，A 下方 2 单位、地面 y=0 上方）。inverse binds 全
    /// identity → bodyOffset = T(shapePos)，snap 位置 = bone 平移 + shapePos。
    /// A→B 关节在 B 中心：线性轴焊接、角度轴自由（M-CHAIN-1 同构摆锤）。
    /// 碰撞掩码 0：本组用例测同步层，不测碰撞。
    /// </summary>
    private static (MMDPhysics Ph, float[] BoneWorld, float[] BoneInvBind) CreatePendulumRig()
    {
        List<RigidBodyDef> defs =
        [
            new RigidBodyDef
            {
                Name = "A", EnglishName = "A", BoneIndex = 0, Group = 1, CollisionMask = 0,
                Shape = RigidbodyShape.Sphere, Size = new Vector3(0.3f, 0, 0),
                ShapePosition = new Vector3(0, 5, 0), ShapeRotation = Vector3.Zero,
                Mass = 0f, Type = RigidbodyType.Static,
                LinearDamping = 0, AngularDamping = 0, Restitution = 0.2f, Friction = 0.5f,
            },
            new RigidBodyDef
            {
                Name = "B", EnglishName = "B", BoneIndex = 1, Group = 1, CollisionMask = 0,
                Shape = RigidbodyShape.Sphere, Size = new Vector3(0.3f, 0, 0),
                ShapePosition = new Vector3(0, 3, 0), ShapeRotation = Vector3.Zero,
                Mass = 1f, Type = RigidbodyType.Dynamic,
                LinearDamping = 0.4f, AngularDamping = 0.4f, Restitution = 0.2f, Friction = 0.5f,
            },
        ];
        List<JointDef> joints =
        [
            new JointDef
            {
                Name = "j", EnglishName = "j", Type = 0,
                RigidbodyIndexA = 0, RigidbodyIndexB = 1,
                Position = new Vector3(0, 3, 0), Rotation = Vector3.Zero,
                PositionMin = Vector3.Zero, PositionMax = Vector3.Zero,
                RotationMin = new Vector3(1, 1, 1), RotationMax = new Vector3(-1, -1, -1),
                SpringPosition = Vector3.Zero, SpringRotation = Vector3.Zero,
            },
        ];
        MMDPhysics ph = new(defs, joints);
        float[] boneWorld = new float[2 * 16];
        float[] boneInvBind = new float[2 * 16];
        for (int b = 0; b < 2; b++) Mat4.SetIdentity(boneInvBind, b * 16);
        SetBoneTranslation(boneWorld, 0, 0, 0, 0);
        SetBoneTranslation(boneWorld, 1, 0, 0, 0);
        return (ph, boneWorld, boneInvBind);
    }

    private static void SetBoneTranslation(float[] bones, int b, float x, float y, float z)
    {
        Mat4.SetIdentity(bones, b * 16);
        bones[b * 16 + 12] = x;
        bones[b * 16 + 13] = y;
        bones[b * 16 + 14] = z;
    }

    /// <summary>方案 B6 的每帧顺序（S1 gate 在 B6 插桩）。</summary>
    private static void Frame(MMDPhysics ph, float[] boneWorld, float[] boneInvBind, float dt)
    {
        ph.SetKinematicTargets(boneWorld, boneInvBind, dt);
        ph.Step(dt);
        ph.WriteBack(boneWorld);
    }

    private static void AssertFinite(string label, float[] a)
    {
        for (int i = 0; i < a.Length; i++)
            Assert.True(float.IsFinite(a[i]), $"{label}[{i}] not finite: {a[i]}");
    }

    // =========================================================================
    // M-KIN-1 kinematic 跟随：骨骼矩阵匀速平移 → 体位置跟随、动态子体获得
    // 传递速度；匀速运动不得误触发 teleport（阈值语义的下半边）。
    // =========================================================================

    [Fact]
    public void MKin1_KinematicFollowsAndDragsDynamicChild()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = CreatePendulumRig();
        RigidBodyStore store = ph.Store;

        // 首帧初始化（reze firstFrame 块）。
        Frame(ph, bw, bib, Dt);
        Assert.Equal(0, ph.TeleportCount);

        // 120 帧 × 1 unit/s 匀速 +x：每帧目标位移 Dt。
        for (int n = 1; n <= 120; n++)
        {
            SetBoneTranslation(bw, 0, n * Dt, 0, 0);
            Frame(ph, bw, bib, Dt);
        }

        // A（static 跟随体）逐子步落到目标 f=1 → 位置精确 == bone+shapePos。
        float ax = store.Positions[0], ay = store.Positions[1], az = store.Positions[2];
        Assert.True(MathF.Abs(ax - 2f) < 1e-3f, $"A x {ax}");
        Assert.True(MathF.Abs(ay - 5f) < 1e-3f, $"A y {ay}");
        Assert.True(MathF.Abs(az) < 1e-3f, $"A z {az}");

        // B（动态子体）被关节拖到新锚点下方，并获得传递速度 ≈ 1 unit/s。
        float bx = store.Positions[3], by = store.Positions[4], bz = store.Positions[5];
        Assert.True(MathF.Abs(bx - 2f) < 0.25f, $"B x {bx}");
        Assert.True(MathF.Abs(by - 3f) < 0.25f, $"B y {by}");
        Assert.True(MathF.Abs(bz) < 0.25f, $"B z {bz}");
        float lvx = store.LinearVelocities[3], lvy = store.LinearVelocities[4], lvz = store.LinearVelocities[5];
        Assert.True(lvx > 0.6f && lvx < 1.4f, $"B lvx {lvx} (expect ≈1)");
        Assert.True(MathF.Abs(lvy) < 0.5f, $"B lvy {lvy}");
        Assert.True(MathF.Abs(lvz) < 0.3f, $"B lvz {lvz}");

        // 匀速运动从不触发 teleport（0.0167 units/帧 ≪ 阈值 4.17）。
        Assert.Equal(0, ph.TeleportCount);
    }

    // =========================================================================
    // M-TEL-1 teleport carry：目标突变 > 阈值（dt=1/60 → max(4, 250/60)=4.17）
    // → 根体 snap、子树刚体随动（刚性搬移 + 动量清零）、无速度爆炸；
    // 阈下突变（3 units）不得触发。
    // =========================================================================

    [Fact]
    public void MTel1_TeleportCarriesSubtreeAndSubThresholdDoesNotTrigger()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = CreatePendulumRig();
        RigidBodyStore store = ph.Store;

        Frame(ph, bw, bib, Dt);
        for (int n = 0; n < 30; n++) Frame(ph, bw, bib, Dt);
        Assert.Equal(0, ph.TeleportCount);

        float b0x = store.Positions[3], b0y = store.Positions[4], b0z = store.Positions[5];

        // 瞬移 +10 x（10 > 4.17）。
        SetBoneTranslation(bw, 0, 10f, 0, 0);
        Frame(ph, bw, bib, Dt);

        Assert.Equal(1, ph.TeleportCount);

        // 根体 snap 到目标。
        Assert.True(MathF.Abs(store.Positions[0] - 10f) < 1e-4f, $"A x {store.Positions[0]}");

        // 子树随动：纯平移 → B 平移同样 delta（刚性搬移）。
        Assert.True(MathF.Abs(store.Positions[3] - (b0x + 10f)) < 0.05f,
            $"B carried x {store.Positions[3]} vs {b0x + 10f}");
        Assert.True(MathF.Abs(store.Positions[4] - b0y) < 0.05f, $"B carried y {store.Positions[4]}");
        Assert.True(MathF.Abs(store.Positions[5] - b0z) < 0.05f, $"B carried z {store.Positions[5]}");

        // 动量清零：carry 后一个子步内无速度爆炸。
        float speed = MathF.Sqrt(
            store.LinearVelocities[3] * store.LinearVelocities[3] +
            store.LinearVelocities[4] * store.LinearVelocities[4] +
            store.LinearVelocities[5] * store.LinearVelocities[5]);
        Assert.True(speed < 0.5f, $"post-teleport speed {speed}");

        // 继续模拟回落稳定，计数不再增长。
        for (int n = 0; n < 30; n++) Frame(ph, bw, bib, Dt);
        Assert.Equal(1, ph.TeleportCount);
        Assert.True(MathF.Abs(store.Positions[3] - 10f) < 0.25f, $"settled B x {store.Positions[3]}");
        Assert.True(MathF.Abs(store.Positions[4] - 3f) < 0.25f, $"settled B y {store.Positions[4]}");
    }

    [Fact]
    public void MTel1_SubThresholdJumpKeepsMomentum()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = CreatePendulumRig();
        Frame(ph, bw, bib, Dt);
        for (int n = 0; n < 10; n++) Frame(ph, bw, bib, Dt);

        // 3 units 一帧（< 4.17 阈值）：是"快速运动"不是瞬移——不触发 carry，
        // 不清动量，锚点以 tv = 3/dt 拖动链。
        SetBoneTranslation(bw, 0, 3f, 0, 0);
        Frame(ph, bw, bib, Dt);

        Assert.Equal(0, ph.TeleportCount);
        AssertFinite("state", ph.Store.Positions);
        AssertFinite("velocities", ph.Store.LinearVelocities);
    }

    // =========================================================================
    // M-RESET-1 reset 等价初态：乱序模拟后 reset → 所有位置 == boneWorld×offset、
    // 速度 == 0、kinematic target 已 reseed（reseed 用行为判据锁定：reset 后
    // 同姿态再走一帧，若 target 未 reseed，陈旧 target 差会以轨迹速度拖动 B）。
    // =========================================================================

    [Fact]
    public void MReset1_ResetRestoresSnappedInitialState()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = CreatePendulumRig();
        RigidBodyStore store = ph.Store;

        Frame(ph, bw, bib, Dt);
        // 乱序模拟：骨骼往复运动 90 帧，B 带上速度。
        for (int n = 1; n <= 90; n++)
        {
            SetBoneTranslation(bw, 0, 0.5f * MathF.Sin(n * Dt * 6f), 0, 0);
            Frame(ph, bw, bib, Dt);
        }
        Assert.NotEqual(0f, store.LinearVelocities[3]); // 前置：B 确实在动

        // reset 到新姿态 P0（bone1 随 bone0 平移，保持 B 在 A 下方 2 单位）。
        SetBoneTranslation(bw, 0, 0.7f, 0.3f, -0.2f);
        SetBoneTranslation(bw, 1, 0.7f, 0.3f, -0.2f);
        ph.Reset(bw);

        // 1) 位置 == boneWorld × bodyOffset（identity bind → bone 平移 + shapePos）。
        Assert.True(MathF.Abs(store.Positions[0] - 0.7f) < 1e-4f, $"A x {store.Positions[0]}");
        Assert.True(MathF.Abs(store.Positions[1] - 5.3f) < 1e-4f, $"A y {store.Positions[1]}");
        Assert.True(MathF.Abs(store.Positions[2] + 0.2f) < 1e-4f, $"A z {store.Positions[2]}");
        Assert.True(MathF.Abs(store.Positions[3] - 0.7f) < 1e-4f, $"B x {store.Positions[3]}");
        Assert.True(MathF.Abs(store.Positions[4] - 3.3f) < 1e-4f, $"B y {store.Positions[4]}");
        Assert.True(MathF.Abs(store.Positions[5] + 0.2f) < 1e-4f, $"B z {store.Positions[5]}");

        // 2) 速度清零。
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(0f, store.LinearVelocities[i]);
            Assert.Equal(0f, store.AngularVelocities[i]);
        }

        // 3) kinematic target 已 reseed：同姿态再走一帧。若未 reseed，陈旧
        //    target（往复运动末帧位置）与本帧目标之差会产生非零轨迹速度并
        //    通过关节拖动 B；reseed 后 tv=0，B 保持静止。
        Frame(ph, bw, bib, Dt);
        Assert.True(MathF.Abs(store.LinearVelocities[3]) < 0.05f,
            $"target not reseeded: B lvx {store.LinearVelocities[3]}");
        Assert.True(MathF.Abs(store.Positions[3] - 0.7f) < 0.05f,
            $"B drifted after reset frame: {store.Positions[3]}");
        // 同姿态也不触发 teleport。
        Assert.Equal(0, ph.TeleportCount);
    }

    // =========================================================================
    // M-NAN-1 NaN gate：注入极端数值 → 写回被丢弃，骨骼矩阵无 NaN。
    // 两层防线分别锁定：restore backstop（子步内爆点从上一健康子步恢复）与
    // WriteBack gate（m[0] 非有限/超界 → 静默丢弃该骨骼更新）。
    // =========================================================================

    [Fact]
    public void MNan1_WriteBackGateDropsPoisonedBody()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = CreatePendulumRig();
        RigidBodyStore store = ph.Store;

        Frame(ph, bw, bib, Dt);
        for (int n = 0; n < 10; n++) Frame(ph, bw, bib, Dt);

        // 记录 B 骨骼矩阵的当前（健康）写回值。
        float[] healthy = new float[16];
        Array.Copy(bw, 16, healthy, 0, 16);

        // 直接污染动态体 B 的姿态（模拟解算爆点后的残留态），走 WriteBack。
        for (int k = 0; k < 4; k++) store.Orientations[4 + k] = float.NaN;
        store.Positions[3] = float.PositiveInfinity;

        ph.WriteBack(bw);

        // gate（isFinite(m[0]) && |m[0]|<1e6）丢弃 B 的更新 → 骨骼矩阵保持
        // 上一次的健康值，无 NaN 扩散。
        for (int k = 0; k < 16; k++)
            Assert.Equal(healthy[k], bw[16 + k]);
    }

    [Fact]
    public void MNan1_InjectedNaNDoesNotReachBoneMatrices()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = CreatePendulumRig();
        RigidBodyStore store = ph.Store;

        Frame(ph, bw, bib, Dt);
        for (int n = 0; n < 10; n++) Frame(ph, bw, bib, Dt);

        float[] healthy = new float[16];
        Array.Copy(bw, 16, healthy, 0, 16);

        // 帧间注入 NaN（时间轴拖动/外部篡改形态）：B 的全部状态槽。
        for (int k = 0; k < 3; k++) store.Positions[3 + k] = float.NaN;
        for (int k = 0; k < 4; k++) store.Orientations[4 + k] = float.NaN;
        for (int k = 0; k < 3; k++) store.LinearVelocities[3 + k] = float.NaN;
        for (int k = 0; k < 3; k++) store.AngularVelocities[3 + k] = float.NaN;

        Frame(ph, bw, bib, Dt);

        // 静态体 A 未被污染（invMass=0 的体不参与冲量交换，NaN 不跨体传播）。
        AssertFinite("A position", new float[] { store.Positions[0], store.Positions[1], store.Positions[2] });

        // 骨骼矩阵无 NaN：B 的写回被 gate 丢弃，保持上一次健康值。
        for (int k = 0; k < 16; k++)
        {
            Assert.True(float.IsFinite(bw[k]), $"boneWorld[{k}] not finite");
            Assert.True(float.IsFinite(bw[16 + k]), $"boneWorld[16+{k}] not finite");
        }
        for (int k = 0; k < 16; k++)
            Assert.Equal(healthy[k], bw[16 + k]);

        // 后续帧继续运行不崩溃、bone 0（动画驱动）不受影响。
        for (int n = 0; n < 5; n++) Frame(ph, bw, bib, Dt);
        AssertFinite("boneWorld after 5 more frames", bw);
    }

    // =========================================================================
    // mode-2 pinned（WriteBack checklist 项）：Dynamic+Aligned 体直接挂在
    // bone-follow 体下 → 位置每帧重钉到动画骨骼（store 精确相等），旋转保持
    // 模拟；骨骼矩阵只写旋转列、平移保留动画 FK 值。
    // =========================================================================

    [Fact]
    public void PinnedBody_PositionRepinsToBone_RotationStaysSimulated()
    {
        List<RigidBodyDef> defs =
        [
            new RigidBodyDef
            {
                Name = "A", EnglishName = "A", BoneIndex = 0, Group = 1, CollisionMask = 0,
                Shape = RigidbodyShape.Sphere, Size = new Vector3(0.3f, 0, 0),
                ShapePosition = new Vector3(0, 5, 0), ShapeRotation = Vector3.Zero,
                Mass = 0f, Type = RigidbodyType.Static,
                LinearDamping = 0, AngularDamping = 0, Restitution = 0.2f, Friction = 0.5f,
            },
            new RigidBodyDef
            {
                Name = "P", EnglishName = "P", BoneIndex = 1, Group = 1, CollisionMask = 0,
                Shape = RigidbodyShape.Sphere, Size = new Vector3(0.3f, 0, 0),
                ShapePosition = new Vector3(0, 3, 0), ShapeRotation = Vector3.Zero,
                Mass = 1f, Type = RigidbodyType.Dynamic, Aligned = true,
                LinearDamping = 0.4f, AngularDamping = 0.2f, Restitution = 0.2f, Friction = 0.5f,
            },
        ];
        List<JointDef> joints =
        [
            new JointDef
            {
                Name = "j", EnglishName = "j", Type = 0,
                RigidbodyIndexA = 0, RigidbodyIndexB = 1,
                Position = new Vector3(0, 3, 0), Rotation = Vector3.Zero,
                PositionMin = Vector3.Zero, PositionMax = Vector3.Zero,
                RotationMin = new Vector3(1, 1, 1), RotationMax = new Vector3(-1, -1, -1),
                SpringPosition = Vector3.Zero, SpringRotation = Vector3.Zero,
            },
        ];
        MMDPhysics ph = new(defs, joints);
        float[] bw = new float[2 * 16];
        float[] bib = new float[2 * 16];
        for (int b = 0; b < 2; b++) Mat4.SetIdentity(bib, b * 16);
        SetBoneTranslation(bw, 0, 0, 0, 0);
        SetBoneTranslation(bw, 1, 0, 0, 0);
        RigidBodyStore store = ph.Store;

        Frame(ph, bw, bib, Dt);

        // 给 P 自旋（绕 x），让模拟旋转偏离动画姿态（全 identity）。
        store.AngularVelocities[1 * 3 + 0] = 6f;

        for (int n = 1; n <= 40; n++)
        {
            SetBoneTranslation(bw, 0, n * Dt * 0.5f, 0, 0);
            SetBoneTranslation(bw, 1, n * Dt * 0.5f, 0, 0);
            Frame(ph, bw, bib, Dt);
        }

        // 最后一帧手动拆相位：WriteBack 的 alpha=0（dt 恰为一子步）语义写的是
        // **上一子步**姿态（reze Fix-Your-Timestep 的一帧延迟），因此期望旋转
        // 取本帧 Step 前的 store 朝向（= 子步开始时 SavePrevState 捕获的 prev）。
        float[] oriBefore = new float[4];
        Array.Copy(store.Orientations, 4, oriBefore, 0, 4);
        SetBoneTranslation(bw, 0, 41f * Dt * 0.5f, 0, 0);
        SetBoneTranslation(bw, 1, 41f * Dt * 0.5f, 0, 0);
        ph.SetKinematicTargets(bw, bib, Dt);
        ph.Step(Dt);
        ph.WriteBack(bw);

        // store 位置精确重钉到 boneWorld1 × offset = bone 平移 + shapePos
        // （AlignPinnedBodiesToBones 是矩阵直拷，非积分值）。
        float px = store.Positions[1 * 3 + 0], py = store.Positions[1 * 3 + 1], pz = store.Positions[1 * 3 + 2];
        Assert.True(MathF.Abs(px - (41f * Dt * 0.5f)) < 1e-4f, $"pinned x {px}");
        Assert.True(MathF.Abs(py - 3f) < 1e-4f, $"pinned y {py}");
        Assert.True(MathF.Abs(pz) < 1e-4f, $"pinned z {pz}");

        // 旋转确有模拟值（自旋衰减后仍非 identity）。
        float qx = oriBefore[0], qy = oriBefore[1], qz = oriBefore[2], qw = oriBefore[3];
        Assert.True(MathF.Abs(qx) > 1e-3f, $"simulated rotation is identity: q=({qx},{qy},{qz},{qw})");

        // 骨骼矩阵：平移保留动画 FK 值（写回未触碰 v[12..14]），旋转列 ==
        // R(模拟姿态)（bodyWorld × offset⁻¹ 的旋转块 = R(ori)）。
        float animX = 41f * Dt * 0.5f;
        Assert.True(MathF.Abs(bw[1 * 16 + 12] - animX) < 1e-4f, $"bone translation x {bw[1 * 16 + 12]}");
        Assert.True(MathF.Abs(bw[1 * 16 + 13]) < 1e-4f, $"bone translation y {bw[1 * 16 + 13]}");
        float[] expectedRot = new float[16];
        Mat4.FromQuatInto(qx, qy, qz, qw, expectedRot, 0);
        // 列主序旋转块：v[0..2], v[4..6], v[8..10]。
        for (int col = 0; col < 3; col++)
        {
            for (int row = 0; row < 3; row++)
            {
                float actual = bw[1 * 16 + col * 4 + row];
                float expected = expectedRot[col * 4 + row];
                Assert.True(MathF.Abs(actual - expected) < 1e-4f,
                    $"bone rot[{row},{col}]: {actual} vs {expected}");
            }
        }
        // 齐次行未破坏。
        Assert.Equal(1f, bw[1 * 16 + 15]);
        Assert.Equal(0f, bw[1 * 16 + 3]);
        Assert.Equal(0f, bw[1 * 16 + 7]);
        Assert.Equal(0f, bw[1 * 16 + 11]);
    }

    // =========================================================================
    // M-FLOOR-1 setFloor 开关位：关 → 地面 plane pass 跳过，自由动态球穿透
    // y=0 继续下落；开 → 球停在 y≈半径；中途重开 → 球被接住（体常驻 store、
    // GroundIndex 翻转不扰动求解器索引——reze 注释的保留语义）。
    // =========================================================================

    /// <summary>单自由动态球，空骨骼数组驱动：纯重力 + 地面。阻尼 0.4 与摆锤
    /// rig 一致——M-JIGGLE-2 复用此 rig 测 authored 衰减。B 挂 identity 假骨
    /// 0：SetJiggleDamping 按骨索引匹配，无骨体匹配不到；假骨全 identity，
    /// snap/写回落回原 shapePos，对下落与地面判据零影响。</summary>
    private static (MMDPhysics Ph, float[] BoneWorld, float[] BoneInvBind) CreateFreeFallRig()
    {
        List<RigidBodyDef> defs =
        [
            new RigidBodyDef
            {
                Name = "B", EnglishName = "B", BoneIndex = 0, Group = 1, CollisionMask = 1,
                Shape = RigidbodyShape.Sphere, Size = new Vector3(0.3f, 0, 0),
                ShapePosition = new Vector3(0, 5, 0), ShapeRotation = Vector3.Zero,
                Mass = 1f, Type = RigidbodyType.Dynamic,
                LinearDamping = 0.4f, AngularDamping = 0.4f, Restitution = 0f, Friction = 0.5f,
            },
        ];
        MMDPhysics ph = new(defs, null);
        float[] bw = new float[16];
        float[] bib = new float[16];
        Mat4.SetIdentity(bib, 0);
        Mat4.SetIdentity(bw, 0);
        return (ph, bw, bib);
    }

    [Fact]
    public void MFloor1_SetFloorTogglesPlanePass()
    {
        // 开（默认 MMD 行为常开）：60 帧 → 球停在地面 y≈半径（r=0.3 + 接触校正）。
        (MMDPhysics phOn, float[] bw1, float[] bib1) = CreateFreeFallRig();
        for (int n = 0; n < 60; n++) Frame(phOn, bw1, bib1, Dt);
        float yOn = phOn.Store.Positions[1];
        Assert.True(yOn > 0.15f && yOn < 0.6f, $"floor-on resting y {yOn}");

        // 关：60 帧 → 穿透 y=0 继续下落（自由落体 ≈ 5 − ½·98·1² = −44）。
        (MMDPhysics phOff, float[] bw2, float[] bib2) = CreateFreeFallRig();
        phOff.SetFloor(false);
        for (int n = 0; n < 60; n++) Frame(phOff, bw2, bib2, Dt);
        float yOff = phOff.Store.Positions[1];
        Assert.True(yOff < 0f, $"floor-off fallen y {yOff}");

        // 中途重开：先关 20 帧（y ≈ 5 − ½·98·(1/3)² ≈ 1.9，未穿），再开 60 帧
        // → 球被接住。开关只翻 GroundIndex 位，体常驻 store，无索引扰动。
        (MMDPhysics phToggle, float[] bw3, float[] bib3) = CreateFreeFallRig();
        phToggle.SetFloor(false);
        for (int n = 0; n < 20; n++) Frame(phToggle, bw3, bib3, Dt);
        phToggle.SetFloor(true);
        for (int n = 0; n < 60; n++) Frame(phToggle, bw3, bib3, Dt);
        float yToggle = phToggle.Store.Positions[1];
        Assert.True(yToggle > 0.15f && yToggle < 0.6f, $"re-enabled resting y {yToggle}");
    }

    // =========================================================================
    // M-DAMP-1 setJiggleDamping（reserved，S3 配套）：scale 乘 authored 值、
    // 幂等（快照后 set 而非 compound）、按 boneIndices 过滤、空集无操作；
    // 行为判据（M-DAMP-2）：scale=0 摆动无衰减，authored 阻尼显著耗散。
    // =========================================================================

    [Fact]
    public void MJiggle1_DampingScalesAuthoredValuesIdempotently()
    {
        (MMDPhysics ph, float[] bw, float[] bib) = CreatePendulumRig();
        RigidBodyStore store = ph.Store;

        // 半阻尼：authored 0.4 → 0.2。
        ph.SetJiggleDamping([1], 0.5f);
        Assert.Equal(0.2f, store.LinearDamping[1], 3f);
        Assert.Equal(0.2f, store.AngularDamping[1], 3f);

        // 幂等：重复调用基于 authored 快照 set，而不是在当前值上 compound。
        ph.SetJiggleDamping([1], 0.5f);
        Assert.Equal(0.2f, store.LinearDamping[1], 3f);

        // 恢复：scale=1 回到 authored 0.4。
        ph.SetJiggleDamping([1], 1f);
        Assert.Equal(0.4f, store.LinearDamping[1], 3f);
        Assert.Equal(0.4f, store.AngularDamping[1], 3f);

        // 过滤：只碰指定骨骼的体。bone 0 的体（静态 A）与其它体不受影响；
        // scale=0 后 B 无阻尼，A 保持 authored。
        ph.SetJiggleDamping([1], 0f);
        Assert.Equal(0f, store.LinearDamping[1], 3f);
        Assert.Equal(0f, store.LinearDamping[0], 3f);

        // 空集：无操作（含 authored 快照未建立的路径）。
        ph.SetJiggleDamping(Array.Empty<int>(), 0.5f);
        Assert.Equal(0f, store.LinearDamping[1], 3f);

        // GetPhysicsDrivenBones：动态体 + 真实骨 → [1]（静态 A 不在列）。
        List<int> driven = ph.GetPhysicsDrivenBones();
        Assert.Single(driven);
        Assert.Equal(1, driven[0]);
    }

    [Fact]
    public void MJiggle2_ZeroDampingKeepsVelocityAuthoredDampingDecaysIt()
    {
        // 判据取自由体 x 分量（重力沿 -y，x 轴唯一作用项是阻尼；关地面避免
        // 接触摩擦干扰，落点碰地前结束）。World 每子步 v *= (1-d)^dt，60 帧
        // 恰好 60 子步 → 总因子 (1-d)^1：authored 0.4 → vx=6，scale=0 → vx=10。
        (MMDPhysics phDamped, float[] bw1, float[] bib1) = CreateFreeFallRig();
        (MMDPhysics phFree, float[] bw2, float[] bib2) = CreateFreeFallRig();
        phFree.SetFloor(false);
        phDamped.SetFloor(false);
        phFree.SetJiggleDamping([0], 0f);

        Frame(phDamped, bw1, bib1, Dt);
        Frame(phFree, bw2, bib2, Dt);
        // FreeFall rig：B 是 store 体 0（地面追加在尾）→ lv[0..2]。
        phDamped.Store.LinearVelocities[0] = 10f;
        phFree.Store.LinearVelocities[0] = 10f;

        for (int n = 0; n < 60; n++)
        {
            Frame(phDamped, bw1, bib1, Dt);
            Frame(phFree, bw2, bib2, Dt);
        }
        float vxDamped = phDamped.Store.LinearVelocities[0];
        float vxFree = phFree.Store.LinearVelocities[0];
        Assert.True(MathF.Abs(vxDamped - 6f) < 0.5f, $"authored vx {vxDamped} (expect 10·0.6=6)");
        Assert.True(MathF.Abs(vxFree - 10f) < 0.05f, $"zero-damped vx {vxFree} (expect 10, no damping)");
    }
}

using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// 物理后付与（PostPhysicsAppend / S3）测试：
///   * 拓扑预计算（<see cref="SkeletalModel.SetPhysicsDrivenBones"/>）：不动点闭包、
///     物理驱动骨排除、父边与付与边传播、无拓扑早退；
///   * <see cref="SkeletalModel.ApplyPhysicsAppend"/>：付与链消费模拟后的世界矩阵
///     （旋转 / 平移 / 父边传播）、物理驱动骨本体不被重算、幂等。
///
/// 全部走「合成 PmxModel → SkeletalModelConverter」完整链路，与真实模型同构。
/// 测试骨架（绑定位置全在 Y 轴 / X 轴上，便于手算期望值）：
/// <code>
///   0 物理骨     (0,0,0) 根                 —— 被物理覆写的骨
///   1 付与子     (0,5,0) 根，付与旋转 ← 0 (ratio 1)
///   2 普通子     (0,8,0) 父 = 1             —— 靠父边进入受影响集
///   3 付与移動子 (2,0,0) 根，付与移动 ← 0 (ratio 1)
/// </code>
/// </summary>
public class SkeletalModelPhysicsAppendTests
{
    // ---------------------------------------------------------------- 合成 PMX

    private static PmxHeader Header() => new()
    {
        Signature = "PMX",
        Version = 2.0f,
        Encoding = PmxEncoding.Utf8,
        AdditionalVec4Count = 0,
        VertexIndexSize = 4,
        TextureIndexSize = 4,
        MaterialIndexSize = 4,
        BoneIndexSize = 4,
        MorphIndexSize = 4,
        RigidBodyIndexSize = 4,
        ModelName = "test",
        EnglishModelName = "test",
        Comment = "",
        EnglishComment = "",
    };

    private const PmxBoneFlag BaseFlag =
        PmxBoneFlag.IsRotatable | PmxBoneFlag.IsMovable | PmxBoneFlag.IsVisible | PmxBoneFlag.IsControllable;

    private static PmxBone Bone(string name, int parent, Vector3 pos,
        PmxBoneFlag flag = BaseFlag, PmxAppendTransform? append = null) => new()
    {
        Name = name,
        EnglishName = name,
        Position = pos,
        ParentBoneIndex = parent,
        TransformOrder = 0,
        Flag = flag,
        TailBoneIndex = -1,
        TailPosition = Vector3.Zero,
        IsTailBoneIndex = false,
        AppendTransform = append,
    };

    private static SkeletalModel Build()
    {
        var pmx = new PmxModel
        {
            Header = Header(),
            Vertices = [],
            Indices = [],
            Textures = [],
            Materials = [],
            Bones =
            [
                Bone("物理骨", -1, new Vector3(0, 0, 0)),
                Bone("付与子", -1, new Vector3(0, 5, 0),
                    flag: BaseFlag | PmxBoneFlag.HasAppendRotate,
                    append: new PmxAppendTransform { ParentIndex = 0, Ratio = 1f }),
                Bone("普通子", 1, new Vector3(0, 8, 0)),
                Bone("付与移動子", -1, new Vector3(2, 0, 0),
                    flag: BaseFlag | PmxBoneFlag.HasAppendMove,
                    append: new PmxAppendTransform { ParentIndex = 0, Ratio = 1f }),
            ],
            Morphs = [],
            DisplayFrames = [],
            RigidBodies = [],
            Joints = [],
            SoftBodies = [],
        };
        return SkeletalModelConverter.Convert(pmx);
    }

    // ---------------------------------------------------------------- 断言工具

    private static void AssertVec(Vector3 expected, Vector3 actual, string ctx, float tol = 1e-4f)
        => Assert.True((expected - actual).Length() <= tol, $"{ctx}: 期望 {expected} 实际 {actual}");

    /// <summary>四元数 q 与 -q 是同一旋转，比较它们对基向量的作用而不是分量。</summary>
    private static void AssertRotation(Matrix4x4 world, Quaternion expected, string ctx)
    {
        var actual = Quaternion.CreateFromRotationMatrix(world);
        AssertVec(Vector3.Transform(Vector3.UnitX, expected), Vector3.Transform(Vector3.UnitX, actual), $"{ctx}.X");
        AssertVec(Vector3.Transform(Vector3.UnitY, expected), Vector3.Transform(Vector3.UnitY, actual), $"{ctx}.Y");
        AssertVec(Vector3.Transform(Vector3.UnitZ, expected), Vector3.Transform(Vector3.UnitZ, actual), $"{ctx}.Z");
    }

    // ---------------------------------------------------------------- 拓扑预计算

    [Fact]
    public void Topology_IncludesAppendChainAndParentSubtree_ExcludesDrivenBones()
    {
        var model = Build();

        model.SetPhysicsDrivenBones([0]);

        Assert.True(model.HasPostPhysicsAppend);
        // 受影响集 = {1(付与旋转), 2(父边), 3(付与移动)}；0 是物理输出本体，被刻意排除
        Assert.Equal(3, model.PhysicsAppendBoneCount);
        int[] expected = [1, 2, 3];
        // 重算序必须按 deform 序（源先于依赖）
        Assert.Equal(expected, model.DeformOrder.Where(i => i != 0).ToArray());
    }

    [Fact]
    public void Topology_DrivenAppendChildStaysExcluded()
    {
        var model = Build();

        // 0 与 1 都被物理覆写：1 是物理输出本体（虽然它有付与），不进重算集；
        // 2 是 1 的普通子骨 —— reze 同构闭包里父边只在【受影响骨】下传播，
        // 被驱动骨不标记 affected，因此 2 不进重算集；只剩 3（付与 ← 0）。
        model.SetPhysicsDrivenBones([0, 1]);

        Assert.True(model.HasPostPhysicsAppend);
        Assert.Equal(1, model.PhysicsAppendBoneCount);
    }

    [Fact]
    public void Topology_EmptyOrIrrelevantDriven_NoTopology()
    {
        var model = Build();

        model.SetPhysicsDrivenBones(Array.Empty<int>());
        Assert.False(model.HasPostPhysicsAppend);

        // 2 是叶子：没人付与它、也没人有它当付与源 → 无拓扑
        model.SetPhysicsDrivenBones([2]);
        Assert.False(model.HasPostPhysicsAppend);
    }

    // ---------------------------------------------------------------- 后付与应用

    [Fact]
    public void Apply_ChainConsumesSimulatedPose_AndIsIdempotent()
    {
        var model = Build();
        model.SetPhysicsDrivenBones([0]);

        // 动画姿态：物理骨绕 Y 转 90°（付与链此刻应继承动画旋转）
        var qAnim = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        model.LocalRotations[0] = qAnim;
        model.UpdateWorldMatrices();
        AssertRotation(model.WorldMatrices[1], qAnim, "物理前·付与子");

        // 模拟物理写回：物理骨的世界矩阵被直接覆写（绕 Z 转 45° + 平移）
        var qPhys = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f);
        var mPhys = Matrix4x4.CreateFromQuaternion(qPhys);
        mPhys.Translation = new Vector3(1, 2, 3);
        model.WorldMatrices[0] = mPhys;

        model.ApplyPhysicsAppend();

        // ① 物理驱动骨本体不被重算（世界矩阵就是物理输出）
        Assert.Equal(mPhys, model.WorldMatrices[0]);

        // ② 付与旋转消费模拟结果（而非物理前的动画姿态）
        AssertRotation(model.WorldMatrices[1], qPhys, "物理后·付与子");
        AssertVec(new Vector3(0, 5, 0), model.WorldMatrices[1].Translation, "付与子位置");

        // ③ 父边传播：普通子（父 = 付与子）跟着模拟旋转绕付与子原点走
        //    (0,3,0) 绕 Z 转 45° → (-3/√2, 3/√2, 0)，加付与子原点 (0,5,0)
        AssertRotation(model.WorldMatrices[2], qPhys, "物理后·普通子");
        AssertVec(
            new Vector3(-3f / MathF.Sqrt(2f), 5f + 3f / MathF.Sqrt(2f), 0f),
            model.WorldMatrices[2].Translation, "普通子位置");

        // ④ 付与移動消费模拟位移：bind(2,0,0) + (srcWorld.T − srcBind) = (2,0,0)+(1,2,3)
        AssertVec(new Vector3(3, 2, 3), model.WorldMatrices[3].Translation, "付与移動子位置");

        // ⑤ 幂等：连跑两次结果不变（反解只写暂存，重算读同一暂存）
        var worlds = (Matrix4x4[])model.WorldMatrices.Clone();
        model.ApplyPhysicsAppend();
        Assert.Equal(worlds, model.WorldMatrices);
    }

    [Fact]
    public void Apply_NoTopology_IsSafeNoOp()
    {
        var model = Build();

        // 从未调用 SetPhysicsDrivenBones / 传入无关集合 ⇒ 拓扑为空，调用必须无害
        model.UpdateWorldMatrices();
        var worlds = (Matrix4x4[])model.WorldMatrices.Clone();

        model.ApplyPhysicsAppend();

        Assert.Equal(worlds, model.WorldMatrices);

        model.SetPhysicsDrivenBones([2]);
        model.ApplyPhysicsAppend();
        Assert.Equal(worlds, model.WorldMatrices);
    }

    [Fact]
    public void Apply_DoesNotLeakIntoLocalPose()
    {
        var model = Build();
        model.SetPhysicsDrivenBones([0]);

        model.LocalRotations[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        model.UpdateWorldMatrices();
        model.WorldMatrices[0] = Matrix4x4.CreateFromQuaternion(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f));
        model.ApplyPhysicsAppend();

        // 反解只写 _finalRotations/_finalTranslations 暂存 —— Local* 保持动画写入值，
        // 下一帧 UpdateWorldMatrices 从 Local* 全量重建，无跨帧泄漏
        Assert.Equal(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), model.LocalRotations[0]);

        // 再来一次全量更新：付与链应回到动画姿态（暂存被重建覆盖）
        model.UpdateWorldMatrices();
        AssertRotation(model.WorldMatrices[1],
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), "重建后·付与子");
    }
}

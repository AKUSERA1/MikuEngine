using System.Numerics;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// 外部親（outside parent）：SkeletalModel 根父钩子 + MmdExternalParentController 绑定管理。
///
/// MMD 官方语义（reze-engine 同法）：
///  1. 绑定生效时<b>所有无父骨</b>挂到根父矩阵下：world = (local − primaryRootBind) × RootParent；
///  2. primaryRootBind = 首个无父骨的绑定姿势世界平移 —— 减掉后首个无父骨（全ての親）
///     恰好「坐」在亲骨上，其余无父骨保持与它的相对布局；
///  3. 根父矩阵 = 亲骨世界矩阵 × VMD 键偏移（行主序：先偏移、后亲骨世界）；
///  4. 解绑 = 普通骨架（根骨锚在模型原点）。
/// </summary>
public class MmdExternalParentTests
{
    // ---------------------------------------------------------------- 根父钩子

    [Fact]
    public void Unbound_RootAnchorsAtModelOrigin()
    {
        var m = Rig();
        m.UpdateWorldMatrices();

        Assert.Null(m.RootParent);
        Assert.Equal(new Vector3(5, 0, 0), m.WorldMatrices[0].Translation);   // 全ての親 绑定位置
        Assert.Equal(new Vector3(5, 1, 0), m.WorldMatrices[1].Translation);   // 子骨跟随
    }

    [Fact]
    public void Bound_PrimaryRootSitsOnParentBone()
    {
        var m = Rig();   // 全ての親 bind 在 (5,0,0)
        m.UpdateWorldMatrices();

        // 亲骨世界 = T(10, 2, 0)；无偏移。绑定后全ての親 世界平移应恰为亲骨位置。
        var parentWorld = Matrix4x4.CreateTranslation(10, 2, 0);
        m.SetRootParent(parentWorld);
        m.UpdateWorldMatrices();

        Assert.Equal(parentWorld, m.RootParent);
        Assert.Equal(new Vector3(10, 2, 0), m.WorldMatrices[0].Translation);  // 5−5 校正后坐上亲骨
        Assert.Equal(new Vector3(10, 3, 0), m.WorldMatrices[1].Translation);  // 子骨保持相对布局

        // 其余无父骨：bind 在 (5,0,3)，保持与全ての親 的相对偏移 (0,0,3)
        Assert.Equal(new Vector3(10, 2, 3), m.WorldMatrices[2].Translation);
    }

    [Fact]
    public void Bound_LocalRotationComposesWithRootParent()
    {
        var m = Rig();
        m.SetRootParent(Matrix4x4.CreateRotationY(MathF.PI / 2));   // 亲骨带旋转
        m.LocalRotations[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        m.UpdateWorldMatrices();

        // 全ての親：bind(5,0,0) − primary(5,0,0) = 原点，旋转后被根父带到 Y 轴旋转空间
        Assert.Equal(Vector3.Zero, m.WorldMatrices[0].Translation);

        // 行主序 world = local(R·T) × rootParent：平移 (0,1,0) 沿 Y 轴不受 Y 旋转影响，
        // 旋转部分 = R_x(90°) × R_y(90°)。逐元素比对矩阵（含 1e-4 容差）。
        Matrix4x4 expectedLocal = Matrix4x4.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        expectedLocal.Translation = new Vector3(0, 1, 0);
        var expected = expectedLocal * Matrix4x4.CreateRotationY(MathF.PI / 2);

        var actual = m.WorldMatrices[1];
        Assert.True(AreClose(expected, actual, 1e-4f),
            $"world=\n{actual}\nexpected=\n{expected}");
    }

    [Fact]
    public void Unbind_RestoresPlainRig()
    {
        var m = Rig();
        m.SetRootParent(Matrix4x4.CreateTranslation(100, 0, 0));
        m.UpdateWorldMatrices();

        m.SetRootParent(null);
        m.UpdateWorldMatrices();

        Assert.Null(m.RootParent);
        Assert.Equal(new Vector3(5, 0, 0), m.WorldMatrices[0].Translation);
    }

    // ---------------------------------------------------------------- 控制器

    [Fact]
    public void Controller_BindsAtKeyFrame_AndFollowsParentBone()
    {
        var parent = Rig();          // bone1 = 手
        parent.BoneNames = ["全ての親", "右手首", "左手首"];
        parent.UpdateWorldMatrices();

        var child = Rig();
        var animation = AnimationOf(
            Key(10, "親モデル", "右手首"),
            Key(20, "", ""));

        var controller = new MmdExternalParentController();
        controller.Register("親モデル", parent);
        controller.Register("子モデル", child, animation);

        // 绑定前：普通骨架
        controller.Update(5);
        Assert.Null(child.RootParent);

        // 绑定帧：根父 = 偏移(单位) × 亲骨世界
        controller.Update(10);
        Assert.NotNull(child.RootParent);
        Assert.Equal(parent.WorldMatrices[1], child.RootParent);

        // 键间保持
        controller.Update(15);
        Assert.Equal(parent.WorldMatrices[1], child.RootParent);

        // 解除键之后
        controller.Update(20);
        Assert.Null(child.RootParent);
        controller.Update(99);
        Assert.Null(child.RootParent);
    }

    [Fact]
    public void Controller_OffsetFromKeyComposesOntoParentBone()
    {
        var parent = Rig();
        parent.BoneNames = ["全ての親", "右手首", "左手首"];
        parent.UpdateWorldMatrices();

        var child = Rig();
        var animation = AnimationOf(Key(0, "親モデル", "右手首",
            offsetPos: new Vector3(0, 1, 0), offsetRot: Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)));

        var controller = new MmdExternalParentController();
        controller.Register("親モデル", parent);
        controller.Register("子モデル", child, animation);
        controller.Update(0);

        // 行主序：offset(R·T) × parentWorld —— 对子空间点先偏移再进亲骨世界
        var expected = Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)
                     * Matrix4x4.CreateTranslation(0, 1, 0)
                     * parent.WorldMatrices[1];
        Assert.Equal(expected, child.RootParent);
    }

    [Fact]
    public void Controller_MissingParentModel_OrBone_Unbinds()
    {
        var parent = Rig();
        parent.BoneNames = ["全ての親", "右手首", "左手首"];
        parent.UpdateWorldMatrices();

        var missingBone = Rig();
        var missingModel = Rig();

        var controller = new MmdExternalParentController();
        controller.Register("親モデル", parent);
        controller.Register("缺骨子", missingBone, AnimationOf(Key(0, "親モデル", "存在しない骨")));
        controller.Register("缺亲子", missingModel, AnimationOf(Key(0, "存在しない親", "右手首")));

        controller.Update(0);

        // 缺骨 → 挂亲模型根（恒等矩阵）；缺亲模型 → 未绑定
        Assert.NotNull(missingBone.RootParent);
        Assert.Equal(Matrix4x4.Identity, missingBone.RootParent);
        Assert.Null(missingModel.RootParent);
    }

    // ---------------------------------------------------------------- 合成构件

    /// <summary>
    /// 三个骨的最小骨架：0=全ての親（bind 5,0,0）、1=手（父0，bind 相对 0,1,0）、
    /// 2=另一无父骨（bind 5,0,3，验证相对布局保持）。
    /// </summary>
    private static SkeletalModel Rig() => SkeletalPoseHelper.Build(
        new[] { -1, 0, -1 },
        new[] { new Vector3(5, 0, 0), new Vector3(0, 1, 0), new Vector3(5, 0, 3) },
        new[] { "全ての親", "右手首", "左手首" });

    private static MmdExternalParentKey Key(int frame, string parentModel, string parentBone,
        Vector3? offsetPos = null, Quaternion? offsetRot = null) =>
        new(frame, parentModel, parentBone, offsetPos ?? Vector3.Zero, offsetRot ?? Quaternion.Identity);

    private static MmdAnimation AnimationOf(params MmdExternalParentKey[] keys) => new()
    {
        ExternalParentTrack = MmdExternalParentTrack.FromVmd([.. keys]),
    };

    private static bool AreClose(in Matrix4x4 a, in Matrix4x4 b, float eps)
    {
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
            if (MathF.Abs(a[r, c] - b[r, c]) > eps) return false;
        return true;
    }
}

/// <summary>合成骨架构件（与 SkeletalPoseTests 同规则；InverseBind 从绑定世界矩阵取逆）。</summary>
internal static class SkeletalPoseHelper
{
    internal static SkeletalModel Build(int[] parents, Vector3[] localPositions, string[]? names = null)
    {
        int n = parents.Length;

        var bindWorld = new Matrix4x4[n];
        var inverseBind = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            Matrix4x4 local = Matrix4x4.Identity;
            local.Translation = localPositions[i];
            int p = parents[i];
            bindWorld[i] = p >= 0 ? local * bindWorld[p] : local;
            inverseBind[i] = Matrix4x4.Invert(bindWorld[i], out var inv) ? inv : Matrix4x4.Identity;
        }

        return new SkeletalModel
        {
            BoneCount = n,
            BoneNames = names ?? Enumerable.Range(0, n).Select(i => $"bone{i}").ToArray(),
            ParentIndices = parents,
            LocalPositions = (Vector3[])localPositions.Clone(),
            LocalTranslations = (Vector3[])localPositions.Clone(),
            LocalRotations = Enumerable.Repeat(Quaternion.Identity, n).ToArray(),
            AppendSources = Enumerable.Repeat(-1, n).ToArray(),
            AppendRatios = new float[n],
            AppendRotate = new bool[n],
            AppendMove = new bool[n],
            AppendIsLocal = new bool[n],
            AxisLimits = new Vector3[n],
            InverseBind = inverseBind,
            WorldMatrices = new Matrix4x4[n],
            SkinMatrices = new Matrix4x4[n],
            DeformOrder = Enumerable.Range(0, n).ToArray(),
        };
    }
}

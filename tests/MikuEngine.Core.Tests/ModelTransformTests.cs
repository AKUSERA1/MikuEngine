using System.Numerics;
using MikuEngine.Core.Math;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// 模型变换（MMD モデル操作）单测：
///   * 移動/回転 注入 全ての親（载体），子孙跟随 —— 物理/IK 消费的 WorldMatrices 自动包含；
///   * 操作中心 是并列根骨，不在变形链上 ⇒ 「始终留在原地」；
///   * 状态为绝对值，逐帧从 D 重建 ⇒ 幂等，ResetPose 不冲掉面板状态。
///
/// 模型：Alu_SummerAl_v1.5.pmx，实测根骨层级 [0]=操作中心 / [1]=全ての親 / [2]=センター(parent=1)。
/// </summary>
public class ModelTransformTests
{
    private const string AluRelative = "tests/MikuEngine.Core.Tests/TestModels/Alu_SummerAl_v1.5.pmx";

    private static string FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new IOException($"未找到 {relative}，请确认测试资源存在。");
    }

    private static SkeletalModel Load()
    {
        var m = SkeletalModelConverter.Convert(PmxParser.Parse(File.ReadAllBytes(FindUp(AluRelative))));
        Assert.True(m.RootTransformBoneIndex >= 0, "测试模型应含 全ての親");
        Assert.True(m.OperationCenterBoneIndex >= 0, "测试模型应含 操作中心");
        return m;
    }

    // ------------------------------------------------------------- 骨骼定位

    [Fact]
    public void Resolve_RootBonesMatchNames()
    {
        var m = Load();

        Assert.Equal(m.FindBone("全ての親"), m.RootTransformBoneIndex);
        Assert.Equal(m.FindBone("操作中心"), m.OperationCenterBoneIndex);
        // 实测：两根都是根骨（parent = -1），互不隶属
        Assert.Equal(-1, m.ParentIndices[m.RootTransformBoneIndex]);
        Assert.Equal(-1, m.ParentIndices[m.OperationCenterBoneIndex]);
    }

    // ------------------------------------------------------------- 移動

    [Fact]
    public void PanelMove_TranslatesSubtree_LeavesOperationCenterInPlace()
    {
        var m = Load();
        int ap = m.RootTransformBoneIndex;
        int oc = m.OperationCenterBoneIndex;

        var beforeAll = (Matrix4x4[])m.WorldMatrices.Clone();
        var beforeOc = m.WorldMatrices[oc];

        m.ModelTranslationOffset = new Vector3(3f, -2f, 5f);
        m.ApplyModelTransform();
        m.UpdateWorldMatrices();

        // 全ての親 自身平移 = 面板偏移（绑定局部平移为 0）
        var deltaAp = m.WorldMatrices[ap].Translation - beforeAll[ap].Translation;
        Assert.Equal(new Vector3(3f, -2f, 5f), deltaAp);

        // 任意子孙（センター 及更深的骨）都整体平移同样的量
        int center = m.FindBone("センター");
        Assert.True(center >= 0);
        var deltaCenter = m.WorldMatrices[center].Translation - beforeAll[center].Translation;
        Approx(deltaCenter, new Vector3(3f, -2f, 5f), 1e-4f);

        // 操作中心「始终留在原地」——世界矩阵逐位不变
        Assert.Equal(beforeOc, m.WorldMatrices[oc]);
    }

    // ------------------------------------------------------------- 回転

    [Fact]
    public void PanelRotateY90_RotatesSubtreeAboutBindPivot_LeavesOperationCenterInPlace()
    {
        var m = Load();
        int ap = m.RootTransformBoneIndex;
        int oc = m.OperationCenterBoneIndex;

        var beforeAll = (Matrix4x4[])m.WorldMatrices.Clone();
        var beforeOc = m.WorldMatrices[oc];

        float y = MathF.PI / 2f;
        m.ModelRotationAngles = new Vector3(0f, y, 0f);   // (x, y, z)
        m.ApplyModelTransform();
        m.UpdateWorldMatrices();

        Quaternion r = MmdMath.QuaternionFromYxzEuler(y, 0f, 0f);
        Matrix4x4 rot = Matrix4x4.CreateFromQuaternion(r);

        // 全ての親 绑定位置在原点 ⇒ 子孙世界位置 = 原位置绕原点旋转
        int center = m.FindBone("センター");
        Assert.True(center >= 0);
        var expected = Vector3.Transform(beforeAll[center].Translation, rot);
        Approx(expected, m.WorldMatrices[center].Translation, 1e-4f);

        // 到枢轴（原点）的距离不变
        float d0 = beforeAll[center].Translation.Length();
        float d1 = m.WorldMatrices[center].Translation.Length();
        Assert.True(MathF.Abs(d0 - d1) <= 1e-4f, $"枢轴距离应不变：{d0} vs {d1}");

        // 操作中心不变
        Assert.Equal(beforeOc, m.WorldMatrices[oc]);
    }

    [Fact]
    public void PanelRotate_ComposesWithMove_WorldAxisTranslation()
    {
        // 旋转后再平移：平移必须沿世界轴（不随旋转偏转），且旋转绕固定枢轴
        var m = Load();
        int ap = m.RootTransformBoneIndex;

        var before = m.WorldMatrices[ap].Translation;

        m.ModelRotationAngles = new Vector3(0f, MathF.PI, 0f);
        m.ModelTranslationOffset = new Vector3(10f, 0f, 0f);
        m.ApplyModelTransform();
        m.UpdateWorldMatrices();

        // v' = ((v - 0)·R_180) + 0 + (10,0,0)；全ての親 绑定平移为 0 ⇒ 世界平移 = (10,0,0)
        var delta = m.WorldMatrices[ap].Translation - before;
        Approx(delta, new Vector3(10f, 0f, 0f), 1e-4f);
    }

    // ------------------------------------------------------------- 幂等性 / 姿态共存

    [Fact]
    public void PanelTransform_IsIdempotentAcrossFrames()
    {
        var m = Load();
        m.ModelTranslationOffset = new Vector3(1f, 2f, 3f);
        m.ModelRotationAngles = new Vector3(0.1f, 0.2f, 0.3f);
        m.ApplyModelTransform();
        m.UpdateWorldMatrices();
        var first = (Matrix4x4[])m.WorldMatrices.Clone();

        // 连续多帧（典型宿主循环：每帧 ApplyModelTransform + FK）
        for (int f = 0; f < 3; f++)
        {
            m.ApplyModelTransform();
            m.UpdateWorldMatrices();
        }

        Assert.Equal(first, m.WorldMatrices);
    }

    [Fact]
    public void ResetPose_KeepsPanelTransformEffective()
    {
        var m = Load();
        int center = m.FindBone("センター");

        m.ModelTranslationOffset = new Vector3(4f, 0f, 0f);
        m.ApplyModelTransform();
        m.UpdateWorldMatrices();
        var withPanel = m.WorldMatrices[center].Translation;

        m.ResetPose();   // 清空局部 T/R，但面板状态是独立的，下一次 FK 依旧生效

        Approx(withPanel, m.WorldMatrices[center].Translation, 1e-4f);
    }

    [Fact]
    public void PanelTransform_CoexistsWithBoneLocalRotation()
    {
        // VMD 同时驱动 全ての親（SetBoneLocalRotation 模拟）+ 面板变换叠加，两者都要生效
        var m = Load();
        int ap = m.RootTransformBoneIndex;
        int center = m.FindBone("センター");

        Quaternion animRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);
        m.SetBoneLocalRotation(ap, animRot);
        m.ApplyModelTransform();
        m.UpdateWorldMatrices();
        var withAnimOnly = m.WorldMatrices[center].Translation;

        m.ModelTranslationOffset = new Vector3(0f, 0f, -7f);
        m.ApplyModelTransform();
        m.UpdateWorldMatrices();

        Approx(withAnimOnly + new Vector3(0f, 0f, -7f),
            m.WorldMatrices[center].Translation, 1e-4f);
    }

    // ------------------------------------------------------------- 物理解耦的边界

    [Fact]
    public void PanelTransform_DoesNotTouchSkinMatrixRelation()
    {
        // Skin[i] = InverseBind[i] · World[i] 的关系必须在注入面板矩阵后依然成立
        //（蒙皮 SSBO 直接吃 SkinMatrices，视觉包含面板变换）
        var m = Load();
        m.ModelTranslationOffset = new Vector3(2f, 3f, 4f);
        m.ApplyModelTransform();
        m.UpdateWorldMatrices();

        for (int i = 0; i < m.BoneCount; i += 97)   // 抽样
        {
            Matrix4x4 expected = m.InverseBind[i] * m.WorldMatrices[i];
            Assert.Equal(expected, m.SkinMatrices[i]);
        }
    }

    /// <summary>Vector3 容差断言。</summary>
    private static void Approx(Vector3 expected, Vector3 actual, float tol)
        => Assert.True(Vector3.DistanceSquared(expected, actual) <= tol * tol,
            $"期望 {expected}，实际 {actual}（容差 {tol}）");
}

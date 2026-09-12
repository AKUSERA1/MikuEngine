using System.Numerics;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// 表情（morph）求值测试：Group 传播 / 骨 morph / 材质 morph 混合 /
/// Flip·Impulse 显式跳过（见 docs/2026-09-11-anim-morph-plan.md §0.2、§9）。
///
/// 全部走「合成 PmxModel → SkeletalModelConverter → MmdMorphEvaluator」完整链路，
/// 因此转换器建的稀疏表与 Group 求值序也一并被覆盖。
/// </summary>
public class MmdMorphEvaluatorTests
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

    private static PmxBone Bone(string name, Vector3 position, Vector3? axisLimit = null) => new()
    {
        Name = name,
        EnglishName = name,
        Position = position,
        ParentBoneIndex = -1,
        TransformOrder = 0,
        Flag = PmxBoneFlag.None,
        TailBoneIndex = -1,
        TailPosition = Vector3.Zero,
        IsTailBoneIndex = false,
        AxisLimit = axisLimit,
    };

    private static PmxMaterial Material(string name) => new()
    {
        Name = name,
        EnglishName = name,
        Diffuse = new Vector4(1f, 1f, 1f, 1f),
        Specular = new Vector3(0.5f),
        Shininess = 10f,
        Ambient = new Vector3(0.5f),
        Flag = PmxMaterialFlag.None,
        EdgeColor = new Vector4(0f, 0f, 0f, 1f),
        EdgeSize = 1f,
        TextureIndex = -1,
        SphereTextureIndex = -1,
        SphereTextureMode = PmxMaterialSphereMode.Off,
        IsSharedToonTexture = false,
        ToonTextureIndex = -1,
        Comment = "",
        IndexCount = 0,
    };

    private static PmxMorph GroupMorph(string name, int[] children, float[] ratios) => new()
    {
        Name = name,
        EnglishName = name,
        Category = PmxMorphCategory.Other,
        Type = PmxMorphType.Group,
        Indices = children,
        Ratios = ratios,
    };

    private static PmxMorph BoneMorph(string name, int[] bones, Vector3[] translations, Quaternion[] rotations) => new()
    {
        Name = name,
        EnglishName = name,
        Category = PmxMorphCategory.Other,
        Type = PmxMorphType.Bone,
        Indices = bones,
        Positions = Flatten(translations),
        Rotations = Flatten(rotations),
    };

    private static PmxMorph MaterialMorph(string name, PmxMaterialMorphElement[] elements) => new()
    {
        Name = name,
        EnglishName = name,
        Category = PmxMorphCategory.Other,
        Type = PmxMorphType.Material,
        MaterialElements = elements,
    };

    private static PmxMorph TypedMorph(string name, PmxMorphType type, int[] indices, float[] ratios) => new()
    {
        Name = name,
        EnglishName = name,
        Category = PmxMorphCategory.Other,
        Type = type,
        Indices = indices,
        Ratios = ratios,
    };

    private static float[] Flatten(Vector3[] v) => v.SelectMany(x => new[] { x.X, x.Y, x.Z }).ToArray();

    private static float[] Flatten(Quaternion[] q) => q.SelectMany(x => new[] { x.X, x.Y, x.Z, x.W }).ToArray();

    private static SkeletalModel Build(PmxMorph[] morphs, PmxBone[]? bones = null, PmxMaterial[]? materials = null)
    {
        var pmx = new PmxModel
        {
            Header = Header(),
            Vertices = [],
            Indices = [],
            Textures = [],
            Materials = materials ?? [],
            Bones = bones ?? [],
            Morphs = morphs,
            DisplayFrames = [],
            RigidBodies = [],
            Joints = [],
            SoftBodies = [],
        };
        return SkeletalModelConverter.Convert(pmx);
    }

    /// <summary>按名字取 morph 索引（测试里用名字更直观）。</summary>
    private static int Index(SkeletalModel model, string name)
    {
        int i = Array.IndexOf(model.MorphNames, name);
        Assert.True(i >= 0, $"合成模型里没有 morph「{name}」");
        return i;
    }

    private static void SetRaw(SkeletalModel model, string name, float weight)
        => model.MorphRawWeights[Index(model, name)] = weight;

    private static float Effective(SkeletalModel model, string name)
        => model.MorphWeights[Index(model, name)];

    // ================================================================ Group

    [Fact]
    public void Group_PropagatesWithRatio()
    {
        var model = Build(new[]
        {
            GroupMorph("组", [1], [0.5f]),
            GroupMorph("空组", [], []),
        });
        SetRaw(model, "组", 1f);

        MmdMorphEvaluator.Evaluate(model);

        Assert.Equal(0.5f, Effective(model, "空组"), 1e-6f);
    }

    [Fact]
    public void Group_AccumulatesFromMultipleGroups()
    {
        var model = Build(new[]
        {
            GroupMorph("组A", [2], [0.5f]),
            GroupMorph("组B", [2], [0.25f]),
            GroupMorph("空组", [], []),
        });
        SetRaw(model, "组A", 1f);
        SetRaw(model, "组B", 1f);

        MmdMorphEvaluator.Evaluate(model);

        Assert.Equal(0.75f, Effective(model, "空组"), 1e-6f);
    }

    [Fact]
    public void Group_CascadesThroughNestedGroups()
    {
        // 组套组：组0 →(0.5) 组1 →(1.0) 空组。reze 只做单层、babylon-mmd 直接跳过，本方案级联。
        var model = Build(new[]
        {
            GroupMorph("组0", [1], [0.5f]),
            GroupMorph("组1", [2], [1.0f]),
            GroupMorph("空组", [], []),
        });

        Assert.Equal(new[] { 0, 1, 2 }, model.GroupOrder);   // 引用者先于被引用者

        SetRaw(model, "组0", 1f);
        MmdMorphEvaluator.Evaluate(model);

        Assert.Equal(0.5f, Effective(model, "组1"), 1e-6f);
        Assert.Equal(0.5f, Effective(model, "空组"), 1e-6f);   // 0.5 × 1.0
    }

    [Fact]
    public void Group_ZeroWeightIsPruned()
    {
        var model = Build(new[]
        {
            GroupMorph("组", [1], [0.5f]),
            GroupMorph("空组", [], []),
        });

        MmdMorphEvaluator.Evaluate(model);

        Assert.Equal(0f, Effective(model, "空组"));
    }

    [Fact]
    public void Group_SurvivesCycle()
    {
        // 互引用成环：必须能终止（环上的边被丢弃），且不抛异常。
        var model = Build(new[]
        {
            GroupMorph("A", [1], [1f]),
            GroupMorph("B", [0], [1f]),
        });

        Assert.Equal(2, model.GroupOrder.Length);

        SetRaw(model, "A", 1f);
        MmdMorphEvaluator.Evaluate(model);   // 不挂 = 通过

        Assert.Equal(1f, Effective(model, "B"), 1e-6f);
    }

    [Fact]
    public void Group_SelfReferenceIsDropped()
    {
        var model = Build(new[] { GroupMorph("自引用", [0], [1f]) });

        Assert.Empty(model.GroupMorphs[0]!.Value.ChildIndices);

        SetRaw(model, "自引用", 1f);
        MmdMorphEvaluator.Evaluate(model);

        Assert.Equal(1f, Effective(model, "自引用"));   // 不自我放大
    }

    // ================================================================ Flip / Impulse

    [Fact]
    public void Flip_IsParsedButNotEvaluated()
    {
        // Flip 与 Group 文件布局相同，但语义不同 —— 本步不支持：权重不得传播出去。
        var model = Build(new[]
        {
            TypedMorph("翻转", PmxMorphType.Flip, [1], [1f]),
            GroupMorph("空组", [], []),
        });

        Assert.Equal((byte)PmxMorphType.Flip, model.MorphKinds[0]);
        Assert.Null(model.GroupMorphs[0]);            // 不建运行时表
        Assert.DoesNotContain(0, model.GroupOrder);   // 不进入求值序

        SetRaw(model, "翻转", 1f);
        MmdMorphEvaluator.Evaluate(model);

        Assert.Equal(0f, Effective(model, "空组"));    // 未传播
        Assert.Equal(1f, Effective(model, "翻转"));    // 自身权重保留（只是不消费）
    }

    [Fact]
    public void Impulse_IsParsedButNotEvaluated()
    {
        var model = Build(new[]
        {
            TypedMorph("冲击", PmxMorphType.Impulse, [0], [1f]),
            GroupMorph("空组", [], []),
        });

        Assert.Equal((byte)PmxMorphType.Impulse, model.MorphKinds[0]);
        Assert.Null(model.GroupMorphs[0]);

        MmdMorphEvaluator.Evaluate(model);

        Assert.Equal(0f, Effective(model, "空组"));
    }

    // ================================================================ 骨 morph

    [Fact]
    public void BoneMorph_AddsTranslationAndSlerpsRotation()
    {
        var model = Build(
            new[]
            {
                BoneMorph("骨表情",
                    bones: [1],
                    translations: [new Vector3(2f, 0f, 0f)],
                    rotations: [Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f)]),
            },
            bones: [Bone("根", Vector3.Zero), Bone("子", new Vector3(1f, 0f, 0f))]);

        SetRaw(model, "骨表情", 0.5f);
        MmdMorphEvaluator.Evaluate(model);

        // 平移：父空间线性叠加 × 权重
        Assert.Equal(new Vector3(1f + 2f * 0.5f, 0f, 0f), model.LocalTranslations[1]);

        // 旋转：own * Slerp(Identity, morph, w)（右乘）
        var expected = Quaternion.Identity
                     * Quaternion.Slerp(Quaternion.Identity,
                                        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f), 0.5f);
        Assert.Equal(expected.X, model.LocalRotations[1].X, 1e-6f);
        Assert.Equal(expected.Y, model.LocalRotations[1].Y, 1e-6f);
        Assert.Equal(expected.Z, model.LocalRotations[1].Z, 1e-6f);
        Assert.Equal(expected.W, model.LocalRotations[1].W, 1e-6f);
    }

    [Fact]
    public void BoneMorph_IsAppliedBeforeAxisLimit()
    {
        // 骨 1 带 Y 轴軸制限，骨 0 不带；同一个「绕 X 轴 90°」的骨 morph 作用在两根骨上。
        // 顺序为 morph → 軸制限 → 付与，因此：
        //   骨 0（无限制）= 绕 X 90°；骨 1（限制 Y）= 投影后 X 分量被丢弃 → 单位旋转。
        // 若顺序反了（先軸制限后 morph），骨 1 会保留绕 X 的 90°，本用例即失败。
        var model = Build(
            new[]
            {
                BoneMorph("绕X",
                    bones: [0, 1],
                    translations: [Vector3.Zero, Vector3.Zero],
                    rotations:
                    [
                        Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
                        Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
                    ]),
            },
            bones: [Bone("自由", Vector3.Zero), Bone("限Y", Vector3.Zero, axisLimit: Vector3.UnitY)]);

        SetRaw(model, "绕X", 1f);
        MmdMorphEvaluator.Evaluate(model);
        model.UpdateWorldMatrices();

        // 注意：軸制限只作用于「求值用的旋转」，不回写 LocalRotations（保证 UpdateWorldMatrices
        // 幂等），所以判据要看最终世界矩阵的旋转部分。
        var free = Quaternion.CreateFromRotationMatrix(model.WorldMatrices[0]);
        var limited = Quaternion.CreateFromRotationMatrix(model.WorldMatrices[1]);

        Assert.True(Quaternion.Dot(Quaternion.Normalize(free),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f)) > 0.9999f,
            "无軸制限的骨应保留绕 X 90°");
        Assert.True(Quaternion.Dot(Quaternion.Normalize(limited), Quaternion.Identity) > 0.9999f,
            "带 Y 軸制限的骨：绕 X 的 morph 旋转应被投影丢弃（morph → 軸制限 的顺序）");
    }

    // ================================================================ 材质 morph

    /// <summary>
    /// 方案 §7.1-17：材质 morph 不单独混合 —— 混合器写 <see cref="SkeletalModel.MorphRawWeights"/>，
    /// <see cref="MmdMorphEvaluator"/> 传播到有效权重后 <see cref="MmdMorphEvaluator.ResolveMaterial"/>
    /// 必须自动跟随<b>混合后</b>的权重（层间竞争归一 0.5×1 + 0.5×0.4 = 0.7）。
    /// </summary>
    [Fact]
    public void MaterialMorph_FollowsBlendedMorphWeights()
    {
        var model = Build(
            new[]
            {
                MaterialMorph("乘算", [
                    new PmxMaterialMorphElement
                    {
                        MaterialIndex = 0,
                        Type = PmxMaterialMorphType.Multiply,
                        Diffuse = new Vector4(0.5f, 1f, 1f, 1f),
                        Specular = Vector3.One,
                        Shininess = 1f,
                        Ambient = Vector3.One,
                        EdgeColor = new Vector4(1f, 1f, 1f, 1f),
                        EdgeSize = 0f,
                        TextureColor = Vector4.One,
                        SphereTextureColor = Vector4.One,
                        ToonTextureColor = Vector4.One,
                    },
                ]),
            },
            materials: [Material("脸"), Material("体")]);

        VmdMorphKey Key(string name, uint frame, float weight)
            => new(name, System.Text.Encoding.UTF8.GetBytes(name), frame, weight);

        var motionA = new VmdMotion();
        motionA.MorphKeys.Add(Key("乘算", 0, 1f));
        var motionB = new VmdMotion();
        motionB.MorphKeys.Add(Key("乘算", 0, 0.4f));

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, model)) { Weight = 1f });
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, model)) { Weight = 1f });

        mixer.Evaluate(model, 0);
        Assert.Equal(0.7f, model.MorphRawWeights[0], 4f);   // 逐项竞争归一（5d-3 偏离）

        MmdMorphEvaluator.Evaluate(model);
        Assert.Equal(0.7f, model.MorphWeights[0], 4f);      // 无 Group ⇒ 有效值 = 原始值

        // Multiply：v + (v·m − v)·w，w = 0.7 ⇒ Diffuse.R: 1 + (0.5−1)×0.7 = 0.65；EdgeSize: 1 − 0.7 = 0.3
        var face = MmdMorphEvaluator.ResolveMaterial(model, 0);
        Assert.Equal(1f + (0.5f - 1f) * 0.7f, face.Diffuse.X, 4f);
        Assert.Equal(1f - 0.7f, face.EdgeSize, 4f);
        var body = MmdMorphEvaluator.ResolveMaterial(model, 1);
        Assert.Equal(1f, body.Diffuse.X);                   // 只作用于材质 0
    }

    [Fact]
    public void ResolveMaterial_Multiply()
    {
        var model = Build(
            new[]
            {
                MaterialMorph("乘算", [
                    new PmxMaterialMorphElement
                    {
                        MaterialIndex = 0,
                        Type = PmxMaterialMorphType.Multiply,
                        Diffuse = new Vector4(0.5f, 1f, 1f, 1f),
                        Specular = Vector3.One,
                        Shininess = 1f,
                        Ambient = Vector3.One,
                        EdgeColor = new Vector4(1f, 1f, 1f, 1f),
                        EdgeSize = 0f,
                        TextureColor = Vector4.One,
                        SphereTextureColor = Vector4.One,
                        ToonTextureColor = Vector4.One,
                    },
                ]),
            },
            materials: [Material("脸"), Material("体")]);

        SetRaw(model, "乘算", 1f);
        MmdMorphEvaluator.Evaluate(model);

        var face = MmdMorphEvaluator.ResolveMaterial(model, 0);
        var body = MmdMorphEvaluator.ResolveMaterial(model, 1);

        // Multiply：v + (v*m - v)*w，w = 1 时即 v*m
        Assert.Equal(new Vector4(0.5f, 1f, 1f, 1f), face.Diffuse);
        Assert.Equal(0f, face.EdgeSize);                  // 描边宽度被乘算到 0
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), body.Diffuse);   // 只作用于材质 0
        Assert.Equal(1f, body.EdgeSize);
    }

    [Fact]
    public void ResolveMaterial_Add_MinusOneAffectsAllMaterials()
    {        var model = Build(
            new[]
            {
                MaterialMorph("加算", [
                    new PmxMaterialMorphElement
                    {
                        MaterialIndex = -1,          // 全部材质
                        Type = PmxMaterialMorphType.Add,
                        Diffuse = new Vector4(0.25f, 0f, 0f, 0f),
                        Specular = Vector3.Zero,
                        Shininess = 0f,
                        Ambient = Vector3.Zero,
                        EdgeColor = Vector4.Zero,
                        EdgeSize = 0f,
                        TextureColor = Vector4.Zero,
                        SphereTextureColor = Vector4.Zero,
                        ToonTextureColor = Vector4.Zero,
                    },
                ]),
            },
            materials: [Material("脸"), Material("体")]);

        SetRaw(model, "加算", 0.5f);
        MmdMorphEvaluator.Evaluate(model);

        // Add：v + m*w，0.25 × 0.5 = 0.125
        for (int i = 0; i < 2; i++)
        {
            var state = MmdMorphEvaluator.ResolveMaterial(model, i);
            Assert.Equal(1.125f, state.Diffuse.X, 1e-6f);
        }
    }

    [Fact]
    public void ResolveMaterial_ZeroWeightIsIdentity()
    {
        var model = Build(
            new[]
            {
                MaterialMorph("乘算", [
                    new PmxMaterialMorphElement
                    {
                        MaterialIndex = -1,
                        Type = PmxMaterialMorphType.Multiply,
                        Diffuse = new Vector4(0.1f, 0.2f, 0.3f, 0.4f),
                        Specular = Vector3.Zero,
                        Shininess = 0f,
                        Ambient = Vector3.Zero,
                        EdgeColor = Vector4.Zero,
                        EdgeSize = 0f,
                        TextureColor = Vector4.Zero,
                        SphereTextureColor = Vector4.Zero,
                        ToonTextureColor = Vector4.Zero,
                    },
                ]),
            },
            materials: [Material("脸")]);

        // 权重为 0（不设 raw）⇒ 逐字段等于基础材质（回归基线）
        MmdMorphEvaluator.Evaluate(model);

        var baseMaterial = model.Materials[0];
        var state = MmdMorphEvaluator.ResolveMaterial(model, 0);

        Assert.Equal(baseMaterial.Diffuse, state.Diffuse);
        Assert.Equal(baseMaterial.Specular, state.Specular);
        Assert.Equal(baseMaterial.Shininess, state.Shininess);
        Assert.Equal(baseMaterial.Ambient, state.Ambient);
        Assert.Equal(baseMaterial.EdgeColor, state.EdgeColor);
        Assert.Equal(baseMaterial.EdgeSize, state.EdgeSize);
        Assert.Equal(Vector4.One, state.TextureColor);
        Assert.Equal(Vector4.One, state.SphereColor);
        Assert.Equal(Vector4.One, state.ToonColor);
    }

    [Fact]
    public void Evaluate_IsIdempotent()
    {
        var model = Build(new[]
        {
            GroupMorph("组", [1], [0.5f]),
            GroupMorph("空组", [], []),
        });
        SetRaw(model, "组", 1f);

        MmdMorphEvaluator.Evaluate(model);
        var first = Effective(model, "空组");
        MmdMorphEvaluator.Evaluate(model);

        Assert.Equal(first, Effective(model, "空组"), 1e-6f);   // 不会二次累加
    }
}

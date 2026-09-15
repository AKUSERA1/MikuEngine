using System.Numerics;
using System.Text;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// Step 5d-2：<c>SampleInto</c> / <c>MmdPoseBuffer</c> 重构（不改行为）+ <c>MmdAnimationLayer</c>
/// 时间轴映射（Offset / Trim / Weight / Fade / Loop）。
///
/// 回归基线 = 拆分前的单动效 <c>Sample</c>：新 <c>Sample</c> 必须与「<c>SampleInto</c> + 写回」
/// 以及本文件里独立照抄的旧实现<b>逐位一致</b>（既用合成轨道，也用真实 Motion.vmd 的真实轨道数据）。
/// </summary>
public class MmdAnimationLayerTests
{
    // ---------------------------------------------------------------- 定位（同 VmdParserTests）

    private static readonly string VmdPath = TestAssets.Motion("Motion.vmd");

    // ---------------------------------------------------------------- 合成构件

    private const byte LinearA = 20;    // MMD 默认线性控制点
    private const byte LinearB = 107;

    private static byte[] LinearInterp()
    {
        var raw = new byte[64];
        for (int c = 0; c < 4; c++)
        {
            int b = c * 16;
            raw[b + 0] = LinearA;
            raw[b + 8] = LinearB;
            raw[b + 4] = LinearA;
            raw[b + 12] = LinearB;
        }
        return raw;
    }

    private static VmdBoneKey Bone(string name, uint frame, Vector3 position, Quaternion rotation)
        => new(name, Encoding.UTF8.GetBytes(name), frame, position, rotation, LinearInterp());

    private static VmdMorphKey Morph(string name, uint frame, float weight)
        => new(name, Encoding.UTF8.GetBytes(name), frame, weight);

    private static VmdMotion MotionOf(VmdBoneKey[] bones, VmdMorphKey[]? morphs = null,
        VmdPropertyKey[]? properties = null)
    {
        var motion = new VmdMotion();
        motion.BoneKeys.AddRange(bones);
        if (morphs != null) motion.MorphKeys.AddRange(morphs);
        if (properties != null) motion.PropertyKeys.AddRange(properties);
        return motion;
    }

    /// <summary>建一个尺寸自洽的合成模型（骨 / 表情数组与名称表同长，绑定位置给确定性递增值）。</summary>
    private static SkeletalModel MakeModel(string[] boneNames, string[]? morphNames = null)
    {
        morphNames ??= [];
        var model = new SkeletalModel
        {
            BoneCount = boneNames.Length,
            BoneNames = boneNames,
            MorphNames = morphNames,
            LocalPositions = new Vector3[boneNames.Length],
            LocalTranslations = new Vector3[boneNames.Length],
            LocalRotations = new Quaternion[boneNames.Length],
            MorphRawWeights = new float[morphNames.Length],
            MorphWeights = new float[morphNames.Length],
        };

        for (int i = 0; i < boneNames.Length; i++)
        {
            model.LocalPositions[i] = new Vector3(i, i * 0.5f, -i);
            model.LocalTranslations[i] = model.LocalPositions[i];
        }
        Array.Fill(model.LocalRotations, Quaternion.Identity);
        return model;
    }

    /// <summary>
    /// 重构前 <c>MmdAnimation.Sample</c> 的独立参考实现（逐行照抄 5d-1 版本，用于回归对照）。
    /// </summary>
    private static void LegacySample(MmdAnimation anim, SkeletalModel model, double frame)
    {
        var rotations = model.LocalRotations;
        for (int i = 0; i < rotations.Length; i++)
            rotations[i] = Quaternion.Identity;

        var translations = model.LocalTranslations;
        var bindPositions = model.LocalPositions;
        for (int i = 0; i < translations.Length; i++)
            translations[i] = bindPositions[i];

        var morphWeights = model.MorphRawWeights;
        for (int i = 0; i < morphWeights.Length; i++)
            morphWeights[i] = 0f;

        foreach (var track in anim.BoneTracks)
        {
            int index = track.BoneIndex;
            if ((uint)index >= (uint)model.BoneCount) continue;

            track.Sample(frame, out var rotation, out var offset);
            rotations[index] = rotation;
            translations[index] = bindPositions[index] + offset;
        }

        foreach (var track in anim.MorphTracks)
        {
            float weight = track.SampleWeight(frame);
            var indices = track.MorphIndices;
            for (int k = 0; k < indices.Length; k++)
            {
                int index = indices[k];
                if ((uint)index >= (uint)morphWeights.Length) continue;
                morphWeights[index] = weight;
            }
        }
    }

    /// <summary>回归对照用的帧样本：端点 / 小数帧 / 首末键之外（钳制）都覆盖。</summary>
    private static double[] RegressionFrames(MmdAnimation anim) =>
    [
        -5.0, 0.0, 0.5, 1.0, 7.25,
        anim.StartFrame, anim.StartFrame + 0.5,
        (anim.StartFrame + anim.EndFrame) * 0.5,
        anim.EndFrame - 0.5, anim.EndFrame, anim.EndFrame + 100.0,
    ];

    // ================================================================ MmdPoseBuffer

    [Fact]
    public void MmdPoseBuffer_Reset_ClearsPoseAndCoveredLists()
    {
        var buffer = new MmdPoseBuffer(2, 3);

        buffer.Rotations[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        buffer.Translations[1] = new Vector3(1, 2, 3);
        buffer.MorphWeights[2] = 0.7f;
        buffer.CoveredBones.Add(1);
        buffer.CoveredMorphs.Add(2);

        buffer.Reset();

        Assert.Equal(Quaternion.Identity, buffer.Rotations[0]);
        Assert.Equal(Vector3.Zero, buffer.Translations[1]);
        Assert.Equal(0f, buffer.MorphWeights[2]);
        Assert.Empty(buffer.CoveredBones);
        Assert.Empty(buffer.CoveredMorphs);
    }

    [Fact]
    public void MmdPoseBuffer_ForModel_MatchesModelShape()
    {
        var model = MakeModel(["センター", "腕"], ["まばたき", "にこり", "まばたき"]);
        var buffer = MmdPoseBuffer.ForModel(model);

        Assert.Equal(2, buffer.BoneCount);
        Assert.Equal(3, buffer.MorphCount);
        Assert.True(buffer.Matches(model));
        Assert.False(buffer.Matches(MakeModel(["センター"])));
    }

    // ================================================================ SampleInto

    [Fact]
    public void SampleInto_WritesRelativeOffsets_AndRecordsCovered()
    {
        var motion = MotionOf(
            [
                Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
                Bone("センター", 10, new Vector3(2, -1, 0.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f)),
                Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
                Bone("腕", 10, new Vector3(0, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.8f)),
            ],
            [Morph("まばたき", 0, 0f), Morph("まばたき", 10, 1f)]);

        var model = MakeModel(["センター", "腕", "余り骨"], ["まばたき", "余り表情"]);
        var anim = MmdAnimation.Bind(motion, model);
        var buffer = MmdPoseBuffer.ForModel(model);

        anim.SampleInto(10, buffer);

        // 旋转 / 位移都写到绑定姿势「之上」（偏移是相对量，未加 LocalPositions）
        Assert.Equal(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f), buffer.Rotations[0]);
        Assert.Equal(new Vector3(2, -1, 0.5f), buffer.Translations[0]);
        Assert.Equal(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.8f), buffer.Rotations[1]);
        Assert.Equal(new Vector3(0, 3, 0), buffer.Translations[1]);

        // 未被驱动 → 保持复位值（绑定姿势 / 无表情）
        Assert.Equal(Quaternion.Identity, buffer.Rotations[2]);
        Assert.Equal(Vector3.Zero, buffer.Translations[2]);
        Assert.Equal(0f, buffer.MorphWeights[1]);

        Assert.Equal(1f, buffer.MorphWeights[0]);
        Assert.Equal(new[] { 0, 1 }, buffer.CoveredBones);
        Assert.Equal(new[] { 0 }, buffer.CoveredMorphs);
    }

    [Fact]
    public void SampleInto_IsPureFunction_RepeatedCallsAreIdentical()
    {
        var motion = MotionOf(
            [
                Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
                Bone("センター", 24, new Vector3(4, 0, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f)),
            ],
            [Morph("にこり", 0, 0f), Morph("にこり", 24, 0.8f)]);

        var model = MakeModel(["センター"], ["にこり"]);
        var anim = MmdAnimation.Bind(motion, model);
        var buffer = MmdPoseBuffer.ForModel(model);

        anim.SampleInto(9.5, buffer);
        var first = ((Quaternion[])buffer.Rotations.Clone(), (Vector3[])buffer.Translations.Clone(),
            (float[])buffer.MorphWeights.Clone());

        // 先在别的帧上跑一遍（若存在状态残留就会暴露），再回到 9.5
        anim.SampleInto(30, buffer);
        anim.SampleInto(9.5, buffer);

        Assert.Equal(first.Item1, buffer.Rotations);
        Assert.Equal(first.Item2, buffer.Translations);
        Assert.Equal(first.Item3, buffer.MorphWeights);
    }

    // ================================================================ Sample 重构回归（5d-2 验收）

    [Fact]
    public void Sample_EqualsSampleIntoPlusWriteBack_BitExact()
    {
        var motion = MotionOf(
            [
                Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
                Bone("センター", 15, new Vector3(3, -2, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.9f)),
                Bone("センター", 40, new Vector3(-1, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.4f)),
                Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
                Bone("腕", 40, new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.5f)),
            ],
            [Morph("まばたき", 0, 0f), Morph("まばたき", 20, 1f), Morph("にこり", 10, 0.5f)]);

        var boneNames = new[] { "センター", "腕", "余り" };
        var morphNames = new[] { "まばたき", "にこり" };
        var anim = MmdAnimation.Bind(motion, MakeModel(boneNames, morphNames));
        var buffer = MmdPoseBuffer.ForModel(MakeModel(boneNames, morphNames));

        foreach (double frame in RegressionFrames(anim))
        {
            var viaSample = MakeModel(boneNames, morphNames);
            anim.Sample(viaSample, frame);

            anim.SampleInto(frame, buffer);
            var viaWriteback = MakeModel(boneNames, morphNames);
            for (int i = 0; i < viaWriteback.LocalRotations.Length; i++)
            {
                viaWriteback.LocalRotations[i] = buffer.Rotations[i];
                viaWriteback.LocalTranslations[i] = viaWriteback.LocalPositions[i] + buffer.Translations[i];
            }
            for (int i = 0; i < viaWriteback.MorphRawWeights.Length; i++)
                viaWriteback.MorphRawWeights[i] = buffer.MorphWeights[i];

            Assert.Equal(viaWriteback.LocalRotations, viaSample.LocalRotations);
            Assert.Equal(viaWriteback.LocalTranslations, viaSample.LocalTranslations);
            Assert.Equal(viaWriteback.MorphRawWeights, viaSample.MorphRawWeights);
        }
    }

    /// <summary>
    /// 决定性的行为不变性：用真实 Motion.vmd 的全部真实轨道（774 骨 / 77 表情，含贝塞尔与钳制）
    /// 与独立照抄的旧实现逐位对照 —— 重构若动了任何一位都会在这里暴露。
    /// </summary>
    [Fact]
    public void Sample_MatchesLegacyReference_BitExact_OnRealVmd()
    {
        var expanded = MmdAnimation.FromVmd(VmdParser.Parse(File.ReadAllBytes(VmdPath)));
        var boneNames = expanded.BoneTracks.Select(t => t.Name).ToArray();
        var morphNames = expanded.MorphTracks.Select(t => t.Name).ToArray();
        // Motion.vmd 是用户可替换的 demo 资源，轨道数会变；只要求样本量足以覆盖多种轨道形态
        Assert.True(boneNames.Length > 20, $"骨轨道样本不足：{boneNames.Length}");

        var bound = expanded.Bind(MakeModel(boneNames, morphNames));
        Assert.Equal(boneNames.Length, bound.BoneTracks.Length);     // 合成模型名表保证全部命中
        Assert.Equal(morphNames.Length, bound.MorphTracks.Length);

        int checkedFrames = 0;
        foreach (double frame in RegressionFrames(bound))
        {
            var actual = MakeModel(boneNames, morphNames);
            var expected = MakeModel(boneNames, morphNames);

            bound.Sample(actual, frame);
            LegacySample(bound, expected, frame);

            Assert.Equal(expected.LocalRotations, actual.LocalRotations);
            Assert.Equal(expected.LocalTranslations, actual.LocalTranslations);
            Assert.Equal(expected.MorphRawWeights, actual.MorphRawWeights);
            checkedFrames++;
        }

        Assert.Equal(11, checkedFrames);
    }

    // ================================================================ MmdAnimationLayer 默认值

    [Fact]
    public void Layer_DefaultTrimIsContentRange()
    {
        // 首键在本地帧 10（VMD 自带前导空帧）
        var motion = MotionOf([
            Bone("腕", 10, Vector3.Zero, Quaternion.Identity),
            Bone("腕", 25, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f)),
        ]);
        var anim = MmdAnimation.FromVmd(motion);

        var layer = new MmdAnimationLayer(anim);

        Assert.Equal(10, layer.ContentStart);
        Assert.Equal(25, layer.ContentEnd);
        Assert.Equal(10, layer.TrimStart);
        Assert.Equal(25, layer.TrimEnd);
        Assert.Equal(1f, layer.Weight);
        Assert.False(layer.Loop);
        Assert.Equal(10, layer.ActiveStart);    // Offset 默认 0 + TrimStart = 内容起点
        Assert.Equal(25, layer.ActiveEnd);
        Assert.Equal(1f, layer.FadeAt(17));     // 无淡入淡出 ⇒ 包络恒 1
    }

    // ================================================================ Offset（任意帧导入）

    [Fact]
    public void Layer_Offset_MapsAnimationFrameZeroToTimelineFrame()
    {
        var motion = MotionOf([
            Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
            Bone("センター", 10, new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f)),
        ]);
        var anim = MmdAnimation.FromVmd(motion);

        var layer = new MmdAnimationLayer(anim) { Offset = 30 };

        Assert.Equal(30, layer.ActiveStart);    // 内容起点 0 ⇒ 30 + 0
        Assert.Equal(40, layer.ActiveEnd);
        Assert.True(layer.IsActive(30));
        Assert.Equal(0, layer.ToLocal(30));
        Assert.Equal(10, layer.ToLocal(40));

        // 时间轴 30 处的姿势 == 源动画帧 0（与独立旧实现在源帧 0 的结果逐位一致）
        var boneNames = new[] { "センター" };
        var viaLayer = MakeModel(boneNames);
        var viaSource = MakeModel(boneNames);
        anim.Sample(viaLayer, layer.ToLocal(30));
        LegacySample(anim, viaSource, 0);

        Assert.Equal(viaSource.LocalRotations, viaLayer.LocalRotations);
        Assert.Equal(viaSource.LocalTranslations, viaLayer.LocalTranslations);
    }

    // ================================================================ Trim（空白保留）

    [Fact]
    public void Layer_DefaultTrim_KeepsLeadingBlank()
    {
        // 首键在本地帧 10：导入到时间轴 30 ⇒ 活跃区间 [40, 50]，[30, 40) 不贡献
        var motion = MotionOf([
            Bone("腕", 10, Vector3.Zero, Quaternion.Identity),
            Bone("腕", 20, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)),
        ]);
        var anim = MmdAnimation.FromVmd(motion);

        var layer = new MmdAnimationLayer(anim) { Offset = 30 };

        Assert.False(layer.IsActive(29.9));
        Assert.False(layer.IsActive(30));
        Assert.False(layer.IsActive(39.9));     // 空白保留：不被首键外推填满
        Assert.True(layer.IsActive(40));
        Assert.True(layer.IsActive(50));
        Assert.False(layer.IsActive(50.1));     // 非 Loop ⇒ 区间外不贡献

        // 显式把 TrimStart 设到内容起点之前 ⇒ 等价「首键外推」（显式选择，不是默认）
        var extrapolating = new MmdAnimationLayer(anim) { Offset = 30, TrimStart = 0 };
        Assert.Equal(30, extrapolating.ActiveStart);
        Assert.True(extrapolating.IsActive(30));
        Assert.Equal(0, extrapolating.ToLocal(30));     // 本地帧 0（早于首键）

        // ToLocal 不做钳制（那是轨道的职责）：本地帧 0 由轨道钳到首键（本地帧 10）
        var viaExplicitTrim = MakeModel(["腕"]);
        var viaFirstKey = MakeModel(["腕"]);
        anim.Sample(viaExplicitTrim, extrapolating.ToLocal(30));
        anim.Sample(viaFirstKey, layer.ContentStart);   // 本地帧 10 = 首键
        Assert.Equal(viaFirstKey.LocalRotations, viaExplicitTrim.LocalRotations);
        Assert.Equal(viaFirstKey.LocalTranslations, viaExplicitTrim.LocalTranslations);
    }

    [Fact]
    public void Layer_AfterTrimEnd_ContributesNothing_UnlessLoop()
    {
        var motion = MotionOf([
            Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
            Bone("腕", 10, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)),
        ]);
        var anim = MmdAnimation.FromVmd(motion);

        var once = new MmdAnimationLayer(anim);
        Assert.True(once.IsActive(10));
        Assert.False(once.IsActive(10.001));
        Assert.False(once.IsActive(500));

        var loop = new MmdAnimationLayer(anim) { Loop = true };
        Assert.True(loop.IsActive(10.001));
        Assert.True(loop.IsActive(500));
    }

    // ================================================================ Loop

    [Fact]
    public void Layer_Loop_WrapsLocalFrameIntoContentRange()
    {
        var motion = MotionOf([
            Bone("腕", 100, Vector3.Zero, Quaternion.Identity),
            Bone("腕", 200, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)),
        ]);
        var anim = MmdAnimation.FromVmd(motion);

        var layer = new MmdAnimationLayer(anim) { Loop = true };
        Assert.Equal(100, layer.TrimStart);
        Assert.Equal(200, layer.TrimEnd);

        Assert.Equal(100, layer.ToLocal(100));
        Assert.Equal(150, layer.ToLocal(150));
        Assert.Equal(200, layer.ToLocal(200));      // 端点原样（区间闭）
        Assert.Equal(100.5, layer.ToLocal(200.5));  // 越过末端 ⇒ 回卷
        Assert.Equal(150, layer.ToLocal(350));      // 整段回卷
        Assert.Equal(110, layer.ToLocal(110 + 1000));

        // 起点之前即使 Loop 也不贡献（动效还没开始 ⇒ 空白保留）
        Assert.False(layer.IsActive(99));
        Assert.True(layer.IsActive(100));
        Assert.True(layer.IsActive(1_000_000));
    }

    [Fact]
    public void Layer_Loop_DegenerateSingleFrameRange_DoesNotWrap()
    {
        var motion = MotionOf([Bone("腕", 7, Vector3.Zero, Quaternion.Identity)]);
        var anim = MmdAnimation.FromVmd(motion);

        var layer = new MmdAnimationLayer(anim) { Loop = true };
        Assert.Equal(7, layer.TrimStart);
        Assert.Equal(7, layer.TrimEnd);

        Assert.Equal(7, layer.ToLocal(7));
        Assert.Equal(7, layer.ToLocal(999));    // 单帧区间：回卷无意义，退回 TrimStart
    }

    // ================================================================ Weight / Fade

    [Fact]
    public void Layer_WeightZero_IsNeverActive()
    {
        var motion = MotionOf([
            Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
            Bone("腕", 10, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)),
        ]);
        var anim = MmdAnimation.FromVmd(motion);

        var layer = new MmdAnimationLayer(anim) { Weight = 0f };
        Assert.False(layer.IsActive(0));
        Assert.False(layer.IsActive(5));

        layer.Weight = 0.5f;
        Assert.True(layer.IsActive(5));

        layer.Weight = -1f;
        Assert.False(layer.IsActive(5));
    }

    [Fact]
    public void Layer_FadeEnvelope_IsLinearRamp()
    {
        var motion = MotionOf([
            Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
            Bone("腕", 100, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)),
        ]);
        var anim = MmdAnimation.FromVmd(motion);

        var layer = new MmdAnimationLayer(anim) { FadeIn = 10, FadeOut = 20 };
        Assert.Equal(0, layer.ActiveStart);
        Assert.Equal(100, layer.ActiveEnd);

        Assert.Equal(0f, layer.FadeAt(0));          // 淡入起点
        Assert.Equal(0.5f, layer.FadeAt(5));
        Assert.Equal(1f, layer.FadeAt(10));         // 淡入结束
        Assert.Equal(1f, layer.FadeAt(50));         // 中段
        Assert.Equal(1f, layer.FadeAt(80));         // 淡出起点
        Assert.Equal(0.5f, layer.FadeAt(90));
        Assert.Equal(0f, layer.FadeAt(100));        // 淡出终点

        // 淡入首帧包络为 0，但该层仍算活跃（可见性投票不受 fade 影响）
        Assert.True(layer.IsActive(0));
    }

    // ================================================================ 表示枠（按本地帧采样）

    [Fact]
    public void Layer_IsVisibleAt_UsesPropertyTrackAtLocalFrame()
    {
        var motion = MotionOf(
            [
                Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
                Bone("腕", 100, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)),
            ],
            properties: [
                new VmdPropertyKey(10, false, []),
                new VmdPropertyKey(20, true, []),
            ]);
        var anim = MmdAnimation.FromVmd(motion);

        var layer = new MmdAnimationLayer(anim) { Offset = 30 };

        // 时间轴 35 → 本地 5（早于首键 ⇒ 可见）；45 → 本地 15（隐藏窗口）；55 → 本地 25（恢复可见）
        Assert.True(layer.IsVisibleAt(layer.ToLocal(35)));
        Assert.False(layer.IsVisibleAt(layer.ToLocal(45)));
        Assert.True(layer.IsVisibleAt(layer.ToLocal(55)));
    }
}

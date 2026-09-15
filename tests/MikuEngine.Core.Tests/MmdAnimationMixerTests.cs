using System.Numerics;
using System.Text;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// Step 5d-3：<see cref="MmdAnimationMixer"/> 多动效混合（docs/2026-09-11-anim-blend-plan.md 3.3/3.4/3.6）。
///
/// 钉死的性质：
/// 1. 单层 <c>Weight=1, Offset=0</c> ⇒ 与单动效 <see cref="MmdAnimation.Sample"/> <b>位级一致</b>（回归基线）；
/// 2. 残差按「覆盖该项的权重和」混回绑定姿势（不是全局权重）；
/// 3. 未覆盖项 = 绑定姿势 / 0（每帧整体写回，无残留）；
/// 4. 可见性 = 活跃层阶梯布尔 AND，<b>不</b>被权重缩放（babylon-mmd 的 0.5 半透明反例）；
/// 5. 帧号纯函数：幂等、seek ≡ 连续播放。
/// </summary>
public class MmdAnimationMixerTests
{
    // ---------------------------------------------------------------- 合成构件（同 MmdAnimationLayerTests）

    private const byte LinearA = 20;
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

    private static readonly string VmdPath = TestAssets.Motion("Motion.vmd");

    private static void AssertQuaternionClose(Quaternion expected, Quaternion actual, float epsilon = 1e-5f)
    {
        // 注意：不能用 1 - eps²（eps² 在 float 下下溢为 0，阈值变成 > 1f，恒假）
        Assert.True(Quaternion.Dot(expected, actual) > 1f - epsilon,
            $"旋转不一致：期望 {expected}，实际 {actual}");
    }

    // ================================================================ 单层一致性（回归基线）

    [Fact]
    public void SingleLayer_WeightOne_EqualsSingleAnimationSample_BitExact()
    {
        // 真实 Motion.vmd 的全部轨道（含贝塞尔与端键钳制）—— 混合器路径若与单动效差一位就在这里暴露
        var expanded = MmdAnimation.FromVmd(VmdParser.Parse(File.ReadAllBytes(VmdPath)));
        var boneNames = expanded.BoneTracks.Select(t => t.Name).ToArray();
        var morphNames = expanded.MorphTracks.Select(t => t.Name).ToArray();

        var bound = expanded.Bind(MakeModel(boneNames, morphNames));

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(bound) { Weight = 1f, Offset = 0 });
        var viaMixer = MakeModel(boneNames, morphNames);

        double[] frames = [bound.StartFrame, bound.StartFrame + 0.5, 7.25, 100.0,
            (bound.StartFrame + bound.EndFrame) * 0.5, bound.EndFrame - 0.5, bound.EndFrame];
        // 注意：帧样本只取活跃区间 [StartFrame, EndFrame] 之内 —— 区间外「不贡献 = 绑定姿势」是
        // 混合器的空白保留语义，与单动效 Sample 的端键钳制**有意不同**，不在此对照（层测试已覆盖）。

        foreach (double frame in frames)
        {
            var viaSample = MakeModel(boneNames, morphNames);
            bound.Sample(viaSample, frame);
            mixer.Evaluate(viaMixer, frame);

            Assert.Equal(viaSample.LocalRotations, viaMixer.LocalRotations);
            Assert.Equal(viaSample.LocalTranslations, viaMixer.LocalTranslations);
            Assert.Equal(viaSample.MorphRawWeights, viaMixer.MorphRawWeights);
        }
    }

    [Fact]
    public void SingleLayer_Visibility_MatchesAnimationSampleVisible()
    {
        var motion = MotionOf(
            [Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
             Bone("腕", 30, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f))],
            properties: [new VmdPropertyKey(10, false, []), new VmdPropertyKey(20, true, [])]);
        var anim = MmdAnimation.Bind(motion, MakeModel(["腕"]));

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(anim));
        var model = MakeModel(["腕"]);

        mixer.Evaluate(model, 5);
        Assert.True(mixer.Visible);         // 早于隐藏键 ⇒ 可见
        Assert.True(model.Visible);
        mixer.Evaluate(model, 15);
        Assert.False(mixer.Visible);        // 隐藏窗口
        Assert.False(model.Visible);
        mixer.Evaluate(model, 25);
        Assert.True(mixer.Visible);
        Assert.True(model.Visible);
    }

    // ================================================================ 旋转 / 平移混合

    [Fact]
    public void Blend_TwoLayers_EqualWeights_AverageRotations()
    {
        var qA = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.8f);
        var qB = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f);
        var motionA = MotionOf([Bone("センター", 0, Vector3.Zero, qA)]);
        var motionB = MotionOf([Bone("センター", 0, Vector3.Zero, qB)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel(["センター"]))) { Weight = 0.5f });
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, MakeModel(["センター"]))) { Weight = 0.5f });

        var model = MakeModel(["センター"]);
        mixer.Evaluate(model, 0);

        // 0.5qA + 0.5qB 归一 = 两者夹角的半程旋转（nlerp）
        var expected = Quaternion.Normalize(qA * 0.5f + qB * 0.5f);
        AssertQuaternionClose(expected, model.LocalRotations[0]);
        // 平移各自为 0 偏移 ⇒ 保持绑定位置
        Assert.Equal(model.LocalPositions[0], model.LocalTranslations[0]);
    }

    [Fact]
    public void Blend_HemisphereAligned_EquivalentRotations_BlendToSameResult()
    {
        // 同一旋转的两种四元数表示（q 与 −q）：半球对齐必须让两者混合结果一致，
        // 而不是对消成 Identity（这是 nlerp 顺序无关性的关键）
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f);
        var motionA = MotionOf([Bone("センター", 0, Vector3.Zero, q)]);
        var motionB = MotionOf([Bone("センター", 0, Vector3.Zero, -q)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel(["センター"]))) { Weight = 0.5f });
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, MakeModel(["センター"]))) { Weight = 0.5f });

        var model = MakeModel(["センター"]);
        mixer.Evaluate(model, 0);

        AssertQuaternionClose(q, model.LocalRotations[0]);   // 对齐后 0.5q + 0.5q = q
    }

    [Fact]
    public void Blend_WeightSumAboveOne_Normalizes()
    {
        // Σw = 2 > 1 ⇒ norm = 0.5：两层同旋转、各满权 ⇒ 结果 == 单层满权
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.9f);
        var motion = MotionOf([Bone("センター", 0, Vector3.Zero, q)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motion, MakeModel(["センター"]))) { Weight = 1f });
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motion, MakeModel(["センター"]))) { Weight = 1f });

        var model = MakeModel(["センター"]);
        mixer.Evaluate(model, 0);

        AssertQuaternionClose(q, model.LocalRotations[0]);
    }

    // ================================================================ 残差（Σw < 1 混回绑定）

    [Fact]
    public void Blend_WeightBelowOne_BlendsTowardBindPose()
    {
        // 90° 旋转、权重 0.5 ⇒ 残差 0.5 混回 Identity ⇒ 半程 45°
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var motion = MotionOf([Bone("センター", 0, Vector3.Zero, q)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motion, MakeModel(["センター"]))) { Weight = 0.5f });

        var model = MakeModel(["センター"]);
        mixer.Evaluate(model, 0);

        var expected = Quaternion.Normalize(q * 0.5f + Quaternion.Identity * 0.5f);
        AssertQuaternionClose(expected, model.LocalRotations[0]);
        Assert.Equal(MathF.PI / 4f, 2f * MathF.Acos(System.Math.Clamp(model.LocalRotations[0].W, -1f, 1f)), 3f);
    }

    [Fact]
    public void Blend_TranslationResidual_IsBindPosition()
    {
        // 权重 0.25 的平移层：偏移按 0.25 计入，其余 0.75 回绑定位移
        var offset = new Vector3(8, -4, 2);
        var motion = MotionOf([Bone("センター", 0, offset, Quaternion.Identity)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motion, MakeModel(["センター"]))) { Weight = 0.25f });

        var model = MakeModel(["センター"]);
        mixer.Evaluate(model, 0);

        Assert.Equal(model.LocalPositions[0] + 0.25f * offset, model.LocalTranslations[0]);
    }

    [Fact]
    public void Blend_UncoveredBone_IsBindPose()
    {
        var motionA = MotionOf(
            [Bone("センター", 0, new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f))]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel(["センター", "腕"]))));

        var model = MakeModel(["センター", "腕"]);
        mixer.Evaluate(model, 0);

        Assert.NotEqual(Quaternion.Identity, model.LocalRotations[0]);      // 覆盖项被驱动
        Assert.Equal(Quaternion.Identity, model.LocalRotations[1]);         // 未覆盖项 = 绑定
        Assert.Equal(model.LocalPositions[1], model.LocalTranslations[1]);
    }

    [Fact]
    public void Blend_UncoveredMorph_IsZero()
    {
        var motionA = MotionOf([], [Morph("にこり", 0, 1f)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel(["センター"], ["にこり", "まばたき"]))));

        var model = MakeModel(["センター"], ["にこり", "まばたき"]);
        mixer.Evaluate(model, 0);

        Assert.Equal(1f, model.MorphRawWeights[0]);
        Assert.Equal(0f, model.MorphRawWeights[1]);     // 未覆盖 = 0（不是残留）
    }

    // ================================================================ morph 加权和

    [Fact]
    public void Blend_MorphWeights_AreWeightedSums()
    {
        // 两层同 morph：0.5×0.8 + 0.5×0.4 = 0.6
        var motionA = MotionOf([], [Morph("にこり", 0, 0.8f)]);
        var motionB = MotionOf([], [Morph("にこり", 0, 0.4f)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel([], ["にこり"]))) { Weight = 0.5f });
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, MakeModel([], ["にこり"]))) { Weight = 0.5f });

        var model = MakeModel([], ["にこり"]);
        mixer.Evaluate(model, 0);

        Assert.Equal(0.6f, model.MorphRawWeights[0], 5f);
    }

    [Fact]
    public void Blend_MorphsAcrossLayers_UnionNotOverride()
    {
        // MMD「表情槽」语义的推广：不同层的不同 morph 各自独立累加，互不覆盖
        var motionA = MotionOf([], [Morph("にこり", 0, 1f)]);
        var motionB = MotionOf([], [Morph("まばたき", 0, 0.7f)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel([], ["にこり", "まばたき"]))));
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, MakeModel([], ["にこり", "まばたき"]))));

        var model = MakeModel([], ["にこり", "まばたき"]);
        mixer.Evaluate(model, 0);

        Assert.Equal(1f, model.MorphRawWeights[0]);
        Assert.Equal(0.7f, model.MorphRawWeights[1]);
    }

    [Fact]
    public void Blend_SharedMorph_CompetingWeights_Normalizes()
    {
        // 同一 morph 被两层竞争且覆盖权重和 > 1 ⇒ 逐项归一：0.5×1 + 0.5×0.7 = 0.85
        var motionA = MotionOf([], [Morph("にこり", 0, 1f)]);
        var motionB = MotionOf([], [Morph("にこり", 0, 0.7f)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel([], ["にこり"]))) { Weight = 1f });
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, MakeModel([], ["にこり"]))) { Weight = 1f });

        var model = MakeModel([], ["にこり"]);
        mixer.Evaluate(model, 0);

        Assert.Equal(0.85f, model.MorphRawWeights[0], 5f);
    }

    // ================================================================ 可见性 AND（3.6）

    private static (MmdAnimation Visible, MmdAnimation Hidden) MakeVisibilityPair()
    {
        var visible = MotionOf(
            [Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
             Bone("腕", 20, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f))]);
        var hidden = MotionOf(
            [Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
             Bone("腕", 20, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f))],
            properties: [new VmdPropertyKey(10, false, [])]);
        var model = MakeModel(["腕"]);
        return (MmdAnimation.Bind(visible, model), MmdAnimation.Bind(hidden, model));
    }

    [Fact]
    public void Blend_Visibility_IsAnd_AnyHiddenLayerHidesModel()
    {
        var (visible, hidden) = MakeVisibilityPair();

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(visible));
        mixer.AddLayer(new MmdAnimationLayer(hidden));

        var model = MakeModel(["腕"]);
        mixer.Evaluate(model, 5);       // 隐藏层本地 5：早于其隐藏键 ⇒ 可见
        Assert.True(mixer.Visible);
        Assert.True(model.Visible);

        mixer.Evaluate(model, 15);      // 隐藏层本地 15：隐藏窗口
        Assert.False(mixer.Visible);
        Assert.False(model.Visible);
    }

    [Fact]
    public void Blend_Visibility_NotScaledByWeight_BabylonHalfCase()
    {
        // 钉死 babylon-mmd 的 0.5 反例：可见层 0.5 + 隐藏层 0.5 ⇒ 必须完全不可见，不是 50% 透明
        var (visible, hidden) = MakeVisibilityPair();

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(visible) { Weight = 0.5f });
        mixer.AddLayer(new MmdAnimationLayer(hidden) { Weight = 0.5f });

        var model = MakeModel(["腕"]);
        mixer.Evaluate(model, 15);

        Assert.False(mixer.Visible);
        Assert.False(model.Visible);
    }

    [Fact]
    public void Blend_InactiveLayer_DoesNotVetoVisibility()
    {
        var (visible, hidden) = MakeVisibilityPair();

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(visible));
        // 隐藏层放到时间轴 [100, 120]：时间轴 15 处不活跃 ⇒ 不投票
        mixer.AddLayer(new MmdAnimationLayer(hidden) { Offset = 100 });

        var model = MakeModel(["腕"]);
        mixer.Evaluate(model, 15);

        Assert.True(mixer.Visible);
        Assert.True(model.Visible);
    }

    [Fact]
    public void Evaluate_NoActiveLayers_ModelResetsToBindPose_AndVisible()
    {
        var motion = MotionOf(
            [Bone("センター", 0, new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f)),
             Bone("センター", 20, new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f))],
            [Morph("にこり", 0, 1f), Morph("にこり", 20, 1f)]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motion, MakeModel(["センター"], ["にこり"]))));

        var model = MakeModel(["センター"], ["にこり"]);
        mixer.Evaluate(model, 10);      // 活跃：有姿势
        Assert.NotEqual(Quaternion.Identity, model.LocalRotations[0]);
        Assert.Equal(1f, model.MorphRawWeights[0]);

        mixer.Evaluate(model, 500);     // 层不活跃：整体复位（不是保留上一帧的姿势）
        Assert.Equal(Quaternion.Identity, model.LocalRotations[0]);
        Assert.Equal(model.LocalPositions[0], model.LocalTranslations[0]);
        Assert.Equal(0f, model.MorphRawWeights[0]);
        Assert.True(mixer.Visible);     // 无活跃层 ⇒ 可见
    }

    // ================================================================ 纯函数性质

    [Fact]
    public void Evaluate_IsIdempotent()
    {
        var motionA = MotionOf(
            [Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
             Bone("センター", 20, new Vector3(2, 1, -1), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f))],
            [Morph("にこり", 0, 0f), Morph("にこり", 20, 0.9f)]);
        var motionB = MotionOf([Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
                                Bone("腕", 20, new Vector3(0, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.6f))]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel(["センター", "腕"], ["にこり"]))) { Weight = 0.7f });
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, MakeModel(["センター", "腕"], ["にこり"]))) { Weight = 0.4f });

        var model = MakeModel(["センター", "腕"], ["にこり"]);
        mixer.Evaluate(model, 13.5);
        var once = ((Quaternion[])model.LocalRotations.Clone(), (Vector3[])model.LocalTranslations.Clone(),
            (float[])model.MorphRawWeights.Clone());

        mixer.Evaluate(model, 13.5);    // 同帧重复求值
        Assert.Equal(once.Item1, model.LocalRotations);
        Assert.Equal(once.Item2, model.LocalTranslations);
        Assert.Equal(once.Item3, model.MorphRawWeights);

        mixer.Evaluate(model, 99);      // 先跑到别的帧再回来
        mixer.Evaluate(model, 13.5);
        Assert.Equal(once.Item1, model.LocalRotations);
        Assert.Equal(once.Item2, model.LocalTranslations);
        Assert.Equal(once.Item3, model.MorphRawWeights);
    }

    [Fact]
    public void Evaluate_SeekEqualsStepByStep()
    {
        var motionA = MotionOf(
            [Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
             Bone("センター", 30, new Vector3(3, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f))],
            [Morph("にこり", 0, 0f), Morph("にこり", 30, 1f)]);
        var motionB = MotionOf(
            [Bone("腕", 5, Vector3.Zero, Quaternion.Identity),
             Bone("腕", 25, new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f))]);

        var mixer = new MmdAnimationMixer();
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, MakeModel(["センター", "腕"], ["にこり"]))) { Weight = 0.9f });
        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, MakeModel(["センター", "腕"], ["にこり"]))) { Weight = 0.6f });

        var stepped = MakeModel(["センター", "腕"], ["にこり"]);
        for (double f = 0; f <= 30; f += 5)
            mixer.Evaluate(stepped, f);

        var seeked = MakeModel(["センター", "腕"], ["にこり"]);
        mixer.Evaluate(seeked, 30);

        Assert.Equal(stepped.LocalRotations, seeked.LocalRotations);
        Assert.Equal(stepped.LocalTranslations, seeked.LocalTranslations);
        Assert.Equal(stepped.MorphRawWeights, seeked.MorphRawWeights);
        Assert.Equal(stepped.Visible, seeked.Visible);
    }

    // ================================================================ ActiveRange / 杂项

    [Fact]
    public void Mixer_ActiveRange_IsUnionOfLayerIntervals()
    {
        var motionA = MotionOf([Bone("腕", 10, Vector3.Zero, Quaternion.Identity),
                                Bone("腕", 30, Vector3.Zero, Quaternion.Identity)]);
        var motionB = MotionOf([Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
                                Bone("腕", 100, Vector3.Zero, Quaternion.Identity)]);
        var model = MakeModel(["腕"]);

        var mixer = new MmdAnimationMixer();
        Assert.Equal((0.0, 0.0), mixer.ActiveRange);        // 无层

        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, model)) { Offset = 20 });
        Assert.Equal((30.0, 50.0), mixer.ActiveRange);      // [20+10, 20+30]

        mixer.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, model)) { Offset = 40 });
        Assert.Equal((30.0, 140.0), mixer.ActiveRange);     // 并集 [30,50] ∪ [40,140]
    }

    [Fact]
    public void Evaluate_EmptyMixer_ResetsModelAndKeepsVisible()
    {
        var mixer = new MmdAnimationMixer();
        var model = MakeModel(["センター"], ["にこり"]);

        model.LocalRotations[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        model.MorphRawWeights[0] = 0.5f;
        model.Visible = false;

        mixer.Evaluate(model, 42);

        Assert.Equal(Quaternion.Identity, model.LocalRotations[0]);
        Assert.Equal(0f, model.MorphRawWeights[0]);
        Assert.True(model.Visible);
    }

    // ================================================================ 真实模型集成（Demo 同款资源）

    [Fact]
    public void Mixer_OnRealDemoModel_DrivesCenterBoneAndFacialMorphs()
    {
        // Demo 的四层组合（Motion + Lips + Eyes + Facial）打在真实 Model/1/1.pmx 上：
        // 锁死「解析 → 绑定 → 混合 → 写回」在真实资源上确实驱动骨与 morph。
        // （此用例与 --anim-smoke 互为印证；资源被替换时按结构性条件跳过。）
        var modelPath = TestAssets.Model();
        var model = SkeletalModelConverter.Convert(PmxParser.Parse(File.ReadAllBytes(modelPath)));

        int centerIndex = model.FindBone("センター");
        Assert.True(centerIndex >= 0, "模型没有 センター 骨");
        Assert.True(model.MorphNames.Length > 10, "模型 morph 样本不足");

        var mixer = new MmdAnimationMixer();
        foreach (string file in new[] { "Motion.vmd", "Lips.vmd", "Eyes.vmd", "Facial.vmd" })
        {
            string path = TestAssets.Motion(file);
            var vmd = VmdParser.Parse(File.ReadAllBytes(path));
            var expanded = MmdAnimation.FromVmd(vmd);
            mixer.AddLayer(new MmdAnimationLayer(expanded.Bind(model)));

            if (file == "Motion.vmd")
            {
                var missing = expanded.BoneTracks
                    .Where(t => model.FindBone(t.Name) < 0)
                    .Select(t => t.Name)
                    .ToArray();
                Assert.True(missing.Length < expanded.BoneTracks.Length,
                    $"Motion.vmd 全部 {expanded.BoneTracks.Length} 条骨轨道都未绑定到该模型，未命中示例：{string.Join(" | ", missing.Take(10))}");
            }
        }

        // センター 在 Motion.vmd 里逐帧有键；帧 190 附近旋转为恒等、但位置大幅偏移
        // （原始字节实测：pos ≈ (0.83, -1.84, -6.89)），因此该帧的平移必须显著偏离绑定位置。
        mixer.Evaluate(model, 190);
        var bind = model.LocalPositions[centerIndex];
        var trans = model.LocalTranslations[centerIndex];
        float offsetLen = System.Numerics.Vector3.Distance(bind, trans);
        Assert.True(offsetLen > 1f, $"センター 在帧 190 的平移偏移仅 {offsetLen:F3}，骨动效层未生效");

        // 表情层：至少一个 morph 权重非零
        int activeMorphs = model.MorphRawWeights.Count(w => w != 0f);
        Assert.True(activeMorphs > 0, "表情 / 口型层未生效（morph 权重全 0）");
        Assert.True(model.Visible);
    }
}

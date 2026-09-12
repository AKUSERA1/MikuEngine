using System.Numerics;
using System.Text;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// Step 5d-4：<see cref="MmdTimeline"/> 多层时间轴（播放控制 + 层增删）。
///
/// 方案 6 验收锚点：
/// 1. 时间轴 30 处姿势 == 源动画帧 0（导入映射）；
/// 2. <c>[30, 30 + ContentStart)</c> 零贡献（空白保留）；
/// 3. seek ≡ 连续播放。
/// 另含 5d-3 讨论定下的层清除契约：RemoveLayer / ClearLayers 下一帧自动回绑定姿势（无残留），
/// 且清除后动效对象图可被 GC 整体回收（WeakReference 钉死，防隐藏根）。
/// </summary>
public class MmdTimelineTests
{
    // ---------------------------------------------------------------- 合成构件（同 MmdAnimationMixerTests）

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

    // ================================================================ 导入映射（方案 §6 锚点 1）

    [Fact]
    public void Timeline_ImportAtFrame30_PosesMatchSourceFrame0()
    {
        var motion = MotionOf(
            [Bone("センター", 0, new Vector3(1, 2, 3), Quaternion.Identity),
             Bone("センター", 10, new Vector3(4, 5, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.8f))]);

        var bound = MmdAnimation.Bind(motion, MakeModel(["センター"]));
        var timeline = new MmdTimeline();
        timeline.AddLayer(new MmdAnimationLayer(bound), importAt: 30);

        Assert.Equal((30.0, 40.0), timeline.Range());

        // 时间轴 30 处的姿势 == 源动画帧 0
        var viaTimeline = MakeModel(["センター"]);
        timeline.Seek(30);
        timeline.Apply(viaTimeline);

        var viaSource = MakeModel(["センター"]);
        bound.Sample(viaSource, 0);

        Assert.Equal(viaSource.LocalRotations, viaTimeline.LocalRotations);
        Assert.Equal(viaSource.LocalTranslations, viaTimeline.LocalTranslations);
    }

    [Fact]
    public void Timeline_BlankBeforeImport_ContributesNothing()
    {
        // 方案 §6 锚点 2：内容起点在本地帧 10，导入到 30 ⇒ [30, 40) 零贡献（空白保留）
        var motion = MotionOf(
            [Bone("腕", 10, Vector3.Zero, Quaternion.Identity),
             Bone("腕", 25, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f))]);

        var bound = MmdAnimation.Bind(motion, MakeModel(["腕"]));
        var timeline = new MmdTimeline();
        timeline.AddLayer(new MmdAnimationLayer(bound), importAt: 30);

        Assert.Equal((40.0, 55.0), timeline.Range());   // 30+10 .. 30+25

        var model = MakeModel(["腕"]);
        foreach (double frame in new[] { 30.0, 35.0, 39.9 })
        {
            timeline.Seek(frame);
            timeline.Apply(model);
            Assert.Equal(Quaternion.Identity, model.LocalRotations[0]);
            Assert.Equal(model.LocalPositions[0], model.LocalTranslations[0]);
        }

        timeline.Seek(40);      // 活跃起点：立即有内容
        timeline.Apply(model);
        Assert.Equal(Quaternion.Identity, model.LocalRotations[0]);   // 本地 10 = 首键（Identity）
        Assert.Equal(model.LocalPositions[0], model.LocalTranslations[0]);

        timeline.Seek(50);      // 本地 20：已经动起来
        timeline.Apply(model);
        Assert.NotEqual(Quaternion.Identity, model.LocalRotations[0]);
    }

    // ================================================================ seek ≡ 连续播放（锚点 3）

    [Fact]
    public void Timeline_SeekEqualsStepByStep_MultipleLayers()
    {
        var motionA = MotionOf(
            [Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
             Bone("センター", 30, new Vector3(3, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f))],
            [Morph("にこり", 0, 0f), Morph("にこり", 30, 1f)]);
        var motionB = MotionOf(
            [Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
             Bone("腕", 20, new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f))]);

        var model = MakeModel(["センター", "腕"], ["にこり"]);
        var timeline = new MmdTimeline();
        timeline.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, model)) { Weight = 0.9f });
        timeline.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, model)) { Weight = 0.6f });

        var stepped = MakeModel(["センター", "腕"], ["にこり"]);
        for (double f = 0; f <= 30; f += 5)
        {
            timeline.Seek(f);
            timeline.Apply(stepped);
        }

        var seeked = MakeModel(["センター", "腕"], ["にこり"]);
        timeline.Seek(30);
        timeline.Apply(seeked);

        Assert.Equal(stepped.LocalRotations, seeked.LocalRotations);
        Assert.Equal(stepped.LocalTranslations, seeked.LocalTranslations);
        Assert.Equal(stepped.MorphRawWeights, seeked.MorphRawWeights);

        // Advance 与 Seek 同帧结果一致（连续帧号上插值，不取整）
        var advanced = MakeModel(["センター", "腕"], ["にこり"]);
        timeline.Seek(0);
        timeline.Advance(1.0);          // 30fps × 1s = 帧 30
        timeline.Apply(advanced);
        Assert.Equal(30.0, timeline.CurrentFrame, 5);
        Assert.Equal(seeked.LocalRotations, advanced.LocalRotations);
    }

    // ================================================================ 播放控制

    [Fact]
    public void Timeline_Advance_LoopsWhenEnabled_ClampsOtherwise()
    {
        var motion = MotionOf(
            [Bone("腕", 10, Vector3.Zero, Quaternion.Identity),
             Bone("腕", 30, Vector3.Zero, Quaternion.Identity)]);
        var timeline = new MmdTimeline();
        timeline.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motion, MakeModel(["腕"]))));

        Assert.Equal((10.0, 30.0), timeline.Range());

        timeline.Seek(25);
        timeline.Advance(1.0);          // 25 + 30 = 55 > 30 ⇒ 钳制到末端
        Assert.Equal(30.0, timeline.CurrentFrame);      // Loop=false 钳制
        timeline.Advance(1.0);
        Assert.Equal(30.0, timeline.CurrentFrame);      // 钳制后保持

        timeline.Loop = true;
        timeline.Seek(10);
        timeline.Advance(1.0);          // 40 越过末端 ⇒ 回卷 10 + (40-10)%20 = 20
        Assert.Equal(20.0, timeline.CurrentFrame);

        timeline.Paused = true;
        timeline.Advance(10.0);
        Assert.Equal(20.0, timeline.CurrentFrame);      // 暂停无操作
    }

    [Fact]
    public void Timeline_StructuralChange_RewritesRange()
    {
        var motionA = MotionOf([Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
                                Bone("腕", 20, Vector3.Zero, Quaternion.Identity)]);
        var motionB = MotionOf([Bone("腕", 100, Vector3.Zero, Quaternion.Identity),
                                Bone("腕", 200, Vector3.Zero, Quaternion.Identity)]);
        var model = MakeModel(["腕"]);

        var timeline = new MmdTimeline();
        Assert.Equal((0.0, 0.0), timeline.Range());     // 空时间轴

        var layerA = timeline.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, model)));
        Assert.Equal((0.0, 20.0), timeline.Range());

        var layerB = timeline.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionB, model)), importAt: 40);
        Assert.Equal((0.0, 240.0), timeline.Range());   // 并集扩展

        timeline.Seek(240);
        timeline.RemoveLayer(layerB);
        Assert.Equal((0.0, 20.0), timeline.Range());
        Assert.Equal(240.0, timeline.CurrentFrame);     // 区间缩短不自动改游标；该帧各层不活跃 ⇒ 绑定姿势

        timeline.ClearLayers();
        Assert.Equal((0.0, 0.0), timeline.Range());
    }

    // ================================================================ 层清除（5d-3 讨论定下的契约）

    [Fact]
    public void Timeline_RemoveLayer_TracksReturnToBindPose_AndVisibilityVoteLifted()
    {
        var motionA = MotionOf(
            [Bone("センター", 0, new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f)),
             Bone("センター", 20, new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f))]);
        var hidden = MotionOf(
            [Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
             Bone("腕", 20, Vector3.Zero, Quaternion.Identity)],
            properties: [new VmdPropertyKey(0, false, [])]);

        var model = MakeModel(["センター", "腕"], ["にこり"]);
        var timeline = new MmdTimeline();
        var layerA = timeline.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(motionA, model)));
        var layerHidden = timeline.AddLayer(new MmdAnimationLayer(MmdAnimation.Bind(hidden, model)));

        timeline.Seek(10);
        timeline.Apply(model);
        Assert.False(model.Visible);                    // 隐藏层一票否决

        Assert.True(timeline.RemoveLayer(layerHidden));
        timeline.Apply(model);
        Assert.True(model.Visible);                     // 投票解除
        Assert.Equal(Quaternion.Identity, model.LocalRotations[1]);   // 其轨道回绑定

        // 被删层不再参与求值：センター 仍被 A 层驱动
        Assert.NotEqual(Quaternion.Identity, model.LocalRotations[0]);

        timeline.RemoveLayer(layerA);
        timeline.Apply(model);
        Assert.Equal(Quaternion.Identity, model.LocalRotations[0]);   // 整模型回绑定姿势
        Assert.Equal(0f, model.MorphRawWeights[0]);
        Assert.True(model.Visible);
    }

    // GC 测试专用：创建 + 添加 + 移除必须在同一个普通 JIT 编译的 helper 栈帧内完成，
    // 测试方法只接收 WeakReference。原因：xunit 经反射解释帧（InterpretedInvoke）调用测试方法，
    // 解释器的求值栈槽弹出后不清空 —— 目标对象若经过该栈（如作为 helper 返回值），会留下一个
    // 解释帧持有的陈旧强引用（Debug 下假阳性，实测对照实验证实；Release 无解释路径所以通过）。
    // helper 是被正常调用（非反射）的原生方法，返回即整帧销毁，弱引用以外的引用全部消失。
    private static (WeakReference LayerRef, WeakReference AnimRef) CreateAddRemove(MmdTimeline timeline)
    {
        var motion = MotionOf(
            [Bone("センター", 0, new Vector3(1, 0, 0), Quaternion.Identity),
             Bone("センター", 20, new Vector3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f))],
            [Morph("にこり", 0, 1f)]);
        var model = MakeModel(["センター"], ["にこり"]);
        var expanded = MmdAnimation.FromVmd(motion);
        var bound = expanded.Bind(model);
        var layer = new MmdAnimationLayer(bound);
        timeline.AddLayer(layer);

        var layerRef = new WeakReference(layer);
        var animRef = new WeakReference(bound);
        Assert.True(layerRef.IsAlive);

        timeline.RemoveLayer(layer);
        return (layerRef, animRef);
    }

    [Fact]
    public void ClearLayers_ReleasesAnimationGraph_ForGc()
    {
        // 清除后动效对象图必须可整体回收（无隐藏根）。用 WeakReference + 双 GC 钉死。
        var timeline = new MmdTimeline();
        (WeakReference layerRef, WeakReference animRef) = CreateAddRemove(timeline);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(layerRef.IsAlive, "Layer 在移除后仍被引用，存在隐藏根");
        Assert.False(animRef.IsAlive, "绑定动效在移除后仍被引用，存在隐藏根");
        Assert.Empty(timeline.Layers);
    }

    /// <summary>Debug/反射帧 GC 行为的对照实验：不含任何引擎类型的纯 CLR 对象图，同一模式。</summary>
    [Fact]
    public void DebugFrame_GcBaseline_PlainClrObjects()
    {
        WeakReference innerRef = CreatePlainGraph();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(innerRef.IsAlive, "纯 CLR 对照对象也未被回收 ⇒ 是环境（Debug/反射帧）而非引擎隐藏根");
    }

    private static WeakReference CreatePlainGraph()
    {
        var inner = new List<object> { "a", "b", "c" };
        var outer = new List<object> { inner };
        var wr = new WeakReference(inner);
        Assert.True(wr.IsAlive);
        outer.Clear();
        return wr;
    }
}

/// <summary>Range 的便捷读取（测试可读性；核心逻辑不依赖）。</summary>
file static class MmdTimelineTestExtensions
{
    public static (double Start, double End) Range(this MmdTimeline timeline)
        => (timeline.StartFrame, timeline.EndFrame);
}

using System.Numerics;
using System.Text;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// Step 5d-1：property（表示枠）解析与阶梯采样 + 整模型可见性写回契约。
///
/// 表示枠是<b>离散状态</b>（显示 / 非表示）：键与键之间保持、绝不插值；
/// 帧号早于首键 ⇒ 可见（未声明「非表示」的动效应照常渲染）。
///
/// 真实文件基线：<c>samples/MikuEngine.Demo/Motion/test.vmd</c> 在帧 0 / 10 / 20 各有一个
/// property 键（可见 / 非表示 / 可见），构成「帧 10-19 完全不可见」的验收窗口；
/// 每键附带 13 条 IK 开关（解析保留、本引擎无 IK ⇒ 不消费）。
/// </summary>
public class MmdPropertyTrackTests
{
    private static readonly string TestVmdPath = TestAssets.Motion("test.vmd");

    // ---------------------------------------------------------------- 合成构件

    private static VmdPropertyKey Property(int frame, bool visible, int ikStateCount = 0)
    {
        var states = new VmdIkState[ikStateCount];
        for (int i = 0; i < ikStateCount; i++)
        {
            string name = $"IK{i}";
            states[i] = new VmdIkState(name, Encoding.UTF8.GetBytes(name), true);
        }
        return new VmdPropertyKey(frame, visible, states);
    }

    private static MmdAnimation AnimationOf(params VmdPropertyKey[] propertyKeys)
    {
        var motion = new VmdMotion();
        motion.PropertyKeys.AddRange(propertyKeys);
        return MmdAnimation.FromVmd(motion);
    }

    // ---------------------------------------------------------------- 阶梯采样

    [Fact]
    public void PropertyTrack_StepsBetweenKeys()
    {
        var anim = AnimationOf(Property(0, true), Property(10, false), Property(20, true));
        Assert.Equal(3, anim.PropertyKeyCount);

        Assert.True(anim.SampleVisible(0));
        Assert.True(anim.SampleVisible(9.999));    // 键间保持：绝无 0.5 之类的中间态
        Assert.False(anim.SampleVisible(10));      // 键所在帧即生效
        Assert.False(anim.SampleVisible(19.999));
        Assert.True(anim.SampleVisible(20));
        Assert.True(anim.SampleVisible(1000));     // 晚于末键：保持末键状态
    }

    [Fact]
    public void PropertyTrack_BeforeFirstKey_IsVisible()
    {
        var anim = AnimationOf(Property(100, false));

        Assert.True(anim.SampleVisible(-1));
        Assert.True(anim.SampleVisible(0));        // 尚未声明「非表示」⇒ 照常渲染
        Assert.True(anim.SampleVisible(99.999));
        Assert.False(anim.SampleVisible(100));
    }

    [Fact]
    public void PropertyTrack_EmptyTrack_IsAlwaysVisible()
    {
        var anim = MmdAnimation.FromVmd(new VmdMotion());

        Assert.Equal(0, anim.PropertyKeyCount);
        Assert.True(anim.PropertyTrack.IsEmpty);
        Assert.True(anim.SampleVisible(0));
        Assert.True(anim.SampleVisible(-500));
        Assert.True(anim.SampleVisible(1e6));
    }

    [Fact]
    public void PropertyTrack_DuplicateFrames_KeepLast()
    {
        // 与骨骼 / morph 轨道同规则：同帧去重保留文件顺序的最后一次出现
        var anim = AnimationOf(Property(10, true), Property(10, false));

        Assert.Equal(1, anim.PropertyKeyCount);
        Assert.True(anim.SampleVisible(9));
        Assert.False(anim.SampleVisible(10));
    }

    [Fact]
    public void PropertyTrack_DoesNotExtendPlaybackRange()
    {
        // property 是叠加在姿态上的布尔量、不是姿态来源 ⇒ 播放区间仍只由骨 / morph 轨道决定
        var motion = new VmdMotion();
        motion.BoneKeys.Add(new VmdBoneKey("腕", Encoding.UTF8.GetBytes("腕"), 100,
            Vector3.Zero, Quaternion.Identity, new byte[64]));
        motion.BoneKeys.Add(new VmdBoneKey("腕", Encoding.UTF8.GetBytes("腕"), 200,
            Vector3.Zero, Quaternion.Identity, new byte[64]));
        motion.PropertyKeys.Add(Property(0, false));

        var anim = MmdAnimation.FromVmd(motion);

        Assert.Equal(100, anim.StartFrame);
        Assert.Equal(200, anim.EndFrame);
        Assert.False(anim.SampleVisible(0));   // 隐藏窗口照样成立，只是不在播放区间内
    }

    [Fact]
    public void Bind_CarriesPropertyTrack()
    {
        // 表示枠是整模型量：不做「该模型是否存在对应骨」的过滤，原样透传
        var model = new SkeletalModel { BoneNames = [], MorphNames = [] };
        var bound = AnimationOf(Property(5, false, ikStateCount: 2)).Bind(model);

        Assert.Equal(1, bound.PropertyKeyCount);
        Assert.Equal(2, bound.PropertyTrack.IkStates[0].Length);
        Assert.True(bound.SampleVisible(4));
        Assert.False(bound.SampleVisible(5));
    }

    [Fact]
    public void Sample_PoseWriteback_DoesNotTouchVisibility()
    {
        // 契约：Sample 只管姿态（Local T/R + 原始 morph 权重）；可见性由 SampleVisible 单独采样、
        // 调用方显式写回 —— 单层动效不得在被混合时单方面否决整模型可见性
        // （多动效为各活跃层 AND 合并，见 docs/2026-09-11-anim-blend-plan.md 3.6）
        var model = new SkeletalModel { Visible = false };
        AnimationOf(Property(0, true)).Sample(model, 0);

        Assert.False(model.Visible);
    }

    [Fact]
    public void SkeletalModel_VisibleDefaultsTrue()
    {
        Assert.True(new SkeletalModel().Visible);
    }

    // ---------------------------------------------------------------- 真实 test.vmd（结构校验）

    // 注：test.vmd 是用户可随时整体替换的 demo 资源，5d-1 时代的「3 键 @0/10/20、帧 10-19 隐藏窗口」
    // 数值锚点已随文件更换失效；隐藏窗口 / 阶梯保持等语义由上方的合成用例覆盖，
    // 这里只对任意真实文件都成立的性质做断言。

    [Fact]
    public void RealTestVmd_PropertyKeys_AreStructurallyValid()
    {
        var vmd = VmdParser.Parse(File.ReadAllBytes(TestVmdPath));

        Assert.Equal(vmd.PropertyKeys.Count, vmd.PropertyKeyCount);
        for (int i = 1; i < vmd.PropertyKeys.Count; i++)
            Assert.True(vmd.PropertyKeys[i - 1].Frame <= vmd.PropertyKeys[i].Frame,
                $"property 键帧号非升序：{vmd.PropertyKeys[i - 1].Frame} -> {vmd.PropertyKeys[i].Frame}");

        foreach (var key in vmd.PropertyKeys)
            foreach (var state in key.IkStates)
            {
                Assert.NotEmpty(state.NameRaw);                 // 原始字节是权威标识
                Assert.False(string.IsNullOrEmpty(state.BoneName));   // 932 解码不抛异常且非空
            }
    }

    [Fact]
    public void RealTestVmd_PropertyTrack_MatchesParsedKeys()
    {
        var anim = MmdAnimation.FromVmd(VmdParser.Parse(File.ReadAllBytes(TestVmdPath)));
        var track = anim.PropertyTrack;

        Assert.Equal(anim.PropertyKeyCount, track.KeyCount);

        // 阶梯采样在键所在帧必须与解析值一致（键间保持由合成用例覆盖）
        for (int i = 0; i < track.KeyCount; i++)
            Assert.Equal(track.Visibles[i], anim.SampleVisible(track.Frames[i]));

        // 早于首键恒可见
        if (track.KeyCount > 0 && track.Frames[0] > 0)
            Assert.True(anim.SampleVisible(track.Frames[0] - 1));
    }
}

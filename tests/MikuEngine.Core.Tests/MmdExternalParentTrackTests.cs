using System.Numerics;
using System.Text;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// 外部親绑定：VMD 骨键拦截（名字含冒号）→ 离散状态机采样 → MmdAnimation 集成透传。
///
/// MMD 语义：外部親键藏在外部親対象モデル自己的 VMD 骨键轨道里，15B 骨名字段是
/// 「親モデル名：親ボーン名」（优先全角冒号，半角兜底）；键间<b>保持</b>、绝不插值；
/// 区间之前（无键覆盖）= 未绑定。
/// </summary>
public class MmdExternalParentTrackTests
{
    // ---------------------------------------------------------------- 离散采样

    [Fact]
    public void EmptyTrack_NeverBinds()
    {
        var track = new MmdExternalParentTrack();
        Assert.True(track.IsEmpty);
        Assert.False(track.TrySample(0, out _));
        Assert.False(track.TrySample(100, out _));
        Assert.Equal(0, track.EndFrame);
    }

    [Fact]
    public void TrySample_HoldsBetweenKeys()
    {
        var track = MmdExternalParentTrack.FromVmd([
            Key(10, "人物", "右手首"),
            Key(20, "", ""),   // 解除绑定
        ]);

        // 首键之前 = 未绑定
        Assert.False(track.TrySample(5, out _));

        // 首键起绑定；键间保持
        Assert.True(track.TrySample(10, out var k1));
        Assert.Equal("人物", k1.ParentModel);
        Assert.Equal("右手首", k1.ParentBone);

        Assert.True(track.TrySample(15, out var k15));
        Assert.Equal("人物", k15.ParentModel);

        // 解除键之后恒未绑定（ParentModel 空）
        Assert.True(track.TrySample(20, out var k20));
        Assert.Equal("", k20.ParentModel);
        Assert.True(track.TrySample(99, out var k99));
        Assert.Equal("", k99.ParentModel);
    }

    [Fact]
    public void FromVmd_SortsStable_KeepsSameFrameOrder()
    {
        // 乱序输入 + 同帧两条（合法数据：一条绑定对，后者覆盖前者）
        var track = MmdExternalParentTrack.FromVmd([
            Key(30, "B", "boneB"),
            Key(10, "A", "boneA1"),
            Key(10, "A", "boneA2"),
        ]);

        Assert.Equal(3, track.Keys.Length);
        Assert.Equal(30, track.Keys[^1].Frame);
        Assert.Equal("boneA1", track.Keys[0].ParentBone);   // 同帧保持文件顺序
        Assert.Equal("boneA2", track.Keys[1].ParentBone);
        Assert.Equal(30, track.EndFrame);
    }

    // ---------------------------------------------------------------- VMD 解析拦截

    [Fact]
    public void Parser_RoutesColonBoneKeysToExternalParentKeys()
    {
        // 一条普通骨键 + 一条全角冒号外部親键（pos/rot = 叠加偏移）
        var motion = VmdParser.Parse(Vmd(
            NormalBoneKey("センター", frame: 5),
            ExternalParentKey("人物：右手首", frame: 1945)));

        // 含冒号的键不进 BoneKeys，转成外部親绑定键
        var k = Assert.Single(motion.ExternalParentKeys);
        Assert.Equal("人物", k.ParentModel);
        Assert.Equal("右手首", k.ParentBone);
        Assert.Equal(1945, k.Frame);
        Assert.Single(motion.BoneKeys);
    }

    [Fact]
    public void Parser_SplitsFullWidthColon_PrefersItOverHalfWidth()
    {
        var motion = VmdParser.Parse(Vmd(
            ExternalParentKey("親モデル：右腕", frame: 1)));

        var k = Assert.Single(motion.ExternalParentKeys);
        Assert.Equal("親モデル", k.ParentModel);
        Assert.Equal("右腕", k.ParentBone);
        Assert.Equal(1, k.Frame);
    }

    [Fact]
    public void Parser_HalfWidthColonFallback()
    {
        var motion = VmdParser.Parse(Vmd(
            ExternalParentKey("model:bone", frame: 2)));

        var k = Assert.Single(motion.ExternalParentKeys);
        Assert.Equal("model", k.ParentModel);
        Assert.Equal("bone", k.ParentBone);
    }

    [Fact]
    public void Parser_ColonKey_CarriesOffsetPosRot()
    {
        var motion = VmdParser.Parse(Vmd(
            ExternalParentKey("人物：右手首", frame: 3, pos: (0.25f, 0.5f, 0.75f), rot: (0, 0, 0, 1))));

        var k = Assert.Single(motion.ExternalParentKeys);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), k.OffsetTranslation);
        Assert.Equal(Quaternion.Identity, k.OffsetRotation);
    }

    [Fact]
    public void Parser_NormalBoneKeys_Unaffected()
    {
        var motion = VmdParser.Parse(Vmd(
            NormalBoneKey("センター", frame: 1),
            NormalBoneKey("右腕", frame: 2)));

        Assert.Empty(motion.ExternalParentKeys);
        Assert.Equal(2, motion.BoneKeys.Count);
        Assert.Equal("センター", motion.BoneKeys[0].BoneName);
    }

    // ---------------------------------------------------------------- MmdAnimation 集成

    [Fact]
    public void FromVmd_BuildsTrack_AndExtendsPlayRange()
    {
        var motion = new VmdMotion();
        motion.ExternalParentKeys.Add(Key(1945, "人物", "右手首"));
        motion.ExternalParentKeys.Add(Key(3000, "", ""));

        var animation = MmdAnimation.FromVmd(motion);

        Assert.True(animation.ExternalParentTrack.TrySample(2000, out var k));
        Assert.Equal("人物", k.ParentModel);   // 键间保持：1945 的绑定键延续到 2999
        Assert.True(animation.ExternalParentTrack.TrySample(3000, out var k2));
        Assert.Equal("", k2.ParentModel);      // 解除键生效
        Assert.True(animation.ExternalParentTrack.TrySample(1945, out var k3));
        Assert.Equal("右手首", k3.ParentBone);

        // 外部親键参与播放区间
        Assert.Equal(1945, animation.StartFrame);
        Assert.Equal(3000, animation.EndFrame);
    }

    [Fact]
    public void Bind_PassesTrackThrough()
    {
        var motion = new VmdMotion();
        motion.ExternalParentKeys.Add(Key(10, "人物", "右手首"));

        var source = MmdAnimation.FromVmd(motion);
        var bound = source.Bind(new SkeletalModel { BoneNames = [], MorphNames = [] });

        Assert.Same(source.ExternalParentTrack, bound.ExternalParentTrack);
    }

    // ---------------------------------------------------------------- 合成构件

    private static MmdExternalParentKey Key(int frame, string parentModel, string parentBone) =>
        new(frame, parentModel, parentBone, Vector3.Zero, Quaternion.Identity);

    /// <summary>
    /// 合成 VMD：头部 + 骨骼键区 + morph 计数（VMD 各区计数内联，morphCount 紧跟骨骼键区之后）。
    /// </summary>
    private static byte[] Vmd(params byte[][] boneRecords)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.UTF8.GetBytes("Vocaloid Motion Data 0002")); // 25 字节
        w.Write(new byte[5]);  // 补齐签名区到 30 字节
        w.Write(new byte[20]); // 模型名
        w.Write((uint)boneRecords.Length);
        foreach (var r in boneRecords) w.Write(r);
        w.Write(0u);           // morphCount（骨骼键区之后）
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>普通骨键记录：15B 名 + u32 帧 + pos + rot + 64B 插值。</summary>
    private static byte[] NormalBoneKey(string name, uint frame,
        (float X, float Y, float Z)? pos = null)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(SjisBytes(name, 15));
        w.Write(frame);
        var p = pos ?? (0, 0, 0);
        w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
        w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);
        w.Write(new byte[64]);
        return ms.ToArray();
    }

    /// <summary>外部親骨键记录：名字即「親モデル名：親ボーン名」，pos/rot = 叠加偏移。</summary>
    private static byte[] ExternalParentKey(string spec, uint frame,
        (float X, float Y, float Z)? pos = null, (float X, float Y, float Z, float W)? rot = null)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(SjisBytes(spec, 15));
        w.Write(frame);
        var p = pos ?? (0, 0, 0);
        w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
        var r = rot ?? (0, 0, 0, 1);
        w.Write(r.X); w.Write(r.Y); w.Write(r.Z); w.Write(r.W);
        w.Write(new byte[64]);
        return ms.ToArray();
    }

    /// <summary>Shift-JIS 名字字节，补零 / 截断到 <paramref name="size"/>（与 VMD 字段同规则）。</summary>
    private static byte[] SjisBytes(string name, int size)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(932).GetBytes(name);
        Array.Resize(ref bytes, size);   // 补零；超长由截断语义兜底（测试数据不会超）
        return bytes;
    }
}

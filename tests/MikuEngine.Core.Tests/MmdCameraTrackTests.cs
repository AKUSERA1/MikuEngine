using System.Buffers.Binary;
using System.Text;
using MikuEngine.Core.Animation;

namespace MikuEngine.Core.Tests;

/// <summary>
/// VMD 相机轨道测试：
///   1. 合成相机 VMD 的解析（61B 键布局 / 24B 连续插值 / fov 度数）
///   2. 轨道构建（升序 / 同帧去重保留最后 / fov 度→弧度）
///   3. 六通道贝塞尔采样（rotation 单通道三轴共用 / 曲线取后键 / 区间钳制 / 空轨道）
///   4. 真实相机 VMD（samples/MikuEngine.Demo/Motion/镜头.vmd）端到端解析
/// </summary>
public class MmdCameraTrackTests
{
    // ---------------------------------------------------------------- 合成 VMD

    private const int CameraKeyBytes = 61;

    /// <summary>构造一台「纯相机 VMD」字节流：头 + 空 bone/morph 区 + camera 区（无 light/selfShadow/property）。</summary>
    private static byte[] BuildCameraVmd((uint Frame, float Distance, float Tx, float Ty, float Tz,
        float Rx, float Ry, float Rz, uint FovDeg, byte[] Interp)[] keys)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            var sig = Encoding.UTF8.GetBytes("Vocaloid Motion Data 0002");
            w.Write(sig);
            w.Write(new byte[30 - sig.Length]);
            var name = Encoding.ASCII.GetBytes("test");
            w.Write(name);
            w.Write(new byte[20 - name.Length]);

            w.Write(0u);   // boneCount
            w.Write(0u);   // morphCount

            w.Write((uint)keys.Length);
            foreach (var k in keys)
            {
                w.Write(k.Frame);
                w.Write(k.Distance);
                w.Write(k.Tx); w.Write(k.Ty); w.Write(k.Tz);
                w.Write(k.Rx); w.Write(k.Ry); w.Write(k.Rz);
                w.Write(k.Interp);                       // 24B：6 通道 × (x1,x2,y1,y2)
                w.Write(k.FovDeg);                       // u32 度数
                w.Write((byte)0);                        // perspective 标志
            }

            w.Write(0u);   // lightCount（MMD 规范：相机区后必有 light 区计数）
        }
        return ms.ToArray();
    }

    /// <summary>MMD 线性插值对（20/107 落对角线 → Bezier01 恒等）。</summary>
    private static byte[] LinearInterp()
    {
        var ip = new byte[24];
        for (int c = 0; c < 6; c++)
        {
            ip[c * 4 + 0] = 20;   // x1
            ip[c * 4 + 1] = 107;  // x2
            ip[c * 4 + 2] = 20;   // y1
            ip[c * 4 + 3] = 107;  // y2
        }
        return ip;
    }

    // ---------------------------------------------------------------- 1. 解析

    [Fact]
    public void Parse_CameraSection_Fields_MatchBytes()
    {
        var interp = LinearInterp();
        interp[16] = 30; interp[17] = 90; interp[18] = 40; interp[19] = 100;  // distance 通道覆盖
        byte[] data = BuildCameraVmd(new[]
        {
            (Frame: 10u, Distance: -45.5f, Tx: 1f, Ty: 10f, Tz: 0f, Rx: 0.1f, Ry: 3.14f, Rz: -0.2f, FovDeg: 30u, Interp: interp),
            (Frame: 0u,  Distance: -20f,   Tx: 0f, Ty: 8f,  Tz: 5f, Rx: 0f,   Ry: 0f,    Rz: 0f,    FovDeg: 27u, Interp: interp),
        });

        var m = VmdParser.Parse(data);

        Assert.Equal(2, m.CameraKeys.Count);
        Assert.Equal(0, m.LeftoverBytes);

        var first = m.CameraKeys[0];
        Assert.Equal(10u, first.Frame);
        Assert.Equal(-45.5f, first.Distance);
        Assert.Equal((1f, 10f, 0f), (first.Target.X, first.Target.Y, first.Target.Z));
        Assert.Equal((0.1f, 3.14f, -0.2f), (first.RotationEuler.X, first.RotationEuler.Y, first.RotationEuler.Z));
        Assert.Equal(30f, first.FovDegrees);   // u32 度数
        // 24B 插值原样保留（连续布局）
        Assert.Equal(24, first.Interpolation.Length);
        Assert.Equal((30, 90, 40, 100), (first.Interpolation[16], first.Interpolation[17], first.Interpolation[18], first.Interpolation[19]));
    }

    [Fact]
    public void Parse_MotionOnlyVmd_HasNoCameraKeys()
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            var sig = Encoding.UTF8.GetBytes("Vocaloid Motion Data 0002");
            w.Write(sig);
            w.Write(new byte[30 - sig.Length]);
            w.Write(new byte[20]);
            w.Write(0u);   // boneCount
            w.Write(0u);   // morphCount
            // 文件在 morph 区后结束：无 camera/light 区
        }

        var m = VmdParser.Parse(ms.ToArray());

        Assert.Empty(m.CameraKeys);
        Assert.Equal(0, m.CameraSectionOffset);
    }

    // ---------------------------------------------------------------- 2. 轨道构建

    [Fact]
    public void FromVmd_Sorts_DedupesKeepLast_ConvertsFovToRadians()
    {
        var interp = LinearInterp();
        var m = VmdParser.Parse(BuildCameraVmd(new[]
        {
            (Frame: 30u, Distance: -30f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 45u, Interp: interp),
            (Frame: 30u, Distance: -99f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 60u, Interp: interp),
            (Frame: 10u, Distance: -10f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 30u, Interp: interp),
        }));

        var track = MmdCameraTrack.FromVmd(m.CameraKeys);

        Assert.False(track.IsEmpty);
        Assert.Equal(new[] { 10, 30 }, track.Frames);
        Assert.Equal(-10f, track.Distances[0], 5);
        Assert.Equal(-99f, track.Distances[1], 5);   // 同帧保留最后一次出现
        Assert.Equal(60f * MathF.PI / 180f, track.Fovs[1], 5);   // 度 → 弧度
        Assert.Equal(30, track.EndFrame);
        // 播放区间包含相机键（纯相机 VMD 也要可播）
        var anim = MmdAnimation.FromVmd(m);
        Assert.Equal(10, anim.StartFrame);
        Assert.Equal(30, anim.EndFrame);
    }

    // ---------------------------------------------------------------- 3. 采样

    [Fact]
    public void Sample_Linear_MidpointLerpsAllChannels_RotationSingleCurve()
    {
        var m = VmdParser.Parse(BuildCameraVmd(new[]
        {
            (Frame: 0u,  Distance: -40f, Tx: 0f,  Ty: 0f,  Tz: 0f,  Rx: 0f,  Ry: 0f,  Rz: 0f,  FovDeg: 30u, Interp: LinearInterp()),
            (Frame: 20u, Distance: -60f, Tx: 10f, Ty: 20f, Tz: -4f, Rx: 0.2f, Ry: 1.0f, Rz: -0.4f, FovDeg: 40u, Interp: LinearInterp()),
        }));
        var track = MmdCameraTrack.FromVmd(m.CameraKeys);

        bool ok = track.Sample(10, out var pose);
        Assert.True(ok);
        Assert.Equal(5f, pose.Target.X, 5);
        Assert.Equal(10f, pose.Target.Y, 5);
        Assert.Equal(-2f, pose.Target.Z, 5);
        Assert.Equal(-50f, pose.Distance, 5);
        Assert.Equal(35f * MathF.PI / 180f, pose.Fov, 5);
        // rotation 三轴共用单条贝塞尔 → 中点各自精确取半
        Assert.Equal((0.1f, 0.5f, -0.2f), (pose.RotationEuler.X, pose.RotationEuler.Y, pose.RotationEuler.Z));
    }

    [Fact]
    public void Sample_CurveComesFromEndKey()
    {
        // 后键带强 ease-in 曲线（rotation 通道 20,20,107,107）：g=0.5 时权重明显 < 0.5
        var endInterp = LinearInterp();
        endInterp[12] = 20; endInterp[13] = 20; endInterp[14] = 107; endInterp[15] = 107;
        var m = VmdParser.Parse(BuildCameraVmd(new[]
        {
            (Frame: 0u,  Distance: -40f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 30u, Interp: LinearInterp()),
            (Frame: 20u, Distance: -60f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 30u, Interp: endInterp),
        }));
        var track = MmdCameraTrack.FromVmd(m.CameraKeys);

        track.Sample(10, out var pose);

        // distance 通道仍是线性（只有 rotation 通道被改）
        Assert.Equal(-50f, pose.Distance, 5);

        // 首键的插值不影响区间 [a,b]：曲线只取后键 —— 用首键 = 强 ease-in、后键 = 线性反证
        var firstInterp = LinearInterp();
        firstInterp[16] = 20; firstInterp[17] = 20; firstInterp[18] = 107; firstInterp[19] = 107;
        var m2 = VmdParser.Parse(BuildCameraVmd(new[]
        {
            (Frame: 0u,  Distance: -40f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 30u, Interp: firstInterp),
            (Frame: 20u, Distance: -60f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 30u, Interp: LinearInterp()),
        }));
        var track2 = MmdCameraTrack.FromVmd(m2.CameraKeys);
        track2.Sample(10, out var pose2);
        Assert.Equal(-50f, pose2.Distance, 5);
    }

    [Fact]
    public void Sample_ClampsToTrackEnds_EmptyReturnsFalse()
    {
        var m = VmdParser.Parse(BuildCameraVmd(new[]
        {
            (Frame: 10u, Distance: -40f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 30u, Interp: LinearInterp()),
            (Frame: 20u, Distance: -60f, Tx: 0f, Ty: 0f, Tz: 0f, Rx: 0f, Ry: 0f, Rz: 0f, FovDeg: 40u, Interp: LinearInterp()),
        }));
        var track = MmdCameraTrack.FromVmd(m.CameraKeys);

        Assert.True(track.Sample(0, out var early));
        Assert.Equal(-40f, early.Distance, 5);
        Assert.True(track.Sample(999, out var late));
        Assert.Equal(-60f, late.Distance, 5);
        Assert.Equal(40f * MathF.PI / 180f, late.Fov, 5);

        Assert.False(new MmdCameraTrack().Sample(5, out _));
    }

    // ---------------------------------------------------------------- 4. 真实相机 VMD

    [Fact]
    public void RealCameraVmd_Parses_Builds_Samples()
    {
        string? path = TestAssets.Find("Motion/镜头.vmd");
        if (path is null)
        {
            Assert.Fail("未找到 samples/*/Motion/镜头.vmd");
            return;
        }

        var m = VmdParser.Parse(File.ReadAllBytes(path));
        Assert.True(m.CameraKeys.Count > 0, "镜头.vmd 应含相机关键帧");

        var track = MmdCameraTrack.FromVmd(m.CameraKeys);
        Assert.False(track.IsEmpty);

        // 全程可采样：首键姿态 / 中间 / 末键，fov 落在合理视角范围（度）
        Assert.True(track.Sample(track.Frames[0], out var firstPose));
        Assert.True(track.Sample((track.Frames[0] + track.EndFrame) / 2, out var midPose));
        Assert.True(track.Sample(track.EndFrame + 100, out var lastPose));
        foreach (var p in new[] { firstPose, midPose, lastPose })
        {
            float fovDeg = p.Fov * 180f / MathF.PI;
            Assert.InRange(fovDeg, 1f, 180f);
            Assert.True(float.IsFinite(p.Target.X) && float.IsFinite(p.Target.Y) && float.IsFinite(p.Target.Z));
            Assert.True(float.IsFinite(p.RotationEuler.X) && float.IsFinite(p.RotationEuler.Y) && float.IsFinite(p.RotationEuler.Z));
            Assert.True(float.IsFinite(p.Distance));
        }
    }
}

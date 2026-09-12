using System.Numerics;
using System.Text;
using System.Text.Json;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// Step 2 对照测试：轨道构建（排序 / 同帧去重 / 最短弧 / 按通道分读插值）、
/// 贝塞尔采样（对独立二分参考）、采样的纯函数性与钳制、末键曲线语义、morph 线性插值、Bind 绑定。
///
/// 真实 Motion.vmd 上的插值提取断言以 <c>TestData/Motion.vmd.golden.json</c>（babylon-mmd 解析器产出）
/// 的原始 64B 为基准 —— 逐字节验证去冗余后的每通道控制点。
/// </summary>
public class MmdAnimationTests
{
    // ---------------------------------------------------------------- 定位（同 VmdParserTests）

    private static string? FindUp(Func<string, string?> probe)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var hit = probe(dir.FullName);
            if (hit != null) return hit;
        }
        return null;
    }

    private static readonly string GoldenPath = FindUp(dir =>
        File.Exists(Path.Combine(dir, "TestData", "Motion.vmd.golden.json"))
            ? Path.Combine(dir, "TestData", "Motion.vmd.golden.json")
            : File.Exists(Path.Combine(dir, "tests", "MikuEngine.Core.Tests", "TestData", "Motion.vmd.golden.json"))
                ? Path.Combine(dir, "tests", "MikuEngine.Core.Tests", "TestData", "Motion.vmd.golden.json")
                : null)
        ?? throw new IOException("未找到 Motion.vmd.golden.json");

    private static readonly string VmdPath = FindUp(dir =>
        File.Exists(Path.Combine(dir, "samples", "MikuEngine.Demo", "Motion", "Motion.vmd"))
            ? Path.Combine(dir, "samples", "MikuEngine.Demo", "Motion", "Motion.vmd")
            : null)
        ?? throw new IOException("未找到 samples/MikuEngine.Demo/Motion/Motion.vmd");

    private static VmdMotion ParseRealVmd() => VmdParser.Parse(File.ReadAllBytes(VmdPath));

    private static JsonElement Golden()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GoldenPath));
        return doc.RootElement.Clone();
    }

    // ---------------------------------------------------------------- 合成 VMD 构件

    private const byte LinearA = 20;    // MMD 默认线性控制点：(20,20) 与 (107,107)
    private const byte LinearB = 107;

    /// <summary>构造 64B 原始插值块（写在各通道自身副本 c*16 上，等价 babylon-mmd 分读源）。</summary>
    private static byte[] RawInterp(
        (byte X1, byte X2, byte Y1, byte Y2) x,
        (byte X1, byte X2, byte Y1, byte Y2) y,
        (byte X1, byte X2, byte Y1, byte Y2) z,
        (byte X1, byte X2, byte Y1, byte Y2) r)
    {
        var raw = new byte[64];
        Write(raw, 0, x);
        Write(raw, 1, y);
        Write(raw, 2, z);
        Write(raw, 3, r);
        return raw;

        static void Write(byte[] raw, int c, (byte X1, byte X2, byte Y1, byte Y2) p)
        {
            int b = c * 16;
            raw[b + 0] = p.X1;  // x1
            raw[b + 8] = p.X2;  // x2
            raw[b + 4] = p.Y1;  // y1
            raw[b + 12] = p.Y2; // y2
        }
    }

    private static byte[] LinearInterp() => RawInterp(
        (LinearA, LinearB, LinearA, LinearB),
        (LinearA, LinearB, LinearA, LinearB),
        (LinearA, LinearB, LinearA, LinearB),
        (LinearA, LinearB, LinearA, LinearB));

    private static VmdBoneKey Bone(string name, uint frame, Vector3 position, Quaternion rotation, byte[]? interp = null)
        => new(name, Encoding.UTF8.GetBytes(name), frame, position, rotation, interp ?? LinearInterp());

    private static VmdMorphKey Morph(string name, uint frame, float weight)
        => new(name, Encoding.UTF8.GetBytes(name), frame, weight);

    private static VmdMotion Motion(VmdBoneKey[] bones, VmdMorphKey[]? morphs = null)
    {
        var motion = new VmdMotion();
        motion.BoneKeys.AddRange(bones);
        if (morphs != null) motion.MorphKeys.AddRange(morphs);
        return motion;
    }

    // ---------------------------------------------------------------- 独立参考实现

    /// <summary>独立参考：80 次真二分（与实现的 15 次半程步进法写法不同）。</summary>
    private static float RefBezier(byte bx1, byte bx2, byte by1, byte by2, float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        float x1 = bx1 / 127f, x2 = bx2 / 127f, y1 = by1 / 127f, y2 = by2 / 127f;
        float lo = 0f, hi = 1f, u = 0.5f;
        for (int i = 0; i < 80; i++)
        {
            u = (lo + hi) * 0.5f;
            float x = 3f * (1f - u) * (1f - u) * u * x1 + 3f * (1f - u) * u * u * x2 + u * u * u;
            if (x < t) lo = u; else hi = u;
        }
        return 3f * (1f - u) * (1f - u) * u * y1 + 3f * (1f - u) * u * u * y2 + u * u * u;
    }

    /// <summary>独立参考：手写最短弧 slerp。</summary>
    private static Quaternion SlerpRef(Quaternion a, Quaternion b, float t)
    {
        float dot = Quaternion.Dot(a, b);
        if (dot < 0f) { b = -b; dot = -dot; }
        if (dot > 0.9995f) return Quaternion.Normalize(a + (b - a) * t);
        float theta = MathF.Acos(System.Math.Clamp(dot, -1f, 1f));
        float sin = MathF.Sin(theta);
        float wa = MathF.Sin((1f - t) * theta) / sin;
        float wb = MathF.Sin(t * theta) / sin;
        return new Quaternion(
            a.X * wa + b.X * wb, a.Y * wa + b.Y * wb,
            a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
    }

    /// <summary>独立参考采样：直接消费解析出的键（自带排序/去重/最短弧/插值分读）。</summary>
    private static (Quaternion Rot, Vector3 Off) ReferenceSample(IReadOnlyList<VmdBoneKey> sorted, double frame)
    {
        int n = sorted.Count;
        if (n == 0) return (Quaternion.Identity, Vector3.Zero);
        if (n == 1 || frame <= sorted[0].Frame) return (sorted[0].Rotation, sorted[0].Position);

        int a = 0;
        for (int i = 0; i < n; i++)
        {
            if (sorted[i].Frame <= frame) a = i;
            else break;
        }
        if (a >= n - 1) return (sorted[n - 1].Rotation, sorted[n - 1].Position);

        var keyA = sorted[a];
        var keyB = sorted[a + 1];
        float g = (float)((frame - keyA.Frame) / (keyB.Frame - keyA.Frame));
        var raw = keyB.Interpolation;

        float rw = RefBezier(raw[3 * 16 + 0], raw[3 * 16 + 8], raw[3 * 16 + 4], raw[3 * 16 + 12], g);
        var rot = SlerpRef(keyA.Rotation, keyB.Rotation, rw);

        float wx = RefBezier(raw[0 * 16 + 0], raw[0 * 16 + 8], raw[0 * 16 + 4], raw[0 * 16 + 12], g);
        float wy = RefBezier(raw[1 * 16 + 0], raw[1 * 16 + 8], raw[1 * 16 + 4], raw[1 * 16 + 12], g);
        float wz = RefBezier(raw[2 * 16 + 0], raw[2 * 16 + 8], raw[2 * 16 + 4], raw[2 * 16 + 12], g);

        var off = new Vector3(
            keyA.Position.X + (keyB.Position.X - keyA.Position.X) * wx,
            keyA.Position.Y + (keyB.Position.Y - keyA.Position.Y) * wy,
            keyA.Position.Z + (keyB.Position.Z - keyA.Position.Z) * wz);
        return (rot, off);
    }

    private static void AssertRotationNear(Quaternion expected, Quaternion actual, float tolerance)
    {
        // 允许整体取负（同一旋转）
        if (Quaternion.Dot(expected, actual) < 0f) actual = -actual;
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.W - actual.W), 0f, tolerance);
    }

    // ================================================================ 贝塞尔

    [Fact]
    public void Bezier01_Endpoints()
    {
        Assert.Equal(0f, MmdBoneTrack.Bezier01(0, 127, 0, 127, 0f));
        Assert.Equal(1f, MmdBoneTrack.Bezier01(0, 127, 0, 127, 1f));
        Assert.Equal(0f, MmdBoneTrack.Bezier01(127, 0, 127, 0, 0f));
        Assert.Equal(1f, MmdBoneTrack.Bezier01(127, 0, 127, 0, 1f));
    }

    [Fact]
    public void Bezier01_DiagonalControlPoints_IsIdentity()
    {
        // x1==y1 且 x2==y2 → 控制点在对角线，曲线即恒等
        Assert.Equal(0.25f, MmdBoneTrack.Bezier01(LinearA, LinearB, LinearA, LinearB, 0.25f));
        Assert.Equal(0.75f, MmdBoneTrack.Bezier01(0, 127, 0, 127, 0.75f));
    }

    [Fact]
    public void Bezier01_MatchesIndependentBisection()
    {
        byte[] values = [0, 20, 64, 107, 127];
        float[] ts = [0.05f, 0.2f, 0.35f, 0.5f, 0.65f, 0.8f, 0.95f];

        foreach (var x1 in values)
            foreach (var x2 in values)
                foreach (var y1 in values)
                    foreach (var y2 in values)
                        foreach (var t in ts)
                        {
                            float expected = RefBezier(x1, x2, y1, y2, t);
                            float actual = MmdBoneTrack.Bezier01(x1, x2, y1, y2, t);
                            // 实现刻意对齐 babylon-mmd（15 次迭代 / eps 1e-5），极陡曲线下权重误差约 1e-3 量级
                            Assert.InRange(MathF.Abs(expected - actual), 0f, 5e-3f);
                        }
    }

    // ================================================================ 轨道构建

    [Fact]
    public void FromVmd_SortsAscending_AndKeepsLastOnDuplicateFrames()
    {
        var q0 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f);
        var q10 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f);
        var q10Last = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f);

        var motion = Motion([
            Bone("センター", 10, new Vector3(1, 0, 0), q10),
            Bone("センター", 0, Vector3.Zero, q0),
            Bone("センター", 10, new Vector3(2, 0, 0), q10Last),
        ]);

        var anim = MmdAnimation.FromVmd(motion);
        var track = Assert.Single(anim.BoneTracks);

        Assert.Equal(new[] { 0, 10 }, track.Frames);
        Assert.Equal(new Vector3(2, 0, 0), track.Offsets[1]);   // 同帧保留最后一次出现
        AssertRotationNear(q10Last, track.Rotations[1], 1e-6f);
        Assert.Equal(0, anim.StartFrame);
        Assert.Equal(10, anim.EndFrame);
    }

    [Fact]
    public void FromVmd_ExtractsInterpolationPerChannel()
    {
        var raw = RawInterp(
            (1, 2, 3, 4),
            (5, 6, 7, 8),
            (9, 10, 11, 12),
            (13, 14, 15, 16));
        var motion = Motion([Bone("腕", 0, Vector3.Zero, Quaternion.Identity, raw)]);

        var track = Assert.Single(MmdAnimation.FromVmd(motion).BoneTracks);

        // 存储布局：key*16 + channel*4 + (x1, x2, y1, y2)
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, track.Interpolation);
    }

    [Fact]
    public void FromVmd_AlignsConsecutiveRotationsToShortestArc()
    {
        var a = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f);
        // 与 a 反向表示（dot < 0）：模拟 VMD 里相邻键处于不同半球
        var b = Quaternion.Normalize(-Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f));

        var motion = Motion([
            Bone("腕", 0, Vector3.Zero, a),
            Bone("腕", 10, Vector3.Zero, b),
        ]);

        var track = Assert.Single(MmdAnimation.FromVmd(motion).BoneTracks);
        Assert.True(Quaternion.Dot(track.Rotations[0], track.Rotations[1]) >= 0f);
    }

    // ================================================================ 采样

    [Fact]
    public void Sample_ClampsToEndKeys()
    {
        var q0 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f);
        var q10 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f);
        var motion = Motion([
            Bone("腕", 0, new Vector3(1, 2, 3), q0),
            Bone("腕", 10, new Vector3(4, 5, 6), q10),
        ]);
        var track = Assert.Single(MmdAnimation.FromVmd(motion).BoneTracks);

        AssertRotationNear(q0, track.SampleRotation(-100.0), 1e-6f);
        Assert.Equal(new Vector3(1, 2, 3), track.SampleOffset(-100.0));
        AssertRotationNear(q10, track.SampleRotation(1000.0), 1e-6f);
        Assert.Equal(new Vector3(4, 5, 6), track.SampleOffset(1000.0));
    }

    [Fact]
    public void Sample_IsPureFunction_OrderIndependent()
    {
        var motion = Motion([
            Bone("センター", 0, new Vector3(0, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0f),
                RawInterp((0, 127, 0, 127), (0, 127, 0, 127), (0, 127, 0, 127), (10, 117, 10, 117))),
            Bone("センター", 10, new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f),
                RawInterp((30, 90, 30, 90), (5, 122, 5, 122), (0, 127, 0, 127), (60, 60, 60, 60))),
            Bone("センター", 25, new Vector3(-3, 1, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.5f),
                LinearInterp()),
        ]);
        var track = Assert.Single(MmdAnimation.FromVmd(motion).BoneTracks);

        var r1 = track.SampleRotation(3.2);
        var o1 = track.SampleOffset(3.2);
        var r2 = track.SampleRotation(17.7);
        var r3 = track.SampleRotation(3.2);
        var o3 = track.SampleOffset(3.2);

        Assert.Equal(r1, r3);
        Assert.Equal(o1, o3);
        Assert.NotEqual(r2, r1); // 不同帧号不应相同（非退化轨道）
    }

    [Fact]
    public void Sample_UsesEndKeyInterpolationCurve()
    {
        var a = Quaternion.Identity;
        var b = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f);

        // 前键线性；后键的旋转曲线强缓入（x1=127,y1=0,x2=127,y2=0）→ 中点权重大幅小于 0.5
        var frontInterp = LinearInterp();
        var backInterp = RawInterp(
            (LinearA, LinearB, LinearA, LinearB),
            (LinearA, LinearB, LinearA, LinearB),
            (LinearA, LinearB, LinearA, LinearB),
            (127, 127, 0, 0));

        var motion = Motion([
            Bone("腕", 0, Vector3.Zero, a, frontInterp),
            Bone("腕", 10, Vector3.Zero, b, backInterp),
        ]);
        var track = Assert.Single(MmdAnimation.FromVmd(motion).BoneTracks);

        var expected = SlerpRef(a, b, RefBezier(127, 127, 0, 0, 0.5f));
        var actual = track.SampleRotation(5.0);
        AssertRotationNear(expected, actual, 1e-3f);

        // 若误用前键（线性）曲线，中点权重会是 0.5 —— 必须与实现结果显著不同
        var wrongPath = SlerpRef(a, b, RefBezier(LinearA, LinearB, LinearA, LinearB, 0.5f));
        Assert.True(Quaternion.Dot(expected, wrongPath) < 0.999f);
    }

    [Fact]
    public void Sample_RotationTakesShortestArc()
    {
        var a = Quaternion.Identity;
        // 270° 绕 Y = 与 -90° 同姿态但四元数反向
        var b = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 4.71238898f);

        var motion = Motion([
            Bone("腕", 0, Vector3.Zero, a),
            Bone("腕", 10, Vector3.Zero, b),
        ]);
        var track = Assert.Single(MmdAnimation.FromVmd(motion).BoneTracks);

        var mid = track.SampleRotation(5.0);
        float angle = 2f * MathF.Atan2(MathF.Abs(mid.Y), mid.W);
        Assert.InRange(angle * 180f / MathF.PI, 44.0f, 46.0f);  // 短弧 → ±45°，而非 135°
        Assert.True(mid.Y < 0f);                                 // 朝 -90° 方向
    }

    // ================================================================ morph

    [Fact]
    public void MorphTrack_LinearInterpolates_AndClamps()
    {
        var motion = Motion(Array.Empty<VmdBoneKey>(), new[]
        {
            Morph("まばたき", 0, 0f),
            Morph("まばたき", 10, 1f),
        });
        var track = Assert.Single(MmdAnimation.FromVmd(motion).MorphTracks);

        Assert.Equal(0f, track.SampleWeight(-5.0));
        Assert.Equal(0.5f, track.SampleWeight(5.0), 1e-6f);
        Assert.Equal(0.3f, track.SampleWeight(3.0), 1e-6f);
        Assert.Equal(1f, track.SampleWeight(999.0));
    }

    // ================================================================ Bind

    [Fact]
    public void Bind_ResolvesIndices_AndDropsMissingTracks()
    {
        var model = new SkeletalModel
        {
            BoneNames = ["腕", "センター"],
            MorphNames = ["まばたき"],
        };

        var motion = Motion(
            new[]
            {
                Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
                Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
                Bone("存在しない骨", 0, Vector3.Zero, Quaternion.Identity),
            },
            new[]
            {
                Morph("まばたき", 0, 0f),
                Morph("存在しない表情", 0, 0f),
            });

        var bound = MmdAnimation.Bind(motion, model);

        Assert.Equal(2, bound.BoneTracks.Length);
        Assert.Single(bound.MorphTracks);
        Assert.Equal(1, Array.Find(bound.BoneTracks, t => t.Name == "センター")!.BoneIndex);
        Assert.Equal(0, Array.Find(bound.BoneTracks, t => t.Name == "腕")!.BoneIndex);
        Assert.Equal(new[] { 0 }, bound.MorphTracks[0].MorphIndices);
    }

    [Fact]
    public void Bind_WritesAllDuplicateMorphNames()
    {
        // MMD 允许不同 morph 重名 —— 权重必须写给全部同名项（此前只取第一个）。
        var model = new SkeletalModel
        {
            BoneNames = [],
            MorphNames = ["まばたき", "にこり", "まばたき"],
            MorphRawWeights = new float[3],
            MorphWeights = new float[3],
        };

        var motion = Motion(
            Array.Empty<VmdBoneKey>(),
            new[] { Morph("まばたき", 0, 1f) });

        var bound = MmdAnimation.Bind(motion, model);

        var track = Assert.Single(bound.MorphTracks);
        Assert.Equal(new[] { 0, 2 }, track.MorphIndices);

        bound.Sample(model, 0);
        Assert.Equal(1f, model.MorphRawWeights[0]);
        Assert.Equal(0f, model.MorphRawWeights[1]);
        Assert.Equal(1f, model.MorphRawWeights[2]);
    }

    // ================================================================ 真实 Motion.vmd

    [Fact]
    public void RealVmd_TracksAreStrictlyAscending()
    {
        var anim = MmdAnimation.FromVmd(ParseRealVmd());

        // demo 资源可被整体替换（骨动效 / 纯表情动效都可能），只要求「有什么轨道就严格升序」
        Assert.True(anim.BoneTracks.Length + anim.MorphTracks.Length > 0, "没有任何轨道");
        foreach (var track in anim.BoneTracks)
            for (int i = 1; i < track.Frames.Length; i++)
                Assert.True(track.Frames[i - 1] < track.Frames[i], $"{track.Name} 帧号非严格升序");
        foreach (var track in anim.MorphTracks)
            for (int i = 1; i < track.Frames.Length; i++)
                Assert.True(track.Frames[i - 1] < track.Frames[i], $"{track.Name} 帧号非严格升序");
    }

    [Fact]
    public void RealVmd_InterpolationExtraction_MatchesGoldenRawBytes()
    {
        var vmd = ParseRealVmd();
        var anim = MmdAnimation.FromVmd(vmd);
        var tracksByName = anim.BoneTracks.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var g = Golden();

        int checkedKeys = 0;
        foreach (var sample in g.GetProperty("sampledBoneKeys").EnumerateArray())
        {
            int fileIndex = sample.GetProperty("index").GetInt32();
            uint frame = sample.GetProperty("frame").GetUInt32();
            var source = vmd.BoneKeys[fileIndex];

            if (!tracksByName.TryGetValue(source.BoneName, out var track)) continue;
            int keyIndex = Array.IndexOf(track.Frames, (int)frame);
            if (keyIndex < 0) continue; // 同帧去重时被丢弃的重复键

            var raw = sample.GetProperty("interp").EnumerateArray().Select(e => e.GetByte()).ToArray();
            int o = keyIndex * MmdBoneTrack.InterpolationStride;
            for (int c = 0; c < 4; c++)
            {
                int b = c * 16;
                Assert.Equal(raw[b + 0], track.Interpolation[o + 0]);   // x1
                Assert.Equal(raw[b + 8], track.Interpolation[o + 1]);   // x2
                Assert.Equal(raw[b + 4], track.Interpolation[o + 2]);   // y1
                Assert.Equal(raw[b + 12], track.Interpolation[o + 3]);  // y2
                o += MmdBoneTrack.ChannelStride;
            }
            checkedKeys++;
        }

        Assert.True(checkedKeys >= 100, $"仅校验了 {checkedKeys} 个键，样本不足");
    }

    [Fact]
    public void RealVmd_Sampling_MatchesIndependentReference()
    {
        var vmd = ParseRealVmd();
        var anim = MmdAnimation.FromVmd(vmd);

        // 取关键帧最多的轨道作为对照（同时覆盖旋转与位移）
        var track = anim.BoneTracks.OrderByDescending(t => t.Frames.Length).First();
        Assert.True(track.Frames.Length >= 3);

        var sorted = vmd.BoneKeys
            .Where(k => k.BoneName == track.Name)
            .OrderBy(k => k.Frame)
            .GroupBy(k => k.Frame)
            .Select(g => g.Last())   // 同帧保留最后一次出现，与轨道构建一致
            .ToList();

        int n = track.Frames.Length;
        double[] frames =
        [
            track.Frames[0],
            track.Frames[1],
            (track.Frames[1] + track.Frames[2]) / 2.0,
            (track.Frames[n - 2] + track.Frames[n - 1]) / 2.0,
            track.Frames[n - 1],
        ];

        foreach (var frame in frames)
        {
            var (expectedRot, expectedOff) = ReferenceSample(sorted, frame);
            track.Sample(frame, out var actualRot, out var actualOff);

            AssertRotationNear(expectedRot, actualRot, 1e-3f);
            Assert.InRange((expectedOff - actualOff).Length(), 0f, 1e-2f);
        }
    }

    // ================================================================ Step 4：模型姿态写入

    private static SkeletalModel MakeModel(params (string Name, Vector3 Bind)[] bones)
    {
        var model = new SkeletalModel { BoneCount = bones.Length };
        model.BoneNames = bones.Select(b => b.Name).ToArray();
        model.LocalPositions = bones.Select(b => b.Bind).ToArray();
        model.LocalTranslations = (Vector3[])model.LocalPositions.Clone();
        model.LocalRotations = new Quaternion[bones.Length];
        Array.Fill(model.LocalRotations, Quaternion.Identity);
        return model;
    }

    [Fact]
    public void Sample_WritesPose_AndResetsUnanimatedBones()
    {
        var bindCenter = new Vector3(0, 10, 0);
        var bindArm = new Vector3(0, 20, 0);
        var model = MakeModel(("センター", bindCenter), ("腕", bindArm));

        // 让"腕"先处于非绑定状态，验证 Sample 会把它复位
        model.LocalRotations[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f);
        model.LocalTranslations[1] += new Vector3(5, 0, 0);

        var q10 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f);
        var motion = Motion([
            Bone("センター", 0, Vector3.Zero, Quaternion.Identity),
            Bone("センター", 10, new Vector3(2, -1, 0.5f), q10),
        ]);
        var anim = MmdAnimation.Bind(motion, model);

        anim.Sample(model, 10);

        AssertRotationNear(q10, model.LocalRotations[0], 1e-6f);
        Assert.Equal(bindCenter + new Vector3(2, -1, 0.5f), model.LocalTranslations[0]);
        // 未被动画驱动 → 复位到绑定姿势
        Assert.Equal(Quaternion.Identity, model.LocalRotations[1]);
        Assert.Equal(bindArm, model.LocalTranslations[1]);
    }

    [Fact]
    public void Sample_EmptyTracks_KeepsBindPose()
    {
        var bind = new Vector3(1, 2, 3);
        var model = MakeModel(("腕", bind));
        model.LocalRotations[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        model.LocalTranslations[0] += new Vector3(9, 9, 9);

        MmdAnimation.FromVmd(new VmdMotion()).Sample(model, 0);

        Assert.Equal(Quaternion.Identity, model.LocalRotations[0]);
        Assert.Equal(bind, model.LocalTranslations[0]);
    }

    /// <summary>
    /// 决定性测试（对齐 Step 4 验收 3）：连续播放到帧 N 的姿态 ≡ 直接 seek 到帧 N 的姿态。
    /// 采样是帧号纯函数，向前跳、向后跳、逐小步推进都必须得到同一结果。
    /// </summary>
    [Fact]
    public void Sample_PlaybackToFrame_EqualsDirectSeek()
    {
        var model = MakeModel(("センター", new Vector3(0, 10, 0)), ("腕", new Vector3(0, 20, 0)));
        var motion = Motion([
            Bone("センター", 0, Vector3.Zero, Quaternion.Identity,
                RawInterp((0, 127, 0, 127), (10, 117, 10, 117), (0, 127, 0, 127), (30, 90, 30, 90))),
            Bone("センター", 10, new Vector3(5, 0, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f),
                RawInterp((20, 107, 20, 107), (5, 122, 5, 122), (0, 127, 0, 127), (127, 127, 20, 20))),
            Bone("腕", 0, Vector3.Zero, Quaternion.Identity),
            Bone("腕", 20, new Vector3(0, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.8f)),
        ]);
        var anim = MmdAnimation.Bind(motion, model);

        const double n = 13.5;

        var playthrough = MakeModel(("センター", new Vector3(0, 10, 0)), ("腕", new Vector3(0, 20, 0)));
        for (double f = anim.StartFrame; f < n; f += 0.25)
            anim.Sample(playthrough, f);
        anim.Sample(playthrough, n);

        var directSeek = MakeModel(("センター", new Vector3(0, 10, 0)), ("腕", new Vector3(0, 20, 0)));
        anim.Sample(directSeek, n);

        var backward = MakeModel(("センター", new Vector3(0, 10, 0)), ("腕", new Vector3(0, 20, 0)));
        anim.Sample(backward, 999);   // 先跑到尾帧（残留状态若存在会暴露）
        anim.Sample(backward, n);

        Assert.Equal(playthrough.LocalRotations, directSeek.LocalRotations);
        Assert.Equal(playthrough.LocalTranslations, directSeek.LocalTranslations);
        Assert.Equal(directSeek.LocalRotations, backward.LocalRotations);
        Assert.Equal(directSeek.LocalTranslations, backward.LocalTranslations);
    }

    // ================================================================ Step 4：播放器

    [Fact]
    public void Player_RealTimeAdvancesByDeltaTimesFps()
    {
        var player = new MmdAnimationPlayer();
        player.Configure(0, 100);
        player.PlaybackFps = 30f;

        player.Advance(0.5);   // 0.5s × 30fps = 15 帧

        Assert.Equal(15.0, player.CurrentFrame);
    }

    [Fact]
    public void Player_FrameLockedAdvancesOneFramePerCall()
    {
        var player = new MmdAnimationPlayer { Mode = MmdPlaybackMode.FrameLocked };
        player.Configure(5, 100);

        player.Advance(1.0);
        player.Advance(1.0);

        Assert.Equal(7.0, player.CurrentFrame);
    }

    [Fact]
    public void Player_ClampsAtEndByDefault_AndLoopsWhenAsked()
    {
        var clamp = new MmdAnimationPlayer { PlaybackFps = 30f };
        clamp.Configure(0, 100);
        clamp.Advance(10.0);   // 300 → 钳制
        Assert.Equal(100.0, clamp.CurrentFrame);

        var loop = new MmdAnimationPlayer { PlaybackFps = 100f, Loop = true };
        loop.Configure(0, 100);
        loop.Advance(1.5);     // 150 → 回卷到 50
        Assert.Equal(50.0, loop.CurrentFrame);
    }

    [Fact]
    public void Player_PausedDoesNotAdvance()
    {
        var player = new MmdAnimationPlayer { PlaybackFps = 30f, Paused = true };
        player.Configure(0, 100);

        player.Advance(1.0);

        Assert.Equal(0.0, player.CurrentFrame);
    }

    [Fact]
    public void Player_SeekAndStepAreClampedToRange()
    {
        var player = new MmdAnimationPlayer();
        player.Configure(10, 20);
        Assert.Equal(10.0, player.CurrentFrame);

        player.Seek(15);
        Assert.Equal(15.0, player.CurrentFrame);

        player.Step(-100);     // 钳制到首帧
        Assert.Equal(10.0, player.CurrentFrame);

        player.Step(3);
        Assert.Equal(13.0, player.CurrentFrame);
    }
}

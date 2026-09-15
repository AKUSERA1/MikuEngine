using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MikuEngine.Core.Animation;

namespace MikuEngine.Core.Tests;

/// <summary>
/// VMD 解析器对照测试：以 babylon-mmd 解析器（经 TestData/gen_vmd_golden.mjs 移植执行）的
/// 解析结果为权威基准，对 samples/MikuEngine.Demo/Motion/Motion.vmd 做 6 个层级断言：
///
///   1. 计数 / 分区偏移             —— 结构定位正确
///   2. 分区原始字节 FNV-1a64        —— 分区边界逐字节正确
///   3. 全量关键帧规范流 FNV-1a64    —— 每一帧的每一字节（名字原始字节/帧号/浮点比特位/插值）正确
///   4. 确定性抽样全字段             —— 指纹之外的逐字段冗余校验
///   5. 轨道聚合                     —— 原始字节键控的分组 / 计数 / 首末帧
///   6. property（表示枠）键          —— 帧号 / 可见性 / IK 开关列表 + property 分区字节哈希
///
/// 名字以【原始字节】为权威基准（断言 nameRaw / modelNameRaw 逐字节一致）。
/// 解码字符串不与 babylon 直接对齐：WHATWG(shift-jis) 与 .NET 932 对非标准字节
/// （本文件有 26 处，如 B1 E8 2D）映射不同——JS 得 U+FFFD+'-'，.NET 932 得 '・'。
/// 引擎内部 PMX/VMD 统一用 .NET 932，名字绑定自洽；模仿 babylon 的 U+FFFD 反而破坏绑定。
/// 测试只要求：golden 名字无 U+FFFD 时（即两边都干净解码），解码结果必须一致。
///
/// golden 再生：node TestData/gen_vmd_golden.mjs <vmd> <out.json>
/// </summary>
public class VmdParserTests
{
    // ---------------------------------------------------------------- 定位

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
        ?? throw new IOException("未找到 Motion.vmd.golden.json（TestData 目录）");

    private static readonly string VmdPath = TestAssets.Motion("Motion.vmd");

    private static VmdMotion ParseRealVmd() => VmdParser.Parse(File.ReadAllBytes(VmdPath));

    private static JsonElement Golden()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GoldenPath));
        return doc.RootElement.Clone();
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        const string digits = "0123456789abcdef";
        var chars = new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = digits[bytes[i] >> 4];
            chars[i * 2 + 1] = digits[bytes[i] & 0xF];
        }
        return new string(chars);
    }

    /// <summary>golden 解码名是否含 U+FFFD（babylon/WHATWG 对非标准字节的替换符）——这类名字不做解码对齐断言。</summary>
    private static bool GoldenNameIsClean(string name) => !name.Contains('\uFFFD');

    // ---------------------------------------------------------------- FNV-1a 64（与 golden 生成脚本逐位一致）

    private const ulong FnvOffset = 0xcbf29ce484222325UL;
    private const ulong FnvPrime = 0x100000001b3UL;

    private static ulong FnvUpdate(ulong h, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            h ^= b;
            h *= FnvPrime;
        }
        return h;
    }

    private static ulong FnvU32(ulong h, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return FnvUpdate(h, b);
    }

    private static ulong FnvF32(ulong h, float v) => FnvU32(h, BitConverter.SingleToUInt32Bits(v));

    private static string FnvHex(ulong h) => h.ToString("x16");

    /// <summary>规范流：名字原始字节 + 0x1f + 帧 u32le + 0x1f + 浮点比特位 + 插值 64B（与 golden 生成脚本一致）。</summary>
    private static ulong StreamHash(VmdMotion m)
    {
        ulong h = FnvOffset;
        Span<byte> sep = [0x1f];

        foreach (var k in m.BoneKeys)
        {
            h = FnvUpdate(h, k.NameRaw);
            h = FnvUpdate(h, sep);
            h = FnvU32(h, k.Frame);
            h = FnvUpdate(h, sep);
            h = FnvF32(h, k.Position.X);
            h = FnvF32(h, k.Position.Y);
            h = FnvF32(h, k.Position.Z);
            h = FnvF32(h, k.Rotation.X);
            h = FnvF32(h, k.Rotation.Y);
            h = FnvF32(h, k.Rotation.Z);
            h = FnvF32(h, k.Rotation.W);
            h = FnvUpdate(h, k.Interpolation);
        }

        foreach (var k in m.MorphKeys)
        {
            h = FnvUpdate(h, k.NameRaw);
            h = FnvUpdate(h, sep);
            h = FnvU32(h, k.Frame);
            h = FnvUpdate(h, sep);
            h = FnvF32(h, k.Weight);
        }

        return h;
    }

    /// <summary>分区原始字节哈希——用解析器回报的偏移直接哈希源文件字节。</summary>
    private static ulong SectionHash(byte[] file, int offset, int bytes)
        => FnvUpdate(FnvOffset, file.AsSpan(offset, bytes));

    // ---------------------------------------------------------------- 1. 计数 / 模型名 / 分区

    [Fact]
    public void Counts_ModelName_Sections_MatchGolden()
    {
        var g = Golden();
        var m = ParseRealVmd();

        // 模型名：原始字节权威；干净解码名再对齐
        Assert.Equal(g.GetProperty("modelNameRaw").GetString(), Hex(m.ModelNameRaw));
        if (GoldenNameIsClean(g.GetProperty("modelName").GetString()!))
            Assert.Equal(g.GetProperty("modelName").GetString(), m.ModelName);

        var c = g.GetProperty("counts");
        Assert.Equal(c.GetProperty("bone").GetInt32(), m.BoneKeys.Count);
        Assert.Equal(c.GetProperty("morph").GetInt32(), m.MorphKeys.Count);
        Assert.Equal(c.GetProperty("camera").GetInt32(), m.CameraKeys.Count);
        Assert.Equal(c.GetProperty("light").GetInt32(), m.LightKeyCount);
        Assert.Equal(c.GetProperty("selfShadow").GetInt32(), m.SelfShadowKeyCount);
        Assert.Equal(c.GetProperty("property").GetInt32(), m.PropertyKeyCount);
        Assert.Equal(c.GetProperty("leftoverBytes").GetInt32(), m.LeftoverBytes);
        Assert.Equal(0, m.LeftoverBytes); // 基准动捕数据应无非规范尾巴

        var s = g.GetProperty("sections");
        Assert.Equal(s.GetProperty("boneOffset").GetInt32(), m.BoneSectionOffset);
        Assert.Equal(s.GetProperty("boneBytes").GetInt32(), m.BoneSectionBytes);
        Assert.Equal(s.GetProperty("morphOffset").GetInt32(), m.MorphSectionOffset);
        Assert.Equal(s.GetProperty("morphBytes").GetInt32(), m.MorphSectionBytes);
    }

    // ---------------------------------------------------------------- 2. 分区原始字节哈希

    [Fact]
    public void SectionRawBytes_Fnv_MatchGolden()
    {
        var g = Golden();
        var m = ParseRealVmd();
        var file = File.ReadAllBytes(VmdPath);

        Assert.Equal(g.GetProperty("sections").GetProperty("boneFnv").GetString(),
            FnvHex(SectionHash(file, m.BoneSectionOffset, m.BoneSectionBytes)));
        Assert.Equal(g.GetProperty("sections").GetProperty("morphFnv").GetString(),
            FnvHex(SectionHash(file, m.MorphSectionOffset, m.MorphSectionBytes)));
    }

    // ---------------------------------------------------------------- 2b. property（表示枠）键

    /// <summary>
    /// property 键的全字段对照：帧号、可见性（<c>byte != 0 ⇒ 可见</c>）、
    /// 每条 IK 开关（原始字节名 + 启用位），以及 property 分区的定位与原始字节哈希。
    /// </summary>
    [Fact]
    public void PropertyKeys_MatchGolden()
    {
        var g = Golden();
        var m = ParseRealVmd();
        var file = File.ReadAllBytes(VmdPath);

        Assert.Equal(g.GetProperty("counts").GetProperty("property").GetInt32(), m.PropertyKeyCount);
        Assert.Equal(m.PropertyKeyCount, m.PropertyKeys.Count);

        var expected = g.GetProperty("propertyKeys").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, m.PropertyKeys.Count);

        int checkedIkStates = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            var e = expected[i];
            var k = m.PropertyKeys[i];

            Assert.Equal(e.GetProperty("frame").GetInt32(), k.Frame);
            Assert.Equal(e.GetProperty("visible").GetBoolean(), k.Visible);

            var ik = e.GetProperty("ikStates").EnumerateArray().ToArray();
            Assert.Equal(ik.Length, k.IkStates.Length);
            for (int j = 0; j < ik.Length; j++)
            {
                // 原始字节是权威标识（解码字符串随解码器风味变化，只在 golden 干净时对齐）
                Assert.Equal(ik[j].GetProperty("nameRaw").GetString(), Hex(k.IkStates[j].NameRaw));
                Assert.Equal(ik[j].GetProperty("enabled").GetBoolean(), k.IkStates[j].Enabled);
                if (GoldenNameIsClean(ik[j].GetProperty("name").GetString()!))
                    Assert.Equal(ik[j].GetProperty("name").GetString(), k.IkStates[j].BoneName);
                checkedIkStates++;
            }
        }
        // 「golden 里每条 IK 开关都必须被实际核对过」—— 数量由 golden 自身决定（fixture 可被整体替换）
        int expectedIkStates = expected.Sum(e => e.GetProperty("ikStates").GetArrayLength());
        Assert.Equal(expectedIkStates, checkedIkStates);

        var s = g.GetProperty("sections");
        Assert.Equal(s.GetProperty("propertyOffset").GetInt32(), m.PropertySectionOffset);
        Assert.Equal(s.GetProperty("propertyBytes").GetInt32(), m.PropertySectionBytes);
        Assert.Equal(s.GetProperty("propertyFnv").GetString(),
            FnvHex(SectionHash(file, m.PropertySectionOffset, m.PropertySectionBytes)));
    }

    // ---------------------------------------------------------------- 3. 全量规范流指纹（241,460 + 17,686 帧 × 每字节）

    [Fact]
    public void FullKeyStream_Fnv_MatchGolden()
    {
        var g = Golden();
        var m = ParseRealVmd();

        Assert.Equal(g.GetProperty("streamFnv").GetString(), FnvHex(StreamHash(m)));
    }

    // ---------------------------------------------------------------- 4. 抽样全字段逐位对照

    [Fact]
    public void SampledBoneKeys_ExactBitMatch()
    {
        var g = Golden();
        var m = ParseRealVmd();

        foreach (var s in g.GetProperty("sampledBoneKeys").EnumerateArray())
        {
            int i = s.GetProperty("index").GetInt32();
            var k = m.BoneKeys[i];

            Assert.Equal(s.GetProperty("nameRaw").GetString(), Hex(k.NameRaw));
            if (GoldenNameIsClean(s.GetProperty("name").GetString()!))
                Assert.Equal(s.GetProperty("name").GetString(), k.BoneName);
            Assert.Equal(s.GetProperty("frame").GetUInt32(), k.Frame);

            var pos = s.GetProperty("pos");
            Assert.Equal(Bits(pos[0]), BitConverter.SingleToUInt32Bits(k.Position.X));
            Assert.Equal(Bits(pos[1]), BitConverter.SingleToUInt32Bits(k.Position.Y));
            Assert.Equal(Bits(pos[2]), BitConverter.SingleToUInt32Bits(k.Position.Z));

            var rot = s.GetProperty("rot");
            Assert.Equal(Bits(rot[0]), BitConverter.SingleToUInt32Bits(k.Rotation.X));
            Assert.Equal(Bits(rot[1]), BitConverter.SingleToUInt32Bits(k.Rotation.Y));
            Assert.Equal(Bits(rot[2]), BitConverter.SingleToUInt32Bits(k.Rotation.Z));
            Assert.Equal(Bits(rot[3]), BitConverter.SingleToUInt32Bits(k.Rotation.W));

            var interp = s.GetProperty("interp").EnumerateArray().Select(e => e.GetByte()).ToArray();
            Assert.Equal(interp, k.Interpolation);
        }
    }

    [Fact]
    public void SampledMorphKeys_ExactBitMatch()
    {
        var g = Golden();
        var m = ParseRealVmd();

        foreach (var s in g.GetProperty("sampledMorphKeys").EnumerateArray())
        {
            int i = s.GetProperty("index").GetInt32();
            var k = m.MorphKeys[i];

            Assert.Equal(s.GetProperty("nameRaw").GetString(), Hex(k.NameRaw));
            if (GoldenNameIsClean(s.GetProperty("name").GetString()!))
                Assert.Equal(s.GetProperty("name").GetString(), k.MorphName);
            Assert.Equal(s.GetProperty("frame").GetUInt32(), k.Frame);
            Assert.Equal(Bits(s.GetProperty("weight")), BitConverter.SingleToUInt32Bits(k.Weight));
        }
    }

    private static uint Bits(JsonElement e) => uint.Parse(e.GetString()!);

    // ---------------------------------------------------------------- 5. 轨道聚合（原始字节键控）

    [Fact]
    public void BoneTracks_Aggregation_MatchGolden()
    {
        var g = Golden();
        var m = ParseRealVmd();

        AssertTrackEqual(g.GetProperty("boneTracks"),
            Aggregate(m.BoneKeys.Select(k => (Hex(k.NameRaw), k.Frame))));
    }

    [Fact]
    public void MorphTracks_Aggregation_MatchGolden()
    {
        var g = Golden();
        var m = ParseRealVmd();

        AssertTrackEqual(g.GetProperty("morphTracks"),
            Aggregate(m.MorphKeys.Select(k => (Hex(k.NameRaw), k.Frame))));
    }

    private static List<(string Key, int Count, uint First, uint Last)> Aggregate(
        IEnumerable<(string Key, uint Frame)> keys)
    {
        var map = new Dictionary<string, (int Count, uint First, uint Last)>();
        foreach (var (key, frame) in keys)
        {
            if (!map.TryGetValue(key, out var t))
                map[key] = (1, frame, frame);
            else
                map[key] = (t.Count + 1, t.First, frame); // First 不动，Last 逐步覆盖
        }
        return map.Select(kv => (kv.Key, kv.Value.Count, kv.Value.First, kv.Value.Last)).ToList();
    }

    private static void AssertTrackEqual(JsonElement expected,
        List<(string Key, int Count, uint First, uint Last)> actual)
    {
        var e = expected.EnumerateArray().ToArray();
        Assert.Equal(e.Length, actual.Count);
        for (int i = 0; i < e.Length; i++)
        {
            Assert.Equal(e[i].GetProperty("nameRaw").GetString(), actual[i].Key);
            Assert.Equal(e[i].GetProperty("count").GetInt32(), actual[i].Count);
            Assert.Equal(e[i].GetProperty("firstFrame").GetUInt32(), actual[i].First);
            Assert.Equal(e[i].GetProperty("lastFrame").GetUInt32(), actual[i].Last);
        }
    }

    // ---------------------------------------------------------------- 6. 坏数据异常路径（合成 buffer，不依赖真实文件）

    private static byte[] HeaderOnly(uint boneCount = 0, uint morphCount = 0)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.UTF8.GetBytes("Vocaloid Motion Data 0002")); // 25 字节
        w.Write(new byte[5]);  // 补齐签名区到 30 字节
        w.Write(new byte[20]); // 模型名
        w.Write(boneCount);
        w.Write(morphCount);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void TooShort_Throws()
    {
        Assert.Throws<VmdParseException>(() => VmdParser.Parse(new byte[10]));
        Assert.Throws<VmdParseException>(() => VmdParser.Parse([]));
    }

    [Fact]
    public void BadSignature_Throws()
    {
        var bad = HeaderOnly();
        bad[0] = (byte)'X';
        Assert.Throws<VmdParseException>(() => VmdParser.Parse(bad));
    }

    [Fact]
    public void TruncatedBoneSection_Throws()
    {
        // 声明 1 条骨骼关键帧但不给数据体
        var bad = HeaderOnly(boneCount: 1);
        Assert.Throws<VmdParseException>(() => VmdParser.Parse(bad));
    }

    [Fact]
    public void TruncatedMorphSection_Throws()
    {
        var bad = HeaderOnly(morphCount: 1);
        Assert.Throws<VmdParseException>(() => VmdParser.Parse(bad));
    }
}

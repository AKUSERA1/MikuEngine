using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace MikuEngine.Core.Animation;

/// <summary>VMD 解析失败（签名不符 / 区段越界 / 文件截断）。</summary>
public sealed class VmdParseException(string message) : Exception(message);

/// <summary>一条骨骼关键帧（VMD 原始数据，帧号不保证有序——排序在 Step 2 轨道构建时做）。</summary>
public readonly record struct VmdBoneKey(
    string BoneName,
    /// <summary>名字原始字节（截断到首个 0x00）——解码器无关的权威标识，供跨模型绑定兜底。</summary>
    byte[] NameRaw,
    uint Frame,
    Vector3 Position,
    Quaternion Rotation,
    /// <summary>64 字节插值参数原样保留（X/Y/Z/ROT 四条贝塞尔的 4×16B 副本，按通道分读在 Step 2）。</summary>
    byte[] Interpolation);

/// <summary>一条表情（morph）关键帧。</summary>
public readonly record struct VmdMorphKey(string MorphName, byte[] NameRaw, uint Frame, float Weight);

/// <summary>VMD 解析结果：骨骼/表情关键帧原样数据 + 其余区段仅计数。</summary>
public sealed class VmdMotion
{
    public string ModelName = "";
    /// <summary>模型名原始字节（20B 字段截断到首个 0x00）。</summary>
    public byte[] ModelNameRaw = [];

    /// <summary>文件顺序（与 babylon-mmd VmdObject 遍历顺序一致）。</summary>
    public List<VmdBoneKey> BoneKeys = [];
    public List<VmdMorphKey> MorphKeys = [];

    // 以下区段解析但只记数（相机/光照/自阴影本方案不消费；属性帧的 IK 开关在 IK 立项前无用）
    public int CameraKeyCount;
    public int LightKeyCount;
    public int SelfShadowKeyCount;
    public int PropertyKeyCount;

    /// <summary>按 VMD 规范解析完所有区段后的剩余字节数（非 0 说明文件有非规范尾巴）。</summary>
    public int LeftoverBytes;

    // 分区位置（字节），供测试做「分区原始字节哈希」级对照
    public int BoneSectionOffset;
    public int BoneSectionBytes;
    public int MorphSectionOffset;
    public int MorphSectionBytes;
}

/// <summary>
/// VMD (Vocaloid Motion Data 0002) 解析器。
///
/// 结构校验与字段偏移逐行对照 babylon-mmd 的 VmdData.CheckedCreate / BoneKeyFrame /
/// MorphKeyFrame（esm/Loader/Parser/vmdObject.js），测试以它的解析结果为权威基准。
///
/// 与 babylon 的两处已知差异（均不影响正确关键帧数据）：
///  1. Shift-JIS 非法字节：JS TextDecoder 输出 U+FFFD，.NET 932 输出 best-fit '?'——
///     只可能出现在脏名字里，合法 Shift-JIS 名两者逐字符一致。
///  2. 名字截断语义相同：在第一个 0x00 字节处截断再解码。
/// </summary>
public static class VmdParser
{
    private const string Signature = "Vocaloid Motion Data 0002";
    private const int SignatureBytes = 30;
    private const int ModelNameBytes = 20;
    private const int BoneKeyFrameBytes = 15 + 4 + 12 + 16 + 64;   // 111
    private const int MorphKeyFrameBytes = 15 + 4 + 4;             // 23
    private const int CameraKeyFrameBytes = 4 + 4 + 12 + 12 + 24 + 4 + 1; // 61
    private const int LightKeyFrameBytes = 4 + 12 + 12;            // 28
    private const int SelfShadowKeyFrameBytes = 4 + 1 + 4;         // 9
    private const int PropertyBaseBytes = 4 + 1;                   // + 4 (ikStateCount)
    private const int IkStateBytes = 20 + 1;

    private static readonly Encoding Sjis = CreateSjis();

    private static Encoding CreateSjis()
    {
        // net10 无内置 932 代码页，CodePages 包需注册后才能取
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    public static VmdMotion Parse(byte[] data)
    {
        // 文件头最小长度：签名 30 + 模型名 20
        if (data.Length < SignatureBytes + ModelNameBytes)
            throw new VmdParseException($"文件过短：{data.Length} 字节，不足头部 50 字节");

        // 签名：UTF-8 解码前 30 字节，比对前 26 字符（babylon 同样只比前缀、跳过整个 30 字节）
        var signature = Encoding.UTF8.GetString(data, 0, SignatureBytes);
        if (!signature.StartsWith(Signature, StringComparison.Ordinal))
            throw new VmdParseException($"签名不符：{signature[..System.Math.Min(30, signature.Length)]}");

        var motion = new VmdMotion();
        int offset = SignatureBytes;

        (motion.ModelName, motion.ModelNameRaw) = DecodeName(data, offset, ModelNameBytes);
        offset += ModelNameBytes;

        // —— 结构校验 + 分区定位（对应 CheckedCreate 的顺序：bone→morph→camera→light→selfShadow→property）——
        uint boneCount = ReadU32(data, ref offset);
        motion.BoneSectionOffset = offset;
        motion.BoneSectionBytes = checked((int)boneCount) * BoneKeyFrameBytes;
        RequireAvailable(data, offset, motion.BoneSectionBytes, "bone 关键帧区越界");
        offset += motion.BoneSectionBytes;

        uint morphCount = ReadU32(data, ref offset);
        motion.MorphSectionOffset = offset;
        motion.MorphSectionBytes = checked((int)morphCount) * MorphKeyFrameBytes;
        RequireAvailable(data, offset, motion.MorphSectionBytes, "morph 关键帧区越界");
        offset += motion.MorphSectionBytes;

        // 有些 VMD 没有 camera/light 区
        if (data.Length - offset != 0)
        {
            motion.CameraKeyCount = checked((int)ReadU32(data, ref offset));
            RequireAvailable(data, offset, motion.CameraKeyCount * CameraKeyFrameBytes, "camera 关键帧区越界");
            offset += motion.CameraKeyCount * CameraKeyFrameBytes;

            motion.LightKeyCount = checked((int)ReadU32(data, ref offset));
            RequireAvailable(data, offset, motion.LightKeyCount * LightKeyFrameBytes, "light 关键帧区越界");
            offset += motion.LightKeyCount * LightKeyFrameBytes;
        }

        // 有些 VMD 没有 selfShadow 区
        if (data.Length - offset != 0)
        {
            motion.SelfShadowKeyCount = checked((int)ReadU32(data, ref offset));
            RequireAvailable(data, offset, motion.SelfShadowKeyCount * SelfShadowKeyFrameBytes, "selfShadow 关键帧区越界");
            offset += motion.SelfShadowKeyCount * SelfShadowKeyFrameBytes;
        }

        // property 区帧长不定（含 IK 开关列表），必须逐帧跳
        if (data.Length - offset != 0)
        {
            motion.PropertyKeyCount = checked((int)ReadU32(data, ref offset));
            for (int i = 0; i < motion.PropertyKeyCount; i++)
            {
                RequireAvailable(data, offset, PropertyBaseBytes, "property 关键帧越界");
                offset += PropertyBaseBytes;   // frameNumber(u32) + visible(u8)
                int ikStateCount = checked((int)ReadU32(data, ref offset));
                RequireAvailable(data, offset, ikStateCount * IkStateBytes, "property IK 开关区越界");
                offset += ikStateCount * IkStateBytes;
            }
        }

        motion.LeftoverBytes = data.Length - offset;

        // —— 内容解码（文件顺序；与 babylon BoneKeyFrames/MorphKeyFrames 惰性读取一致）——
        // 注意：上面的结构校验是顺序扫描，offset 已走到文件尾；这里必须拨回分区起点再逐键解码。
        offset = motion.BoneSectionOffset;
        motion.BoneKeys.Capacity = (int)boneCount;
        for (uint i = 0; i < boneCount; i++)
        {
            (var name, var nameRaw) = DecodeName(data, offset, 15);
            offset += 15;
            uint frame = ReadU32(data, ref offset);
            var pos = new Vector3(ReadF32(data, ref offset), ReadF32(data, ref offset), ReadF32(data, ref offset));
            // VMD 四元数内存序 x,y,z,w，与 System.Numerics.Quaternion 构造参数一致
            var rot = new Quaternion(ReadF32(data, ref offset), ReadF32(data, ref offset),
                                     ReadF32(data, ref offset), ReadF32(data, ref offset));
            var interp = new byte[64];
            for (int k = 0; k < 64; k++) interp[k] = data[offset++];

            motion.BoneKeys.Add(new VmdBoneKey(name, nameRaw, frame, pos, rot, interp));
        }

        offset = motion.MorphSectionOffset;
        motion.MorphKeys.Capacity = (int)morphCount;
        for (uint i = 0; i < morphCount; i++)
        {
            (var name, var nameRaw) = DecodeName(data, offset, 15);
            offset += 15;
            uint frame = ReadU32(data, ref offset);
            float weight = ReadF32(data, ref offset);
            motion.MorphKeys.Add(new VmdMorphKey(name, nameRaw, frame, weight));
        }

        return motion;
    }

    // ---------------------------------------------------------------- 基础读取

    private static uint ReadU32(byte[] d, ref int offset)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset, 4));
        offset += 4;
        return v;
    }

    private static float ReadF32(byte[] d, ref int offset)
    {
        float v = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset, 4)));
        offset += 4;
        return v;
    }

    /// <summary>Shift-JIS 定长名：在第一个 0x00 处截断再解码（babylon getDecoderString trim=true 语义）。
    /// 同时返回原始字节——解码字符串随解码器（WHATWG vs .NET 932）对非标准字节映射不同，原始字节才是权威标识。</summary>
    private static (string Name, byte[] Raw) DecodeName(byte[] d, int offset, int length)
    {
        int end = offset + length;
        int len = 0;
        for (int i = offset; i < end; i++)
        {
            if (d[i] == 0) break;
            len++;
        }
        var raw = new byte[len];
        Array.Copy(d, offset, raw, 0, len);
        return (Sjis.GetString(d, offset, len), raw);
    }

    private static void RequireAvailable(byte[] d, int offset, int bytes, string message)
    {
        if (d.Length - offset < bytes)
            throw new VmdParseException($"{message}（需要 {bytes} 字节，实际剩余 {d.Length - offset}）");
    }
}

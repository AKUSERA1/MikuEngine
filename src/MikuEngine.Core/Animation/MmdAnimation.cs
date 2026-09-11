using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Animation;

/// <summary>
/// 单根骨骼的动画轨道（运行时形态）。
///
/// 关键帧按帧号<b>严格升序</b>，同一帧号去重并保留文件顺序的最后一次出现
/// （与 babylon-mmd <c>vmdLoader</c> 的 duplicate resolve 一致）。
/// 旋转序列在构建时已做<b>最短弧</b>处理（与前一键 dot &lt; 0 则取负），
/// 因此键间 slerp 天然走短弧，且相邻键四元数同半球。
/// </summary>
public sealed class MmdBoneTrack
{
    /// <summary>每键插值字节数：4 通道 × (x1, x2, y1, y2)。</summary>
    public const int InterpolationStride = 16;

    /// <summary>单通道控制点字节数。</summary>
    public const int ChannelStride = 4;

    /// <summary>骨骼名（解码后，与 <see cref="SkeletalModel.FindBone"/> 同一命名空间）。</summary>
    public string Name = "";

    /// <summary>绑定到模型后的骨索引；-1 = 未绑定（不同模型共用动效的常态）。</summary>
    public int BoneIndex = -1;

    /// <summary>帧号，严格升序。</summary>
    public int[] Frames = [];

    /// <summary>相对绑定姿势的父空间平移偏移（VMD 原始值）。</summary>
    public Vector3[] Offsets = [];

    /// <summary>局部旋转（已做最短弧对齐）。</summary>
    public Quaternion[] Rotations = [];

    /// <summary>
    /// 每键 <see cref="InterpolationStride"/> 字节的贝塞尔控制点：
    /// 索引 = <c>key * 16 + channel * 4 + {0:x1, 1:x2, 2:y1, 3:y2}</c>，
    /// channel 顺序 0=X, 1=Y, 2=Z, 3=ROTATION。
    ///
    /// VMD 原始只有 64B：每条通道的 4 个控制点散落在 4 份各 16B 的副本里，
    /// 通道 c 必须从其<b>自身副本</b> <c>c*16</c> 处读取
    /// <c>raw[c*16 + {0:x1, 4:y1, 8:x2, 12:y2}]</c>（babylon-mmd <c>vmdLoader</c> 与
    /// reze-engine 一致）。这里只保留这 4 个值、丢掉 3/4 的重复副本，
    /// 顺带避开 MMD 复用 <c>raw[2]/raw[3]</c> 存物理开关的坑。
    ///
    /// 区间 [a, b] 的曲线取自<b>后键 b</b>（MMD 语义：关键帧的插值参数描述的是从上一个键到它的曲线，
    /// babylon-mmd <c>MmdModelAnimationContainerBezierBuilder</c> 与 reze-engine 同此）。
    /// </summary>
    public byte[] Interpolation = [];

    public bool IsEmpty => Frames.Length == 0;

    /// <summary>
    /// 采样：<paramref name="frame"/> 为连续帧号，是帧号的<b>纯函数</b>（零累积状态）。
    /// 帧号早于首键 / 晚于末键时钳制到端键。
    /// </summary>
    public void Sample(double frame, out Quaternion rotation, out Vector3 offset)
    {
        int n = Frames.Length;
        if (n == 0)
        {
            rotation = Quaternion.Identity;
            offset = Vector3.Zero;
            return;
        }

        if (n == 1 || frame <= Frames[0])
        {
            rotation = Rotations[0];
            offset = Offsets[0];
            return;
        }

        int a = FindSegmentStart(Frames, frame);
        if (a >= n - 1)
        {
            rotation = Rotations[n - 1];
            offset = Offsets[n - 1];
            return;
        }

        int b = a + 1;
        double span = Frames[b] - Frames[a];
        if (span <= 0)
        {
            // 去重后不可能出现；兜底避免除零。
            rotation = Rotations[b];
            offset = Offsets[b];
            return;
        }

        float g = (float)((frame - Frames[a]) / span);

        int io = b * InterpolationStride;
        float rw = Bezier01(Interpolation[io + 12], Interpolation[io + 13],
                            Interpolation[io + 14], Interpolation[io + 15], g);
        rotation = Quaternion.Normalize(Quaternion.Slerp(Rotations[a], Rotations[b], rw));

        float wx = Bezier01(Interpolation[io + 0], Interpolation[io + 1],
                            Interpolation[io + 2], Interpolation[io + 3], g);
        float wy = Bezier01(Interpolation[io + 4], Interpolation[io + 5],
                            Interpolation[io + 6], Interpolation[io + 7], g);
        float wz = Bezier01(Interpolation[io + 8], Interpolation[io + 9],
                            Interpolation[io + 10], Interpolation[io + 11], g);

        offset = new Vector3(
            Offsets[a].X + (Offsets[b].X - Offsets[a].X) * wx,
            Offsets[a].Y + (Offsets[b].Y - Offsets[a].Y) * wy,
            Offsets[a].Z + (Offsets[b].Z - Offsets[a].Z) * wz);
    }

    /// <summary>只取旋转（<see cref="Sample"/> 的轻量包装）。</summary>
    public Quaternion SampleRotation(double frame)
    {
        Sample(frame, out var rotation, out _);
        return rotation;
    }

    /// <summary>只取平移偏移（<see cref="Sample"/> 的轻量包装）。</summary>
    public Vector3 SampleOffset(double frame)
    {
        Sample(frame, out _, out var offset);
        return offset;
    }

    /// <summary>返回轨道内最后一个 <c>Frames[i] &lt;= frame</c> 的 i；若 frame 早于首键返回 -1。</summary>
    internal static int FindSegmentStart(int[] frames, double frame)
    {
        int lo = 0, hi = frames.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (frames[mid] <= frame) lo = mid + 1;
            else hi = mid;
        }
        return lo - 1;
    }

    /// <summary>构建绑定到某骨索引的浅拷贝（共享数值数组，仅换 BoneIndex）。</summary>
    internal MmdBoneTrack WithBoneIndex(int boneIndex) => new()
    {
        Name = Name,
        BoneIndex = boneIndex,
        Frames = Frames,
        Offsets = Offsets,
        Rotations = Rotations,
        Interpolation = Interpolation,
    };

    // ---------------------------------------------------------------- 贝塞尔

    private const float Inv127 = 1f / 127f;

    /// <summary>
    /// MMD 三次贝塞尔：P0=(0,0)、P1=(x1,y1)、P2=(x2,y2)、P3=(127,127)，
    /// 输入 <paramref name="t"/> ∈ [0,1] 为归一化帧进度，返回缓动后的权重 ∈ [0,1]。
    ///
    /// 先用二分法解 <c>Bx(u) = t</c> 得参数 u，再求 <c>By(u)</c>。
    /// 控制点原样取字节（0..127），调用方无需预除。
    /// </summary>
    public static float Bezier01(byte bx1, byte bx2, byte by1, byte by2, float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        float x1 = bx1 * Inv127, x2 = bx2 * Inv127;
        float y1 = by1 * Inv127, y2 = by2 * Inv127;

        // 控制点落对角线 → Bx ≡ By ≡ 恒等，直接返回（MMD 默认线性键即此形）。
        if (x1 == y1 && x2 == y2) return t;

        // 二分法（对齐 babylon-mmd BezierInterpolate：15 次迭代 / eps 1e-5）。
        float c = 0.5f;
        float u = c;
        float s = 1f - u;
        float sst3 = 0f, stt3 = 0f, uuu = 0f;

        for (int i = 0; i < 15; i++)
        {
            sst3 = 3f * s * s * u;
            stt3 = 3f * s * u * u;
            uuu = u * u * u;
            float f = sst3 * x1 + stt3 * x2 + uuu - t;
            if (MathF.Abs(f) < 1e-5f) break;
            c *= 0.5f;
            u += f < 0f ? c : -c;
            s = 1f - u;
        }

        return sst3 * y1 + stt3 * y2 + uuu;
    }
}

/// <summary>
/// 单条表情（morph）轨道。权重为<b>线性</b>插值（VMD 不为 morph 存插值曲线）。
/// </summary>
public sealed class MmdMorphTrack
{
    public string Name = "";

    /// <summary>绑定到模型后的 morph 索引；-1 = 未绑定。</summary>
    public int MorphIndex = -1;

    public int[] Frames = [];
    public float[] Weights = [];

    public bool IsEmpty => Frames.Length == 0;

    /// <summary>采样权重（帧号的纯函数）；帧号早于首键 / 晚于末键时钳制到端键。</summary>
    public float SampleWeight(double frame)
    {
        int n = Frames.Length;
        if (n == 0) return 0f;
        if (n == 1 || frame <= Frames[0]) return Weights[0];

        int a = MmdBoneTrack.FindSegmentStart(Frames, frame);
        if (a >= n - 1) return Weights[n - 1];

        int b = a + 1;
        double span = Frames[b] - Frames[a];
        if (span <= 0) return Weights[b];

        float g = (float)((frame - Frames[a]) / span);
        return Weights[a] + (Weights[b] - Weights[a]) * g;
    }

    internal MmdMorphTrack WithMorphIndex(int morphIndex) => new()
    {
        Name = Name,
        MorphIndex = morphIndex,
        Frames = Frames,
        Weights = Weights,
    };
}

/// <summary>
/// VMD 动效在运行时侧的展开形态：按骨/表情聚合好的轨道集合。
///
/// 构建（<see cref="FromVmd"/>）与绑定（<see cref="Bind(SkeletalModel)"/>）分离：
/// 轨道数据与模型无关，可跨模型复用；<see cref="Bind(SkeletalModel)"/> 为每个模型产出一份
/// 仅含「该模型存在对应骨/表情」的实例（数值数组共享，不复制）。
/// </summary>
public sealed class MmdAnimation
{
    public string ModelName = "";

    public MmdBoneTrack[] BoneTracks = [];
    public MmdMorphTrack[] MorphTracks = [];

    /// <summary>首键帧号（所有轨道取并），无轨道时为 0。</summary>
    public double StartFrame;

    /// <summary>末键帧号（所有轨道取并），无轨道时为 0。</summary>
    public double EndFrame;

    /// <summary>
    /// 由 VMD 解析结果构建轨道集合（未绑定到任何模型）。
    /// 轨道按名字（解码后）聚合，键按帧号稳定升序排序、同帧去重保留最后。
    /// </summary>
    public static MmdAnimation FromVmd(VmdMotion vmd)
    {
        var animation = new MmdAnimation { ModelName = vmd.ModelName };

        var boneGroups = new Dictionary<string, List<VmdBoneKey>>(StringComparer.Ordinal);
        foreach (var key in vmd.BoneKeys)
        {
            if (!boneGroups.TryGetValue(key.BoneName, out var list))
                boneGroups[key.BoneName] = list = [];
            list.Add(key);
        }

        var boneTracks = new MmdBoneTrack[boneGroups.Count];
        int ti = 0;
        foreach (var (name, keys) in boneGroups)
            boneTracks[ti++] = BuildBoneTrack(name, keys);
        animation.BoneTracks = boneTracks;

        var morphGroups = new Dictionary<string, List<VmdMorphKey>>(StringComparer.Ordinal);
        foreach (var key in vmd.MorphKeys)
        {
            if (!morphGroups.TryGetValue(key.MorphName, out var list))
                morphGroups[key.MorphName] = list = [];
            list.Add(key);
        }

        var morphTracks = new MmdMorphTrack[morphGroups.Count];
        int mi = 0;
        foreach (var (name, keys) in morphGroups)
            morphTracks[mi++] = BuildMorphTrack(name, keys);
        animation.MorphTracks = morphTracks;

        double min = double.MaxValue, max = double.MinValue;
        foreach (var t in boneTracks)
            if (!t.IsEmpty) { min = System.Math.Min(min, t.Frames[0]); max = System.Math.Max(max, t.Frames[^1]); }
        foreach (var t in morphTracks)
            if (!t.IsEmpty) { min = System.Math.Min(min, t.Frames[0]); max = System.Math.Max(max, t.Frames[^1]); }
        if (max >= min)
        {
            animation.StartFrame = min;
            animation.EndFrame = max;
        }

        return animation;
    }

    /// <summary>
    /// 为 <paramref name="model"/> 产出一份绑定实例：找不到对应骨/表情的轨道<b>丢弃</b>
    /// （不同模型共用动效是常态）。数值数组与源轨道共享。
    /// </summary>
    public MmdAnimation Bind(SkeletalModel model)
    {
        var bones = new List<MmdBoneTrack>(BoneTracks.Length);
        foreach (var track in BoneTracks)
        {
            int index = model.FindBone(track.Name);
            if (index >= 0) bones.Add(track.WithBoneIndex(index));
        }

        var morphs = new List<MmdMorphTrack>(MorphTracks.Length);
        foreach (var track in MorphTracks)
        {
            int index = FindMorph(model, track.Name);
            if (index >= 0) morphs.Add(track.WithMorphIndex(index));
        }

        return new MmdAnimation
        {
            ModelName = ModelName,
            StartFrame = StartFrame,
            EndFrame = EndFrame,
            BoneTracks = bones.ToArray(),
            MorphTracks = morphs.ToArray(),
        };
    }

    /// <summary>一步完成「展开 + 绑定」。</summary>
    public static MmdAnimation Bind(VmdMotion vmd, SkeletalModel model) => FromVmd(vmd).Bind(model);

    /// <summary>
    /// 把 <paramref name="frame"/> 处的姿态写入模型（骨骼）。
    ///
    /// 纯函数语义：<b>先整体复位到绑定姿势</b>（局部旋转归单位、局部平移归 <see cref="SkeletalModel.LocalPositions"/>），
    /// 再逐轨道写入，不依赖也不保留上一帧的任何状态 —— 因此「连续播放到帧 N」与「直接 seek 到帧 N」结果完全一致。
    /// 只改局部 T/R，不重算世界矩阵（由调用方的 <c>PrepareFrame</c> 负责）。
    /// morph 权重本步骤不应用（Step 5）。
    /// </summary>
    public void Sample(SkeletalModel model, double frame)
    {
        var rotations = model.LocalRotations;
        for (int i = 0; i < rotations.Length; i++)
            rotations[i] = Quaternion.Identity;

        var translations = model.LocalTranslations;
        var bindPositions = model.LocalPositions;
        for (int i = 0; i < translations.Length; i++)
            translations[i] = bindPositions[i];

        foreach (var track in BoneTracks)
        {
            int index = track.BoneIndex;
            if ((uint)index >= (uint)model.BoneCount) continue;

            track.Sample(frame, out var rotation, out var offset);
            rotations[index] = rotation;
            // VMD 平移是相对绑定姿势的父空间偏移
            translations[index] = bindPositions[index] + offset;
        }
    }

    private static int FindMorph(SkeletalModel model, string name)
    {
        var names = model.MorphNames;
        for (int i = 0; i < names.Length; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    // ---------------------------------------------------------------- 轨道构建

    private static MmdBoneTrack BuildBoneTrack(string name, List<VmdBoneKey> keys)
    {
        // OrderBy 为稳定排序：同帧键保持文件顺序 → 后续保留"最后一次出现"与 babylon 一致。
        var sorted = keys.OrderBy(k => k.Frame).ToArray();
        int count = 0;
        for (int i = 0; i < sorted.Length; i++)
            if (i + 1 >= sorted.Length || sorted[i + 1].Frame != sorted[i].Frame)
                count++;

        var frames = new int[count];
        var offsets = new Vector3[count];
        var rotations = new Quaternion[count];
        var interpolation = new byte[count * MmdBoneTrack.InterpolationStride];

        int w = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i + 1 < sorted.Length && sorted[i + 1].Frame == sorted[i].Frame) continue;

            var key = sorted[i];
            var rotation = key.Rotation;
            if (w > 0 && Quaternion.Dot(rotations[w - 1], rotation) < 0f)
                rotation = -rotation;   // 最短弧：相邻键同半球

            frames[w] = (int)key.Frame;
            offsets[w] = key.Position;
            rotations[w] = rotation;
            WriteInterpolation(interpolation, w, key.Interpolation);
            w++;
        }

        return new MmdBoneTrack
        {
            Name = name,
            Frames = frames,
            Offsets = offsets,
            Rotations = rotations,
            Interpolation = interpolation,
        };
    }

    /// <summary>把 VMD 原始 64B 按通道分读成去冗余的 16B（见 <see cref="MmdBoneTrack.Interpolation"/>）。</summary>
    private static void WriteInterpolation(byte[] destination, int keyIndex, byte[] raw)
    {
        int o = keyIndex * MmdBoneTrack.InterpolationStride;
        for (int c = 0; c < 4; c++)
        {
            int b = c * 16;
            destination[o + 0] = raw[b + 0];    // x1
            destination[o + 1] = raw[b + 8];    // x2
            destination[o + 2] = raw[b + 4];    // y1
            destination[o + 3] = raw[b + 12];   // y2
            o += MmdBoneTrack.ChannelStride;
        }
    }

    private static MmdMorphTrack BuildMorphTrack(string name, List<VmdMorphKey> keys)
    {
        var sorted = keys.OrderBy(k => k.Frame).ToArray();
        int count = 0;
        for (int i = 0; i < sorted.Length; i++)
            if (i + 1 >= sorted.Length || sorted[i + 1].Frame != sorted[i].Frame)
                count++;

        var frames = new int[count];
        var weights = new float[count];

        int w = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i + 1 < sorted.Length && sorted[i + 1].Frame == sorted[i].Frame) continue;
            frames[w] = (int)sorted[i].Frame;
            weights[w] = sorted[i].Weight;
            w++;
        }

        return new MmdMorphTrack
        {
            Name = name,
            Frames = frames,
            Weights = weights,
        };
    }
}

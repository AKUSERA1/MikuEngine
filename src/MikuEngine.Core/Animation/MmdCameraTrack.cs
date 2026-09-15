using System.Numerics;

namespace MikuEngine.Core.Animation;

/// <summary>VMD 相机轨道的一个采样姿态。</summary>
public readonly record struct MmdCameraPose(
    /// <summary>注视点（MMD 世界空间，LH / Y-up）。</summary>
    Vector3 Target,
    /// <summary>相机朝向 euler（弧度，VMD 原始值，<b>未取负</b>；取负在相机侧做）。</summary>
    Vector3 RotationEuler,
    /// <summary>与注视点的距离（<b>负值</b> = 相机在注视点后方，VMD 原始语义）。</summary>
    float Distance,
    /// <summary>视角（弧度）。</summary>
    float Fov);

/// <summary>
/// VMD 相机轨道（运行时形态）：六条贝塞尔插值通道 ——
/// target(X/Y/Z 各一条)、rotation（<b>三轴共用一条</b>）、distance、fov。
///
/// 与骨骼轨道（<see cref="MmdBoneTrack"/>）的关键差异：
///  1. 插值字节布局不同：相机每键 24B，按通道<b>连续</b>排布（通道 c 占 [4c, 4c+3]，
///     字节序 x1,x2,y1,y2）；骨骼是 64B 的「每通道 4 份 16B 副本」结构。
///  2. 无模型绑定：相机是整场景量，不参与 <see cref="MmdAnimation.Bind"/> 的按模型过滤。
///  3. fov 原始是 u32 度数，构建时转弧度存储。
///
/// 键按帧号<b>严格升序</b>，同一帧号去重并保留文件顺序的最后一次出现（与骨骼轨道一致）。
/// 区间 [a, b] 的曲线取自<b>后键 b</b>（MMD 语义，与骨骼轨道一致）。
/// 采样为帧号的纯函数：区间外钳制到端键，空轨道返回 false。
/// </summary>
public sealed class MmdCameraTrack
{
    /// <summary>每键插值字节数：6 通道 × (x1, x2, y1, y2)。</summary>
    public const int InterpolationStride = 24;

    public int[] Frames = [];
    /// <summary>与注视点的距离（VMD 原始语义，负值 = 相机在后方）。</summary>
    public float[] Distances = [];
    public Vector3[] Targets = [];
    /// <summary>相机朝向 euler（弧度，VMD 原始值）。</summary>
    public Vector3[] Rotations = [];
    /// <summary>视角（弧度；VMD 原始 u32 度数在构建时已转）。</summary>
    public float[] Fovs = [];

    /// <summary>每键 <see cref="InterpolationStride"/> 字节贝塞尔控制点，
    /// 索引 = <c>key * 24 + channel * 4 + {0:x1, 1:x2, 2:y1, 3:y2}</c>，
    /// channel 顺序 0..5 = targetX, targetY, targetZ, rotation, distance, fov。</summary>
    public byte[] Interpolation = [];

    public bool IsEmpty => Frames.Length == 0;

    /// <summary>末键帧号（空轨道为 0）。</summary>
    public double EndFrame => Frames.Length > 0 ? Frames[^1] : 0;

    /// <summary>
    /// 采样（帧号的纯函数）。空轨道返回 false；区间外钳制到端键。
    /// rotation 三轴共用通道 3 的单条贝塞尔权重（MMD 相机旋转是单通道动画）。
    /// </summary>
    public bool Sample(double frame, out MmdCameraPose pose)
    {
        int n = Frames.Length;
        if (n == 0)
        {
            pose = default;
            return false;
        }

        if (n == 1 || frame <= Frames[0])
        {
            pose = new MmdCameraPose(Targets[0], Rotations[0], Distances[0], Fovs[0]);
            return true;
        }

        int a = MmdBoneTrack.FindSegmentStart(Frames, frame);
        if (a >= n - 1)
        {
            pose = new MmdCameraPose(Targets[n - 1], Rotations[n - 1], Distances[n - 1], Fovs[n - 1]);
            return true;
        }

        int b = a + 1;

        // 1 帧间隔的两键之间不做插值。这不是优化而是**语义**：MMD 只在整数帧上出画，相机切换（cut）的标准编码就是
        // 「两键相距 1 帧、值差异巨大」若照常插值，这一帧内会变成一次跨整个画幅的高速滑动。
        if (Frames[b] - Frames[a] <= 1)
        {
            pose = new MmdCameraPose(Targets[a], Rotations[a], Distances[a], Fovs[a]);
            return true;
        }

        double span = Frames[b] - Frames[a];
        if (span <= 0)
        {
            // 去重后不可能出现；兜底避免除零。
            pose = new MmdCameraPose(Targets[b], Rotations[b], Distances[b], Fovs[b]);
            return true;
        }

        float g = (float)((frame - Frames[a]) / span);
        int io = b * InterpolationStride;

        float Bezier(int channel) => MmdBoneTrack.Bezier01(
            Interpolation[io + channel * 4 + 0],
            Interpolation[io + channel * 4 + 1],
            Interpolation[io + channel * 4 + 2],
            Interpolation[io + channel * 4 + 3],
            g);

        float wx = Bezier(0), wy = Bezier(1), wz = Bezier(2);
        float wr = Bezier(3);   // rotation 三轴共用
        float wd = Bezier(4);
        float wf = Bezier(5);

        pose = new MmdCameraPose(
            Target: new Vector3(
                Targets[a].X + (Targets[b].X - Targets[a].X) * wx,
                Targets[a].Y + (Targets[b].Y - Targets[a].Y) * wy,
                Targets[a].Z + (Targets[b].Z - Targets[a].Z) * wz),
            RotationEuler: new Vector3(
                Rotations[a].X + (Rotations[b].X - Rotations[a].X) * wr,
                Rotations[a].Y + (Rotations[b].Y - Rotations[a].Y) * wr,
                Rotations[a].Z + (Rotations[b].Z - Rotations[a].Z) * wr),
            Distance: Distances[a] + (Distances[b] - Distances[a]) * wd,
            Fov: Fovs[a] + (Fovs[b] - Fovs[a]) * wf);
        return true;
    }

    /// <summary>由 VMD 解析结果构建相机轨道（稳定升序、同帧去重保留最后一次出现）。</summary>
    public static MmdCameraTrack FromVmd(List<VmdCameraKey> keys)
    {
        var track = new MmdCameraTrack();
        if (keys.Count == 0) return track;

        // OrderBy 为稳定排序：同帧键保持文件顺序 → 后续保留「最后一次出现」与 babylon-mmd 一致。
        var sorted = keys.OrderBy(k => k.Frame).ToArray();
        int count = 0;
        for (int i = 0; i < sorted.Length; i++)
            if (i + 1 >= sorted.Length || sorted[i + 1].Frame != sorted[i].Frame)
                count++;

        var frames = new int[count];
        var distances = new float[count];
        var targets = new Vector3[count];
        var rotations = new Vector3[count];
        var fovs = new float[count];
        var interpolation = new byte[count * InterpolationStride];

        int w = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i + 1 < sorted.Length && sorted[i + 1].Frame == sorted[i].Frame) continue;

            var key = sorted[i];
            frames[w] = (int)key.Frame;
            distances[w] = key.Distance;
            targets[w] = key.Target;
            rotations[w] = key.RotationEuler;
            fovs[w] = key.FovDegrees * MathF.PI / 180f;   // u32 度数 → 弧度
            Array.Copy(key.Interpolation, 0, interpolation, w * InterpolationStride, InterpolationStride);
            w++;
        }

        track.Frames = frames;
        track.Distances = distances;
        track.Targets = targets;
        track.Rotations = rotations;
        track.Fovs = fovs;
        track.Interpolation = interpolation;
        return track;
    }
}

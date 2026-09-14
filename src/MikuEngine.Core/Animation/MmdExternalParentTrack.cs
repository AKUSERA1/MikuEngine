using System.Numerics;

namespace MikuEngine.Core.Animation;

/// <summary>
/// 一条外部親绑定键（MMD VMD「外部親」轨道 / 程序化注册共用）。
///
/// MMD 语义：从 <see cref="Frame"/> 起，把子模型的根（全ての親 等无父骨）挂到
/// <see cref="ParentModel"/> 模型的 <see cref="ParentBone"/> 骨上；
/// <see cref="OffsetTranslation"/> / <see cref="OffsetRotation"/> 是叠加在亲骨世界变换
/// <b>之后</b>的偏移（VMD 键的 pos/rot 字段）。
///
/// VMD 里键存于子模型动作文件的骨键轨道「外部親」，但每个键自身的 15B 骨名字段
/// 存的是「親モデル名:親ボーン名」（优先全角冒号「：」，半角「:」兜底）——
/// 解析时已切分进 <see cref="ParentModel"/> / <see cref="ParentBone"/>。
/// <see cref="ParentModel"/> 为空串 = 解除绑定键。
/// </summary>
public readonly record struct MmdExternalParentKey(
    int Frame,
    string ParentModel,
    string ParentBone,
    Vector3 OffsetTranslation,
    Quaternion OffsetRotation);

/// <summary>
/// 外部親轨道：<b>离散状态机</b>，与表示枠（<see cref="MmdPropertyTrack"/>）同族 ——
/// 键与键之间保持、绝不插值；采样返回「≤ frame 的最后一键」，无键 = 未绑定。
///
/// 与骨骼轨道的关键差异：
///  1. <b>不做同帧去重</b>——同一帧的多条键是合法数据（每条一个绑定对，后者覆盖前者）；
///  2. 换亲 = 新键直接切换（无过渡）；解除 = ParentModel 为空的键；
///  3. 无模型绑定：轨道挂在 <see cref="MmdAnimation"/> 上原样透传（<see cref="MmdAnimation.Bind"/>），
///     亲模型按名字由消费者（<see cref="MmdExternalParentController"/>）解析。
/// </summary>
public sealed class MmdExternalParentTrack
{
    /// <summary>按 <see cref="MmdExternalParentKey.Frame"/> 升序（同帧保持文件顺序）。</summary>
    public MmdExternalParentKey[] Keys = [];

    public bool IsEmpty => Keys.Length == 0;

    /// <summary>末键帧号（空轨道为 0）。</summary>
    public double EndFrame => Keys.Length > 0 ? Keys[^1].Frame : 0;

    /// <summary>
    /// 采样：返回「≤ frame 的最后一键」。区间之前（无键覆盖）返回 false = 未绑定。
    /// 二分上界——键与键之间保持上一键的绑定状态（MMD 离散语义）。
    /// </summary>
    public bool TrySample(double frame, out MmdExternalParentKey key)
    {
        int lo = 0, hi = Keys.Length - 1, hit = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (Keys[mid].Frame <= frame) { hit = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (hit < 0)
        {
            key = default;
            return false;
        }
        key = Keys[hit];
        return true;
    }

    /// <summary>由解析结果构建（稳定升序排序，同帧保持文件顺序 → 后者覆盖前者）。</summary>
    public static MmdExternalParentTrack FromVmd(List<MmdExternalParentKey> keys)
    {
        var track = new MmdExternalParentTrack();
        if (keys.Count == 0) return track;

        track.Keys = keys.OrderBy(k => k.Frame).ToArray();
        return track;
    }
}

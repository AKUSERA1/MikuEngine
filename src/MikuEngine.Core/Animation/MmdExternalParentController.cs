using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Animation;

/// <summary>
/// MMD 外部親绑定控制器：按 VMD 外部親轨道（<see cref="MmdAnimation.ExternalParentTrack"/>）
/// 把子模型挂到亲模型的指定骨骼上。
///
/// MMD 官方语义：
/// <list type="bullet">
///   <item>绑定生效帧起，子模型<b>所有无父骨</b>挂到「亲模型根 × 亲骨世界矩阵 × VMD 键偏移」下
///         （骨骼级注入，见 <see cref="SkeletalModel.SetRootParent"/> —— 物理留在模型空间、
///         子模型自身动效照常叠加）；</item>
///   <item>绑定是<b>离散状态</b>：键间保持上一键的绑定，区间之前 / 解除键之后 = 普通骨架；</item>
///   <item>亲模型缺指定骨时挂亲模型根</item>
/// </list>
///
/// 每帧契约（先亲后子）：<b>亲模型</b>先完成姿态采样与 <see cref="SkeletalModel.UpdateWorldMatrices"/>，
/// 再调 <see cref="Update"/>，之后子模型才做自己的采样与 FK —— 子模型读到的是亲骨「本帧」的世界矩阵。
/// 链式绑定（A→B→C）按调用 <see cref="Update"/> 的顺序天然成立，深层链请按拓扑序逐帧调用或整体排序。
/// </summary>
public sealed class MmdExternalParentController
{
    private sealed class Entry
    {
        public required string Name;
        public required SkeletalModel Model;
        /// <summary>提供外部親轨道的动效；null = 纯亲模型（不采样，只被别人挂）。</summary>
        public MmdAnimation? Animation;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>「親モデル名:親ボーン名」→ 亲模型骨索引。外部親键是离散状态，绑定对在键间高度重复，避免每帧线性扫骨名。</summary>
    private readonly Dictionary<(string Model, string Bone), int> _boneIndexCache = [];

    /// <summary>注册模型。<paramref name="animation"/> 为该模型的外部親轨道来源（亲模型传 null）。</summary>
    public void Register(string name, SkeletalModel model, MmdAnimation? animation = null)
    {
        _entries[name] = new Entry { Name = name, Model = model, Animation = animation };
        _boneIndexCache.Clear();   // 模型集变化可能让缓存里的骨索引指向错误模型
    }

    /// <summary>注销模型。挂在其上的子模型下一帧自动解绑（亲模型不在注册表即回退普通骨架）。</summary>
    public void Unregister(string name)
    {
        _entries.Remove(name);
        _boneIndexCache.Clear();
    }

    /// <summary>清空注册表。</summary>
    public void Clear()
    {
        _entries.Clear();
        _boneIndexCache.Clear();
    }

    /// <summary>
    /// 每帧求值：对每个注册了动效的模型采样其外部親轨道，写 / 摘根父矩阵。
    /// 只写 <see cref="SkeletalModel.SetRootParent"/>，<b>不</b>触发 FK —— 由调用方按
    /// 「亲先子后」的顺序统一驱动各模型的姿态采样与 <c>UpdateWorldMatrices</c>。
    /// </summary>
    /// <param name="frame">当前时间轴帧号（与驱动各模型动效的游标同一来源）。</param>
    public void Update(double frame)
    {
        foreach (var (name, entry) in _entries)
        {
            var track = entry.Animation?.ExternalParentTrack;
            if (track is null || track.IsEmpty
                || !track.TrySample(frame, out var key)
                || key.ParentModel.Length == 0)
            {
                entry.Model.SetRootParent(null);
                continue;
            }

            if (!_entries.TryGetValue(key.ParentModel, out var parent) || parent.Model == entry.Model)
            {
                // 亲模型不存在 / 自绑定：MMD 面板上无从选择即无绑定，按未绑定处理
                entry.Model.SetRootParent(null);
                continue;
            }

            // 亲模型缺指定骨 → 挂亲模型根（亲根矩阵恒为单位阵）
            int boneIndex = ResolveBoneIndex(key.ParentModel, parent.Model, key.ParentBone);
            Matrix4x4 parentWorld = (uint)boneIndex < (uint)parent.Model.BoneCount
                ? parent.Model.WorldMatrices[boneIndex]
                : Matrix4x4.Identity;

            // VMD 键偏移 = 亲骨世界变换之后的叠加量。行主序组合：先对子空间点施加偏移，再落进亲骨世界。
            Matrix4x4 offset = Matrix4x4.CreateFromQuaternion(key.OffsetRotation)
                             * Matrix4x4.CreateTranslation(key.OffsetTranslation);
            entry.Model.SetRootParent(offset * parentWorld);
        }
    }

    /// <summary>按名字解析亲骨索引（带缓存）；找不到返回 -1。</summary>
    private int ResolveBoneIndex(string parentModelName, SkeletalModel parentModel, string boneName)
    {
        var key = (parentModelName, boneName);
        if (_boneIndexCache.TryGetValue(key, out int cached)) return cached;

        int index = boneName.Length == 0 ? -1 : parentModel.FindBone(boneName);
        _boneIndexCache[key] = index;
        return index;
    }
}

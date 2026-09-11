using System.Numerics;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// 顶点 morph 偏移 SSBO（<b>binding 2</b>）。
///
/// 布局与 shader 一致：<c>layout(std430, binding = 2) buffer MorphBlock { vec4 uMorphOffsets[]; }</c>
/// —— std430 下 vec4 数组 stride = 16 B，xyz 有效、w 未用。索引直接用 <c>gl_VertexID</c>：
/// <c>glDrawElements</c> 下它等于<b>顶点索引</b>（不是元素序号）。
///
/// 内存模型（与 babylon-mmd 的分野）：本类只有<b>一份</b> O(顶点数) 的偏移缓冲，
/// 大小与 morph 数量<b>无关</b>；每条 morph 的偏移数据保留在 Core 侧的稀疏表里。
/// babylon-mmd 给每条 morph 复制一份与顶点数等长的稠密数组 ⇒ O(顶点数 × morph 数)。
///
/// 每帧开销同样是稀疏的：
///   1. 只把「上一帧动过的顶点」清零（不整段 O(V) 清零）；
///   2. 只遍历「权重非 0」的 morph 的受影响顶点；
///   3. 只上传「上一帧 ∪ 本帧」脏区那一段（BufferSubData 带偏移）。
/// </summary>
public sealed unsafe class GlesMorphBuffer : IDisposable
{
    private readonly GlesDevice _device;

    /// <summary>CPU 侧累加缓冲（xyz = 模型空间偏移）。长度 = <see cref="VertexCapacity"/>。</summary>
    private readonly Vector4[] _offsets;

    /// <summary>上一帧动过的顶点 —— 本帧开头要逐点清零。</summary>
    private readonly List<int> _dirty = new();

    /// <summary>本帧动过的顶点（可能含重复，重复只是多清一次零，无正确性影响）。</summary>
    private readonly List<int> _touched = new();

    public uint BufferId { get; }

    /// <summary>缓冲能容纳的顶点数（= max(模型顶点数, 1)）。</summary>
    public int VertexCapacity { get; }

    /// <summary>最近一次 <see cref="Update"/> 碰过的顶点数（诊断用）。</summary>
    public int LastTouchedVertexCount { get; private set; }

    /// <summary>最近一次 <see cref="Update"/> 实际上传的顶点区间长度（0 = 本帧无需上传）。</summary>
    public int LastUploadVertexCount { get; private set; }

    public GlesMorphBuffer(GlesDevice device, int vertexCount)
    {
        _device = device;

        // 模型没有顶点 morph 时也给 1 个元素：保证 shader 里 binding 2 的块<b>始终有缓冲可绑</b>
        // （避免"声明了块却没绑"的未定义行为），同时靠 uMorphEnabled = 0 保证不读它。
        int capacity = System.Math.Max(vertexCount, 1);
        VertexCapacity = capacity;
        _offsets = new Vector4[capacity];

        var gl = device.Gl;
        BufferId = gl.CreateBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, BufferId);
        gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(capacity * sizeof(float) * 4), null,
            BufferUsageARB.DynamicDraw);
    }

    /// <summary>
    /// 按本帧有效权重重算偏移并上传脏区。必须在 <c>PrepareFrame</c> 里、
    /// 「世界矩阵已重算」之后调用（与蒙皮 SSBO 同帧提交）。
    /// </summary>
    public void Update(Core.Models.SkeletalModel model, ReadOnlySpan<float> weights)
    {
        int minDirty = int.MaxValue, maxDirty = -1;

        // ── 1. 清零上一帧动过的顶点 ────────────────────────────────────
        foreach (int v in _dirty)
        {
            if ((uint)v >= (uint)VertexCapacity) continue;
            _offsets[v] = Vector4.Zero;
            if (v < minDirty) minDirty = v;
            if (v > maxDirty) maxDirty = v;
        }
        _dirty.Clear();

        // ── 2. 稀疏累加：只碰活跃 morph 的受影响顶点 ────────────────────
        foreach (var morph in model.VertexMorphs)
        {
            int mi = morph.MorphIndex;
            if ((uint)mi >= (uint)weights.Length) continue;

            float weight = weights[mi];
            if (weight == 0f) continue;               // 权重为 0 的 morph 完全不碰

            var indices = morph.VertexIndices;
            var offsets = morph.Offsets;
            int n = System.Math.Min(indices.Length, offsets.Length);
            for (int k = 0; k < n; k++)
            {
                int v = indices[k];
                if ((uint)v >= (uint)VertexCapacity) continue;

                Vector3 o = offsets[k] * weight;
                _offsets[v] += new Vector4(o.X, o.Y, o.Z, 0f);
                _touched.Add(v);

                if (v < minDirty) minDirty = v;
                if (v > maxDirty) maxDirty = v;
            }
        }

        LastTouchedVertexCount = _touched.Count;

        // ── 3. 只上传脏区（上一帧 ∪ 本帧）────────────────────────────────
        // 注意：即便本帧没有活跃 morph，也必须把上一帧的脏区传一遍「已清零」的版本，
        // 否则 GPU 上会残留上一层表情的偏移。
        if (maxDirty >= 0)
        {
            var gl = _device.Gl;
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, BufferId);
            fixed (Vector4* p = &_offsets[minDirty])
            {
                gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, (nint)(minDirty * 16),
                    (nuint)((maxDirty - minDirty + 1) * 16), p);
            }
            LastUploadVertexCount = maxDirty - minDirty + 1;
        }
        else
        {
            LastUploadVertexCount = 0;
        }

        // ── 4. 本帧动过的顶点 = 下一帧的待清零集合 ──────────────────────
        _dirty.AddRange(_touched);
        _touched.Clear();
    }

    /// <summary>绑定到 <paramref name="binding"/>（默认 2，与 shader 的 MorphBlock 对应）。</summary>
    public void Bind(uint binding = 2)
        => _device.Gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, BufferId);

    public void Dispose()
    {
        if (BufferId != 0)
            _device.Gl.DeleteBuffer(BufferId);
    }
}

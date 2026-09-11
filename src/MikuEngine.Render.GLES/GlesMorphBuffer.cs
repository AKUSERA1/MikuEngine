using System.Numerics;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// 表情偏移 SSBO 的通用实现（顶点 morph → binding 2，UV morph → binding 3）。
///
/// 元素尺寸由 <c>components</c> 决定，与 shader 里的声明一一对应：
///   * 顶点 morph：<c>vec4 uMorphOffsets[]</c>（std430 stride 16 B，用 xyz）
///   * UV morph  ：<c>vec2 uMorphUvs[]</c>    （std430 stride  8 B）
/// 索引一律用 <c>gl_VertexID</c>：<c>glDrawElements</c> 下它等于<b>顶点索引</b>（不是元素序号）。
///
/// 内存模型（与 babylon-mmd 的分野）：只有<b>一份</b> O(顶点数) 的缓冲，大小与 morph 数量
/// <b>无关</b>；每条 morph 的偏移数据仍保留在 Core 侧的稀疏表里。babylon-mmd 给每条 morph
/// 复制一份与顶点数等长的稠密数组 ⇒ O(顶点数 × morph 数)。
///
/// 每帧开销同样稀疏：
///   1. 只把「上一帧动过的顶点」清零（不整段 O(V) 清零）；
///   2. 只遍历「权重非 0」的 morph 的受影响顶点累加；
///   3. 只上传「上一帧 ∪ 本帧」脏区那一段（BufferSubData 带偏移）。
/// </summary>
public sealed unsafe class GlesMorphBuffer : IDisposable
{
    private readonly GlesDevice _device;

    /// <summary>每顶点分量数：4（vec4 顶点偏移）或 2（vec2 UV 偏移）。</summary>
    private readonly int _components;

    /// <summary>CPU 侧累加缓冲，长度 = <see cref="VertexCapacity"/> × <see cref="_components"/>。</summary>
    private readonly float[] _data;

    /// <summary>上一帧动过的顶点 —— 本帧开头要逐点清零。</summary>
    private readonly List<int> _dirty = new();

    /// <summary>本帧动过的顶点（可能含重复；重复只是多清一次零，无正确性影响）。</summary>
    private readonly List<int> _touched = new();

    public uint BufferId { get; }

    /// <summary>缓冲能容纳的顶点数（= max(模型顶点数, 1)）。</summary>
    public int VertexCapacity { get; }

    /// <summary>最近一次 Update 碰过的顶点数（诊断用）。</summary>
    public int LastTouchedVertexCount { get; private set; }

    /// <summary>最近一次 Update 实际上传的顶点区间长度（0 = 本帧无需上传）。</summary>
    public int LastUploadVertexCount { get; private set; }

    public GlesMorphBuffer(GlesDevice device, int vertexCount, int components)
    {
        _device = device;
        _components = components;

        // 模型没有该类 morph 时也给 1 个元素：保证 shader 里对应 binding 的块<b>始终有缓冲可绑</b>
        // （避免"声明了块却没绑"的未定义行为），是否读它由 uMorph*Enabled 决定。
        int capacity = System.Math.Max(vertexCount, 1);
        VertexCapacity = capacity;
        _data = new float[capacity * components];

        var gl = device.Gl;
        BufferId = gl.CreateBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, BufferId);

        // 必须显式上传一份零数组：glBufferData(..., null, ...) 的内容是【未定义】的，
        // 而本类只上传「脏区」，未受影响顶点永远不会被写 —— 若初始内容是垃圾，
        // 那些顶点会带着垃圾偏移参与蒙皮（表现为模型炸开 / 每次运行结果不同）。
        fixed (float* p = _data)
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(_data.Length * sizeof(float)), p,
                BufferUsageARB.DynamicDraw);
    }

    /// <summary>按本帧有效权重重算<b>顶点 morph</b> 偏移并上传脏区（元素 = vec4）。</summary>
    public void Update(Core.Models.SkeletalModel model, ReadOnlySpan<float> weights)
    {
        int min = int.MaxValue, max = -1;
        ClearDirty(ref min, ref max);

        foreach (var morph in model.VertexMorphs)
        {
            float weight = WeightAt(weights, morph.MorphIndex);
            if (weight == 0f) continue;               // 权重为 0 的 morph 完全不碰

            var indices = morph.VertexIndices;
            var offsets = morph.Offsets;
            int n = System.Math.Min(indices.Length, offsets.Length);
            for (int k = 0; k < n; k++)
            {
                int v = indices[k];
                if ((uint)v >= (uint)VertexCapacity) continue;

                Vector3 o = offsets[k] * weight;
                int b = v * 4;
                _data[b] += o.X;
                _data[b + 1] += o.Y;
                _data[b + 2] += o.Z;
                Touch(v, ref min, ref max);
            }
        }

        EndFrame(min, max);
    }

    /// <summary>按本帧有效权重重算 <b>UV morph</b> 偏移并上传脏区（元素 = vec2）。</summary>
    public void UpdateUv(Core.Models.SkeletalModel model, ReadOnlySpan<float> weights)
    {
        int min = int.MaxValue, max = -1;
        ClearDirty(ref min, ref max);

        foreach (var morph in model.UvMorphs)
        {
            float weight = WeightAt(weights, morph.MorphIndex);
            if (weight == 0f) continue;

            var indices = morph.VertexIndices;
            var offsets = morph.Offsets;
            int n = System.Math.Min(indices.Length, offsets.Length);
            for (int k = 0; k < n; k++)
            {
                int v = indices[k];
                if ((uint)v >= (uint)VertexCapacity) continue;

                Vector2 o = offsets[k] * weight;
                int b = v * 2;
                _data[b] += o.X;
                _data[b + 1] += o.Y;
                Touch(v, ref min, ref max);
            }
        }

        EndFrame(min, max);
    }

    /// <summary>绑定到 <paramref name="binding"/>（顶点 = 2，UV = 3）。</summary>
    public void Bind(uint binding)
        => _device.Gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, BufferId);

    // ---------------------------------------------------------------- 内部

    private static float WeightAt(ReadOnlySpan<float> weights, int index)
        => (uint)index < (uint)weights.Length ? weights[index] : 0f;

    /// <summary>清零上一帧动过的顶点，并把它们的区间并入本次上传范围。</summary>
    private void ClearDirty(ref int min, ref int max)
    {
        foreach (int v in _dirty)
        {
            if ((uint)v >= (uint)VertexCapacity) continue;

            int b = v * _components;
            for (int c = 0; c < _components; c++)
                _data[b + c] = 0f;

            if (v < min) min = v;
            if (v > max) max = v;
        }
        _dirty.Clear();
        LastTouchedVertexCount = 0;
    }

    private void Touch(int v, ref int min, ref int max)
    {
        _touched.Add(v);
        LastTouchedVertexCount = _touched.Count;
        if (v < min) min = v;
        if (v > max) max = v;
    }

    /// <summary>
    /// 上传「上一帧 ∪ 本帧」脏区（只这一段）。
    /// 注意：即便本帧没有活跃 morph，也必须把上一帧的脏区以「已清零」的版本传一遍，
    /// 否则 GPU 上会残留上一层表情的偏移。
    /// </summary>
    private void EndFrame(int min, int max)
    {
        if (max >= 0)
        {
            var gl = _device.Gl;
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, BufferId);
            int first = min * _components;
            fixed (float* p = &_data[first])
            {
                gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer,
                    (nint)(first * sizeof(float)),
                    (nuint)((max - min + 1) * _components * sizeof(float)), p);
            }
            LastUploadVertexCount = max - min + 1;
        }
        else
        {
            LastUploadVertexCount = 0;
        }

        // 本帧动过的顶点 = 下一帧的待清零集合
        _dirty.AddRange(_touched);
        _touched.Clear();
    }

    public void Dispose()
    {
        if (BufferId != 0)
            _device.Gl.DeleteBuffer(BufferId);
    }
}

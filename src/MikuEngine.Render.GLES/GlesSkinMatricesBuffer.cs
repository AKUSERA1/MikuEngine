using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// 整帧所有角色共用的蒙皮矩阵 SSBO。
///
/// 为什么不用 UBO：GLES 3.1 的 <c>GL_MAX_UNIFORM_BLOCK_SIZE</c> 最小保证只有 16 KB，
/// 而 500 骨骼就要 32 KB；测试用的 Model.pmx 有 1099 骨 ≈ 70 KB，UBO 直接爆掉。
/// <c>GL_MAX_SHADER_STORAGE_BLOCK_SIZE</c> 的最小保证是 128 MB，绰绰有余。
///
/// layout 与 shader 一致：<c>layout(std430, binding = 1) buffer { mat4 uSkinMatrices[]; }</c>
/// 矩阵按 System.Numerics 的内存序原样打包（行主序 row-vector ≡ GLSL 列主序 column-vector，
/// 与 GLSL mat4 内存一致，**无需 transpose**）。
/// </summary>
public sealed unsafe class GlesSkinMatricesBuffer : IDisposable
{
    private readonly GlesDevice _device;
    private readonly List<float> _scratch = new();

    public uint BufferId { get; private set; }

    /// <summary>已分配的骨骼容量（不是本帧实际使用量）。</summary>
    public int CapacityBoneCount { get; private set; }

    /// <summary>本帧实际骨数。</summary>
    public int UsedBoneCount { get; private set; }

    public GlesSkinMatricesBuffer(GlesDevice device, int initialBoneCapacity = 0)
    {
        _device = device;
        if (initialBoneCapacity > 0)
            EnsureCapacity(initialBoneCapacity);
    }

    /// <summary>扩容。</summary>
    public void EnsureCapacity(int requiredBoneCount)
    {
        if (requiredBoneCount <= CapacityBoneCount) return;

        var gl = _device.Gl;
        int oldCap = CapacityBoneCount;
        int newCap = Math.Max(requiredBoneCount, Math.Max(oldCap * 2, 64));
        nuint bytes = (nuint)(newCap * 16 * sizeof(float));

        uint newBuf = gl.CreateBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, newBuf);
        gl.BufferData(BufferTargetARB.ShaderStorageBuffer, bytes, null, BufferUsageARB.DynamicDraw);

        // 旧数据不拷：每帧 Flush 都会从 CPU 侧整段重传，拷贝没有意义。
        if (BufferId != 0)
            gl.DeleteBuffer(BufferId);

        BufferId = newBuf;
        CapacityBoneCount = newCap;
    }

    /// <summary>每帧开头调用：重置游标，保留已分配的 GPU 容量。</summary>
    public void BeginFrame()
    {
        UsedBoneCount = 0;
        _scratch.Clear();
    }

    /// <summary>
    /// 追加一批蒙皮矩阵，返回它在 SSBO 里的 base offset（骨骼索引起点）。
    /// 多个角色依次调用即可在同一条缓冲里排开。
    /// </summary>
    public int Append(ReadOnlySpan<Matrix4x4> skinMatrices)
    {
        if (skinMatrices.Length == 0) return UsedBoneCount;

        EnsureCapacity(UsedBoneCount + skinMatrices.Length);

        int baseOffset = UsedBoneCount;
        var floats = MemoryMarshal.Cast<Matrix4x4, float>(skinMatrices);

        if (_scratch.Capacity < _scratch.Count + floats.Length)
            _scratch.Capacity = _scratch.Count + floats.Length;

        for (int i = 0; i < floats.Length; i++)
            _scratch.Add(floats[i]);

        UsedBoneCount += skinMatrices.Length;
        return baseOffset;
    }

    /// <summary>把 CPU 侧累积的矩阵真正提交到 GPU。</summary>
    public void Flush()
    {
        if (UsedBoneCount <= 0) return;

        var gl = _device.Gl;
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, BufferId);

        var span = CollectionsMarshal.AsSpan(_scratch);
        fixed (float* p = span)
            gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0,
                (nuint)(UsedBoneCount * 16 * sizeof(float)), p);
    }

    public void Bind(uint binding = 1)
    {
        _device.Gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, binding, BufferId);
    }

    public void Dispose()
    {
        if (BufferId != 0)
        {
            _device.Gl.DeleteBuffer(BufferId);
            BufferId = 0;
        }
    }
}

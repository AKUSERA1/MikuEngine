using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// grid.vert/frag 的 uniform block 内存布局（std140）。
/// 总大小 192 字节，每个 vec4 16B、mat4 64B。
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 192)]
public struct GridUniform
{
    [FieldOffset(0)] public Matrix4x4 ViewProj;        // 64B
    [FieldOffset(64)] public Vector4 FogColor;         // 16B
    [FieldOffset(80)] public Vector4 FadeParams;       // 16B (x=fadeStart y=fadeEnd z=fogStart w=fogEnd)
    [FieldOffset(96)] public Vector4 GridParams;       // 16B (x=spacing y=lineWidth z=majorEvery w=gridOpacity)
    [FieldOffset(112)] public Vector4 MinorColor;      // 16B
    [FieldOffset(128)] public Vector4 MajorColor;       // 16B
    [FieldOffset(144)] public Vector4 AxisXColor;       // 16B
    [FieldOffset(160)] public Vector4 AxisZColor;       // 16B
    [FieldOffset(176)] public Vector4 Surface;         // 16B (xyz=表面色 w=表面不透明度)

    public static GridUniform Default()
    {
        return new GridUniform
        {
            ViewProj = Matrix4x4.Identity,
            FogColor = new Vector4(0.11f, 0.14f, 0.22f, 1.0f),
            FadeParams = new Vector4(30f, 120f, 20f, 150f),
            GridParams = new Vector4(2f, 0.02f, 5f, 0.9f),
            MinorColor = new Vector4(0.30f, 0.35f, 0.45f, 1.0f),
            MajorColor = new Vector4(0.55f, 0.60f, 0.70f, 1.0f),
            AxisXColor = new Vector4(0.90f, 0.35f, 0.35f, 1.0f),
            AxisZColor = new Vector4(0.35f, 0.55f, 0.95f, 1.0f),
            Surface = new Vector4(0.08f, 0.10f, 0.16f, 1.0f),
        };
    }
}

/// <summary>
/// 迷雾格网地面渲染器。
/// </summary>
public sealed unsafe class GlesGridRenderer : IDisposable
{
    private readonly GlesDevice _device;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _ubo;
    private GridUniform _uniform;
    private bool _disposed;

    public const float Size = 500f;

    public GlesGridRenderer(GlesDevice device)
    {
        _device = device;
        var gl = device.Gl;

        _program = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.grid.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.grid.frag.glsl");

        // 一个大四边形（三角形带）：4 个顶点 position only
        float[] vertices =
        {
            -Size, 0f, -Size,   // 0
             Size, 0f, -Size,   // 1
            -Size, 0f,  Size,   // 2
             Size, 0f,  Size,   // 3
        };
        uint vbo = device.CreateVbo<float>(vertices);

        _vao = gl.CreateVertexArray();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        gl.BindVertexArray(0);
        gl.DeleteBuffer(vbo);

        // UBO: size 192B, bind to binding 0
        _ubo = device.CreateUbo((nuint)sizeof(GridUniform));
        _uniform = GridUniform.Default();
        UpdateUniform();
    }

    private void UpdateUniform()
    {
        _device.UpdateUbo(_ubo, ref _uniform);
    }

    /// <summary>每帧调用：写入 view-proj 矩阵并绘制。</summary>
    public void Draw(Matrix4x4 viewProj)
    {
        var gl = _device.Gl;
        _uniform.ViewProj = viewProj;
        UpdateUniform();

        gl.UseProgram(_program);
        gl.BindVertexArray(_vao);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _ubo);
        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var gl = _device.Gl;
        gl.DeleteVertexArray(_vao);
        gl.DeleteBuffer(_ubo);
        gl.DeleteProgram(_program);
    }
}

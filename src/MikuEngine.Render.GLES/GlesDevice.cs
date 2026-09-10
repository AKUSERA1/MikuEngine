using System.Reflection;
using System.Text;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// GLES 3.1 设备封装：持有 Silk.NET 的 GL API 入口，提供 shader 编译、
/// VAO/VBO/UBO 创建等辅助方法。
///
/// GL 实例由调用方在已有的 GL context 上构建：
///   // window 是 Silk.NET.Windowing.Window
///   var gl = GL.GetApi(window.GLContext);
///   var device = new GlesDevice(gl, width, height);
///
/// Android 端同理，从 GLSurfaceView 的 EGL context 获取。
/// </summary>
public sealed unsafe class GlesDevice : IDisposable
{
    /// <summary>Silk.NET 生成的 OpenGL/GLES API 入口。</summary>
    public GL Gl { get; }

    /// <summary>屏幕尺寸（像素）。</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>默认清屏色：中性灰蓝（与迷雾格网的雾色一致）。</summary>
    public static readonly (float r, float g, float b, float a) DefaultClearColor = (0.11f, 0.14f, 0.22f, 1.0f);

    private bool _disposed;

    public GlesDevice(GL gl, int width, int height)
    {
        Gl = gl;
        Width = width;
        Height = height;

        gl.Enable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.ClearColor(DefaultClearColor.r, DefaultClearColor.g, DefaultClearColor.b, DefaultClearColor.a);
    }

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        Gl.Viewport(0, 0, (uint)width, (uint)height);
    }

    public void BeginFrame()
    {
        Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    // ---------------------------------------------------------------- Shader

    /// <summary>
    /// 从 EmbeddedResource 编译并链接一个 shader program。
    /// resourceName 格式："MikuEngine.Render.GLES.Shaders.grid.vert.glsl"
    /// </summary>
    public uint BuildProgram(string vertexResourceName, string fragmentResourceName)
    {
        string vs = ReadEmbeddedResource(vertexResourceName);
        string fs = ReadEmbeddedResource(fragmentResourceName);
        return BuildProgramFromSource(vs, fs);
    }

    public uint BuildProgramFromSource(string vertexSource, string fragmentSource)
    {
        uint vs = CompileShader(ShaderType.VertexShader, vertexSource);
        uint fs = CompileShader(ShaderType.FragmentShader, fragmentSource);

        uint program = Gl.CreateProgram();
        Gl.AttachShader(program, vs);
        Gl.AttachShader(program, fs);
        Gl.LinkProgram(program);

        Gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            Gl.GetProgramInfoLog(program, out string log);
            throw new InvalidOperationException($"Shader link failed:\n{log}");
        }

        Gl.DeleteShader(vs);
        Gl.DeleteShader(fs);
        return program;
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = Gl.CreateShader(type);
        Gl.ShaderSource(shader, source);
        Gl.CompileShader(shader);

        Gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compileStatus);
        if (compileStatus == 0)
        {
            Gl.GetShaderInfoLog(shader, out string log);
            throw new InvalidOperationException($"Shader compile failed ({type}):\n{log}\n--- source ---\n{source}");
        }
        return shader;
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        var asm = typeof(GlesDevice).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ---------------------------------------------------------------- Buffers

    public uint CreateVbo<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        uint buf = Gl.CreateBuffer();
        Gl.BindBuffer(BufferTargetARB.ArrayBuffer, buf);
        fixed (T* p = data)
            Gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(T)), p, BufferUsageARB.StaticDraw);
        return buf;
    }

    public uint CreateUbo(nuint size)
    {
        uint buf = Gl.CreateBuffer();
        Gl.BindBuffer(BufferTargetARB.UniformBuffer, buf);
        Gl.BufferData(BufferTargetARB.UniformBuffer, size, null, BufferUsageARB.DynamicDraw);
        return buf;
    }

    public void UpdateUbo<T>(uint ubo, ref T data) where T : unmanaged
    {
        Gl.BindBuffer(BufferTargetARB.UniformBuffer, ubo);
        fixed (T* p = &data)
            Gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, (nuint)sizeof(T), p);
    }

    public uint CreateVao(Action<uint> configure)
    {
        uint vao = Gl.CreateVertexArray();
        Gl.BindVertexArray(vao);
        configure(vao);
        Gl.BindVertexArray(0);
        return vao;
    }

    // ---------------------------------------------------------------- IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

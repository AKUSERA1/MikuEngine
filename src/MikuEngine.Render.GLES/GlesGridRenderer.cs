using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// grid.vert/frag 的 uniform block 内存布局（std140）。
/// 总大小 192 字节。ViewProj 是列主序 float[16]，直接从 OrbitCamera.ComputeViewProj 拷贝。
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 192)]
public struct GridUniform
{
    [FieldOffset(0)]  public float M00; [FieldOffset(4)]  public float M10;
    [FieldOffset(8)]  public float M20; [FieldOffset(12)] public float M30;
    [FieldOffset(16)] public float M01; [FieldOffset(20)] public float M11;
    [FieldOffset(24)] public float M21; [FieldOffset(28)] public float M31;
    [FieldOffset(32)] public float M02; [FieldOffset(36)] public float M12;
    [FieldOffset(40)] public float M22; [FieldOffset(44)] public float M32;
    [FieldOffset(48)] public float M03; [FieldOffset(52)] public float M13;
    [FieldOffset(56)] public float M23; [FieldOffset(60)] public float M33;

    [FieldOffset(64)]  public Vector4 FogColor;
    [FieldOffset(80)]  public Vector4 FadeParams;
    [FieldOffset(96)]  public Vector4 GridParams;
    [FieldOffset(112)] public Vector4 MinorColor;
    [FieldOffset(128)] public Vector4 MajorColor;
    [FieldOffset(144)] public Vector4 AxisXColor;
    [FieldOffset(160)] public Vector4 AxisZColor;
    [FieldOffset(176)] public Vector4 Surface;

    /// <summary>把列主序 float[16] 写入 ViewProj 字段。</summary>
    public void SetViewProj(ReadOnlySpan<float> colMajor16)
    {
        M00 = colMajor16[0];  M10 = colMajor16[1];  M20 = colMajor16[2];  M30 = colMajor16[3];
        M01 = colMajor16[4];  M11 = colMajor16[5];  M21 = colMajor16[6];  M31 = colMajor16[7];
        M02 = colMajor16[8];  M12 = colMajor16[9];  M22 = colMajor16[10]; M32 = colMajor16[11];
        M03 = colMajor16[12]; M13 = colMajor16[13]; M23 = colMajor16[14]; M33 = colMajor16[15];
    }

    public static GridUniform Default()
    {
        var u = new GridUniform
        {
            FogColor = new Vector4(0.11f, 0.14f, 0.22f, 1.0f),
            FadeParams = new Vector4(30f, 120f, 20f, 150f),
            GridParams = new Vector4(2f, 0.02f, 5f, 0.9f),
            MinorColor = new Vector4(0.30f, 0.35f, 0.45f, 1.0f),
            MajorColor = new Vector4(0.55f, 0.60f, 0.70f, 1.0f),
            AxisXColor = new Vector4(0.90f, 0.35f, 0.35f, 1.0f),
            AxisZColor = new Vector4(0.35f, 0.55f, 0.95f, 1.0f),
            Surface = new Vector4(0.08f, 0.10f, 0.16f, 1.0f),
        };
        // ViewProj 列主序单位矩阵：(0,0)=1, (1,1)=1, (2,2)=1, (3,3)=1
        u.M00 = 1f; u.M11 = 1f; u.M22 = 1f; u.M33 = 1f;
        return u;
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

        _ubo = device.CreateUbo((nuint)sizeof(GridUniform));
        _uniform = GridUniform.Default();
        UpdateUniform();
    }

    private void UpdateUniform()
    {
        _device.UpdateUbo(_ubo, ref _uniform);
    }

    /// <summary>每帧调用：写入列主序 view-proj 矩阵并绘制。</summary>
    /// <remarks>
    /// 地面格网**不写深度**（只做深度测试），理由：
    ///   ① 地板/格网是"背景"，模型已经在它之前画完并写好深度，格网仍会被模型正确遮挡；
    ///   ② 与同处 y=0 的**床影 overlay 共面**。如果格网写深度，后画的床影就要靠
    ///      浮点深度恰好大于等于格网才能通过 Lequal —— 两个四边形的三角化与插值不同，
    ///      逐象素深度会有几个 ULP 的差，就会出现斑纹闪烁（z-fight）。
    ///      把格网的深度写入关掉，两者之间根本不存在深度比较，问题从机制上消失。
    ///
    /// 参考 PE：PE 的床影 pass 同样写 <c>ZWRITEENABLE = false</c>（fxd 的 tec_floorShadow 开混合、
    /// DrawModel_Shadow 关深度写），使影纯为"贴地 overlay"。PE 视口没有格网，
    /// 本仓的格网是自有的，因此两侧都要让出深度写入才能彻底消除共面争用。
    /// </remarks>
    public void Draw(ReadOnlySpan<float> viewProjColMajor16)
    {
        var gl = _device.Gl;
        _uniform.SetViewProj(viewProjColMajor16);
        UpdateUniform();

        gl.UseProgram(_program);
        gl.BindVertexArray(_vao);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _ubo);

        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(false);          // ← 关键：地面不写深度

        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        gl.DepthMask(true);           // 还原，避免影响后续 pass
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

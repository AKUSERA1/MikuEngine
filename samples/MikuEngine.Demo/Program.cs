using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using MikuEngine.Render.GLES;

// Silk.NET 2.x 在 Windows 上默认用 GLFW 后端，不需要显式注册
var options = WindowOptions.Default;
options.Size = new (960, 600);           // System.Drawing.Size (Silk.NET 传递引用)
options.Title = "MikuEngine GLES Demo — Foggy Grid Ground";
options.WindowState = WindowState.Normal;
options.VSync = true;

using var window = Window.Create(options);

GL? gl = null;
GlesDevice? device = null;
GlesGridRenderer? grid = null;

// 简化版轨道相机（右手坐标系，固定初始视角）
var camera = new SimpleOrbitCamera
{
    Alpha = 0.785f,   // 45°
    Beta = 0.785f,    // 45° 俯仰
    Radius = 60f,
    Target = Vector3.Zero,
    Fov = MathF.PI / 4f,
};

window.Load += () =>
{
    // Loaded 事件中 GL context 已由 GLFW 建好
    gl = GL.GetApi((Silk.NET.Core.Contexts.IGLContext)window.GLContext!);
    var size = window.FramebufferSize;
    device = new GlesDevice(gl, size.X, size.Y);
    grid = new GlesGridRenderer(device);

    Console.WriteLine($"[Demo] GL version: {gl.GetStringS(StringName.Version)}");
    Console.WriteLine($"[Demo] GL renderer: {gl.GetStringS(StringName.Renderer)}");
    Console.WriteLine($"[Demo] Device ready. Framebuffer={size.X}x{size.Y}");
};

window.FramebufferResize += size =>
{
    device?.Resize(size.X, size.Y);
};

window.Render += dt =>
{
    if (device == null || grid == null) return;

    device.BeginFrame();

    int w = window.FramebufferSize.X;
    int h = window.FramebufferSize.Y;
    float aspect = h > 0 ? (float)w / h : 1f;
    camera.Aspect = aspect;

    Matrix4x4 viewProj = camera.ComputeViewProj();
    grid.Draw(viewProj);
};

window.Closing += () =>
{
    grid?.Dispose();
    device?.Dispose();
};

window.Run();

sealed class SimpleOrbitCamera
{
    public float Alpha;
    public float Beta;
    public float Radius;
    public Vector3 Target;
    public float Fov = MathF.PI / 4f;
    public float Aspect = 1f;
    public float Near = 0.1f;
    public float Far = 1000f;

    private const float MinPitch = 0.01f;
    private const float MaxPitch = MathF.PI - 0.01f;

    public Vector3 Position => new(
        Target.X + Radius * MathF.Sin(Beta) * MathF.Sin(Alpha),
        Target.Y + Radius * MathF.Cos(Beta),
        Target.Z + Radius * MathF.Sin(Beta) * MathF.Cos(Alpha));

    public void Orbit(float dx, float dy)
    {
        const float sens = 0.005f;
        Alpha += dx * sens;
        Beta -= dy * sens;
        Beta = MathF.Max(MinPitch, MathF.Min(MaxPitch, Beta));
    }

    public void Zoom(float dy)
    {
        const float sens = 0.05f;
        Radius = MathF.Max(2f, Radius - dy * sens);
        Near = MathF.Max(0.05f, Radius / 50f);
        Far = MathF.Min(3000f, Radius * 20f);
    }

    public Matrix4x4 ComputeViewProj()
    {
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(Fov, Aspect, Near, Far);
        var view = Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);
        return Matrix4x4.Multiply(view, proj);
    }
}

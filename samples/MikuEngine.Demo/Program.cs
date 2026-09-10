using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using Silk.NET.GLFW;
using MikuEngine.Core.Camera;
using MikuEngine.Render.GLES;
using MikuEngine.Engine;

// GLES 3.1 Demo — Foggy Grid Ground + OrbitCamera + OrbitInputController
var options = WindowOptions.Default;
options.Size = new Silk.NET.Maths.Vector2D<int>(960, 600);
options.Title = "MikuEngine GLES — Foggy Grid Ground";
options.WindowState = WindowState.Normal;
options.VSync = true;

using var window = Window.Create(options);

GL? gl = null;
GlesDevice? device = null;
GlesGridRenderer? grid = null;

// 初始视角：45° yaw + 60° pitch，距原点 100 单位
var camera = new OrbitCamera(
    alpha: MathF.PI / 4f,
    beta: MathF.PI / 3f,
    radius: 100f,
    target: System.Numerics.Vector3.Zero,
    fov: MathF.PI / 4f);

// ── 引擎封装的输入控制器：调用方只需决定 enabled = true ──
var input = new OrbitInputController(camera, enabled: true);

window.Load += () =>
{
    gl = GL.GetApi((Silk.NET.Core.Contexts.IGLContext)window.GLContext!);
    var size = window.FramebufferSize;
    device = new GlesDevice(gl, size.X, size.Y);
    grid = new GlesGridRenderer(device);

    // —— 平台层只做"原生事件 → 控制器方法"一行转发 ——
    unsafe
    {
        var glfw = GlfwWindowing.GetExistingApi(window);
        var hwnd = GlfwWindowing.GetHandle(window);

        glfw.SetMouseButtonCallback(hwnd, (w, button, action, mods) =>
        {
            var btn = button switch
            {
                MouseButton.Left => OrbitInputController.PointerButton.Left,
                MouseButton.Right => OrbitInputController.PointerButton.Right,
                MouseButton.Middle => OrbitInputController.PointerButton.Middle,
                _ => OrbitInputController.PointerButton.None,
            };

            glfw.GetCursorPos(hwnd, out double x, out double y);
            if (action == InputAction.Press)
                input.OnPointerDown(0, (float)x, (float)y, btn);
            else if (action == InputAction.Release)
                input.OnPointerUp(0);
        });

        glfw.SetCursorPosCallback(hwnd, (w, x, y) =>
        {
            input.OnPointerMove(0, (float)x, (float)y);
        });

        glfw.SetScrollCallback(hwnd, (w, xOff, yOff) =>
        {
            input.OnScroll((float)yOff);
        });
    }

    Console.WriteLine($"[Demo] GL version: {gl.GetStringS(StringName.Version)}");
    Console.WriteLine($"[Demo] GL renderer: {gl.GetStringS(StringName.Renderer)}");
    Console.WriteLine($"[Demo] Device ready. Framebuffer={size.X}x{size.Y}");
    Console.WriteLine($"[Demo] Camera: pos={camera.Position:F2}, radius={camera.Radius:F1}, near={camera.Near:F2}, far={camera.Far:F0}");
    Console.WriteLine($"[Demo] Input controller enabled: {input.Enabled}");
    Console.WriteLine("[Demo] 鼠标: 左键=旋转 | 右键=平移 | 滚轮=缩放");
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

    Span<float> viewProj = stackalloc float[16];
    camera.ComputeViewProj(viewProj);
    grid.Draw(viewProj);
};

window.Closing += () =>
{
    grid?.Dispose();
    device?.Dispose();
};

window.Run();

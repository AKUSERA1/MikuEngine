# 最小可运行窗口

本页说明一个最小可用宿主需要哪些代码：创建窗口 → 建相机与输入 → 绑定原生事件 → 每帧渲染。

**需要的命名空间**：`Silk.NET.OpenGL`、`Silk.NET.Windowing`、`Silk.NET.GLFW`、
`MikuEngine.Core.Camera`、`MikuEngine.Engine`、`MikuEngine.Render.GLES`。

## Step 1 —— 创建窗口

```csharp
var options = WindowOptions.Default;
options.Size = new Silk.NET.Maths.Vector2D<int>(960, 600);
options.Title = "MikuEngine";
options.VSync = true;

using var window = Window.Create(options);
```

## Step 2 —— 创建相机 + 输入控制器

```csharp
var camera = new OrbitCamera(
    alpha: MathF.PI / 4f,     // yaw = 45°
    beta: MathF.PI / 3f,      // pitch = 60°
    radius: 100f,             // 距原点 100 单位
    target: Vector3.Zero,     // 看向原点
    fov: MathF.PI / 4f);      // 45° 视锥

// 调用方只决定是否启用
var input = new OrbitInputController(camera, enabled: true);
```

参数含义与可调属性见 [OrbitCamera API](../core/orbit-camera.md) 与
[OrbitInputController API](../engine/orbit-input-controller.md)。

## Step 3 —— 平台层绑定 GLFW 回调（一行转发）

```csharp
window.Load += () =>
{
    gl = GL.GetApi((IGLContext)window.GLContext!);
    device = new GlesDevice(gl, width, height);
    grid = new GlesGridRenderer(device);

    // 平台层只做「原生事件 → 控制器方法」一行转发
    unsafe
    {
        var glfw = GlfwWindowing.GetExistingApi(window);
        var hwnd = GlfwWindowing.GetHandle(window);

        glfw.SetMouseButtonCallback(hwnd, (w, btn, action, mods) =>
        {
            glfw.GetCursorPos(hwnd, out double x, out double y);
            if (action == InputAction.Press)
                input.OnPointerDown(0, (float)x, (float)y, ...);
            else if (action == InputAction.Release)
                input.OnPointerUp(0);
        });
        glfw.SetCursorPosCallback(hwnd, (w, x, y) => input.OnPointerMove(0, (float)x, (float)y));
        glfw.SetScrollCallback(hwnd, (w, xo, yo) => input.OnScroll((float)yo));
    }
};
```

`GlfwWindowing.GetHandle` 返回裸指针，因此必须包在 `unsafe` 块里。
完整回调签名与常见坑见 [桌面 GLFW 集成](../platform-integration/desktop-glfw.md)。

## Step 4 —— 每帧渲染

```csharp
window.Render += dt =>
{
    device.BeginFrame();                          // 清屏 + 设置 viewport

    camera.Aspect = width / (float)height;        // 宽高比每帧都要更新
    Span<float> viewProj = stackalloc float[16];
    camera.ComputeViewProj(viewProj);             // 一步算出 View×Proj（列主序）

    grid.Draw(viewProj);                          // 喂给渲染层
};
```

窗口大小变化时必须同步调用 `device.Resize(w, h)`，否则画面会被拉伸：

```csharp
window.FramebufferResize += size => device?.Resize(size.X, size.Y);
```

## Step 5 —— 生命周期清理

```csharp
window.Closing += () =>
{
    grid?.Dispose();      // 先释放 Renderer（它们各自持有 GL 资源）
    device?.Dispose();    // 再释放 Device
};
```

## 下一步

- 坐标系与矩阵格式 → [坐标系约定](../core/coordinate-system.md)
- 相机行为调整 → [OrbitCamera API](../core/orbit-camera.md)
- 加载并渲染模型 → [GlesModelRenderer](../render-gles/gles-model-renderer.md)

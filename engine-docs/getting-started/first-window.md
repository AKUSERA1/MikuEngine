# 跑通第一个 Demo

本页是 `samples/MikuEngine.Demo/Program.cs` 的逐段讲解。  
完整代码见 [Program.cs](../../samples/MikuEngine.Demo/Program.cs)。

## 完整清单

**你需要**：`using Silk.NET.OpenGL`、`using Silk.NET.Windowing`、`using Silk.NET.GLFW`、`using MikuEngine.Core.Camera`、`using MikuEngine.Engine`、`using MikuEngine.Render.GLES`。

## Step 1 —— 创建窗口

```csharp
var options = WindowOptions.Default;
options.Size = new Silk.NET.Maths.Vector2D<int>(960, 600);
options.Title = "MikuEngine GLES Demo";
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

关于参数含义和可调属性，见：
- [OrbitCamera API](../core/orbit-camera.md)
- [OrbitInputController API](../engine/orbit-input-controller.md)

## Step 3 —— 平台层绑定 GLFW 回调（一行转发）

```csharp
window.Load += () =>
{
    gl = GL.GetApi((IGLContext)window.GLContext!);
    device = new GlesDevice(gl, width, height);
    grid = new GlesGridRenderer(device);

    // 平台层只做"原生事件 → 控制器方法"一行转发
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

详细解释 → [桌面 GLFW 集成](../platform-integration/desktop-glfw.md)。

## Step 4 —— 每帧渲染

```csharp
window.Render += dt =>
{
    device.BeginFrame();                          // 清屏 + 设置 viewport

    camera.Aspect = width / (float)height;        // 更新宽高比
    Span<float> viewProj = stackalloc float[16];
    camera.ComputeViewProj(viewProj);              // 一步算出 View×Proj（列主序）

    grid.Draw(viewProj);                           // 喂给渲染层
};
```

## Step 5 —— 生命周期清理

```csharp
window.Closing += () =>
{
    grid?.Dispose();
    device?.Dispose();
};
```

## 下一步

- 想了解坐标系和矩阵格式 → [core/coordinate-system.md](../core/coordinate-system.md)
- 想调整相机行为 → [core/orbit-camera.md](../core/orbit-camera.md)
- 想在 Android 上跑 → [platform-integration/desktop-glfw.md](../platform-integration/desktop-glfw.md)（Android 章节待实现，架构相同）

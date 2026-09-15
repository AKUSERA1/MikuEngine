# 桌面 GLFW 集成

本页讲解 Silk.NET.Windowing.Glfw 2.23 + Silk.NET.GLFW 的输入绑定方式。

## 窗口创建

```csharp
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

var options = WindowOptions.Default;
options.Size = new Vector2D<int>(960, 600);
options.Title = "MikuEngine";
options.VSync = true;
using var window = Window.Create(options);
```

## GL 上下文获取

```csharp
gl = GL.GetApi((IGLContext)window.GLContext!);
```

Silk.NET.OpenGL 2.23 是统一入口类：桌面加载 OpenGL，Android 加载 GLES 函数指针。
**调用方无需条件编译**。

## 输入绑定：使用 GLFW 原生 API

Silk.NET.Windowing 2.23 的 `IWindow` 接口**不暴露**鼠标 / 键盘 / 触控事件；
`Silk.NET.Input` + `Silk.NET.Input.Glfw` 那条路径在当前版本不可用
（`IWindow.Input` 属性不存在）。

**正确做法**：直接用 `Silk.NET.GLFW` 原生回调：

```csharp
using Silk.NET.Windowing.Glfw;   // GlfwWindowing 静态类
using Silk.NET.GLFW;              // Glfw 实例 + 回调委托

unsafe
{
    var glfw = GlfwWindowing.GetExistingApi(window);   // → Glfw 实例
    var hwnd = GlfwWindowing.GetHandle(window);          // → WindowHandle*
    // 然后 glfw.SetMouseButtonCallback(hwnd, handler);
}
```

### 完整的输入绑定

```csharp
using Silk.NET.Windowing.Glfw;
using Silk.NET.GLFW;
using MikuEngine.Engine;

// 构造
var input = new OrbitInputController(camera, enabled: true);

// 在 window.Load 事件里绑定
unsafe
{
    var glfw = GlfwWindowing.GetExistingApi(window);
    var hwnd = GlfwWindowing.GetHandle(window);

    // 鼠标按键
    glfw.SetMouseButtonCallback(hwnd, (w, button, action, mods) =>
    {
        var btn = button switch
        {
            MouseButton.Left   => OrbitInputController.PointerButton.Left,
            MouseButton.Right  => OrbitInputController.PointerButton.Right,
            MouseButton.Middle => OrbitInputController.PointerButton.Middle,
            _                  => OrbitInputController.PointerButton.None,
        };

        glfw.GetCursorPos(hwnd, out double x, out double y);
        if (action == InputAction.Press)
            input.OnPointerDown(0, (float)x, (float)y, btn);
        else if (action == InputAction.Release)
            input.OnPointerUp(0);
    });

    // 鼠标移动
    glfw.SetCursorPosCallback(hwnd, (w, x, y) =>
        input.OnPointerMove(0, (float)x, (float)y));

    // 滚轮
    glfw.SetScrollCallback(hwnd, (w, xOff, yOff) =>
        input.OnScroll((float)yOff));
}
```

### 三个回调委托签名

| 方法 | 回调委托签名 |
|---|---|
| `SetMouseButtonCallback` | `(WindowHandle*, MouseButton, InputAction, KeyModifiers) → void` |
| `SetCursorPosCallback` | `(WindowHandle*, double x, double y) → void` |
| `SetScrollCallback` | `(WindowHandle*, double xOff, double yOff) → void` |

枚举 `MouseButton` / `InputAction` 与 GLFW C API 一致，可直接和
`OrbitInputController.PointerButton` 映射。

回调内取到的坐标是**窗口客户区坐标**（左上原点，y 向下），与
`OrbitInputController` 期望的屏幕坐标一致；GLFW 不提供触控 API，
桌面端 pointerId 只用 `0`。

## 生命周期完整清单

```csharp
window.Load += () =>
{
    gl = GL.GetApi((IGLContext)window.GLContext!);
    device = new GlesDevice(gl, width, height);
    grid   = new GlesGridRenderer(device);
    // 输入绑定（unsafe { ... }）
};

window.FramebufferResize += size =>
    device?.Resize(size.X, size.Y);

window.Render += dt =>
{
    device.BeginFrame();
    camera.Aspect = viewportWidth / (float)viewportHeight;
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
```

## 常见坑

| 坑 | 症状 | 解法 |
|---|---|---|
| 用 `Silk.NET.Input.Glfw` 而不是 `Silk.NET.GLFW` | 编译报错 `IWindow.Input 不存在` | 直接用 GLFW 原生 API |
| 忘记 `unsafe` 块 | `GlfwWindowing.GetHandle` 返回指针，必须 unsafe | 包装 `unsafe { }` |
| Viewport 没调 | 渲染正常但 resize 后内容拉伸 / 压缩 | `FramebufferResize` 事件里调 `device.Resize` |
| GLFW 回调里持有 GL 引用 | 本身没问题（回调在 GL 线程上） | 但不要在非 Load / Render 线程调 GL，保持 GL 调用集中在 Load + Render |

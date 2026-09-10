# GlesDevice

GL 上下文包装 + 帧缓冲状态管理。

## 源文件

[GlesDevice.cs](../../../src/MikuEngine.Render.GLES/GlesDevice.cs)

## 构造函数

```csharp
var device = new GlesDevice(GL gl, int width, int height);
```

`GL` 来自 Silk.NET.OpenGL 的统一入口：

```csharp
using Silk.NET.OpenGL;
gl = GL.GetApi((IGLContext)window.GLContext!);
```

## 公共方法

| 方法 | 说明 |
|---|---|
| `Resize(int w, int h)` | 窗口大小变化时调用，更新 viewport |
| `BeginFrame()` | 每帧开始时调用：设置 viewport、清除颜色缓冲、启用深度测试 / 背面剔除 |
| `Dispose()` | 释放 GL 资源（VAO / VBO / UBO / Shader） |

## BeginFrame 做了什么

```
gl.Viewport(0, 0, width, height);
gl.ClearColor(0.08f, 0.09f, 0.10f, 1.0f);  // 深色背景
gl.Clear(Gl.ColorBufferBit | Gl.DepthBufferBit);
gl.Enable(Gl.DepthTest);
gl.Enable(Gl.CullFace);
gl.CullFace(Gl.Back);
```

背景色默认深灰蓝（接近 MMD 编辑器默认色），可调吗？**当前不暴露**。需要改的话在 GlesDevice.cs 里改 `BeginFrame` 的 `ClearColor`。

## 资源生命周期

```csharp
device = new GlesDevice(gl, width, height);
grid = new GlesGridRenderer(device);    // 构造时创建 GL 资源

// 每帧
device.BeginFrame();
grid.Draw(viewProj);

// 关闭时
grid.Dispose();    // 先 Dispose Renderer（依赖 device 的 GL 指针）
device.Dispose();  // 再 Dispose device
```

顺序：**先释放 Renderer，再释放 Device**。Renderer 持有 GL 引用，Device 是 GL 指针的包装。

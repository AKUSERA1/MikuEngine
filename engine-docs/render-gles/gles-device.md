# GlesDevice

GL 上下文包装 + Shader 编译 + VBO/UBO/SSBO 辅助方法。

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

构造时自动初始化全局状态：
- 启用深度测试（`DepthTest`）
- 启用混合（`Blend`，`SrcAlpha / OneMinusSrcAlpha`）
- 设置清屏色为中性灰蓝（`DefaultClearColor = (0.11, 0.14, 0.22, 1.0)`）

## 公共属性

| 属性 | 类型 | 说明 |
|---|---|---|
| `Gl` | `GL` | Silk.NET.OpenGL 的 GL API 入口 |
| `Width` | `int` | 当前视口宽度（像素） |
| `Height` | `int` | 当前视口高度（像素） |
| `DefaultClearColor` | `(float r, g, b, a)` | 静态默认清屏色，可作为调参起点 |

## 公共方法

### Resize / BeginFrame / Dispose

| 方法 | 说明 |
|---|---|
| `Resize(int w, int h)` | 窗口大小变化时调用，更新 `Width/Height` 并 `gl.Viewport(0, 0, w, h)` |
| `BeginFrame()` | 每帧开始：清除颜色 + 深度缓冲 |
| `Dispose()` | 标记释放（当前版本无实际 GL 资源释放，Renderer 各自负责） |

### Shader 编译

```csharp
// 从 EmbeddedResource 编译并链接（资源名格式：MikuEngine.Render.GLES.Shaders.grid.vert.glsl）
uint program = device.BuildProgram(vertexResourceName, fragmentResourceName);

// 或从源码字符串编译
uint program = device.BuildProgramFromSource(vertexSource, fragmentSource);
```

内部自动做 `CompileShader → AttachShader → LinkProgram`，失败抛 `InvalidOperationException` 并带完整 info log + 源码片段。

### Buffer / VAO 辅助

```csharp
// 静态 VBO（上传后不改）
uint vbo = device.CreateVbo<float>(vertices);

// 动态 UBO（每帧 glBufferSubData）
uint ubo = device.CreateUbo(sizeInBytes);
device.UpdateUbo(ubo, ref dataStruct);   // ref T : unmanaged

// VAO（在 configure 回调里绑定 VBO + 设置 vertex attrib pointer）
uint vao = device.CreateVao(vao => {
    gl.BindBuffer(ArrayBuffer, myVbo);
    gl.EnableVertexAttribArray(0);
    gl.VertexAttribPointer(0, 3, Float, false, 12, (void*)0);
});
```

## 资源生命周期

```csharp
device = new GlesDevice(gl, width, height);

// 各 Renderer 在构造时自己创建 GL 资源（program / vao / ubo）
var grid   = new GlesGridRenderer(device);
var model  = GlesModelRenderer.LoadFromFile(device, "model.pmx");
var shadow = new GlesShadowRenderer(device);

// 每帧
device.BeginFrame();
camera.Aspect = (float)width / height;
camera.ComputeViewProj(viewProj);
shadow.UpdateLight(model.Model, lightDir);
model.PrepareFrame(in frame);
shadow.RenderShadowMaps(device, model, width, height);
model.Draw(in frame);
grid.Draw(viewProj);
shadow.DrawFloor(device, lightColor);

// 关闭时：先释放 Renderer（它们持有 GL 引用）
shadow.Dispose();
model.Dispose();
grid.Dispose();
device.Dispose();
```

顺序：**先释放 Renderer，再释放 Device**。Device 只是 GL 指针的包装，Renderer 各自持有 program / vao / buffer 句柄。

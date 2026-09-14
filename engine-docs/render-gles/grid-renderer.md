# GlesGridRenderer

渲染 500×500 MMD 单位的迷雾格网地面。格网**不写深度**（只做深度测试），与床影共面无 z-fight。

## 源文件

[GlesGridRenderer.cs](../../src/MikuEngine.Render.GLES/GlesGridRenderer.cs)  
[grid.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/grid.vert.glsl)  
[grid.frag.glsl](../../src/MikuEngine.Render.GLES/Shaders/grid.frag.glsl)

## 构造 + Draw

```csharp
using MikuEngine.Render.GLES;

var grid = new GlesGridRenderer(device);

Span<float> viewProj = stackalloc float[16];
camera.ComputeViewProj(viewProj);
grid.Draw(viewProj);

grid.Dispose();
```

## Uniform 布局（GridUniform）

`GridUniform` 用 `[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 192)]` 显式布局，总大小 192 字节。ViewProj 是列主序 `float[16]`（64 字节），通过 `SetViewProj(ReadOnlySpan<float>)` 一次性填充：

| 偏移 | 字段 | 类型 | 说明 |
|---|---|---|---|
| 0 | M00 – M33 | 16×float | 列主序 ViewProj，直接来自 OrbitCamera |
| 64 | FogColor | `Vector4` | 雾色 `(0.11, 0.14, 0.22, 1.0)`，与背景一致 |
| 80 | FadeParams | `Vector4` | `(30, 120, 20, 150)` — 近距离、远距离淡入淡出的距离阈值 |
| 96 | GridParams | `Vector4` | `(2, 0.02, 5, 0.9)` — 小格间距、大格间距、格线粗细、格线 α |
| 112 | MinorColor | `Vector4` | 小格线颜色 |
| 128 | MajorColor | `Vector4` | 大格线颜色 |
| 144 | AxisXColor | `Vector4` | +X 轴线颜色（红色 `(0.90, 0.35, 0.35)`） |
| 160 | AxisZColor | `Vector4` | +Z 轴线颜色（蓝色 `(0.35, 0.55, 0.95)`） |
| 176 | Surface | `Vector4` | 地面填充色 |

调用方不需要手动填充——`GlesGridRenderer.Draw(viewProj)` 内部自动 set。

## 深度写入 = false 的理由

`GlesGridRenderer.Draw` 里显式设 `gl.DepthMask(false)` 然后在返回前还原 `gl.DepthMask(true)`。

原因：格网和床影（floor shadow）都在 y=0 平面上绘制。如果两者都写深度，逐像素深度会有几个 ULP 的差，出现斑纹闪烁（z-fight）。关掉格网的深度写入后，两者之间根本不存在深度比较，问题从机制上消失。这与 PmxEditor 的做法一致——PE 床影 pass 同样设 `ZWRITEENABLE = false`。

## Shader 版本

```glsl
#version 310 es
precision mediump float;
```

GLES 3.1 兼容。桌面 OpenGL 上 Silk.NET.OpenGL 自动加载，不需要改版本号。

## 不是"真正的地面"

这是**调试 / 占位格网**：
- 只有线框，没有法线 / 材质 / 物理碰撞
- 用于相机控制和视锥裁剪的快速验证
- 真正的地面渲染（可能包含地形 / 物理刚体 / Toon Shader）后续实现

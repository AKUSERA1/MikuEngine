# GlesGridRenderer

渲染网格地面 + 雾色淡出。

## 源文件

[GlesGridRenderer.cs](../../../src/MikuEngine.Render.GLES/GlesGridRenderer.cs)  
[grid.vert.glsl](../../../src/MikuEngine.Render.GLES/Shaders/grid.vert.glsl)  
[grid.frag.glsl](../../../src/MikuEngine.Render.GLES/Shaders/grid.frag.glsl)

## 构造 + Draw

```csharp
using MikuEngine.Render.GLES;

var grid = new GlesGridRenderer(device);

Span<float> viewProj = stackalloc float[16];
camera.ComputeViewProj(viewProj);
grid.Draw(viewProj);

grid.Dispose();
```

## Uniform 布局

`GridUniform` 是显式 `float[192]` 结构，16 个 float（列主序 ViewProj）占 64 字节，加上其他标量凑齐 192 字节（UBO std140 要求 256 对齐）：

| 偏移 | 字段 | 类型 | 说明 |
|---|---|---|---|
| 0 | ViewProj (M00-M33) | 16×float | 列主序，直接来自 OrbitCamera |
| 64 | GridSize | float | 网格半宽（默认 500 MMD 单位） |
| 68 | Divisions | float | 每边分段数（默认 40） |
| 72 | FogNear | float | 雾开始距离 |
| 76 | FogFar | float | 雾结束距离 |

调用方不需要手动填充——`GlesGridRenderer.Draw(viewProj)` 内部自动 set。

## 雾色淡出

Fragment Shader 根据 fragment 到相机的距离做线性插值：

```glsl
float fogFactor = smoothstep(FogNear, FogFar, distance);
vec3 color = mix(gridColor, fogColor, fogFactor);
```

`GridSize = 500` 对应 40 分段 → 每格 25 单位（2 米）。  
雾参数（FogNear / FogFar）当前硬编码在 GlesGridRenderer.cs 里，后续如果做地形 / 自定义地面可以暴露。

## Shader 版本

```glsl
#version 310 es
precision mediump float;
```

GLES 3.1 兼容。桌面 OpenGL 上 Silk.NET.OpenGL 自动加载，不需要改版本号。

## 不是"真正的地面"

这是**调试 / 占位网格**：
- 只有线框，没有法线 / 材质 / 物理碰撞
- 用于相机控制和视锥裁剪的快速验证
- 真正的地面渲染（可能包含地形 / 物理刚体 / Toon Shader）后续实现

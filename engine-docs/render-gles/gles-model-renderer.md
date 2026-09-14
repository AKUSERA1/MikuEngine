# GlesModelRenderer

PMX 模型渲染管线。职责：VAO/VBO/EBO + 蒙皮矩阵 SSBO + 纹理绑定 + 按材质段绘制 + 轮廓线 pass
+ 自阴影采样 + 模型面板变换（移動/回転/拡大率）与渲染层根矩阵。

## 源文件

[GlesModelRenderer.cs](../../src/MikuEngine.Render.GLES/GlesModelRenderer.cs)  
[model.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/model.vert.glsl)  
[model.frag.glsl](../../src/MikuEngine.Render.GLES/Shaders/model.frag.glsl)  
[edge.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/edge.vert.glsl)  
[edge.frag.glsl](../../src/MikuEngine.Render.GLES/Shaders/edge.frag.glsl)

## 基本用法

### 从磁盘加载（推荐）

```csharp
var modelRenderer = GlesModelRenderer.LoadFromFile(device, "path/to/model.pmx");
// 内部自动：
//   1. PmxParser.Parse → SkeletalModelConverter.Convert
//   2. 为每个 DrawSegment 加载 diffuse/sphere/toon 纹理（GlesTextureLibrary）
//   3. 创建 VAO/VBO/EBO + SSBO + UBO
```

### 每帧渲染顺序（完整）

```csharp
// 帧 1：光照深度图（自阴影 Z pass）
shadowRenderer.UpdateLight(modelRenderer.Model, lightDirection);
frame.LightViewProj = shadowRenderer.LightViewProj;

// 帧首统一准备：面板变换 + IK/FK + 物理 + 上传 UBO/SSBO/morph
// 必须先于 RenderShadowMaps（它复用同一份 UBO/SSBO）
// 内部顺序：ApplyModelTransform（面板 TR 注入 全ての親）
//        → 渲染层根矩阵（拡大率，蒙皮后施加）
//        → MmdIkSolver.Solve → UpdateWorldMatrices（軸制限 → 付与 → IK）
//        → 物理（注入了 Physics 时：MMDPhysics.Update → ApplyPhysicsAppend）
//        → 蒙皮/UBO/SSBO 上传 + 顶点/UV morph 脏区上传
modelRenderer.PrepareFrame(in frame);

// Z pass：把模型画到光照深度图（depth-only）
shadowRenderer.RenderShadowMaps(device, modelRenderer, width, height);

// 帧 2：主渲染
camera.Aspect = width / (float)height;
camera.ComputeViewProj(viewProj);
frame.ViewProj = Matrix4x4.Transpose(/* viewProj 列主序 → 行主序供 Matrix4x4 */);
// （实际代码里 OrbitCamera 的 Proj/View 分别写入 FrameUniforms.ViewProj/View）

modelRenderer.ShadowZTexture = shadowRenderer.ZTexture;   // 光照深度图
modelRenderer.Draw(in frame, toonMode: 1f);

// 帧 3：床影（可选，MMD 影模式 2）
if (shadowRenderer.Mode == ShadowMode.SelfShadowAndFloor)
    shadowRenderer.DrawFloor(device, frame.LightColor);
```

> 顺序不可打乱的两处：**面板变换必须早于 IK/FK**（IK 内部跑全量 FK、物理读世界矩阵，
> 都要看见含 TR 的姿态）；**拡大率只在渲染层**，与物理/IK 完全解耦。
> 细节见 [model-transform.md](../core/model-transform.md)。

### 无自阴影时

```csharp
// 跳过 UpdateLight + RenderShadowMaps
modelRenderer.ShadowZTexture = 0;    // 或不设置，默认 0
modelRenderer.PrepareFrame(in frame);
modelRenderer.Draw(in frame);
// Draw 里 ShadowZTexture=0 时显式解绑纹理，安全降级
```

---

## FrameUniforms（每帧 UBO）

主渲染 + Z pass + 床影共用的 per-frame UBO（std140）：

```csharp
[StructLayout(LayoutKind.Sequential)]
struct FrameUniforms
{
    Matrix4x4 ViewProj;          // 行主序（System.Numerics）
    Matrix4x4 View;
    Vector4   CameraPosition;    // xyz = 相机世界坐标
    Vector4   LightDirection;    // xyz = 光线传播方向（着色器取反当 ln）
    Vector4   LightColor;
    Vector4   AmbientColor;
    Vector4   BaseAmbient;       // (0.07, 0.07, 0.07, 1) — PmxEditor 默认
    Matrix4x4 LightViewProj;     // 光照 ViewProj（自阴影用）
}
```

默认值对齐 PmxEditor（反编译 PmxEditorCore L12531-12535）：

| 字段 | PE 默认值 |
|---|---|
| `LightDirection` | `(-0.5, -1, 0.5)` 归一化 |
| `LightColor` | `(0.5, 0.5, 0.5, 1)` |
| `AmbientColor` | `(1, 1, 1, 1)` 白 |
| `BaseAmbient` | `(0.07, 0.07, 0.07, 1)` |

---

## 自阴影参数

主渲染消费光照深度图（`ShadowZTexture`），通过 `ShadowStyle` 选择不同阴影风格：

| 风格 | 说明 |
|---|---|
| `Standard` | 16-tap PCF + PE 二选一注入 |
| `Threshold` | 连续场高斯模糊 + smoothstep 阈值提取（硬边本影） |
| `Soft` | 9-tap PCF（普通软影观感） |

可调参数：

| 属性 | 默认 | 说明 |
|---|---|---|
| `ShadowZTexture` | 0 | 光照深度图，来自 `GlesShadowRenderer.ZTexture` |
| `ShadowTexel` | 1/1024 | 1/影图边长，PCF 核缩放 |
| `SelfShadowStrength` | 1.0 | 全局影强度（隔离测试用） |
| `ShadowStyle` | `Standard` | 阴影风格 |
| `ShadowColor` | `(0.5, 0.5, 0.5, 1)` | 硬边本影乘性暗度（影区 = 原色 × 此色） |
| `ShadowBias` | 0.0005 | 深度比较常数偏置底 |
| `ShadowBiasMax` | 0.003 | 偏置上限 |
| `ShadowSlopeBias` | 2.0 | 斜率缩放（掠射面 acne 抑制） |
| `ShadowSoftness` | 1.0 | PCF / 核宽（>1 更软） |
| `ShadowEdge` | 0.35 | 硬边本影阈值带宽 |
| `ShadowNormalOffset` | 0.0 | 接收法线沿世界法线推离量 |
| `SelfShadowMode` | 0 | 0=关 / 1=自阴影 / 2=自阴影+床影 |

> 逐材质收影：材质 PMX flag bit3（`EnabledReceiveShadow`）未置位时，该材质强制关闭自阴影，对齐 MMD 行为。

---

## 模型变换与拡大率

```csharp
// 拡大率（MMD モデル操作）。默认 (1,1,1)；只进渲染层，与物理/IK 解耦
modelRenderer.ModelScale = new Vector3(1.2f, 0.3f, 1.0f);

// 本帧算好的根矩阵（PrepareFrame 里算；影图 pass 复用同一份）
Matrix4x4 root = modelRenderer.ModelRootMatrix;
```

| 成员 | 说明 |
|---|---|
| `ModelScale` | 面板「拡大率」（X/Y/Z 独立缩放）。**只在蒙皮之后施加**，骨骼/刚体/IK/付与全部保持 bind 尺度；逐轴下限 `0.01`（`ModelRootTransform.ClampScale`），无上限 |
| `ModelRootMatrix` | 本帧根矩阵（`ModelRootTransform.ComputeRootMatrix` 的结果），供影图 pass 复用 |

每帧在 `PrepareFrame` 里做三件事：

1. `model.ApplyModelTransform()` —— 重建 全ての親 世界矩阵的后乘因子（面板 移動/回転；
   注入本身发生在 `RecomputeBone`，随后 IK/FK/物理全部自动跟随）；
2. `_rootMatrix = ModelRootTransform.ComputeRootMatrix(hasCarrier, move, rot, ModelScale)`；
3. `_rootNormal = ModelRootTransform.ComputeNormalMatrix(_rootMatrix)`（`Root⁻ᵀ` 的 3×3）。

上传的 uniform：

| uniform | 类型 | 消费方 |
|---|---|---|
| `uModelRoot` | `mat4` | `model.vert`（`sp = uModelRoot * sp`）、`edge.vert`、`shadow.vert`（Z pass） |
| `uModelNormalRoot` | `mat3` | `model.vert`（`nWorld = normalize(uModelNormalRoot * sn)`） |

矩阵按**行主序原样**上传（`glUniformMatrix4fv(transpose = false)`，
即 `GlesMatrixUpload.Mat4` / `Mat3`），与 row-vector 约定互相抵消 —— 调用方不需要手动转置。
原理见 [coordinate-system.md §3](../core/coordinate-system.md)。

> 非均匀缩放（压成纸片）在法线方向上是错的，除非用 `uModelNormalRoot` 修正；
> 该矩阵由引擎侧算好，调用方只管设 `ModelScale`。
> 完整语义、三个 pass 的消费点与已知边界（床影 quad 不缩放、光照视锥按 bind AABB）
> 见 [model-transform.md](../core/model-transform.md)。

---

## 渲染状态

`ApplyRenderState` 对齐 PmxEditor（2026-09-10 反编译结论）：
- blend 恒开 `SRC_ALPHA / INV_SRC_ALPHA`（`a=1` 时是恒等混合，对不透明零副作用）
- 深度写恒开、无 alpha test
- 默认剔除背面（PMX 是 D3D9 LH 约定，正面为顺时针）
- 材质标了両面（IsDoubleSided）才临时关闭剔除，画完恢复

---

## 轮廓线 Pass

PE 的 `tec_edge` technique，独立于主渲染：
1. 主渲染全部画完之后再画（edge 外扩壳与本体共享深度，后画才能被正确遮挡）
2. 只画 `flag & EnabledToonEdge != 0` 且 `EdgeSize > 0` 的材质
3. inverted hull（沿法线外推 EdgeSize × 深度 + 剔除正面）——剔掉正面后只剩壳体背面，恰好只在轮廓边缘露一条边
4. 不沿用材质双面 flag——両面说的是本体两面都画，壳体也剔掉正面才能不盖住本体
5. 外扩壳（位置 + 偏移）整体过 `uModelRoot`，视线距离也按**变换后**位置取
   ⇒ 轮廓线厚度随模型缩放、压扁方向随之压扁（有意为之）

---

## 资源清理

```csharp
modelRenderer.Dispose();
// 自动释放：VAO / VBO / EBO / FrameUbo / program / edgeProgram / SSBO / 纹理库
```

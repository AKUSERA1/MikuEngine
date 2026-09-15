# GlesModelRenderer

PMX 模型渲染管线。职责：VAO / VBO / EBO + 蒙皮矩阵 SSBO + 顶点 / UV morph SSBO + 纹理绑定 +
按材质段绘制 + 轮廓线 pass + 自阴影采样 + 模型变换（移动 / 旋转 / 缩放倍率）与渲染层根矩阵。

## 源文件

[GlesModelRenderer.cs](../../src/MikuEngine.Render.GLES/GlesModelRenderer.cs)  
[model.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/model.vert.glsl)  
[model.frag.glsl](../../src/MikuEngine.Render.GLES/Shaders/model.frag.glsl)  
[edge.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/edge.vert.glsl)  
[edge.frag.glsl](../../src/MikuEngine.Render.GLES/Shaders/edge.frag.glsl)

> 自阴影 Z pass 复用本渲染器的 VAO / UBO / SSBO，但着色器属于阴影系统
> （[shadow.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/shadow.vert.glsl) +
> [shadow_z.frag.glsl](../../src/MikuEngine.Render.GLES/Shaders/shadow_z.frag.glsl)），
> 见 [gles-shadow-renderer.md](gles-shadow-renderer.md)。

## 基本用法

### 从磁盘加载（推荐）

```csharp
var modelRenderer = GlesModelRenderer.LoadFromFile(device, "path/to/model.pmx");
// 内部自动：
//   1. PmxParser.Parse → SkeletalModelConverter.Convert
//   2. 为每个 DrawSegment 加载 diffuse / sphere / toon 纹理（GlesTextureLibrary）
//   3. 创建 VAO/VBO/EBO + SSBO + UBO
```

### 每帧渲染顺序（完整）

```csharp
// 帧 1：光照深度图（自阴影 Z pass）
shadowRenderer.UpdateLight(modelRenderer.Model, lightDirection);
frame.LightViewProj = shadowRenderer.LightViewProj;

// 帧首统一准备：模型变换 + IK / FK + 物理 + 上传 UBO/SSBO/morph
// 必须先于 RenderShadowMaps（它复用同一份 UBO/SSBO）
// 内部顺序：ApplyModelTransform（模型变换注入 全ての親）
//        → 渲染层根矩阵（缩放倍率，蒙皮后施加）
//        → MmdIkSolver.Solve → UpdateWorldMatrices（轴限制 → 赋予 → IK）
//        → 物理（注入了 Physics 时：MMDPhysics.Update → ApplyPhysicsAppend）
//        → 蒙皮 / UBO / SSBO 上传 + 顶点 / UV morph 脏区上传
modelRenderer.PrepareFrame(in frame);

// Z pass：把模型画到光照深度图（depth-only）
shadowRenderer.RenderShadowMaps(device, modelRenderer, width, height);

// 帧 2：主渲染
camera.Aspect = width / (float)height;
Span<float> viewProjSpan = stackalloc float[16];
Span<float> viewSpan = stackalloc float[16];
camera.ComputeViewProj(viewProjSpan);
camera.WriteViewMatrix(viewSpan);
frame.ViewProj = FromColumnMajor(viewProjSpan);   // 见下方 FrameUniforms 说明
frame.View     = FromColumnMajor(viewSpan);
frame.CameraPosition = new Vector4(camera.Position, 0f);

modelRenderer.ShadowZTexture = shadowRenderer.ZTexture;   // 光照深度图
modelRenderer.Draw(in frame, toonMode: 1f);

// 帧 3：床影（可选，MMD 影模式 2）
if (shadowRenderer.Mode == ShadowMode.SelfShadowAndFloor)
    shadowRenderer.DrawFloor(device, frame.LightColor);
```

> 顺序不可打乱的两处：**模型变换必须早于 IK / FK**（IK 内部跑全量 FK、物理读世界矩阵，
> 都要看见含 TR 的姿态）；**缩放倍率只在渲染层**，与物理 / IK 完全解耦。
> 细节见 [model-transform.md](../core/model-transform.md)。

### 无自阴影时

```csharp
// 跳过 UpdateLight + RenderShadowMaps
modelRenderer.ShadowZTexture = 0;    // 或不设置，默认 0
modelRenderer.PrepareFrame(in frame);
modelRenderer.Draw(in frame);
// Draw 里 ShadowZTexture = 0 时显式解绑纹理，安全降级
```

---

## FrameUniforms（每帧 UBO）

主渲染 + Z pass + 床影共用的 per-frame UBO（std140）：

```csharp
[StructLayout(LayoutKind.Sequential)]
struct FrameUniforms
{
    Matrix4x4 ViewProj;          // 16 float 载体，填入顺序 = 列主序（见下）
    Matrix4x4 View;
    Vector4   CameraPosition;    // xyz = 相机世界坐标
    Vector4   LightDirection;    // xyz = 光线传播方向（着色器取反当 ln）
    Vector4   LightColor;
    Vector4   AmbientColor;
    Vector4   BaseAmbient;       // (0.07, 0.07, 0.07, 1)
    Matrix4x4 LightViewProj;     // 光照 ViewProj（自阴影用）
}
```

> **关于 `Matrix4x4` 字段**：UBO 是按内存**原样上传**的，GLSL 按列主序读取这 16 个 float。
> 因此 `FrameUniforms` 里的 `Matrix4x4` 只是 16 个 float 的载体，
> 填入时的数值顺序必须与 `OrbitCamera` 输出的列主序数组一致
> （宿主用 `FromColumnMajor(span)` 线性填入：`M11 = c[0], M12 = c[1], ...`）。
> **不要在这里做 `Transpose`**——转置约定已经由「列主序数组 + GLSL 列主序读取」两者抵消。

默认值：

| 字段 | 默认值 |
|---|---|
| `ViewProj` / `View` | `Matrix4x4.Identity` |
| `CameraPosition` | `(0, 0, 1, 0)` |
| `LightDirection` | `(-0.5, -1, 0.5)` 归一化 |
| `LightColor` | `(0.5, 0.5, 0.5, 1)` |
| `AmbientColor` | `(1, 1, 1, 1)` 白 |
| `BaseAmbient` | `(0.07, 0.07, 0.07, 1)` |

> 这三个光照默认值彼此配套：换成 `LightColor = (1,1,1)` 或把 `LightDirection.Z` 取反
> 都会让被照亮朝向与 MMD 观感不同（整体偏暗 / 受光面相反）。

`GlesModelRenderer.UploadFrame(in frame)` 负责上传；自阴影的影图 pass 复用同一份 UBO，
**因此必须先于 `RenderShadowMaps` 调用**，否则影图会用上一帧的矩阵。

---

## 自阴影参数

主渲染消费光照深度图（`ShadowZTexture`），通过 `ShadowStyle`（类型 `SelfShadowStyle`）
选择采样与注入方式：

| 风格 | 说明 |
|---|---|
| `Standard` | 标准本影：16-tap PCF + 二选一注入（`toonMode == 0` 压亮度 / 否则 lerp 到 toon 角点） |
| `Threshold` | 硬边本影：连续场高斯模糊 + smoothstep 阈值提取 + 影色乘性暗度（硬核心 + 平滑边界） |
| `Soft` | 普通阴影：PCF 软影 + 直接遮蔽乘子 |

可调参数：

| 属性 | 默认 | 说明 |
|---|---|---|
| `ShadowZTexture` | 0 | 光照深度图，来自 `GlesShadowRenderer.ZTexture` |
| `ShadowTexel` | 1/1024 | 1/影图边长，PCF 核缩放 |
| `SelfShadowStrength` | 1.0 | 全局影强度（隔离测试用） |
| `ShadowStyle` | `Standard` | 阴影风格（`SelfShadowStyle`：`Standard` / `Threshold` / `Soft`） |
| `ShadowColor` | `(0.5, 0.5, 0.5, 1)` | 硬边本影乘性暗度（影区 = 原色 × 此色） |
| `ShadowBias` | 0.0005 | 深度比较常数偏置底 |
| `ShadowBiasMax` | 0.003 | 偏置上限 |
| `ShadowSlopeBias` | 2.0 | 斜率缩放（掠射面 acne 抑制） |
| `ShadowSoftness` | 1.0 | PCF / 核宽（>1 更软） |
| `ShadowEdge` | 0.35 | 硬边本影阈值带宽 |
| `ShadowNormalOffset` | 0.0 | 接收法线沿世界法线推离量 |
| `SelfShadowMode` | 0 | 0 = 关 / 1 = 自阴影 / 2 = 自阴影 + 床影 |

> 逐材质收影：材质 PMX flag bit3（`EnabledReceiveShadow`）未置位时，
> 该材质强制关闭自阴影，与 MMD 行为一致。

---

## 模型变换与缩放倍率

```csharp
// 缩放倍率。默认 (1,1,1)；只进渲染层，与物理 / IK 解耦
modelRenderer.ModelScale = new Vector3(1.2f, 0.3f, 1.0f);

// 本帧算好的根矩阵（PrepareFrame 里算；影图 pass 复用同一份）
Matrix4x4 root = modelRenderer.ModelRootMatrix;
```

| 成员 | 说明 |
|---|---|
| `ModelScale` | 「缩放倍率」（X/Y/Z 独立缩放）。**只在蒙皮之后施加**，骨骼 / 刚体 / IK / 赋予全部保持 bind 尺度；逐轴下限 `0.01`（`ModelRootTransform.ClampScale`），无上限 |
| `ModelRootMatrix` | 本帧根矩阵（`ModelRootTransform.ComputeRootMatrix` 的结果），供影图 pass 复用 |

每帧在 `PrepareFrame` 里做三件事：

1. `model.ApplyModelTransform()` —— 重建 全ての親 世界矩阵的后乘因子（移动 / 旋转；
   注入本身发生在 `RecomputeBone`，随后 IK / FK / 物理全部自动跟随）；
2. `_rootMatrix = ModelRootTransform.ComputeRootMatrix(hasCarrier, move, rot, ModelScale)`；
3. `_rootNormal = ModelRootTransform.ComputeNormalMatrix(_rootMatrix)`（`Root⁻ᵀ` 的 3×3）。

上传的 uniform：

| uniform | 类型 | 消费方 |
|---|---|---|
| `uModelRoot` | `mat4` | `model.vert`（`sp = uModelRoot * sp`）、`edge.vert`、`shadow.vert`（Z pass） |
| `uModelNormalRoot` | `mat3` | `model.vert`（`nWorld = normalize(uModelNormalRoot * sn)`） |

矩阵按**行主序原样**上传（`glUniformMatrix4fv(transpose = false)`，
即 `GlesMatrixUpload.Mat4` / `Mat3`），与 row-vector 约定互相抵消——调用方不需要手动转置。
原理见 [coordinate-system.md §3](../core/coordinate-system.md)。

> 非均匀缩放（压成纸片）在法线方向上是错的，除非用 `uModelNormalRoot` 修正；
> 该矩阵由引擎侧算好，调用方只管设 `ModelScale`。
> 完整语义、三个 pass 的消费点与已知边界（床影 quad 不缩放、光照视锥按 bind AABB）
> 见 [model-transform.md](../core/model-transform.md)。

---

## 渲染状态

`ApplyRenderState` 的规则：

- blend 恒开 `SRC_ALPHA / INV_SRC_ALPHA`（`a = 1` 时是恒等混合，对不透明零副作用）
- 深度写恒开、无 alpha test
- 默认剔除背面（PMX 是 D3D9 左手系约定，正面为顺时针）
- 材质标了双面（`IsDoubleSided`）才临时关闭剔除，画完恢复

> **单一队列绘制**：材质段按 **PMX 材质顺序**直接绘制，**不按 Opaque / Cutout / Blended 分队列**，
> 也不按距离排序。半透明材质同样写深度。这样做是为了保持材质顺序与连续 alpha 渐变，
> 与 MMD 的表现一致；自行加排序会改变绘制顺序、破坏渐变。

---

## 轮廓线 Pass

独立于主渲染：

1. 主渲染全部画完之后再画（edge 外扩壳与本体共享深度，后画才能被正确遮挡）
2. 只画 `flag & EnabledToonEdge != 0` 且 `EdgeSize > 0` 的材质
3. inverted hull（沿法线外推 EdgeSize × 深度 + 剔除正面）——剔掉正面后只剩壳体背面，
   恰好只在轮廓边缘露一条边
4. 不沿用材质双面 flag——双面说的是本体两面都画，壳体也剔掉正面才能不盖住本体
5. 外扩壳（位置 + 偏移）整体过 `uModelRoot`，视线距离也按**变换后**位置取
   ⇒ 轮廓线厚度随模型缩放、压扁方向随之压扁（有意为之）

开关：`EdgeVisible`。

---

## 表情（morph）落地

`PrepareFrame` 内按当前 `MorphWeights` 重算并上传顶点 / UV 偏移（SSBO）：

| 缓冲 | 绑定 | 内容 | 元素尺寸 |
|---|---|---|---|
| 顶点 morph | binding 2 | `vec4 uMorphOffsets[]`（只用 xyz） | 16 B/顶点 |
| UV morph | binding 3 | `vec2 uMorphUvs[]` | 8 B/顶点 |

- **稀疏脏区**：只清零上一帧动过的顶点、只遍历权重非 0 的 morph 的受影响顶点、
  只上传「上一帧 ∪ 本帧」的区间，不做 O(顶点数) 全量运算。
- 索引一律用 `gl_VertexID`（`glDrawElements` 下等于**顶点索引**），不需要额外索引缓冲。
- 模型没有对应 morph 时缓冲容量为 0；是否读它由 shader 的 `uMorphEnabled` 决定。

**材质 morph 逐段应用**：绘制每个材质段时调
`MmdMorphEvaluator.ResolveMaterial(model, seg.MaterialIndex)` 取叠加了全部活跃材质 morph 的
「有效材质」（Multiply / Add），再上传该段的材质参数（主渲染与轮廓线 pass 都这么做）。

诊断属性：

| 成员 | 说明 |
|---|---|
| `MorphEnabled` / `MorphUvEnabled` | 该模型是否含顶点 / UV morph |
| `MorphSsbo` | 顶点 morph 缓冲句柄 |
| `MorphTouchedVertexCount` | 最近一帧碰过的顶点数（0 = 本帧无 morph 活跃） |

---

## 多模型

每个模型一个 `GlesModelRenderer`，各自持有 VAO / VBO / SSBO 与纹理库，互不干扰：

- **Core 层自包含**：`SkeletalModel` 持有自己的世界 / 蒙皮 / 逆绑定矩阵与姿势；
  动效经 `MmdAnimation.Bind(model)` 产出绑定副本，同一 VMD 可绑进多个模型；
  动画求值入口（`Sample` / `Mixer.Evaluate` / `Timeline.Apply` / `MorphEvaluator.Evaluate`）
  全部以 `model` 入参驱动，**没有全局或静态动画状态**。每个模型需要各自的
  `MmdTimeline` / `MmdAnimationMixer`。
- **渲染**：逐模型 `PrepareFrame(in frame)` → `Draw(in frame)`，共享同一个相机与
  `FrameUniforms`。蒙皮矩阵 SSBO 每个渲染器一份（见
  [gles-support.md](gles-support.md)），多模型之间不共享缓冲。
- **纹理**：纹理库同样是每渲染器一份，跨模型不去重 ⇒ 同一 PMX 加载两次会重复上传贴图。
- **自阴影**：`GlesShadowRenderer.RenderShadowMaps` 需要指定一个 caster 渲染器。
  当前做法是**只由主模型投射**，其余模型设置 `ShadowZTexture` / `ShadowTexel` /
  `ShadowNormalOffset` 后照常**接收**阴影。要让每个模型都投射，需要各自跑一次 Z pass
  （共享光照视锥会变松，影子变淡——多 caster 的取舍点）。
- **跨模型挂载**：子模型挂到亲模型骨骼上用的是外部親绑定（Core 层能力，与渲染无关），
  见 [external-parent.md](../core/animation/external-parent.md)。

---

## 其他公开成员

| 成员 | 说明 |
|---|---|
| `Model` | 本渲染器持有的 `SkeletalModel` |
| `ModelVisible` | 本帧的可见性快照（帧首取自 `Model.Visible`，三个 pass 都用它） |
| `Textures` | 本渲染器的 `GlesTextureLibrary` |
| `Vao` / `FrameUbo` / `SkinSsbo` / `MorphSsbo` | GL 资源句柄（自定义 pass 时可复用） |
| `SkinMatrixBaseOffset` | 本模型蒙皮矩阵在自有 SSBO 中的起始偏移（只读，由 `PrepareFrame` 写入） |
| `Physics` / `PhysicsEnabled` / `GroundCollisionEnabled` / `PostPhysicsAppendEnabled` / `PhysicsFrame` | 物理接线（见 [physics/mmd-physics.md](../physics/mmd-physics.md)） |

---

## 资源清理

```csharp
modelRenderer.Dispose();
// 自动释放：VAO / VBO / EBO / FrameUbo / program / edgeProgram / SSBO / 纹理库
```

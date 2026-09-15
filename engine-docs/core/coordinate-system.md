# 坐标系约定

**本文档必读。** 所有向量、矩阵、投影公式都依赖这些约定。
如果你的调用方使用不同约定，需要在边界层做转换。

## 1. 坐标系：左手 + Y-up

```
     +Y (up)
     |
     |
     O-----------> +X (right)
    /
   /
  +Z (forward / toward viewer in view space)
```

| 维度 | 值 | 备注 |
|---|---|---|
| 手系 | **左手** | 与 MMD 原生约定一致 |
| 向上 | **+Y** | |
| 前向（世界） | **+Z** | 相机看向 -Z 方向 |
| 右向 | +X | |
| 单位 | **1 MMD 单位 ≈ 7.9 cm** | 158 cm 的人体 ≈ 20 单位 |

### 与右手系 OpenGL 的差异

OpenGL 默认使用右手系（+Z backward / away from viewer）。本引擎在 View 矩阵构建时已经处理好
这一点——**调用方无需关心**，直接使用即可；但自己拼矩阵时必须留意（见文末「违反约定会怎样」）。

## 2. 向量

使用 `System.Numerics.Vector3` / `Vector4`。
注意 `Vector3.Cross(a, b)` 在左手系与右手系下方向相反，本引擎内部封装了左手 lookAt，
已考虑这一点。

## 3. 矩阵：两套表示并存（重要）

引擎里同时存在两种矩阵表示，**它们不是「对错」关系**——两者都直接喂 GLSL，靠约定抵消。
理解这一点是读懂渲染层的前提。

### 3.1 列主序 `float[16]`（相机 / 逐帧数据）

```csharp
Span<float> viewProj = stackalloc float[16];
camera.ComputeViewProj(viewProj);   // 列主序，直接写进 FrameUniforms（UBO）
```

GLSL 的 `mat4` 按列读取。矩阵数据在 `float[16]` 中的排列：

```
列主序：每 4 个 float 是一列
┌ col0 ┐ ┌ col1 ┐ ┌ col2 ┐ ┌ col3 ┐
│ m[0] │ │ m[4] │ │ m[8] │ │ m[12]│   ← 行 0
│ m[1] │ │ m[5] │ │ m[9] │ │ m[13]│   ← 行 1
│ m[2] │ │ m[6] │ │ m[10]│ │ m[14]│   ← 行 2
│ m[3] │ │ m[7] │ │ m[11]│ │ m[15]│   ← 行 3
└──────┘ └──────┘ └──────┘ └──────┘
```

`OrbitCamera.ComputeViewProj(Span<float>)` 直接产出这种布局，整条链路**零转置**：

```
OrbitCamera → float[16] (列主序)
   ↓
FrameUniforms.ViewProj (Matrix4x4 作为 16 float 载体，按列主序顺序线性填入)
   ↓
UBO (glBufferData / std140)
   ↓
GLSL mat4
```

> `FrameUniforms` 的矩阵字段声明成 `Matrix4x4`，但**存的是列主序顺序**：
> UBO 按内存原样上传，GLSL 按列主序读取。宿主用 `FromColumnMajor(span)` 线性填入
> （`M11 = c[0], M12 = c[1], …`），**不要补一次 `Transpose`**。
> 另一个方向：网格地面的 `GridUniform` 用显式 `float[16]` 字段存列主序，同样零转置
> （见 [grid-renderer.md](../render-gles/grid-renderer.md)）。

### 3.2 行主序 `Matrix4x4` 原样上传（蒙皮矩阵 / 根矩阵）

`System.Numerics.Matrix4x4` 是**行主序 row-vector**（`v · M`，平移在 `M41..M43`），
与上一节的列主序存的是**同一个变换的转置表示**。因此它也能直接喂 GLSL：

```csharp
// 注意 transpose = false（不是 true）
gl.UniformMatrix4(location, 1, false, p);   // p = M11..M44 顺序的 16 个 float
```

GLSL 按列主序解释这 16 个 float ⇒ shader 拿到的是引擎矩阵的**转置**；
而 GLSL 的 `M * v`（列向量）与引擎的 `v * M`（行向量）是同一线性映射——
两条约定在此**互相抵消**，调用方不需要手动转置。

走这条路径的有：

| 数据 | 载体 |
|---|---|
| 蒙皮矩阵数组 | SSBO（`mat4 uSkinMatrices[]`，见 [gles-support.md](../render-gles/gles-support.md)） |
| 模型根矩阵 / 法线修正矩阵 | uniform `uModelRoot` / `uModelNormalRoot`（见 [model-transform.md](model-transform.md)） |

> 结论：**在 UBO 里用列主序 float，在 SSBO / 根矩阵 uniform 里用 `Matrix4x4` 原样上传**，
> 两者最终在 GLSL 侧都得到正确的变换。真正的禁忌是「对已经是转置表示的存储再重排一次」
> ——那会把矩阵转两次：平移丢失进 `w`、旋转反向，蒙皮炸成薄片。

### 3.3 列主序矩阵乘法

列主序矩阵乘法公式与行主序**完全相同**：
$$C_{ij} = \sum_{k} A_{ik} \cdot B_{kj}$$

**但索引方式不同**——矩阵的 $(i, j)$ 元素存储在 `A[j*4 + i]`（列 j，行 i）。

`OrbitCamera.ComputeViewProj` 内部使用的 `MultiplyColumnMajor` 已经封装好这个公式：

```csharp
// ComputeViewProj: outMatrix = proj × view
MultiplyColumnMajor(proj, view, outMatrix);
```

语义：先应用 `view`（世界 → 相机），再应用 `proj`（相机 → 裁剪）。等价于 GLSL：

```glsl
gl_Position = proj * view * model * vertex;
```

## 4. 投影：GLES 3.1 默认风格

### 两种投影的 z_clip 范围

| 类型 | z ∈ [near, far] → z_clip | z_ndc | 使用者 |
|---|---|---|---|
| **GLES 默认**（本引擎） | **[-w, +w]** | [-1, 1] | OpenGL / OpenGL ES / DirectX |
| WebGPU / Vulkan | [0, w] | [0, 1] | WebGPU / Vulkan |

### 本引擎投影公式

左手 GLES 默认风格：

```
m[0]  = f/aspect
m[5]  = f                          // f = 1/tan(fov/2)
m[10] = 2/(far - near)
m[11] = 1
m[14] = -(far + near)/(far - near)
```

与 WebGPU 风格的两个不同系数：

- `m[10]`：GLES 是 `2/(f-n)`，WebGPU 是 `f/(f-n)`
- `m[14]`：GLES 是 `-(f+n)/(f-n)`，WebGPU 是 `-nf/(f-n)`

**调用方不需要手写这些**——`OrbitCamera.WriteProjectionMatrix` / `ComputeViewProj`
自动用正确的 GLES 公式。

### 深度缓冲

GLES 默认 UNORM，z ∈ [-1, 1] 映射到 [0, 1]。
本引擎**不启用 reversed-Z**（z_clip = 1.0 对应近裁剪面），用标准方向。

## 5. Near / Far 裁剪面策略

不固定 Near / Far，而是根据 `Radius`（相机到 target 的距离）动态调整：

```
Near = clamp(Radius / 50, 0.3, 10)        → NearMin = 0.3, NearMax = 10
Far  = clamp(Radius × 12 + 600, 200, 8000) → FarMin = 200, FarCap = 8000
```

典型场景：`Radius = 100` → `Near = 2.0`，`Far = 1800`，近远比 ≈ 0.0011，
深度精度安全。因此**不要**手工设成固定 `near = 0.01` 这类小值。

> `Near` / `Far` 是**派生值**：相机在 `Radius` 变化与输出矩阵时会按上式重算并钳制，
> 因此手工写入的值会在下次重算时被覆盖——要改裁剪面就改 `Radius`（或 VMD 的 `Distance`）。
> 相机处于 VMD 驱动时，推算基准从 `Radius` 换成 `|Distance|`（见 [orbit-camera.md](orbit-camera.md)）。

## 6. 单位换算

MMD 单位与真实长度的关系影响一切涉及**物理、碰撞、骨骼位移、地面高度**的计算：

| MMD 单位 | 米 | 厘米 |
|---|---|---|
| 1 | 0.079 | 7.9 |
| 20（158cm 角色身高） | 1.58 | 158 |
| 500（地面平面半宽） | 39.5 | 3950 |

因此默认重力取 98（≈ 9.8 m/s² × 10），地面平面、内置地面盒、teleport 阈值等
都用这套单位。写自定义物理参数时保持同一量纲。

---

## 违反约定会怎样

| 错误 | 症状 |
|---|---|
| 对已经是转置表示的存储**再重排一次**（如把列主序数组按行读） | 模型**扭曲 / 镜像**，或旋转方向相反；物理侧表现为蒙皮炸成薄片 |
| 用右手 lookAt | 相机看向相反方向，法线翻转导致光照错误 |
| 用 WebGPU 投影公式 + GLES 深度缓冲 | 远处物体被错误裁剪，z-fighting 加剧 |
| 手工设过小的 near 平面 | 深度精度崩塌，远处 z-fighting |
| 直接调 `OrbitCamera.AngularSensitivity` | 无效果：Core 层已设为 1.0 做 no-op，灵敏度由 Engine 层接管 |
| 非均匀缩放下用普通矩阵变换法线 | 法线方向错误（光照发暗 / 发亮），必须用 `Root⁻ᵀ`，见 [model-transform.md](model-transform.md) |

# 坐标系约定

**本文档必读。** 所有向量、矩阵、投影公式都依赖这些约定。  
如果你的调用方（比如另一个引擎 / 3D 工具）使用不同约定，需要在边界层做转换。

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
| 手系 | **左手** | 与 Unity、MMD、DirectX 一致；与 OpenGL 传统右手相反 |
| 向上 | **+Y** | |
| 前向（世界） | **+Z** | 相机看向 -Z 方向 |
| 右向 | +X | |
| 单位 | **1 MMD ≈ 8 cm** | 角色身高约 25 单位 |

### 与右手系 OpenGL 的差异

OpenGL 默认使用右手系（+Z backward / away from viewer）。本引擎在 View 矩阵构建时已经处理好这一点——**调用方无需关心**，直接使用即可。

## 2. 向量

使用 `System.Numerics.Vector3` / `Vector4`。  
注意：`Vector3.Cross(a, b)` 的结果方向在左手系和右手系**相反**。本引擎内部封装了左手 lookAt，已考虑这一点。

## 3. 矩阵：列主序 `float[16]`

这是**最容易出错**的约定，务必理解。

### 什么是列主序

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

### 为什么不直接用 `System.Numerics.Matrix4x4`

`Matrix4x4` 是**行主序**（`M11, M12, M13, M14` 是第一行）。  
如果把 `Matrix4x4` 通过 `fixed ref` 传给 GLSL，矩阵会**隐式转置**——这是之前踩过的坑。

**本引擎的解法**：`OrbitCamera.ComputeViewProj(Span<float>)` 直接产出列主序 `float[16]`，整条链路零转置：

```
OrbitCamera → float[16] (列主序)
   ↓
GridUniform.ViewProj (16 个显式 float 字段，直接 SetViewProj)
   ↓
UBO (glBufferData)
   ↓
GLSL mat4 (glUniformMatrix4fv 不用 GL_FALSE 转置标志)
```

### 列主序矩阵乘法

列主序矩阵乘法公式与行主序**完全相同**：
$$C_{ij} = \sum_{k} A_{ik} \cdot B_{kj}$$

**但索引方式不同**——矩阵的 $(i, j)$ 元素存储在 `A[j*4 + i]`（列 j，行 i）。

本引擎的 `OrbitCamera.ComputeViewProj` 内部使用的 `MultiplyColumnMajor` 已经封装好这个公式：

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

对比 WebGPU 风格的两个不同系数：
- `m[10]`：GLES 是 `2/(f-n)`，WebGPU 是 `f/(f-n)`
- `m[14]`：GLES 是 `-(f+n)/(f-n)`，WebGPU 是 `-nf/(f-n)`

**调用方不需要手写这些**——`OrbitCamera.WriteProjectionMatrix` / `ComputeViewProj` 自动用正确的 GLES 公式。

### 深度缓冲

GLES 默认 UNORM，z ∈ [-1, 1] 映射到 [0, 1]。  
本引擎**不启用 reversed-Z**（z_clip=1.0 对应近裁剪面），用标准方向。

## 5. Near / Far 裁剪面策略

不固定 Near/Far，而是根据 `Radius`（相机到 target 的距离）动态调整：

```
Near = max(Radius / 50, 0.3)        → NearMin = 0.3 (下限)
Far  = min(Radius × 12 + 600, 8000) → FarCap  = 8000 (上限)
```

典型场景：`Radius = 100` → `Near = 2.0`，`Far = 1800`，比值 `near/far ≈ 0.0011` 安全。

## 6. MMD 单位 ≈ 8 cm

这个比例关系影响一切涉及**物理、碰撞、骨骼位移、地面高度**的计算：

| MMD 单位 | 米 | 厘米 |
|---|---|---|
| 1 | 0.08 | 8 |
| 25（角色身高） | 2.0 | 200 |
| 500（地面平面半宽） | 40.0 | 4000 |

---

## 违反约定会怎样

| 错误 | 症状 |
|---|---|
| 把 `Matrix4x4`（行主序）直接传给 GLSL | 模型看起来是**扭曲/镜像**的，或旋转方向相反 |
| 用右手 lookAt | 相机看向相反方向，法线翻转光照错误 |
| 用 WebGPU 投影公式 + GLES 深度缓冲 | 远处物体被错误裁剪，z-fighting 加剧 |
| 直接调 `OrbitCamera.AngularSensitivity` | Core 层已经设为 1.0 做 no-op，你改了也不影响 Engine 层输入 |

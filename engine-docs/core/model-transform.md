# 模型变换与渲染层缩放

MMD 的**模型变换**（移动 / 旋转，以及 MMD 本体不支持的缩放）在引擎里的落地方式。核心规则只有一句：

> **移动 / 旋转 进骨骼链（物理与 IK 跟随）；缩放倍率永远不进骨骼链（只在蒙皮之后施加）。**

这条分界是「缩放与物理解耦」的全部来源——模型可以被压成纸片或放大成巨人，
而刚体、关节、teleport 阈值、赋予、IK 全部照常在 bind 尺度上工作。

## 源文件

| 文件                                                                                                                                                                                                                                      | 职责                                    |
| --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------- |
| [SkeletalModel.cs](../../src/MikuEngine.Core/Models/SkeletalModel.cs)                                                                                                                                                                   | 模型变换状态 + 载体骨解析 + 世界矩阵后乘因子             |
| [ModelRootTransform.cs](../../src/MikuEngine.Core/Math/ModelRootTransform.cs)                                                                                                                                                           | 渲染层根矩阵 / 法线修正矩阵（纯数学，可单测）              |
| [GlesModelRenderer.cs](../../src/MikuEngine.Render.GLES/GlesModelRenderer.cs)                                                                                                                                                           | `ModelScale` 属性 + 每帧算根矩阵 + 上传 uniform |
| [model.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/model.vert.glsl) / [edge.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/edge.vert.glsl) / [shadow.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/shadow.vert.glsl) | 三个 pass 消费根矩阵                         |

***

## 1. 骨骼语义（先读这个）

标准 PMX 模型有**两根互不隶属的根骨**（`parentIndex = -1`）：

| 骨名       | 语义                                            | 本引擎的用法                                    |
| -------- | --------------------------------------------- | ----------------------------------------- |
| **全ての親** | 整个模型级别的移动 / 旋转载体。MMD 在录制动作时把 移动 / 旋转 烘焙成它的关键帧 | `RootTransformBoneIndex`——移动 / 旋转 注入这里    |
| **操作中心** | 视点 / 相机锚点。**不在变形链上**                          | `OperationCenterBoneIndex`——**不参与任何模型变换** |

- `SkeletalModelConverter.Convert` 末尾调用 `ResolveModelTransformBones()` 按名解析这两根骨
  （`FindBone("全ての親")` / `FindBone("操作中心")`，找不到为 `-1`）。
- 身体挂在 **全ての親** 之下 ⇒ 只要变换只施加在它身上，**操作中心天然「始终留在原地」**，
  不需要任何特殊处理。反过来，「操作中心也跟着动」几乎一定是把变换注错了骨。
- **不要用「`parentIndex < 0`」来辨认载体**：两根根骨里，操作中心没有子骨、全ての親 有子骨
  （典型为 3\~4 根）。按名匹配，或取「无父骨中有子骨的那根」。
  两根骨的绑定位置通常相同，用错了在默认姿势下不会立刻报错。

***

## 2. 移动 / 旋转

### API

```csharp
int rootIdx = model.RootTransformBoneIndex;      // 模型变换载体（全ての親），-1 = 模型没有
int centerIdx = model.OperationCenterBoneIndex;  // 视点锚点，不参与变换

model.ModelTranslationOffset = new Vector3(3f, -2f, 5f);   // 移动（模型空间，世界轴向）
model.ModelRotationAngles   = new Vector3(0f, MathF.PI / 2f, 0f);  // 旋转（弧度，MMD YXZ 欧拉序）

model.ApplyModelTransform();   // 每帧在 FK 之前调一次，重建载体骨的后乘因子
model.UpdateWorldMatrices();   // FK：因子在 RecomputeBone 里乘进 全ての親 的世界矩阵
```

| 成员                                                           | 说明                                                    |
| ------------------------------------------------------------ | ----------------------------------------------------- |
| `ModelTranslationOffset`                                     | 「移动」**绝对值**（模型空间，世界轴向）。0 = 无偏移                        |
| `ModelRotationAngles`                                        | 「旋转」**绝对值**，弧度，MMD 的 YXZ 欧拉序（`MmdMath.EulerOrderYxz`） |
| `RootTransformBoneIndex`                                     | 全ての親 的骨索引（`-1` = 该模型没有此骨，见 §5 兜底路径）                   |
| `OperationCenterBoneIndex`                                   | 操作中心 的骨索引（`-1` = 无）。仅供宿主取锚点用                          |
| `ResolveModelTransformBones()`                               | 按名解析上面两根骨；转换器已调用，手工构造 `SkeletalModel` 时需自己调           |
| `ApplyModelTransform()`                                      | 由当前模型变换状态重建注入因子。每帧调一次，幂等                              |
| `UpdateWorldMatrices()` / `UpdateWorldMatricesSubtree(bone)` | FK；注入发生在 `RecomputeBone` 内                            |

### 注入方式：后乘因子，不写 Local\*

实现**不写** `LocalRotations` / `LocalTranslations`。原因：那会和 VMD 轨道、`ResetPose()`
互相污染（同一根骨的两种「旋转」抢一个槽位），而且模型变换值是绝对值、要逐帧重算。

取而代之的是在 `RecomputeBone` 里对载体的世界矩阵**后乘**一个因子 `D`：

```
D = T(-pivot) · R · T(pivot + move)                 （行向量 row-vector 约定）
  ⇒ v' = ((v − pivot) · R) + pivot + move
```

- `pivot` = 全ての親 的**绑定姿势世界位置**（取 `InverseBind[carrier]` 的逆的平移分量，
  比读 `LocalPositions` 稳：父链非单位阵时依旧正确；标准模型 = 原点）。
- 世界矩阵每次都由 `local * parentWorld` 重新构建后再后乘 ⇒ **天然幂等、不累积**；
  连续多帧调 `ApplyModelTransform` + `UpdateWorldMatrices` 结果逐位相同。
- 平移在**世界轴**上叠加（不随 `R` 偏转）：先绕枢轴旋转，再世界轴平移。

因为注入点在**世界矩阵**层面，下游全部自动跟随，无需任何额外接线：

```
IK（内部跑全量 FK）      → 链骨基旋转读到的就是含模型变换的世界矩阵
物理 kinematic 目标      → 取骨世界矩阵 ⇒ 刚体跟着模型走
赋予                     → 走 DeformOrder 的常规求值
```

`ResetPose()` **不会**清掉模型变换状态（模型变换状态是独立字段），下一次 FK 依旧生效。

***

## 3. 缩放倍率：渲染层根矩阵（与物理解耦）

### API

```csharp
modelRenderer.ModelScale = new Vector3(1.2f, 0.3f, 1.0f);   // 默认 (1,1,1)
Matrix4x4 root = modelRenderer.ModelRootMatrix;             // 本帧算好的根矩阵（影图 pass 复用）
```

`ModelScale` 只进渲染层：`GlesModelRenderer.PrepareFrame` 里算出根矩阵与法线修正矩阵，
在**蒙皮之后**整体施加。

### ModelRootTransform

```csharp
public static class ModelRootTransform
{
    public const float MinScale = 0.01f;                    // 逐轴下限（0 会让逆矩阵不存在）
    public static Vector3 ClampScale(Vector3 scale);
    public static Matrix4x4 ComputeRootMatrix(bool hasCarrier, Vector3 move, Vector3 rotYxz, Vector3 scale);
    public static Matrix4x4 ComputeNormalMatrix(Matrix4x4 root);   // = Q⁻ᵀ（线性 3×3 部分）
    public static float[] ExtractLinear3x3(in Matrix4x4 m);
    public static float[] ToArray(in Matrix4x4 m);
}
```

乘序（行向量 `v · M`，先写的在左）：

| 路径                                | `Root`            | 含义                |
| --------------------------------- | ----------------- | ----------------- |
| 模型**有** 全ての親（`hasCarrier = true`） | `S`               | 模型变换已进骨骼链，根矩阵只剩缩放 |
| 模型**无** 全ての親（兜底路径）                | `R · T(move) · S` | 骨级 TR + 模型级缩放的分层  |

⇒ 视觉 = 蒙皮结果 →（TR）→ 缩放。缩放永远是最外层、绕模型原点，与 MMD 缩放倍率一致。

`ClampScale` 逐轴钳到 `≥ 0.01f`：负值等价翻转（MMD 不允许）一并钳掉，
0 会让法线修正的逆矩阵不存在。缩放在这里**没有上限**，但过大时会撞上 §5 的视锥边界。

### 法线修正（非均匀缩放必需）

非均匀缩放下法线不能用普通矩阵变换（方向会错），必须乘 `Q⁻ᵀ`（`Q` = 根矩阵线性部分）。
`ComputeNormalMatrix` 返回 `Transpose(Inverse(root))`，上传到 shader 的 `uModelNormalRoot`
（`mat3`）。**均匀与非均匀缩放都由它正确处理**——调用方只管设 `ModelScale`，
不需要为「只支持均匀缩放」做任何额外处理。

***

## 4. 三个 pass 的消费点

| Pass    | Shader        | 用法                                                                         |
| ------- | ------------- | -------------------------------------------------------------------------- |
| 主渲染     | `model.vert`  | `sp = uModelRoot * sp`；`nWorld = normalize(uModelNormalRoot * sn)`         |
| 轮廓线     | `edge.vert`   | 外扩壳（位置 + 偏移）整体过根矩阵；视线距离 `d` 按**变换后**位置取（远处轮廓自然变细）⇒ 轮廓线厚度随模型一起缩放、压在纸片上就一起压扁 |
| 自阴影 Z 图 | `shadow.vert` | `sp = uModelRoot * sp` ⇒ 影子跟随视觉模型（物理跑在 bind 尺度，此处只影响视觉）                    |

`uModelRoot` / `uModelNormalRoot` / 轮廓线的 `uModelRoot` 都由 `GlesModelRenderer` 统一上传；
`GlesShadowRenderer.RenderShadowMaps` 复用 `model.ModelRootMatrix`（同一份）。

### 矩阵上传约定（容易搞错，务必理解）

引擎侧同时存在两套表示，**两者是同一线性映射的转置表示**，都能直接喂 GLSL：

```
路径 A: 列主序 float[16]  → OrbitCamera.ComputeViewProj → FrameUniforms（UBO，std140）
路径 B: 行主序 Matrix4x4  → 蒙皮矩阵 SSBO / uModelRoot / uModelNormalRoot
```

路径 B 的合法性来自 `GlesMatrixUpload`：把 `Matrix4x4` 的 16 个 float **原样**喂给
`glUniformMatrix4fv(transpose = false)`。GLSL 按列主序解释 ⇒ shader 拿到的是引擎矩阵的**转置**，
而 GLSL 的 `M * v`（列向量）与引擎的 `v * M`（行向量）是同一映射 ⇒ **两条约定互相抵消**，
调用方不需要手动转置，**也不要**再手工加一次 `Transpose`。

> `ModelRootTransform.ToArray` / `ExtractLinear3x3` 采用同一约定（行主序原样展平）。

***

## 5. 物理解耦的边界

缩放**不进** `WorldMatrices`，因此以下全部运行在 bind 尺度、逐位不受 `ModelScale` 影响：

- 物理刚体 / 关节（`MMDPhysics` 的 kinematic 目标与写回）
- teleport 阈值（`max(4, 250·dt)`）
- 赋予链、IK 链、`SkinMatrices` 的 World 因子

已知边界与副作用：

| 项              | 现状                                                                                                              |
| -------------- | --------------------------------------------------------------------------------------------------------------- |
| **床影 quad**    | `GlesShadowRenderer` 的影 overlay 是固定 ±300 的模型空间四边形，**不乘根矩阵** ⇒ 放大 / 压扁模型时地面影不随之缩放                                |
| **光照紧视锥**      | `UpdateLight` 用**绑定姿势** AABB 算正交半宽 `_e`。模型放大到超出该视锥时，Z pass 的 caster 会被裁掉 ⇒ 影子缺失 / 变淡。缩放幅度大时需自行重算视锥或改接「缩放后 AABB」 |
| **法线偏移**       | `TexelWorld` 也来自未缩放的 `_e`，放大到超出视锥后偏移量不再匹配                                                                       |
| **内置物理地面**     | 是**模型空间** y = 0 的盒，只跟随骨骼（模型变换已进骨骼链，所以 移动 会带着它走同一坐标系），但**缩放倍率不改变它** ⇒ 压扁模型后脚与地面的相对关系不再匹配                         |
| **轮廓线厚度**      | 有意随缩放变化（距离按变换后位置取），不是 bug                                                                                       |
| **无 全ての親 的模型** | 走兜底路径 `R · T · S`：**模型变换只在渲染层生效，物理不跟随**（与有载体的模型行为不同）。这类模型多为自制 / 简易骨架                                            |
| **外部親挂载**      | 子模型挂到亲骨后，模型变换的注入因子仍在 `RootParent` 之后后乘 ⇒ 两者**可叠加**；**缩放倍率照旧只影响渲染层**（物理仍跑在子模型自己的 bind 尺度）。见 [external-parent.md](animation/external-parent.md) |

***

## 6. 最小用法

```csharp
// 宿主侧：改状态即可，其余交给 PrepareFrame
modelRenderer.ModelScale = new Vector3(1.5f, 1.5f, 1.5f);            // 缩放倍率
modelRenderer.Model.ModelTranslationOffset = new Vector3(0, 5, 0);   // 移动
modelRenderer.Model.ModelRotationAngles = new Vector3(0, MathF.PI / 6f, 0);  // 旋转（YXZ，弧度）

// 每帧（PrepareFrame 内部顺序，勿打乱）：
//   1. ApplyModelTransform（重建 全ての親 后乘因子）
//   2. ComputeRootMatrix + ComputeNormalMatrix（渲染层根矩阵）
//   3. MmdIkSolver.Solve → UpdateWorldMatrices（IK / FK / 赋予，模型变换已就位）
//   4. MMDPhysics.Update → ApplyPhysicsAppend（物理在 bind 尺度上模拟）
//   5. 蒙皮 SSBO / morph 上传
modelRenderer.PrepareFrame(in frame);
```

`PrepareFrame` 内部顺序不可打乱：模型变换必须早于 IK / FK，
而缩放倍率只在蒙皮之后施加、不进 `WorldMatrices`。

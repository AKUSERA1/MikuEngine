# PMX 模型解析

从 PMX 二进制文件到运行时 SkeletalModel 的完整链路。

```
PMX 二进制 (.pmx)
      │
      ▼
PmxParser.Parse(byte[])        ← 纯二进制解析，零平台依赖
      │
      ▼
PmxModel (纯数据容器)          ← 顶点 / 骨骼 / 材质 / 表情 / 刚体 ...
      │
      ▼
SkeletalModelConverter.Convert  ← 交错 VBO + 逆绑定矩阵 + 变形顺序 + 材质段
      │
      ▼
SkeletalModel (运行时模型)      ← 引擎侧直接消费的形态
```

## 源文件

| 文件                                                                                                                  | 说明                                                              |
| ------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| [PmxModelData.cs](../../src/MikuEngine.Core/Models/PmxModelData.cs)                                                 | 所有 PMX 数据类型（PmxModel / PmxVertex / PmxBone / PmxMaterial / ...） |
| [PmxParser.cs](../../src/MikuEngine.Core/Models/PmxParser.cs)                                                       | 二进制解析器（移植自 babylon-mmd）                                         |
| [PmxTexturePath.cs](../../src/MikuEngine.Core/Models/PmxTexturePath.cs)                                             | 纹理路径归一化工具                                                       |
| [SkeletalModel.cs](../../src/MikuEngine.Core/Models/SkeletalModel.cs)                                               | 运行时模型 + 骨骼姿势计算                                                  |
| [SkeletalModelConverter.cs](../../src/MikuEngine.Core/Models/SkeletalModelConverter.cs) | PmxModel → SkeletalModel 转换                                     |

---

## PmxParser

### 基本用法

```csharp
using MikuEngine.Core.Models;

byte[] data = File.ReadAllBytes("model.pmx");
PmxModel pmx = PmxParser.Parse(data);

// 或带 bytesRemaining 诊断
PmxModel pmx = PmxParser.Parse(data, out int remaining);
// remaining 应恒为 0；非 0 说明 PMX 里有本版本未识别的节
```

### PmxModel 结构

| 属性              | 类型                  | 说明                                  |
| --------------- | ------------------- | ----------------------------------- |
| `Header`        | `PmxHeader`         | 签名 / 版本 / 编码 / 各索引宽度                |
| `Vertices`      | `PmxVertex[]`       | 位置 / 法线 / UV / 权重 / 边缘系数            |
| `Indices`       | `int[]`             | 三角形索引                               |
| `Textures`      | `string[]`          | 原始相对路径（不归一化）                        |
| `Materials`     | `PmxMaterial[]`     | 材质定义（漫反射 / 球体贴图 / Toon / 标志位）       |
| `Bones`         | `PmxBone[]`         | 骨骼（位置 / 父骨 / IK / 追加变换 / 角度限制）      |
| `Morphs`        | `PmxMorph[]`        | 表情（顶点 / 骨骼 / UV / 材质 / 组 / 翻转 / 冲量） |
| `DisplayFrames` | `PmxDisplayFrame[]` | 骨骼 / 表情分组                           |
| `RigidBodies`   | `PmxRigidBody[]`    | 刚体（关联骨骼 / 形状 / 质量 / 物理模式）           |
| `Joints`        | `PmxJoint[]`        | 物理关节（约束参数）                          |
| `SoftBodies`    | `PmxSoftBody[]`     | 软体（仅 PMX 2.1）                       |

### 顶点骨骼权重（PmxBoneWeight）

PMX 的权重类型：

| 类型      | 骨数  | 说明                     |
| ------- | --- | ---------------------- |
| `Bdef1` | 1   | 完全刚性绑定                 |
| `Bdef2` | 2   | 两骨线性混合                 |
| `Bdef4` | 4   | 四骨线性混合                 |
| `Sdef`  | 2   | 球形变形（当前 v1 退化为 Bdef2）  |
| `Qdef`  | 4   | 四元数变形（当前 v1 退化为 Bdef4） |

`PmxBoneWeight.EffectiveWeight(i)` 处理了 Bdef2/SDEF 的隐式权重推导。

---

## PmxTexturePath

```csharp
string normalized = PmxTexturePath.Normalize(pmxRelativePath);
// "texture\\cloth/01.png" → "texture/cloth/01.png"
// 清理了 '\' → '/'、尾部 NUL、重复分隔符、开头 "./"
```

不触碰文件名本身（保留大小写与扩展名），也不展开相对上级 `".."`（交由上层决定）。

---

## SkeletalModel

引擎侧直接消费的运行时模型，包含交错 VBO + 骨骼姿势 + 包围盒。

### 顶点格式（VertexStride = 52 字节）

```
offset  size  format            说明
 0      12    vec3 float        aPosition（模型空间）
12      12    vec3 float        aNormal
24      16    vec4 float        aUv.xy = UV, z = EdgeScale, w = DeformType(权重类型枚举)
40       8    uvec4 ushort      aJoints（4 个骨骼索引，UNSIGNED_SHORT ×4）
48       4    ubvec4 ubyte      aWeights（UNSIGNED_BYTE ×4，硬件归一化 ÷255）
```

**为什么骨骼索引用 UNSIGNED_SHORT**：绑定骨骼数远超 UNSIGNED_BYTE 能表达的 255
（历史测试模型 1099 骨，当前 Demo 模型 `Model/1/1.pmx` 726 骨），UNSIGNED_BYTE 装不下。

### 骨骼状态

```csharp
model.LocalRotations[i] = newRotation;   // 设置局部旋转（四元数）
model.UpdateWorldMatrices();              // 按 DeformOrder（拓扑序）重算
// 之后 model.SkinMatrices[i] = InverseBind[i] * WorldMatrices[i] 已准备好上传 GPU
```

乘序（行向量 `v · M`）：`local * parentWorld`、`InverseBind * WorldMatrices`。
绑定姿势下 `Skin ≡ I`，写成另一种顺序数值相同，静态预览看不出差别；
一旦有旋转，`parentWorld * local` 会把父骨原点拿子骨旋转去转，误差随「骨到原点的距离」放大
（上半身 / 手臂 / 手指链条会撕裂成放射状薄片）。

### 姿势求值相关成员

围绕 `UpdateWorldMatrices` 的求值链（軸制限 → 付与 → IK）在 Core 层暴露以下成员，
详细语义见动画子系统文档：

| 成员 | 说明 |
|---|---|
| `DeformOrder` | 「父先于子」且「付与源先于付与目标」的求值序（转换期 `BuildEvaluationOrder` 算好） |
| `AppendSources` / `AppendRatios` / `AppendRotate` / `AppendMove` / `AppendIsLocal` | 付与（见 [append-transform.md](animation/append-transform.md)） |
| `AxisLimits` | 軸制限轴（已归一化，`Zero` = 无限制） |
| `IkChains` / `IsIkLink` / `IkRotations` / `IkLinkBaseRotations` / `IkEnabled` / `IkSolverEnabled` | IK（见 [ik.md](animation/ik.md)） |
| `FinalRotations` / `FinalTranslations` | 最近一次求值的「付与 + IK 之后」有效局部变换（只读快照，不回写 `Local*`） |
| `UpdateWorldMatricesSubtree(bone)` | 只重算某骨及其全部后代（IK 迭代内的增量刷新）。后代成员表**按骨缓存**（首次 O(骨数) 构建，之后每次 O(后代数)），成员按 `DeformOrder` 秩升序排列 —— `DeformOrder` 是「父边 ∪ 付与边」的拓扑序，子树在其中**不连续**，故只能缓存成员表、不能缓存区间 |
| `SetPhysicsDrivenBones(bones)` / `ApplyPhysicsAppend()` / `HasPostPhysicsAppend` / `PhysicsAppendBoneCount` | 物理后付与 S3（见 [append-transform.md](animation/append-transform.md)） |
| `Visible` | 表示枠求值结果（见 [visibility.md](animation/visibility.md)） |
| `MorphRawWeights` / `MorphWeights` / `VertexMorphs` / `UvMorphs` / `BoneMorphs` / `MaterialMorphs` / `GroupMorphs` / `GroupOrder` | 表情（稀疏表，见 [lifecycle.md](animation/lifecycle.md)） |
| `ModelTranslationOffset` / `ModelRotationAngles` / `RootTransformBoneIndex` / `OperationCenterBoneIndex` / `ApplyModelTransform()` | 模型面板变换（见 [model-transform.md](model-transform.md)） |

### 包围盒

```csharp
Vector3 center = model.BoundsCenter;   // 绑定姿势几何中心
Vector3 size   = model.BoundsSize;     // 绑定姿势尺寸
```

---

## SkeletalModelConverter

### 基本用法

```csharp
SkeletalModel model = SkeletalModelConverter.Convert(pmx);
// 内部：BuildBones → BuildVertices → BuildIndices → BuildSegments → BuildMorphs
//       → ResolveModelTransformBones（按名定位 全ての親 / 操作中心）
//       → ResetPose()（所有骨设为单位旋转，LocalTranslations = LocalPositions）
```

### 权重处理（ComputeWeightBytes）

权重转字节时做了多重兜底：

1. SDEF → Bdef2 退化，QDEF → Bdef4 退化
2. 第 4 槽用推导值 `1 - w0 - w1 - w2` 而非文件存的 Weight3
3. 脏数据兜底：负权重清零 + 整体归一化
4. 逐槽四舍五入后，把和的**残差塞给最大槽**，保证四槽字节和恒为 255

> 第 4 条的细节值得记一笔：曾把残差塞给「最后一个非零槽」。当小槽各自 +0.5 的舍入累积
> 把前面槽的和顶过 255 时，`255 - acc` 为负被 Clamp 成 0，总和变成 256
> （实测 `1.pmx` 顶点 54 = 176,78,2,0）。残差必须由**最大槽**吸收：
> `max + delta ≤ 255` 数学上恒成立，因为 `delta = 255 − Σ ≤ 255 − max`。

### 材质段分类（ClassifyMaterial）

简化规则，只看 Diffuse.W（MMD 非透过度）：

| 分类        | 条件                     | 说明    |
| --------- | ---------------------- | ----- |
| `Opaque`  | Diffuse.W >= 0.99 且无贴图 | 不透明   |
| `Cutout`  | 有贴图                    | 贴图裁剪  |
| `Blended` | Diffuse.W < 0.99       | 半透明混合 |

> 注意：当前 GlesModelRenderer **不按此分类分队列**，按 PMX 材质顺序单一队列绘制，对齐 PmxEditor 的行为。

### DeformOrder（拓扑序）

从所有根骨（parent < 0）出发做迭代 DFS，保证"父先于子"。不信任 PMX 的 TransformOrder 字段——虽然规范上它应该是拓扑序，但并非所有文件都严格保证。

---

## 完整示例

```csharp
using MikuEngine.Core.Models;

// 1. 解析
byte[] data = File.ReadAllBytes("model.pmx");
PmxModel pmx = PmxParser.Parse(data);

// 2. 转换
SkeletalModel model = SkeletalModelConverter.Convert(pmx);

// 3. 按名找骨骼
int idx = model.FindBone("上腕_L");
if (idx >= 0)
    model.SetBoneLocalRotation(idx, newRotation);

// 4. 更新姿势 → 准备上传 GPU
model.UpdateWorldMatrices();
// model.SkinMatrices 已就绪
```

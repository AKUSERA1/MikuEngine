# 赋予变换（Append Transform）— SkeletalModel

赋予(日文"付与")是 MMD 的「父级约束」：目标骨在自身动画之外，额外继承源骨的一部分变换
（旋转 / 平移 × 赋予比例）。在 `SkeletalModel.UpdateWorldMatrices` 内求值，属于 **Core 层**。

典型用法（PMX 模型自带）：

| 例子                                     | 效果          |
| -------------------------------------- | ----------- |
| 足D / ひざD / 足首D 来源于 足 / ひざ / 足首         | 复制腿，让它不随腰弯曲 |
| 腰キャンセル左 / 右 来源于 腰（ratio = -1）          | 抵消腰的旋转      |
| 左目 / 左目先 来源于 両目 / 左目（ratio 0.8 / -0.7） | 眼球跟随        |
| 腕捩1..3 来源于 腕捩（ratio 0.25 / 0.5 / 0.75） | 把扭转按比例摊到前臂  |

## 数据来源

PMX 骨数据在模型转换期（`SkeletalModelConverter.Convert`）解析为以下并行数组（长度 = 骨数）。
「PMX 字段」列给出对应的 PMX 骨字段名：

| 字段                | PMX 字段                      | 说明                                        |
| ----------------- | --------------------------- | ----------------------------------------- |
| `AppendSources[]` | `付与親`                       | 源骨索引，`-1` = 无赋予                           |
| `AppendRatios[]`  | `付与率`                       | 比例系数，允许负值（-1 = 反向抵消），已钳制到 \[-1, 1]        |
| `AppendRotate[]`  | `付与回転`（骨标志 bit8 / 0x0100）   | 是否继承旋转                                    |
| `AppendMove[]`    | `付与移動`（骨标志 bit9 / 0x0200）   | 是否继承平移                                    |
| `AppendIsLocal[]` | `付与ローカル`（骨标志 bit7 / 0x0080） | true = 取赋予源骨的**世界**变换；false = 默认的**局部**变换 |

求值顺序由转换期预计算：**赋予源先于赋予目标**（赋予链可级联）。

## 求值语义

```
轴限制 → 赋予 → IK → 世界 / 蒙皮矩阵
```

- 旋转赋予：`rotation = rotation * Slerp(Identity, srcRot, ratio)`（右乘叠加）；
  `IsLocal` 时 `srcRot` 取源骨最终**世界**旋转反解到父旋转系，否则取源骨**最终局部**旋转。
- 平移赋予：同理叠加 `srcOffset * ratio`。
- **自赋予（赋予源 = 自身）视为无操作**，转换期直接剥离，不进入求值序。
- 最终局部变换写入独立暂存 `FinalRotations` / `FinalTranslations`（只读快照），
  **不回写** **`LocalRotations`** **/** **`LocalTranslations`** ⇒ `UpdateWorldMatrices` 幂等，
  同帧多次调用不会二次赋予。

> 同样的纪律也约束 IK：IK 结果写进独立的 `IkRotations[]`，若回写 `LocalRotations`，
> 下一帧赋予会把上次的 IK 再继承一次（双重赋予）。

## 物理后赋予（PostPhysicsAppend）

MMD本体存在一种硬编码的静默处理：当一个骨骼拥有`付与親`，且`付与親`是物理驱动骨时，MMD会忽略模型的值，在此次赋予求解时使用**物理模拟后**的骨骼矩阵。如果不这么做，则部分模型的胸部等依赖该静默处理的地方会丢失物理效果

```csharp
public void SetPhysicsDrivenBones(IReadOnlyList<int> drivenBones);
```

把物理内核报告的「每步覆写其骨骼世界矩阵」的骨列表（`MMDPhysics.GetPhysicsDrivenBones()`）
注入模型，闭包出需要物理后重算的赋予骨集合与源骨集合。
渲染层 `GlesModelRenderer.Physics` 注入器在赋值时自动调用，宿主通常无需手动调。

只读拓扑：

| 成员                       | 说明                     |
| ------------------------ | ---------------------- |
| `HasPostPhysicsAppend`   | 拓扑非空（赋予链真的挂在物理驱动骨上）    |
| `PhysicsAppendBoneCount` | 需在物理步后重算的骨数（0 = S3 无感） |

### 每帧：物理写回后重算

```csharp
public void ApplyPhysicsAppend();
```

把模拟骨的最终局部变换从物理写回的世界矩阵**反解**回暂存，再按 deform 序只重算受影响的
赋予子集（无拓扑时 O(1) 早退）。受影响骨的 `SkinMatrices` 由重算内部更新；
物理写回骨自身的蒙皮矩阵由渲染层全量循环覆盖。

暂存每帧由 `UpdateWorldMatrices` 从 `Local*` 全量重建 ⇒ 物理前的全量 pass 永远读到纯动画，
**无跨帧泄漏**。

## 最小用法

模型渲染路径（`GlesModelRenderer.PrepareFrame`）已内置完整顺序，无需手动调用：

```
MmdIkSolver.Solve → UpdateWorldMatrices（轴限制 → 赋予 → IK）
→ MMDPhysics.Update（物理写回）→ ApplyPhysicsAppend（S3 开关时）→ 重算蒙皮
```

自建帧循环时按上述顺序调用即可；赋予本身没有独立开关（由 PMX 数据决定），
物理后赋予的 A/B 对照用 `GlesModelRenderer.PostPhysicsAppendEnabled`。

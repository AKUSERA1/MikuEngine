# 付与变换（Append Transform）— SkeletalModel

付与是 MMD 的「父约束」：目标骨在自身动画之外，额外继承源骨的一部分变换（旋转 / 平移 × 付与率）。
在 `SkeletalModel.UpdateWorldMatrices` 内求值，属于 **Core 层**。

典型用法（PMX 模型自带）：

| 例子 | 效果 |
|---|---|
| 足D / ひざD / 足首D 付与自 足/ひざ/足首 | 复制腿，让它不随腰弯曲 |
| 腰キャンセル左/右 付与自 腰（ratio = -1） | 抵消腰的旋转 |
| 左目/左目先 付与自 両目/左目（ratio 0.8 / -0.7） | 眼球跟随 |
| 腕捩1..3 付与自 腕捩（ratio 0.25/0.5/0.75） | 把扭转按比例摊到前臂 |

## 数据来源

PMX 骨数据在模型转换期（`SkeletalModelConverter.Convert`）解析为以下并行数组（长度 = 骨数）：

| 字段 | PMX 来源 | 说明 |
|---|---|---|
| `AppendSources[]` | 付与亲索引 | 源骨索引，`-1` = 无付与 |
| `AppendRatios[]` | 付与率 | PMX 允许负值（-1 = 反向抵消），已钳制到 [-1, 1] |
| `AppendRotate[]` | 骨标志 bit8 (0x0100) | 是否继承旋转 |
| `AppendMove[]` | 骨标志 bit9 (0x0200) | 是否继承平移 |
| `AppendIsLocal[]` | 骨标志 bit7 (0x0080) | true = 取付与亲的**世界**变换；false = 默认的**局部**变换 |

求值顺序由转换期的 `BuildEvaluationOrder` 预计算：**付与源先于付与目标**（付与链可级联）。

## 求值语义

```
軸制限 → 付与 → IK → 世界/蒙皮矩阵
```

- 旋转付与：`rotation = rotation * Slerp(Identity, srcRot, ratio)`（右乘叠加）；
  `IsLocal` 时 `srcRot` 取源骨最终**世界**旋转反解到父旋转系，否则取源骨**最终局部**旋转
  （与 babylon-mmd `AppendTransformSolver` 的 isLocal 分支逐式等价）。
- 平移付与：同理叠加 `srcOffset * ratio`。
- **自付与（付与源 = 自身）视为无操作**，转换期直接剥离，不进入求值序。
- 最终局部变换写入独立暂存 `FinalRotations` / `FinalTranslations`（只读快照），
  **不回写 `LocalRotations` / `LocalTranslations`** ⇒ `UpdateWorldMatrices` 幂等，
  同帧多次调用不会二次付与。

> 同样的纪律也约束 IK：IK 结果写进独立的 `IkRotations[]`，若回写 LocalRotations，
> 下一帧付与会把上次的 IK 再继承一次（双重付与）。

## 物理后付与（PostPhysicsAppend / S3）

问题：物理写回只发布被模拟骨的**世界矩阵**，而付与求值读的是源骨的**最终局部变换**。
付与链挂在物理驱动骨上时（胸 rig：可见骨付与自 `胸_回転` 这类被模拟骨），不重算的
付与骨会永远穿着物理前的动画姿态。

### 加载期：拓扑预计算

```csharp
public void SetPhysicsDrivenBones(IReadOnlyList<int> drivenBones);
```

把物理内核报告的"每步覆写其骨骼世界矩阵"的骨列表（`MMDPhysics.GetPhysicsDrivenBones()`）
注入模型，闭包出需要物理后重算的付与骨集合与源骨集合（reze `setPhysicsDrivenBones` 同构）。
渲染层的 [GlesModelRenderer.Physics](../../../src/MikuEngine.Render.GLES/GlesModelRenderer.cs)
注入器在赋值时自动调用，宿主通常无需手动调。

只读拓扑：

| 成员 | 说明 |
|---|---|
| `HasPostPhysicsAppend` | 拓扑非空（付与链真的挂在物理驱动骨上） |
| `PhysicsAppendBoneCount` | 需在物理步后重算的骨数（0 = S3 无感） |

### 每帧：物理写回后重算

```csharp
public void ApplyPhysicsAppend();
```

把模拟骨的最终局部变换从物理写回的世界矩阵**反解**回 `_finalRotations` / `_finalTranslations`
暂存，再按 deform 序只重算受影响的付与子集（无拓扑时 O(1) 早退）。
受影响骨的 `SkinMatrices` 由重算内部更新；物理写回骨自身的蒙皮矩阵由渲染层全量循环覆盖。

暂存每帧由 `UpdateWorldMatrices` 从 `Local*` 全量重建 ⇒ 物理前的全量 pass 永远读到纯动画，
无跨帧泄漏（reze 需要 override 数组 + 读路径特判，是因为它的 localRotations 是跨帧持久状态；
本项目的暂存架构不需要）。

## 最小用法

模型渲染路径（`GlesModelRenderer.PrepareFrame`）已内置完整顺序，无需手动调用：

```
MmdIkSolver.Solve → UpdateWorldMatrices（軸制限 → 付与 → IK）
→ MMDPhysics.Update（物理写回）→ ApplyPhysicsAppend（S3 开关时）→ 重算蒙皮
```

自建帧循环时按上述顺序调用即可；付与本身没有独立开关（由 PMX 数据决定），
物理后付与的 A/B 对照用 `GlesModelRenderer.PostPhysicsAppendEnabled`。

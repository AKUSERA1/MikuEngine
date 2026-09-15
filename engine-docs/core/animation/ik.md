# CCD IK 求解器 — MmdIkSolver

MMD 风格的 CCD IK，沿用引擎的左手 Y-up + Z-forward +
行向量 `v * M` 约定。

全部位于 **Core 层**，纯数学、零渲染依赖。

## 源文件

| 文件                                                                               | 说明                             |
| -------------------------------------------------------------------------------- | ------------------------------ |
| [MmdIkSolver.cs](../../../src/MikuEngine.Core/Animation/MmdIkSolver.cs)          | CCD IK 求解器（静态类）                |
| [MmdIkChain.cs](../../../src/MikuEngine.Core/Models/MmdIkChain.cs)               | 运行时 IK 链 / 链节 / Euler 顺序 / 固定轴 |
| [MmdIkChainBuilder.cs](../../../src/MikuEngine.Core/Models/MmdIkChainBuilder.cs) | 从 PMX 骨数据构建运行时 IK 链            |

***

## 核心角色语义（必读）

搞反会直接表现为「脚永远到不了地面」：

```
        目标（冻结）                        被驱动端（活跃）
   带 IK 标志的骨（如 左足ＩＫ）        PMX Ik.Target 所指的骨（如 左足首）
   世界坐标在迭代前取一次，             每步 link 更新后刷新
   迭代全程不刷新
```

**链序**：`Links` 保持 PMX 文件顺序 = 末端侧 → 根。每轮转角预算 `LimitAngle * (index + 1)`
因此让越靠根的骨获得越大预算（足 IK 的 `[ひざ, 足]` ⇒ 大腿 2× > 小腿 1×），与解剖直觉一致。

***

## 调用入口

### MmdIkSolver.Solve

```csharp
public static void Solve(SkeletalModel model, List<IkChainSolveResult>? results = null);
```

对 `model.IkChains` 中的全部链求解。调用时机约束：

- 必须在**动画已写入局部 T/R（含骨 morph、赋予求值）之后**、**最后一次
  `UpdateWorldMatrices`** **之前**调用（渲染层就是 `PrepareFrame` 里的顺序：`Solve → UpdateWorldMatrices`）。
- 内部先跑一次全量 `UpdateWorldMatrices` 拿到 FK 世界矩阵并导出链骨基旋转；
  迭代中只在链骨子树上增量重算（`UpdateWorldMatricesSubtree`）；结束后由调用方再跑一次全量更新。
- 每次求解先整体复位 `IkRotations`，**无跨帧状态**——
  「连续播放到帧 N ≡ 直接 seek 到帧 N」。
- `results` 传 `null`（渲染路径）只求解不记录；诊断路径传入可拿到每条链的结果。

### IkChainSolveResult

```csharp
public readonly record struct IkChainSolveResult(bool Solved, int IterationsUsed, float ErrorSquared);
```

诊断用，不参与逻辑。`ErrorSquared` 是目标与被驱动端的距离平方。

### 模型侧开关

| 成员                              | 说明                                                        |
| ------------------------------- | --------------------------------------------------------- |
| `SkeletalModel.IkSolverEnabled` | 总开关（false 时 `Solve` 只复位 `IkRotations` 后返回）                |
| `SkeletalModel.IkEnabled[i]`    | 第 i 条链的逐链开关（对应 MMD 的「IK」旋钮）                               |
| `SkeletalModel.IkRotations[]`   | 求解结果（链骨的叠加旋转），`UpdateWorldMatrices` 时折进最终局部旋转             |
| `SkeletalModel.IkChains[]`      | 运行时链（由 `SkeletalModelConverter` 经 `MmdIkChainBuilder` 构建） |

> **IK 结果不写回** **`LocalRotations`**，只写独立的 `IkRotations[]`。
> 若回写 `LocalRotations`，下一帧赋予会把上次的 IK 结果再继承一次（双重赋予）。

***

## 求解流程（单条链）

```
复位 IkRotations → 取冻结目标（只取一次）→ 收敛预检 → 预推进整条链
→ 迭代 chain.Iteration 次 { 逐 link：SolveLink → 链循环后收敛判据 }
```

- **收敛判据**：目标与被驱动端距离平方 < `ConvergenceEpsilonSq`（`1e-8f`），
  在**链循环之后**判断，不在 link 循环内。
- **迭代上限**：`Iteration` 取自 PMX 的 loop 计数并封顶（`min(PmxLoop, 256)`）。
- **单 link（SolveLink）**：
  1. 由链骨位置分别指向被驱动端 / 冻结目标求单位向量，叉积得旋转轴（三点共线则跳过）；
  2. 轴变换到父骨旋转系（父世界旋转的转置）；
  3. 转角 `min(acos(toTarget·toIk), LimitAngle * (linkIndex + 1))`，写入 `IkRotations[bone]`；
  4. 有角度限制时：在 `base * IKRotation` 上做欧拉分解 → 硬钳 / 反射回弹 → 重建（见下）；
  5. 立刻 `UpdateWorldMatricesSubtree(bone)`，供下个 link / 收敛判据读取。

### 角度限制与固定轴

- PMX 只存角度范围 min/max；**Euler 分解顺序是推导出来的**：
  X 范围落在 ±90° 内 → ZXY；否则 Y 在 ±90° 内 → XYZ；否则 YZX。
- **固定轴（`MmdIkFixAxis`）**：六分量全 0 → `Fix`（完全锁死，该 link 直接跳过）；
  三轴中两个恒为 0 → 单轴可动（自由轴被量化成 ±父旋转系基轴）。
- **限制只在前半段迭代生效**（`it < Iteration/2`）。它控制两件事：固定轴的量化投影、
  角度越界时是否允许「反射回弹」（`2*bound − angle`）；硬钳位到 \[min, max] 每轮都做。
- **奇异钳位**：Euler 分解的 asin 参数钳到 ±88°（`1.535889 rad`），
  避免接近垂直时 gimbal 翻转。

### ReverseClamp（反向半球钳位）

目标落在反向半球（`toTarget·toIk < 0`）时，`acos` 会给出 90°\~180° 的巨大单步转角，
CCD 会试图一步把腿甩过去（表现为「腿被吸走 / 翻转」）。该开关把反向一步封顶到 `LimitAngle`
（去掉逐级放大）。**默认开启**。

***

## 诊断开关（环境变量）

| 环境变量                         | 作用                                                 |
| ---------------------------- | -------------------------------------------------- |
| `MIKU_IK_NO_LIMIT=1`         | 忽略全部角度限制与固定轴量化——把「轴 / 每轮转角公式」与「Euler 限位路径」两类差异二分定位 |
| `MIKU_IK_NO_REVERSE_CLAMP=1` | 关闭 ReverseClamp（A/B 对照用）                           |

默认全部关闭（即限制与钳位生效）。

***

## 最小用法

```csharp
// 动画已采样写回 + MmdMorphEvaluator.Evaluate 已跑完（局部 T/R 就绪）
MmdIkSolver.Solve(model);            // IK 叠加进 model.IkRotations
model.UpdateWorldMatrices();         // 全量重算：轴限制 → 赋予 → IK → 世界 / 蒙皮矩阵
```

渲染层（`GlesModelRenderer.PrepareFrame`）已内置该顺序，直接渲染无需手动调用；
仅自建帧循环（离线渲染 / 测试）时需要自己按上述顺序调用。

> **注意**：在MMD本体中，足 IK 通硬编码的骨骼名称判断，走`解析式IK`。本项目足 IK 按普通 CCD 链求解，**不按骨名特判**。若模型依赖 MMD 对足 IK 的特殊处理，
> 行为可能与 MMD 有细节差异。


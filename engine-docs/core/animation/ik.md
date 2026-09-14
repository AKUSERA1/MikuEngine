# CCD IK 求解器 — MmdIkSolver

MMD 风格 CCD（Circular Buffer / Cyclic Coordinate Descent）IK，逐式移植 PmxEditor 的 `IKTransform`
（SlimDX/D3D9 行向量约定与本引擎同族：左手 Y-up + Z-forward + 行向量 `v * M`），不做"等价改写"。

全部位于 **Core 层**，纯数学、零渲染依赖。

## 源文件

| 文件 | 说明 |
|---|---|
| [MmdIkSolver.cs](../../../src/MikuEngine.Core/Animation/MmdIkSolver.cs) | CCD IK 求解器（静态类） |
| [MmdIkChain.cs](../../../src/MikuEngine.Core/Models/MmdIkChain.cs) | 运行时 IK 链 / 链节 / Euler 顺序 / 固定轴 |
| [MmdIkChainBuilder.cs](../../../src/MikuEngine.Core/Models/MmdIkChainBuilder.cs) | 从 PMX 骨数据构建运行时 IK 链 |

---

## 核心角色语义（必读）

搞反会直接表现为"脚永远到不了地面"：

```
        目标（冻结）                        被驱动端（活跃）
   带 IK 标志的骨（如 左足ＩＫ）        PMX Ik.Target 所指的骨（如 左足首）
   世界坐标在迭代前取一次，             每步 link 更新后刷新
   迭代全程不刷新
```

**链序**：`Links` 保持 PMX 文件顺序 = 末端侧 → 根。每轮转角预算 `LimitAngle * (index + 1)`
因此让越靠根的骨获得越大预算（足 IK 的 `[ひざ, 足]` ⇒ 大腿 2× > 小腿 1×），与解剖直觉一致。

---

## 调用入口

### MmdIkSolver.Solve

```csharp
public static void Solve(SkeletalModel model, List<IkChainSolveResult>? results = null);
```

对 `model.IkChains` 中的全部链求解。调用时机约束：

- 必须在**动画已写入局部 T/R（含骨 morph、付与求值）之后**、**最后一次
  `UpdateWorldMatrices` 之前**调用（渲染层就是 [GlesModelRenderer.PrepareFrame](../../../src/MikuEngine.Render.GLES/GlesModelRenderer.cs) 里的顺序：
  `Solve → UpdateWorldMatrices`）。
- 内部先跑一次全量 `UpdateWorldMatrices` 拿到 FK 世界矩阵并导出链骨基旋转；
  迭代中只在链骨子树上增量重算（`UpdateWorldMatricesSubtree`）；结束后由调用方再跑一次全量更新。
  该增量刷新每次是 **O(后代数)**（后代成员表按骨缓存，见 [pmx-parser.md](../pmx-parser.md)）。
  早期实现每调用一次都要「清 O(骨数) 标记数组 + 两趟全扫求值序」，而本模型的 IK 每帧要调 241.6 次
  ⇒ 单帧 52.6 万次求值序访问、真正重算的骨只有 616 个，是当时的头号 CPU 热点。
- 每次求解先整体复位 `IkRotations`（PmxEditor `InitializeAngle()`），无跨帧状态 ——
  "连续播放到帧 N ≡ 直接 seek 到帧 N"。
- `results` 传 `null`（渲染路径）只求解不记录；诊断路径传入可拿到每条链的结果。

### IkChainSolveResult

```csharp
public readonly record struct IkChainSolveResult(bool Solved, int IterationsUsed, float ErrorSquared);
```

诊断用，不参与逻辑。`ErrorSquared` 是目标与被驱动端的距离平方。

### 模型侧开关

| 成员 | 说明 |
|---|---|
| `SkeletalModel.IkSolverEnabled` | 总开关（false 时 `Solve` 只复位 `IkRotations` 后返回） |
| `SkeletalModel.IkEnabled[i]` | 第 i 条链的逐链开关（对应 MMD 的「IK」 ダイヤル） |
| `SkeletalModel.IkRotations[]` | 求解结果（链骨的叠加旋转），`UpdateWorldMatrices` 时折进最终局部旋转 |
| `SkeletalModel.IkChains[]` | 运行时链（由 SkeletalModelConverter 经 `MmdIkChainBuilder` 构建） |

---

## 求解流程（单条链）

```
复位 IkRotations → 取冻结目标（只取一次）→ 收敛预检 → 预推进整条链
→ 迭代 chain.Iteration 次 { 逐 link：SolveLink → 链循环后收敛判据 }
```

- **收敛判据**：目标与被驱动端距离平方 < `ConvergenceEpsilonSq`（`1e-8f`），
  在**链循环之后**判断，不在 link 循环内。
- **单 link（SolveLink）**：
  1. 由链骨位置分别指向被驱动端 / 冻结目标求单位向量，叉积得旋转轴（三点共线则跳过）；
  2. 轴变换到父骨旋转系（父世界旋转的转置）；
  3. 转角 `min(acos(toTarget·toIk), LimitAngle * (linkIndex + 1))`，写入 `IkRotations[bone]`；
  4. 有角度限制时：在 `base * IKRotation` 上做欧拉分解 → 硬钳 / 反射回弹 → 重建（见下）；
  5. 立刻 `UpdateWorldMatricesSubtree(bone)`，供下个 link / 收敛判据读取。

### 角度限制与固定轴

- PMX 只存角度范围 min/max；**Euler 分解顺序与固定轴是推导出来的**
  （`MmdIkChainBuilder.DeriveEulerOrder`，按 PmxLib `NormalizeEulerAxis` 规则：
  X 范围在 ±90° 内 → ZXY；否则 Y 在 ±90° 内 → XYZ；否则 YZX）。
- **固定轴（`MmdIkFixAxis`）**：六分量全 0 → `Fix`（完全锁死，该 link 直接跳过）；
  三轴中两个恒为 0 → 单轴可动（自由轴被量化成 ±父旋转系基轴）。
- **前半段迭代**：PMX 的 `RotationConstraint` 只在前半段迭代生效（`it < Iteration/2`）。
  它只控制两件事：固定轴的量化投影、角度越界时是否允许"反射回弹"
  （`2*bound − angle`）；硬钳位到 [min, max] 每轮都做。
- **奇异钳位**：Euler 分解的 asin 参数钳到 ±88°（`1.535889 rad`，PmxEditor 与 MMD 本体同值），
  避免接近垂直时 gimbal 翻转。

### ReverseClamp（反向半球钳位）

目标落在反向半球（`toTarget·toIk < 0`）时，`acos` 会给出 90°~180° 的巨大单步转角，
CCD 会试图一步把腿甩过去（"腿被吸走 / 翻转"）。该开关把反向一步封顶到 `LimitAngle`
（去掉逐级放大）。默认**开启**。

---

## 诊断开关（环境变量）

| 环境变量 | 作用 |
|---|---|
| `MIKU_IK_NO_LIMIT=1` | 忽略全部角度限制与固定轴量化 —— 把"轴 / 每轮转角公式"与"Euler 限位路径"两类差异二分定位 |
| `MIKU_IK_NO_REVERSE_CLAMP=1` | 关闭 ReverseClamp（A/B 对照用） |

默认全部关闭（即限制与钳位生效）。

---

## 最小用法

```csharp
// 动画已采样写回 + MmdMorphEvaluator.Evaluate 已跑完（局部 T/R 就绪）
MmdIkSolver.Solve(model);            // IK 叠加进 model.IkRotations
model.UpdateWorldMatrices();         // 全量重算：軸制限 → 付与 → IK → 世界/蒙皮矩阵
```

渲染层（`GlesModelRenderer.PrepareFrame`）已内置该顺序，直接渲染无需手动调用；
仅自建帧循环（离线渲染 / 测试）时需要自己按上述顺序调用。

> 已知取舍：PMX 的足 IK 在 MMD 本体走私有解析式，本实现与 PmxEditor 一致，
> 把足 IK 当普通 CCD 链解，不做按骨名特判。

## 与方案文档的关系

求解公式与逐条决策记录在 `docs/2026-09-12-mmd-ik-design.md`（含附录 D 移植纪律、附录 F ReverseClamp）。
本页是面向调用方的 API 文档，不重复记录架构决策。

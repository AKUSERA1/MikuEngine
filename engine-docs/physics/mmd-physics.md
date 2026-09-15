# MMDPhysics — 骨骼同步层

物理内核对接宿主的主入口：静态 / 运动学刚体跟随骨骼（`boneWorld × bodyOffset`），
动态刚体在重力 + 约束下积分，再把姿态写回骨骼（`bodyWorld × bodyOffsetInverse`）。
按渲染帧序拆成三个相位，由 `Update` 一次串起：

```
FK / IK / 赋予（骨骼世界矩阵就绪）
      │
      ▼
MMDPhysics.Update(enabled, boneWorld, boneInvBind, frame, prevFrame)
      ├─ 1. SetKinematicTargets：骨骼 → kinematic 目标（速度取自目标轨迹）
      ├─ 2. Step(ticks)：tick 时钟推进 0..N 个固定物理 tick
      └─ 3. WriteBack：动态体姿态写回骨骼世界矩阵（alpha 渲染插值）
```

## 源文件

[Physics.cs](../../src/MikuEngine.Physics/Physics.cs)

## 构造

```csharp
public MMDPhysics(IReadOnlyList<RigidBodyDef> rigidbodies, IReadOnlyList<JointDef>? joints = null);
```

- 刚体 / 关节定义来自 `RigidBodyDef.FromPmx` / `JointDef.FromPmx`；
  骨骼索引与 PMX 保序一致，`RigidBodyDef.BoneIndex` 直接对应 `SkeletalModel` 的骨骼下标。
- 自动在列表末尾**追加内置地面体**（无骨骼 static 盒，顶面 = 模型空间 y = 0，
  见 [world.md](world.md) 的 `RigidBodyDef.CreateGround`），组掩码清零脱离通用碰撞对，
  `store.GroundIndex` 指向它。
- 约束（`ConstraintBuilder.BuildConstraints`）与 `SolverCache` 一次性构建，跨步复用。
- 预计算：每个动态体在关节图上的 kinematic 根（teleport carry 用）、
  mode-2 对齐钉扎集（直接挂在跟随骨骼体上的 PMX mode-2 体，如 `胸_回転`）。

## 属性

| 成员 | 说明 |
|---|---|
| `RigidBodyStore Store` | 刚体状态（只读视图，调试 / 诊断用） |
| `World World` | 物理世界（可调 `SolverIterations`、`SetWind` 等） |
| `float PlaybackFps` | 动画帧率（默认 30）。tick 时长 = 1/(fps × `TickRateMultiplier`)；变更时失效阻尼缓存 |
| `const int TickRateMultiplier` | 每动画帧的物理 tick 数，固定 2（Standard 档，无 UI 档位） |
| `float TickDuration` | 单个物理 tick 时长（动画域秒），默认 1/60 |
| `int TeleportCount` | 诊断计数：检测到瞬移（scrub / 跳变）并 settle 的帧数 |

## Update（主入口）

```csharp
public void Update(bool enabled, float[] boneWorldMatrices, float[] boneInverseBindMatrices,
                   double frame, double prevFrame);
```

| 参数 | 说明 |
|---|---|
| `enabled` | S1 物理总开关（场景配置）。OFF = 跳过整个物理段，骨骼保持纯动画 |
| `boneWorldMatrices` | 骨骼世界矩阵（列主序 `float[16×骨数]`，FK / IK / 赋予结果）；会被**就地写回** |
| `boneInverseBindMatrices` | 逆绑定矩阵（同布局，常量；仅首帧初始化用） |
| `frame` / `prevFrame` | 当前 / 上一渲染帧的连续动画帧号（`MmdTimeline.CurrentFrame`） |

S1 开关语义：

- **OFF → ON 边沿**：强制 `Reset`（snap 到当前骨骼姿态 + 速度清零 + tick 基准同步），
  ON 首帧 `advance = 0`（只 snap 不模拟），写回 == 动画姿态，无跳变。
- **ON → OFF**：无操作。物理体留在原地，骨骼自然回动画姿态；再开时 snap，同样无跳变。
- 开关状态纳入确定性契约（同配置双跑的开关序列相同 ⇒ 状态序列相同）。

tick 时钟（确定性）：`tickTarget = floor(frame × k)`，本帧前进 `advance = tickTarget − 上帧`
个固定 tick；`alpha = frame × k` 的小数部分供 WriteBack 渲染插值（Fix Your Timestep）。
`advance ≤ 0`（暂停 / 回卷）不推进模拟——回卷的骨骼突变由 teleport 检测兜底。

dt 语义是**动画秒**（帧号差 × 1/`PlaybackFps`），不是 wall dt——teleport 阈值
（250 units/s 按帧时间缩放）在 `FrameLocked` / `RealTime` 两种时钟下都必须以帧号域换算后的 dt 计。

## 分相 API（高级用法）

`Update` 内部按序调用以下三个方法；离线渲染需要自定义插值时可直接使用：

```csharp
public bool SetKinematicTargets(float[] boneWorldMatrices, float[] boneInverseBindMatrices, float dt);
public void Step(int tickAdvance);
public void WriteBack(float[] boneWorldMatrices, float alpha);
```

- `SetKinematicTargets`：从骨骼世界矩阵导出 kinematic 目标位姿与速度。速度取自
  **帧间目标轨迹**（而非子步推进量）——避免高刷新率下累加器相位纹波激励布料链。
  超过瞬移阈值的目标跳变会被检测并标记（carry 子树 / 清零动量）。
- `Step`：推进 N 个固定 tick（每 tick `TickDuration` 秒），内部调用 `World.Step`。
- `WriteBack`：动态体姿态写回骨骼矩阵，`alpha ∈ [0,1)` 在最近两个完成子步间 lerp，
  消除渲染率与 60 Hz 物理步不对齐时的头发 / 裙摆抖动。

## Reset

```csharp
public void Reset(float[] boneWorldMatrices);
```

时间轴拖动 / 乱序模拟后调用，做三件事：

1. **snap**：所有骨骼绑定体位姿钉回 `boneWorld × bodyOffset`，速度清零；
2. prev == curr（消除插值跳变）；
3. **reseed**：kinematic 目标重播种、目标速度清零（否则下一步会拿旧目标差喂出
   一帧巨大的锚点速度）。

首帧前调用是无操作。

## 其他开关与调优

```csharp
public void SetFloor(bool on);                                          // S2 地面碰撞开关
public void SetJiggleDamping(IReadOnlyList<int> boneIndices, float scale); // 抖动调优（预留）
public List<int> GetPhysicsDrivenBones();                               // 物理驱动骨列表
```

- **SetFloor**：只翻转 `store.GroundIndex` 开关位。地面体常驻 store（静态、无骨、
  不在通用碰撞对里，空闲零成本），任意 tick 边界热切安全，无需 reset。
- **SetJiggleDamping**：把指定骨骼所载刚体的阻尼乘以 `scale`（1 = 还原作者值，0.5 = 减半，
  0 = 无阻尼振铃）。调阻尼而非求解迭代：欠收敛的关节摆幅变大但永达不到平衡位置
  （静止下垂），阻尼不影响静止高度。幂等（作者值快照后 set 而非复合）。
  当前版本不接 UI，API 预留。
- **GetPhysicsDrivenBones**：列出「本模拟每步覆写其骨骼世界矩阵」的骨
  （Dynamic 体绑定的真实骨）。供赋予系统的 `SetPhysicsDrivenBones` 消费——
  赋予源骨被模拟时，赋予继承必须消费模拟结果而非帧首动画姿态。
  该列表每次调用都会新建 `List`，**不要放进每帧路径**（渲染层在 `Physics` setter 里缓存）。

## 典型集成（渲染层已内置）

```csharp
// 宿主侧：加载时构造并注入
modelRenderer.Physics = new MMDPhysics(rbDefs, jDefs);

// 每帧：GlesModelRenderer.PrepareFrame 内部完成
//   PlaybackFps = timeline.PlaybackFps; PhysicsFrame = timeline.CurrentFrame;
//   → MMDPhysics.Update → 写回 WorldMatrices → ApplyPhysicsAppend → 重算蒙皮
```

宿主只在按键 / 场景配置时改 `PhysicsEnabled` / `GroundCollisionEnabled` /
`PostPhysicsAppendEnabled` 三个开关位。

# MikuEngine.Physics — MMD 物理引擎

PMX 刚体 / 关节的物理模拟内核，从 reze-engine 的 TypeScript 物理实现（`physics/` 模块，
对应 MMD 本体 build 里的 Bullet 2.75 行为）逐式移植到 C#。仅依赖 `MikuEngine.Core`（数学），
零渲染依赖，可在单元测试与离线渲染中直接跑。

## 模块结构

```
PMX 数据 ──RigidBodyDef.FromPmx / JointDef.FromPmx──▶ 内核定义
                                                        │
                              ┌─────────────────────────┘
                              ▼
        MMDPhysics（骨骼同步层，对接宿主的主入口）
          │  SetKinematicTargets → Step(ticks) → WriteBack
          ▼
        World.Step（确定性物理步进）
          │  1. predict：重力 + 风 + 阻尼 → 速度预测
          │  2. collide：ContactDetection.FindContacts → ContactPool
          │  3. solve：ConstraintSolver（关节 + 接触，warm starting）
          │  4. integrate：半隐式欧拉积分（角速度/线速度防爆钳位）
          ▼
        RigidBodyStore（SoA 状态：位置/朝向/速度/质量/碰撞掩码……）
```

| 文件 | 职责 |
|---|---|
| [Physics.cs](../../src/MikuEngine.Physics/Physics.cs) | `MMDPhysics` 骨骼同步层：kinematic 跟随、snap、teleport carry、写回、reset、地面开关、NaN gate、渲染插值 |
| [World.cs](../../src/MikuEngine.Physics/World.cs) | `World` 确定性步进（predict → collide → solve → integrate）+ 风 |
| [PhysicsTypes.cs](../../src/MikuEngine.Physics/PhysicsTypes.cs) | `RigidBodyDef` / `JointDef` / `RigidbodyShape` / `RigidbodyType`（PMX 映射） |
| [RigidBodyStore.cs](../../src/MikuEngine.Physics/RigidBodyStore.cs) | SoA 刚体状态存储 + 碰撞对列表 |
| [ContactDetection.cs](../../src/MikuEngine.Physics/ContactDetection.cs) | 窄相碰撞：球/盒/胶囊两两 + 内置地面专用平面 pass |
| [ContactManifold.cs](../../src/MikuEngine.Physics/ContactManifold.cs) | `Contact` / `ContactPool`（池化，零分配热路径） |
| [ConstraintBuilder.cs](../../src/MikuEngine.Physics/ConstraintBuilder.cs) | PMX 关节 → 6DOF 弹簧约束（`SixDofSpringConstraint`） |
| [ConstraintSolver.cs](../../src/MikuEngine.Physics/ConstraintSolver.cs) | 顺序脉冲求解器 + `SolverCache` + split impulse |
| [ManifoldCache.cs](../../src/MikuEngine.Physics/ManifoldCache.cs) | 接触冲量历史（warm starting） |

子文档：

| 文档 | 说明 |
|---|---|
| [mmd-physics.md](mmd-physics.md) | `MMDPhysics` API：构造、Update 三相位、开关、reset、抖动阻尼 |
| [world.md](world.md) | `World` 步进细节：风、积分钳位、确定性契约、内核类型 |

## 快速上手

```csharp
using MikuEngine.Physics;

// 1. PMX 刚体/关节 → 内核定义（模式映射沿用 reze pmx-loader）
var pmx = PmxParser.Parse(File.ReadAllBytes("model.pmx"));
var rbDefs = pmx.RigidBodies.Select(RigidBodyDef.FromPmx).ToArray();
var jDefs  = pmx.Joints.Select(JointDef.FromPmx).ToArray();

// 2. 构造内核（自动追加内置地面体；约束与求解缓存一次性建好）
var physics = new MMDPhysics(rbDefs, jDefs);

// 3. 每渲染帧（FK/IK/付与之后；渲染层 GlesModelRenderer 已内置）：
//    physics.Update(enabled, boneWorld, boneInvBind, frame, prevFrame);
//    → 骨骼世界矩阵被物理写回（列主序），随后 model.ApplyPhysicsAppend() 重算付与链
```

渲染侧的接线（宿主只需注入与设开关）：

```csharp
modelRenderer.Physics = physics;                    // 注入即完成物理后付与拓扑预计算
modelRenderer.PhysicsEnabled = true;                // S1 物理总开关
modelRenderer.GroundCollisionEnabled = true;        // S2 地面碰撞开关
modelRenderer.PostPhysicsAppendEnabled = true;      // S3 物理后付与开关
modelRenderer.PhysicsFrame = timeline.CurrentFrame; // tick 时钟由动画帧号驱动
```

## 与动画管线的帧序

```
VMD 采样 → MorphEvaluator → SkeletalModel.ApplyModelTransform（面板 移動/回転）
    → MmdIkSolver.Solve
    → UpdateWorldMatrices（軸制限 → 付与 → IK）
    → MMDPhysics.Update（SetKinematicTargets → Step → WriteBack）
    → SkeletalModel.ApplyPhysicsAppend（物理后付与）
    → 重算蒙皮矩阵 → 渲染（蒙皮后再施加渲染层根矩阵，即 拡大率）
```

物理 tick 时钟由**动画帧号**驱动（`tickTarget = floor(帧号 × 2)`，每 tick 1/60 动画秒）：
动画暂停 / 未加载时帧号不推进 ⇒ `advance = 0` ⇒ 物理冻结（MMD 行为：物理随动画走）。

**面板 移動/回転 在物理之上游，拡大率 在物理之外**：前者注入 全ての親 世界矩阵
⇒ kinematic 目标与 IK 自动跟随；后者只进渲染层根矩阵，物理世界完全看不见它
（刚体、关节、teleport 阈值、付与全跑在 bind 尺度）。见
[模型变换](../core/model-transform.md)。

## 确定性契约（DET）

- 物理状态是**动画帧号的纯函数**：同配置双跑（含开关序列）必须逐位一致。
- 步进顺序（predict → collide → solve → integrate）是确定性契约的一部分，不得重排；
  wall time 不参与子步决策（掉帧追赶上限由 tick 差值天然表达，`MaxCatchUp = 8`）。
- 乱序访问（时间轴拖动 / 发散）调用 `MMDPhysics.Reset` 兜底（snap + 清零 + reseed）。

> 设计与移植决策记录在 `docs/` 下的方案文档（B5/B6/S1~S3 阶段）；
> 行为验证见 `tests/MikuEngine.Physics.Tests`（SyncLayerTests / WorldTests / IntegrationTests）。

# World 与内核类型

确定性物理步进核心（`World`）、PMX 映射类型（`PhysicsTypes.cs`）、以及碰撞 / 约束求解单元。
全部对照 reze `physics/world.ts`、`types.ts`、`contact.ts`、`constraint.ts` 逐行移植。

## 源文件

| 文件 | 说明 |
|---|---|
| [World.cs](../../src/MikuEngine.Physics/World.cs) | `World` / `WindOptions` |
| [PhysicsTypes.cs](../../src/MikuEngine.Physics/PhysicsTypes.cs) | `RigidBodyDef` / `JointDef` / 枚举 |
| [ContactDetection.cs](../../src/MikuEngine.Physics/ContactDetection.cs) | 窄相碰撞 |
| [ConstraintSolver.cs](../../src/MikuEngine.Physics/ConstraintSolver.cs) | 约束求解 |

---

## World

```csharp
public sealed class World
{
    public World(Vector3 gravity);
    public void SetGravity(Vector3 g);
    public void SetWind(WindOptions? wind);
    public WindOptions? GetWind();
    public void Step(RigidBodyStore store, float dt,
                     ContactPool? contacts = null,
                     SixDofSpringConstraint[]? constraints = null,
                     SolverCache? cache = null);
    public int SolverIterations = 10;          // 接触/约束求解迭代次数
    public void InvalidateDampingCache();
}
```

### Step 的确定性阶段（不得重排）

1. **Predict**：重力 + 风叠加进加速度；动态体速度施加 `pow(1−damping, dt)` 阻尼
   （幂形式在大 PMX 阻尼值 0.99 下仍稳定；因子按 dt 键控缓存）。
   静态 / kinematic 体跳过（由同步层在步进前后从骨骼同步）。
2. **Collide**：`ContactDetection.FindContacts` 重算接触，写入池化的 `ContactPool`。
3. **Solve**：`ConstraintSolver.SolveConstraints` —— 关节 + 接触约束的顺序脉冲求解
   （velocity-only，warm starting 由 `ManifoldCache` 提供逐对冲量历史），随后
   split impulse 位移推送与冲量保存。`constraints` 非空时**必须**传配套 `SolverCache`
   （每份约束列表构建一次、跨步复用，否则抛 `ArgumentException`）。
4. **Integrate**：半隐式欧拉。防爆钳位：每步角速度 ≤ π/2（低惯量体的高冲量接触尖峰
   否则会一步转过 π 毁掉四元数积分）、线速度 ≤ 5 单位/步。
   无 sleeping —— 所有动态体每步全量积分（与 reze 一致）。

穿透恢复不做独立位移 pass：Baumgarte（CONTACT_ERP）直接折进接触速度行——
Bullet 2.75 默认 `splitImpulse = false`，PMX rig 是对着这个行为调的。

### 风（WindOptions）

```csharp
new WindOptions {
    Direction  = new Vector3(0, 1, 0),  // 赋值时归一化；零向量 = 关风
    Strength   = 20f,                   // 加速度，量纲与重力一致（内置重力 98）
    Turbulence = 0.3f,                  // 阵风深度，钳到 0–1（0 恒风；1 在 0↔2× 摆动）
    Frequency  = 0.35f,                 // 阵风频率（次/秒）
};
```

- 风与重力是同类项，每子步求和一次，per-body predict 循环不感知风的存在（零开销）。
- 阵风用两个不可公度正弦叠加（`sin(t) + sin(0.37t + 1.3)`），避免单一正弦的节拍器感；
  始终在 `1 ± turbulence` 内。
- 时钟走**模拟时间**：被 scrub 或离线导出的片段阵风与实况一致。

---

## 内核类型（PhysicsTypes.cs）

### RigidbodyShape / RigidbodyType

```csharp
public enum RigidbodyShape : byte { Sphere = 0, Box = 1, Capsule = 2 }

public enum RigidbodyType : byte { Static = 0, Dynamic = 1, Kinematic = 2 }
```

**PMX 模式映射不要 1:1 抄原始字节**——mode 2（物理 + 骨骼位置对齐）在加载层映射为
`Dynamic + Aligned = true`。把 mode 2 当 Kinematic 会冻住刚体（大多数现代裙装 rig 与
所有 `胸_回転` 胸部刚体）。

| PMX mode | 内核映射 |
|---|---|
| 0 跟随骨骼 | `Static`（锚点，跟随骨骼） |
| 1 物理 | `Dynamic` |
| 2 物理 + 骨骼对齐 | `Dynamic` + `Aligned = true`（骨骼只取旋转，位置每帧钉回动画骨骼） |

### RigidBodyDef

PMX 刚体 → 内核定义。字段与 PMX 一一对应（`Name/EnglishName/BoneIndex/Group/CollisionMask/
Shape/Size/ShapePosition/ShapeRotation/Mass/LinearDamping/AngularDamping/Restitution/
Friction`），另加：

- `Aligned`：mode-2 位置钉扎（见上）。
- `ModelGroupId`：S5 预留（多模型共享 world 时的模型组 id），Stage 1 未启用。

`static RigidBodyDef FromPmx(PmxRigidBody rb)` 完成映射；`ShapePosition/ShapeRotation`
是 PMX bind 姿态的模型空间值（rotation 为弧度欧拉角）。

### 内置地面（RigidBodyDef.CreateGround）

一个 500×1×500 的静态盒，**顶面 = 模型空间 y = 0**，让头发与裙摆停在"她自己的脚下"
而不是穿进去。要点：

- 无骨（`BoneIndex = -1`，所有骨骼同步循环跳过它）；
- 组掩码在 `MMDPhysics` 构造时被清零 —— 脱离通用碰撞对，由窄相的**专用平面 pass**
  与每一个动态刚体碰撞（无视组掩码，球/盒/胶囊全覆盖）；
- 它是**模型空间**的——这正是 `SetFloor` 开关存在的理由：y=0 在模型自身原点处，
  不是场景地面。角色站在舞台上、悬在空中、被 root motion 抬起时，地面跟着她走。

### JointDef

PMX 关节 → 内核定义（`Type` 保留 PMX 原始字节，内核不 switch 它——统一按 6DOF 弹簧约束处理）。
`Rotation*` 为弧度欧拉角；`SpringPosition / SpringRotation` 为弹簧刚度。经 `JointDef.FromPmx` 转换。

---

## 碰撞与约束单元

### ContactDetection / ContactPool

- 通用 pair pass：按 `CollisionGroup`（PMX 碰撞组 0..15，store 内转单比特掩码）与
  `WillCollideMask`（16 位"与哪些组碰撞"）筛选后，对球/盒/胶囊两两做窄相。
- 专用平面 pass：`store.GroundIndex` 指向的地面体对全部动态体（见上）。
- 接触点存入 `ContactPool`（池化复用，热路径零分配；`World.Step` 每次先 `Reset` 再填充）。

### ConstraintBuilder / ConstraintSolver

- `ConstraintBuilder.BuildConstraints(rigidbodies, joints)`：把每个 PMX 关节构建为
  `SixDofSpringConstraint`（6 自由度弹簧：3 平移 + 3 旋转限位 + 弹簧刚度），
  frameA/frameB 与 equilibrium 从关节 bind 姿态推出。
- `ConstraintSolver.SolveConstraints(store, constraints, cache, pool, dt, iterations, manifolds)`：
  顺序脉冲求解，`SolverCache` 缓存约束行的质量/柔度等步进不变量（按约束列表构建一次），
  `ManifoldCache` 缓存逐对接触冲量供 warm starting（收敛更快、堆叠更稳）。

调用方通常**不直接触碰**这一层——`MMDPhysics.Update` 已把 store/contacts/constraints/
cache 的生命周期串好；直接用 `World` 做非 MMD 仿真（自定义刚体游戏物理）时才需要自行维护
`ContactPool` / `SolverCache` 实例。

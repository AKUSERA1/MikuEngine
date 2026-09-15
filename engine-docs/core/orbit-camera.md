# OrbitCamera API

轨道相机（第三人称 / Turntable 风格）。相机位置通过球坐标描述：

```
           相机 (alpha, beta, radius)
           /  | \
          /   |  \  radius
         /    |   \
        /  beta|    \  ← pitch: 从 +Y 轴向下
       /      |     \
      /       |      \
     +--------*-------+
    /          O        \  ← target (相机看向这里)
   /            |         \
  +-------------|----------+
                |
               +Y
           ↑
          alpha = yaw (绕 +Y 旋转，左手正方向)
```

## 源文件

[OrbitCamera.cs](../../src/MikuEngine.Core/Camera/OrbitCamera.cs)

## 构造函数

```csharp
public OrbitCamera(
    float alpha,        // yaw (弧度)，0 = 看向 +Z，π/2 = 看向 +X
    float beta,         // pitch (弧度)，0 = 正上方，π/2 = 水平，接近 π = 正下方
    float radius,       // 相机到 target 的距离 (MMD 单位)
    Vector3 target,     // 相机看向的世界坐标点
    float fov = MathF.PI / 4f  // 45° 默认视锥
);
```

### 推荐初始值

| 场景 | alpha | beta | radius | target |
|---|---|---|---|---|
| 俯看地面原点 | π/4 (45°) | π/3 (60°) | 100 | (0,0,0) |
| 正对角色 | π/2 | π/2 | 60 | 角色中心 |
| 低角度仰拍 | 0 | π/4 (45°) | 80 | 角色中心 |

## 公开属性

| 属性 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `Alpha` | `float` | — | yaw（绕 +Y 轴旋转） |
| `Beta` | `float` | — | pitch |
| `Radius` | `float` | — | 相机到 target 的距离 |
| `Target` | `Vector3` | — | 相机看向的点 |
| `Fov` | `float` | π/4 | 垂直视锥角（弧度） |
| `Aspect` | `float` | 1.0 | 宽高比，**每帧必须更新** |
| `Near` | `float` | 自动 | 近裁剪面（根据 `Radius` 动态；VMD 驱动时按 `\|Distance\|`） |
| `Far` | `float` | 自动 | 远裁剪面（同上） |
| `MinZ` / `MaxZ` | `float` | 0.05 / 8000 | `Radius` 的钳制区间（`Zoom` 不会越界） |
| `AngularSensitivity` | `float` | **1.0** | Orbit 内部灵敏度（Engine 层已接管，保持 1.0） |
| `PanSensitivity` | `float` | **1.0** | Pan 内部灵敏度（同上） |
| `WheelPrecision` | `float` | **1.0** | Zoom 内部灵敏度（同上） |
| `VmdDriven` | `bool` | false | 只读。true = 视图由 VMD 相机姿态驱动（见下节） |

> `Near` / `Far` 是**派生值**：在 `Radius` 变化与矩阵输出时按
> `clamp(Radius/50, 0.3, 10)` / `clamp(Radius×12+600, 200, 8000)` 重算，
> 手工写入会被覆盖。约束详见 [coordinate-system.md §5](coordinate-system.md)。

### Beta 硬限制

`OrbitCamera.MinPitch = 0.001`，`OrbitCamera.MaxPitch = π - 0.001`。
防止 pitch = 0 或 π 时万向节锁死。

## 派生属性

```csharp
public Vector3 Position { get; }
// → 世界坐标下相机的位置 = Target + Radius × (sinβ·sinα, cosβ, sinβ·cosα)

public Vector3 GetEyePosition();
// → 实际拍摄点：VMD 驱动时返回 VMD 视点，否则等于 Position
```

> **着色用的相机位置必须用 `GetEyePosition()`**（高光 / rim / 球面贴图的视差量）。
> VMD 驱动时 orbit 的 `Position` 与真实拍摄点无关，用它会让这些着色项错位。

## 变换方法（Engine 层 OrbitInputController 调用）

```csharp
// 绕 target 旋转。deltaX = 水平位移 × RotationSensitivity，deltaY = 垂直位移 × RotationSensitivity
camera.Orbit(float deltaX, float deltaY);

// 平移 target。deltaX/deltaY 单位是「相机距离 Radius 的比例」
camera.Pan(float deltaX, float deltaY);

// 缩放距离。deltaY > 0 拉近，< 0 推远
camera.Zoom(float deltaY);
```

**注意**：这三个方法的灵敏度乘数都固定为 1.0（no-op）。
**调用方请使用 Engine 层的 OrbitInputController**，它在转发时已经乘好灵敏度。

## VMD 相机驱动模式

当 VMD 含相机动画时，把相机切到「VMD 驱动」态，视点与朝向由动效决定：

```csharp
camera.SetVmdDriven(true);                                    // 进入（备份当前 Fov）
camera.SetVmdPose(target, rotationEuler, distance, fov);      // 每帧喂采样结果
camera.SetVmdDriven(false);                                   // 退出（恢复 Fov）
```

| 成员 | 说明 |
|---|---|
| `VmdDriven` | true = 由 VMD 姿态驱动视图 |
| `SetVmdDriven(bool)` | 进入 / 退出驱动态。**幂等**；进入时备份 `Fov`，退出时恢复 |
| `SetVmdPose(target, rotationEuler, distance, fov)` | 写入本帧姿态（MMD 语义：看向 `target`，沿 `forward` 后退 `distance`——**负值 = 在注视点后方**；`fov` 为弧度） |

进入驱动态后的差异：

| 项 | 行为 |
|---|---|
| View 矩阵 | `Rᵀ · T(−eye)`，**不走 lookAt**（朝向由 euler 决定；`distance = 0` 时 lookAt 会退化成全零基） |
| `Fov` | 被逐帧改写；退出时恢复进入前的值 |
| Near / Far | 按 `\|Distance\|` 推算（orbit 的 `Radius` 与取景无关） |
| `Alpha` / `Beta` / `Radius` / `Target` | **不被触碰**，退出后 orbit 视角原样恢复 |
| `Position` | 仍是 orbit 位置，**不是**真实拍摄点；着色取 `GetEyePosition()` |
| 输入响应 | `OrbitInputController` 自动短路（见 [orbit-input-controller.md](../engine/orbit-input-controller.md)） |

> **不要**把 VMD 状态写进 `Alpha` / `Beta` / `Radius`：那会让退出驱动态后的自由视角跳变。
> 采样与轨道细节见 [animation/camera.md](animation/camera.md)。

## 矩阵输出方法

### ComputeViewProj（推荐）

```csharp
Span<float> viewProj = stackalloc float[16];
camera.Aspect = width / (float)height;
camera.ComputeViewProj(viewProj);
// viewProj 是列主序 float[16]，等价于 GLSL 的 proj * view
```

一步计算 View × Proj，并自动刷新 Near / Far。**推荐每帧只调用这一个**。

### WriteViewMatrix + WriteProjectionMatrix（高级场景）

```csharp
Span<float> view = stackalloc float[16];
Span<float> proj = stackalloc float[16];
camera.WriteViewMatrix(view);
camera.WriteProjectionMatrix(proj);
// 需要单独用 view 或 proj 时（比如阴影贴图级联）
```

## 完整使用示例

```csharp
using MikuEngine.Core.Camera;

// 创建
var camera = new OrbitCamera(
    alpha: MathF.PI / 4f,
    beta: MathF.PI / 3f,
    radius: 100f,
    target: Vector3.Zero,
    fov: MathF.PI / 4f);

// 每帧更新
camera.Aspect = viewportWidth / (float)viewportHeight;
Span<float> viewProj = stackalloc float[16];
camera.ComputeViewProj(viewProj);
// → 喂给 Render 层 UBO

// 手动调整视角（比如切换到角色特写）
camera.Target = characterCenter;
camera.Radius = 40f;
camera.Alpha = MathF.PI;      // 转到角色正面
camera.Beta = MathF.PI / 2f;  // 水平视角
```

## 与 Engine 层 OrbitInputController 的关系

```
平台层（GLFW / Android）
   ↓ 原始事件
OrbitInputController          ← 灵敏度在这里：RotationSensitivity / PanSensitivity / ...
   ↓ 已乘好的 delta
OrbitCamera.Orbit / Pan / Zoom  ← sensitivities = 1.0（no-op）
```

**不要同时改两处灵敏度**。OrbitCamera 的三个 sensitivity 字段保持 1.0，
所有灵敏度只在 OrbitInputController 上调整；改了 Core 侧不会产生任何效果。

## 为什么不自己写 View / Proj 矩阵

用 `System.Numerics.Matrix4x4.CreateLookAt`（右手系）或
`CreatePerspectiveFieldOfView` 手工拼装时，需要额外处理三件事：

1. 改方向参数（左手系下前向为 +Z）
2. 行主序 → 列主序的排布
3. 换成 GLES 风格的投影公式（与 `CreatePerspectiveFieldOfView` 的系数不同）

`OrbitCamera` 已内置这些处理。手写的机会成本高，且方向 / 转置 / 投影公式都容易踩坑
（后果见 [coordinate-system.md](coordinate-system.md) 文末）。

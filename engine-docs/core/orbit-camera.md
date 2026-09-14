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
| `Near` | `float` | 自动 | 近裁剪面（根据 Radius 动态） |
| `Far` | `float` | 自动 | 远裁剪面（根据 Radius 动态） |
| `AngularSensitivity` | `float` | **1.0** | Orbit 内部灵敏度（Engine 层已接管，保持 1.0） |
| `PanSensitivity` | `float` | **1.0** | Pan 内部灵敏度（同上） |
| `WheelPrecision` | `float` | **1.0** | Zoom 内部灵敏度（同上） |

### Beta 硬限制

`OrbitCamera.MinPitch = 0.001`，`OrbitCamera.MaxPitch = π - 0.001`。  
防止 pitch=0 或 π 时万向节锁死。

## 派生属性

```csharp
public Vector3 Position { get; }
// → 世界坐标下相机的位置 = Target + Radius × (sinβ·sinα, cosβ, sinβ·cosα)
```

## 变换方法（Engine 层 OrbitInputController 调用）

```csharp
// 绕 target 旋转。deltaX = 水平位移 × RotationSensitivity，deltaY = 垂直位移 × RotationSensitivity
camera.Orbit(float deltaX, float deltaY);

// 平移 target。deltaX/deltaY 单位是"相机距离 Radius 的比例"
camera.Pan(float deltaX, float deltaY);

// 缩放距离。deltaY > 0 拉近，< 0 推远
camera.Zoom(float deltaY);
```

**注意**：这三个方法的灵敏度乘数都设为 1.0（no-op）。  
**调用方请使用 Engine 层的 OrbitInputController**，它会在转发时已经乘好灵敏度。

## 矩阵输出方法

### ComputeViewProj（推荐）

```csharp
Span<float> viewProj = stackalloc float[16];
camera.Aspect = width / (float)height;
camera.ComputeViewProj(viewProj);
// viewProj 是列主序 float[16]，等价于 GLSL 的 proj * view
```

一步计算 View × Proj，自动刷新 Near/Far。**推荐每帧只调用这一个**。

### WriteViewMatrix + WriteProjectionMatrix（高级场景）

```csharp
Span<float> view = stackalloc float[16];
Span<float> proj = stackalloc float[16];
camera.WriteViewMatrix(view);
camera.WriteProjectionMatrix(proj);
// 如果你需要单独用 view 或 proj（比如阴影贴图级联）
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
camera.Alpha = MathF.PI;   // 转到角色正面
camera.Beta = MathF.PI / 2f; // 水平视角
```

## 与 Engine 层 OrbitInputController 的关系

```
平台层（GLFW / Android）
   ↓ 原始事件
OrbitInputController          ← 灵敏度在这里：RotationSensitivity / PanSensitivity / ...
   ↓ 已乘好的 delta
OrbitCamera.Orbit / Pan / Zoom  ← sensitivities = 1.0（no-op）
```

**不要同时改两处灵敏度**。OrbitCamera 的三个 sensitivity 字段保持 1.0，所有灵敏度只在 OrbitInputController 上调整。

## 为什么不自己写 View 矩阵

你可以直接用 `System.Numerics.Matrix4x4.CreateLookAt`（右手系），然后：
1. 改参数（forward = Z.backward 才能匹配左手）
2. 转置（行主序 → 列主序）
3. 再拼 Proj（GLES 公式 ≠ Matrix4x4.CreatePerspectiveFieldOfView）

**OrbitCamera 帮你全做了这些**。手写的机会成本高、容易踩方向/转置/投影公式的坑。

# OrbitInputController API

跨平台轨道相机输入控制器。**调用方只需传入一个 `bool enabled` 开关，平台层负责最薄的一层事件转发。**

## 设计哲学

```
┌─────────────────────────────────────────────────────┐
│ 平台层（必须存在一层，无法绕过）                       │
│ GLFW 回调 / Android MotionEvent / iOS Touch          │
│  └─ 只做：原生事件 → OnPointerDown/Move/Up / OnScroll │
├─────────────────────────────────────────────────────┤
│ ★ OrbitInputController（Engine 层，纯 C# 标准库）    │
│  手势识别 + enabled 开关 + 灵敏度                     │
├─────────────────────────────────────────────────────┤
│ OrbitCamera（Core 层，纯数学）                        │
│  Orbit(dx, dy) / Pan(dx, dy) / Zoom(delta)           │
└─────────────────────────────────────────────────────┘
```

Engine 层是**唯一**应该调整灵敏度的地方。Core 层 OrbitCamera 的三个 sensitivity 字段保持 `1.0`（no-op）。

## 源文件

[OrbitInputController.cs](../../src/MikuEngine.Engine/OrbitInputController.cs)

## 构造函数

```csharp
var input = new OrbitInputController(OrbitCamera camera, bool enabled = true);
```

## Enabled 开关

```csharp
input.Enabled = true;   // 开启输入响应（默认）
input.Enabled = false;  // 全部 OnXXX 调用直接返回，相机不动
```

场景举例：
- 游戏运行时：启用
- UI 弹窗 / 场景切换动画期间：禁用
- 做相机插值动画（timeline）期间：禁用，动画完了再启用

## 手势映射

| 输入 | 手势 | 触发的相机操作 |
|---|---|---|
| 鼠标左键 + 拖拽 | Orbit | `camera.Orbit(-dx, -dy)` |
| 鼠标右键 + 拖拽 | Pan | `camera.Pan(dx, dy)` |
| 鼠标滚轮 ↑↓ | Zoom | `camera.Zoom(-deltaY)` |
| 单指 + 拖拽 | Orbit | 同鼠标左键 |
| 双指 + 中心平移 | Pan | 同鼠标右键 |
| 双指 + 捏合/张开 | Zoom | `Zoom((1 - ratio) * sensitivity)` |

### 鼠标 vs 触控的 pointerId 约定

| pointerId | 含义 |
|---|---|
| `0` | 鼠标（永远 0） |
| `1, 2, ...` | 触控点（Android `MotionEvent.getPointerId(i)` 的返回值） |

Engine 层**不依赖这个约定**——它只看按下/移动/抬起的时序和活跃指针数量——但平台层应该一致使用。

## 灵敏度配置

四个灵敏度属性全部 public 可写，构造后随时调优：

```csharp
var input = new OrbitInputController(camera, enabled: true)
{
    RotationSensitivity = 0.0025f,   // 默认 0.0025 rad/px ≈ 0.14°/px
    PanSensitivity      = 0.003f,    // 默认 0.003 × Radius 单位/px
    WheelZoomSensitivity = 1.0f,     // 默认 1.0 单位/滚轮格
    PinchZoomSensitivity = 50.0f,    // 默认 50 单位 / 捏合比例变化
};
```

### 灵敏度默认值对照表（×100 修正后）

| 属性 | 旧默认（两层相乘） | 新默认（Engine 唯一入口） | 有效效果 |
|---|---|---|---|
| `RotationSensitivity` | 0.005 × 0.005 | **0.0025** | 1 像素 ≈ 0.14° 旋转 |
| `PanSensitivity` | 0.15 × 0.0002 | **0.003** | 1 像素 ≈ 0.3 单位（R=100）平移 |
| `WheelZoomSensitivity` | 1.0 × 0.01 | **1.0** | 1 滚轮格 = 1 单位 |
| `PinchZoomSensitivity` | 0.5 | **50.0** | 双指距离变化 10% → Radius 变 5 单位 |

### 各平台可能的预设值

```csharp
#if ANDROID
    var input = new OrbitInputController(camera)
    {
        RotationSensitivity = 0.004f,   // 触控屏需要更高（手指拖动距离长）
        PanSensitivity      = 0.005f,
        PinchZoomSensitivity = 80.0f,
    };
#else
    var input = new OrbitInputController(camera);  // Desktop 用默认值
#endif
```

### 实时 UI 调优（Inspector 风格）

```csharp
// 比如用 Dear ImGui 或类似工具
input.RotationSensitivity = ImGui.SliderFloat("旋转灵敏度", input.RotationSensitivity, 0.0005f, 0.02f);
input.PanSensitivity      = ImGui.SliderFloat("平移灵敏度", input.PanSensitivity, 0.0005f, 0.02f);
input.WheelZoomSensitivity = ImGui.SliderFloat("缩放灵敏度", input.WheelZoomSensitivity, 0.1f, 10f);
```

## 平台层调用的输入入口

**平台层必须**把原生 API 事件转发到这四个方法。详细代码 → [platform-integration/](../platform-integration/) 章节。

### 方法签名

```csharp
public void OnPointerDown(int pointerId, float x, float y,
    PointerButton button = PointerButton.None);

public void OnPointerMove(int pointerId, float x, float y);

public void OnPointerUp(int pointerId);

public void OnScroll(float deltaY);
```

### PointerButton 枚举

```csharp
public enum PointerButton
{
    None,    // 触控 / 未指定
    Left,    // 鼠标左键
    Right,   // 鼠标右键
    Middle,  // 鼠标中键
}
```

### 转发示例（GLFW）

```csharp
glfw.SetMouseButtonCallback(hwnd, (w, btn, action, mods) =>
{
    glfw.GetCursorPos(hwnd, out double x, out double y);
    if (action == InputAction.Press)
        input.OnPointerDown(0, (float)x, (float)y, ...映射到 PointerButton...);
    else if (action == InputAction.Release)
        input.OnPointerUp(0);
});
glfw.SetCursorPosCallback(hwnd, (w, x, y) => input.OnPointerMove(0, (float)x, (float)y));
glfw.SetScrollCallback(hwnd, (w, xo, yo) => input.OnScroll((float)yo));
```

### 转发示例（Android `View.OnTouchListener`）

```csharp
view.Touch += (v, e) => {
    switch (e.ActionMasked) {
        case MotionEventActions.Down:
            input.OnPointerDown(e.ActionIndex, e.GetX(), e.GetY()); break;
        case MotionEventActions.Move:
            for (int i = 0; i < e.PointerCount; i++)
                input.OnPointerMove(e.GetPointerId(i), e.GetX(i), e.GetY(i)); break;
        case MotionEventActions.Up:
            input.OnPointerUp(e.ActionIndex); break;
        case MotionEventActions.PointerDown:
            input.OnPointerDown(e.ActionIndex,
                e.GetX(e.ActionIndex), e.GetY(e.ActionIndex)); break;
        case MotionEventActions.PointerUp:
            input.OnPointerUp(e.ActionIndex); break;
    }
};
```

## 底层实现说明（给想改手势逻辑的人）

### 指针追踪

用 `Dictionary<int, Vector2> _pointers` 记录所有活跃指针的最新位置。
- `OnPointerDown` 时加入字典
- `OnPointerMove` 时更新
- `OnPointerUp` 时移除

### 手势状态机

```
指针数量变化时重置：
  0 → 1    单指模式启动
  1 → 2    双指模式启动（记录初始捏合距离、中心位置）
  2 → 1    回到单指模式
  2 → 0    全部结束

OnPointerMove 时判断：
  鼠标（pointerId=0）→ 看 _leftMouseDown / _rightMouseDown
  1 个活跃指针         → 单指 Orbit
  2 个活跃指针         → Pan（中心平移）+ Zoom（捏合比例变化）
```

### 双指捏合的 Zoom 累积

双指模式下，每帧：
1. 计算当前两指距离
2. 算比例 `ratio = currentDistance / initialDistance`
3. 应用 `Zoom((1 - ratio) * PinchZoomSensitivity)`
4. **重置** `initialDistance = currentDistance`（让缩放连续不累积）

这样捏合越远，Radius 变化越多，比例合理。

## 不需要用 OrbitInputController 的场景

```csharp
// 1. 做动画 / 插值，完全手动控制相机
camera.Alpha = MathF.Sin(time * speed) * amplitude;
camera.Beta = MathF.PI / 3f;
camera.Radius = 100f;

// 2. 写测试
var c = new OrbitCamera(...);
c.Orbit(0.0025f, 0);   // 直接传 delta（Core sensitivity = 1.0，不缩放）
```

两种情况都直接操作 Core 层，不需要 Engine。

# Android 集成

🚧 **待实现**。

架构与桌面完全相同，只是：
1. 窗口宿主从 GLFW 换成 `Android.App.Activity` + `AndroidGameView`（EGL 上下文）
2. 输入绑定从 GLFW 回调换成 `View.OnTouchListener`

## 预期代码骨架（伪代码）

```csharp
// Activity.OnCreate
var glView = new AndroidGameView(this);
glView.Context = new AndroidRenderingContext();
glView.SetEGLContextClientVersion(3, 1);

var camera = new OrbitCamera(...);
var input  = new OrbitInputController(camera, enabled: true);

glView.Touch += (v, e) => {
    switch (e.ActionMasked) {
        case MotionEventActions.Down:
            input.OnPointerDown(e.ActionIndex, e.GetX(), e.GetY()); break;
        case MotionEventActions.Move:
            for (int i = 0; i < e.PointerCount; i++)
                input.OnPointerMove(e.GetPointerId(i), e.GetX(i), e.GetY(i)); break;
        case MotionEventActions.Up:
            input.OnPointerUp(e.ActionIndex); break;
    }
};

glView.Render += () => {
    gl.MakeCurrent();
    device.BeginFrame();
    camera.Aspect = glView.Width / (float)glView.Height;
    camera.ComputeViewProj(viewProjSpan);
    grid.Draw(viewProjSpan);
    gl.SwapBuffers();
};
```

## 与桌面的统一点

| 层 | 桌面 GLFW | Android | 统一吗 |
|---|---|---|---|
| Core (OrbitCamera) | ✅ 完全相同 | ✅ 完全相同 | ✅ |
| Engine (OrbitInputController) | ✅ 完全相同 | ✅ 完全相同 | ✅ |
| Render (GlesDevice + GlesGridRenderer) | ✅ GLSL 310 es | ✅ GLSL 310 es | ✅ |
| 平台层转发 | GLFW 回调 → OnXXX | MotionEvent → OnXXX | 架构相同 |
| 窗口 / GL 上下文 | GLFW + GL.GetApi | AndroidGameView + EGL | **不同** |

## 依赖项（Android 目标）

需要在 Android 项目里替换 Silk.NET.Windowing.Glfw 为：
- `Xamarin.Android` 自带的 EGL 支持
- 或 `Silk.NET.OpenGL`（跨平台入口不变）

具体包版本待定。

## 下一步行动

Android 集成实现后：
1. 本章移除 🚧 标记
2. 替换伪代码为真实代码
3. 补充 EGL 上下文创建 / SurfaceView 切换 / 生命周期（OnPause / OnResume）等细节

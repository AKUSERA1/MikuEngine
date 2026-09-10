# 平台集成指南

Engine 层（OrbitInputController）是**平台无关**的。但原生输入 API 必须有一层薄转发——因为 .NET 没有跨平台自动分发输入的魔法。

这一层**极其薄**，只需把原生事件翻译成四个方法调用：

```
原生 API 事件 → OrbitInputController.OnPointerDown / OnPointerMove / OnPointerUp / OnScroll
```

## 文档列表

| 文档 | 说明 |
|---|---|
| [desktop-glfw.md](desktop-glfw.md) | 桌面 GLFW：窗口创建 + 输入绑定 |
| [android.md](android.md) | Android：Activity + View.OnTouchListener + EGL 上下文 |

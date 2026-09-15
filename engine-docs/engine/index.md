# Engine 模块 — MikuEngine.Engine

Engine 层介于 Core 和平台层之间，承担**跨平台业务逻辑**：

- 输入事件识别与手势分发
- 灵敏度调整（Core 层的灵敏度入口已归到这里）
- （规划中）场景图、动画调度

Engine 层不依赖任何平台 API（Silk.NET、Android、iOS），只依赖 Core。

## 文档列表

| 文档 | 说明 |
|---|---|
| [orbit-input-controller.md](orbit-input-controller.md) | 跨平台输入控制器完整 API |
| 场景图调度 | 未实现 |
| 骨骼动画调度 | 未实现 |

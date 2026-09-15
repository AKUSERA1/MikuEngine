# 安装与依赖

## 目录结构

```
MikuEngine.slnx
├── src/
│   ├── MikuEngine.Core/           ← 纯数学 / 纯数据，零依赖（解析 · 模型 · 动画 / IK / 赋予 · 模型变换）
│   ├── MikuEngine.Physics/        ← MMD 物理内核（刚体 / 关节 / 碰撞 / 约束，仅依赖 Core）
│   ├── MikuEngine.Engine/         ← 跨平台输入控制
│   └── MikuEngine.Render.GLES/    ← OpenGL ES 3.1 渲染（依赖 Core + Physics）
├── tests/
│   ├── MikuEngine.Core.Tests/     ← 解析 / 模型 / 动画 / IK / 赋予 / 模型变换
│   └── MikuEngine.Physics.Tests/  ← 内核 / 碰撞 / 约束 / 同步层 / 集成
└── engine-docs/                   ← 本文档所在位置
```

## 目标框架

所有项目统一 **net10.0**，`LangVersion=latest`；`Physics` / `Render.GLES` 开 `AllowUnsafeBlocks`。
Android 目标（`net10.0-android`）尚未加入。

## 包引用（集中管理）

版本号集中在项目根目录的 `Directory.Packages.props`，子项目只需 `PackageReference Include="..."`：

| 包 | 版本 | 用途 |
|---|---|---|
| `Silk.NET.OpenGL` | 2.23.0 | OpenGL 函数入口（Android 自动加载 GLES，桌面加载 GL） |
| `Silk.NET.Windowing` / `Silk.NET.Windowing.Glfw` | 2.23.0 | 窗口抽象 + 桌面 GLFW 实现 |
| `Silk.NET.GLFW` | 2.23.0 | GLFW 原生 API（输入回调绑定用） |
| `SixLabors.ImageSharp` | 3.1.12 | 纹理解码 |
| `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio` / `coverlet.collector` | 17.14.1 / 2.9.3 / 3.1.4 / 6.0.4 | 单元测试 |

> 输入事件请用 `Silk.NET.GLFW` 原生回调，不要走 `Silk.NET.Input.Glfw`——
> 当前 `IWindow` 接口不暴露输入事件（见 [桌面 GLFW 集成](../platform-integration/desktop-glfw.md)）。

## 子项目依赖关系

```
宿主应用 ──▶ MikuEngine.Engine
              ├──▶ MikuEngine.Core
              ├──▶ MikuEngine.Physics ──▶ MikuEngine.Core
              └──▶ MikuEngine.Render.GLES
                     ├──▶ MikuEngine.Core
                     └──▶ MikuEngine.Physics
```

依赖方向单向，无环。**Core 零依赖是硬约束**（保证 Core 可直接跑在单元测试 / Android / WebAssembly 上）。

## 构建与单测

```bash
dotnet build MikuEngine.slnx
dotnet test  MikuEngine.slnx     # 单元测试
```

引擎本身不提供可执行入口——需要在宿主应用中自行创建窗口并驱动渲染循环，
最小示例见 [最小可运行窗口](first-window.md)、桌面集成见
[桌面 GLFW 集成](../platform-integration/desktop-glfw.md)。

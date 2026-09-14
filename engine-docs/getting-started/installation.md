# 安装与依赖

## 目录结构

```
MikuEngine.slnx
├── src/
│   ├── MikuEngine.Core/           ← 纯数学 / 纯数据，零依赖（解析 · 模型 · 动画/IK/付与 · 模型变换）
│   ├── MikuEngine.Physics/        ← MMD 物理内核（reze 逐式移植，仅依赖 Core）
│   ├── MikuEngine.Engine/         ← 跨平台输入控制
│   └── MikuEngine.Render.GLES/    ← OpenGL ES 3.1 渲染（依赖 Core + Physics）
├── samples/
│   └── MikuEngine.Demo/           ← GLFW 桌面测试宿主
├── tests/
│   ├── MikuEngine.Core.Tests/     ← 解析 / 模型 / 动画 / IK / 付与 / 模型变换
│   └── MikuEngine.Physics.Tests/  ← 内核 / 碰撞 / 约束 / 同步层 / 集成
├── tools/                         ← 取证脚本（PMX 骨表 / 尺度探针 / VMD 扫描 / PE 常量 dump）
└── engine-docs/                   ← 本文档所在位置
```

## 目标框架

所有项目统一 **net10.0**，`LangVersion=latest`；`Physics` / `Render.GLES` / `Demo` 开 `AllowUnsafeBlocks`。  
Android 平台：后续会增加 `net10.0-android` 目标（暂未实现）。

## NuGet 包引用（集中管理）

版本号集中在项目根目录的 `Directory.Packages.props`，子项目只需 `PackageReference Include="..."`：

| 包 | 版本 | 用途 |
|---|---|---|
| `Silk.NET.OpenGL` | 2.23.0 | OpenGL 函数入口（Android 自动加载 GLES，桌面加载 GL） |
| `Silk.NET.Windowing` / `Silk.NET.Windowing.Glfw` | 2.23.0 | 窗口抽象 + 桌面 GLFW 实现 |
| `Silk.NET.GLFW` | 2.23.0 | GLFW 原生 API（输入回调绑定用） |
| `Silk.NET.Input` / `Silk.NET.Input.Glfw` | 2.23.0 | Demo 引用；引擎本体直接用 GLFW 原生回调 |
| `SixLabors.ImageSharp` | 3.1.12 | 纹理解码 |
| `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio` / `coverlet.collector` | 17.14.1 / 2.9.3 / 3.1.4 / 6.0.4 | 测试 |

## 子项目依赖关系

```
MikuEngine.Demo ──▶ MikuEngine.Engine
                      ├──▶ MikuEngine.Core
                      ├──▶ MikuEngine.Physics ──▶ MikuEngine.Core
                      └──▶ MikuEngine.Render.GLES
                             ├──▶ MikuEngine.Core
                             └──▶ MikuEngine.Physics
```

依赖方向单向，无环。Core 零依赖是硬约束（WebAssembly / Android / 单测都能直接跑）。

## 快速验证

```bash
dotnet build MikuEngine.slnx
dotnet test  MikuEngine.slnx                                          # 单测
dotnet run --project samples/MikuEngine.Demo                          # 桌面窗口
dotnet run --project samples/MikuEngine.Demo -- --smoke               # 无人值守渲染自检
```

应该能看到：
- 控制台输出 GL version / GL renderer（以及 PMX / 物理内核 / 动画层的加载信息）
- 一个 1100×760 窗口，显示模型 + 地面网格（雾色淡出）
- 鼠标左键旋转 / 右键平移 / 滚轮缩放生效

运行时的完整键位见 [engine-docs/README.md](../README.md) 的「Demo 交互速查」。

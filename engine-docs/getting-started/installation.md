# 安装与依赖

## 目录结构

```
MikuEngine.slnx
├── src/
│   ├── MikuEngine.Core/           ← 纯数学 / 纯数据，零依赖
│   ├── MikuEngine.Engine/         ← 跨平台输入控制
│   ├── MikuEngine.Physics/        ← 物理引擎（占位）
│   └── MikuEngine.Render.GLES/    ← OpenGL ES 3.1 渲染
├── samples/
│   └── MikuEngine.Demo/           ← GLFW 桌面测试宿主
├── tests/
└── engine-docs/                   ← 本文档所在位置
```

## 目标框架

所有项目统一 **net10.0**，`LangVersion=latest`。  
Android 平台：后续会增加 `net10.0-android` 目标（暂未实现）。

## NuGet 包引用（集中管理）

版本号集中在项目根目录的 `Directory.Packages.props`，子项目只需 `PackageReference Include="..."`：

| 包 | 版本 | 用途 |
|---|---|---|
| `Silk.NET.OpenGL` | 2.23.0 | OpenGL 函数入口（Android 自动加载 GLES，桌面加载 GL） |
| `Silk.NET.Windowing` | 2.23.0 | 窗口抽象 |
| `Silk.NET.Windowing.Glfw` | 2.23.0 | 桌面 GLFW 实现 |
| `Silk.NET.GLFW` | 2.23.0 | GLFW 原生 API（用于输入回调绑定） |
| `Silk.NET.Input` / `Silk.NET.Input.Glfw` | 2.23.0 | 可选（引擎直接用 GLFW 原生回调，未强依赖） |
| `SixLabors.ImageSharp` | 3.1.12 | 纹理解码 |
| `Microsoft.NET.Test.Sdk` + `xunit` | — | 测试 |

## 子项目依赖关系

```
MikuEngine.Demo
  └── MikuEngine.Engine
        ├── MikuEngine.Core
        ├── MikuEngine.Physics
        └── MikuEngine.Render.GLES
```

## 快速验证

```bash
dotnet build MikuEngine.slnx
dotnet run --project samples/MikuEngine.Demo/MikuEngine.Demo.csproj
```

应该能看到：
- 控制台输出 GL version / GL renderer
- 一个 960×600 窗口，显示地面网格（雾色淡出）
- 鼠标左键旋转 / 右键平移 / 滚轮缩放生效

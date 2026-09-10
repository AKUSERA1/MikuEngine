# Render.GLES 模块 — MikuEngine.Render.GLES

OpenGL ES 3.1 渲染层。

## 设计原则

1. **单 GLES 后端** — 桌面自动加载 OpenGL（Silk.NET.OpenGL 统一入口）
2. **列主序 UBO** — 与 Core 层 OrbitCamera 输出的 `float[16]` 零转置直通
3. **显式 float 字段布局** — Uniform 结构体不用 `Matrix4x4`，用 `float M00...M33`

## 文档列表

| 文档 | 说明 |
|---|---|
| [gles-device.md](gles-device.md) | GL 上下文管理、资源生命周期 |
| [grid-renderer.md](grid-renderer.md) | 网格地面渲染 |
| PMX 模型渲染 🚧 | 待实现 |
| Toon / PBR Shader 🚧 | 待实现 |

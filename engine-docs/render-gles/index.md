# Render.GLES 模块 — MikuEngine.Render.GLES

OpenGL ES 3.1 渲染层。

## 设计原则

1. **单 GLES 后端**——桌面自动加载 OpenGL（Silk.NET.OpenGL 统一入口）
2. **列主序数据零转置**——Core 层 `OrbitCamera` 输出的 `float[16]` 直接进 UBO / Uniform，
   不做任何重排
3. **蒙皮矩阵用 SSBO**——数百至上千根骨骼会撑爆 UBO 的最小 16 KB 限制，SSBO 最小保证 128 MB
4. **两套矩阵表示并存**——逐帧 UBO 用 `Matrix4x4` 作 16 float 载体（列主序顺序线性填入），
   网格地面 `GridUniform` 用显式 `float[16]` 字段；蒙皮矩阵与模型根矩阵走 `Matrix4x4`
   原样上传（`glUniformMatrix4fv(transpose = false)`），靠转置抵消约定。
   两者都无需手动转置（见 [coordinate-system.md §3](../core/coordinate-system.md)）

## 文档列表

| 文档 | 说明 |
|---|---|
| [gles-device.md](gles-device.md) | GL 上下文管理、资源生命周期、Shader / VBO / UBO 工具方法 |
| [grid-renderer.md](grid-renderer.md) | 网格地面渲染（雾色淡出） |
| [gles-model-renderer.md](gles-model-renderer.md) | PMX 模型渲染：顶点格式、材质段、轮廓线、自阴影采样、模型变换根矩阵 |
| [gles-shadow-renderer.md](gles-shadow-renderer.md) | 自阴影 Z 图 + 床影：紧视锥 / texel snapping / 多风格 |
| [gles-support.md](gles-support.md) | GlesTextureLibrary · PmxFileResolver · GlesSkinMatricesBuffer · GlesMorphBuffer · GlesDebugOverlay |

# Core 模块 — MikuEngine.Core

Core 层是**纯数学 / 纯数据**，零平台依赖、零渲染 API 依赖。
因此 Core 层可以直接跑在**单元测试、Android、iOS、WebAssembly** 上，无需任何条件编译。

## 设计原则

1. **左手坐标系 + Y-up**，与 MikuMikuDance 原生约定一致
2. **矩阵输出列主序 `float[16]`**，GLSL `mat4` 直接读取，无需转置
3. **投影使用 GLES 3.1 默认风格**（z_clip ∈ [-w, +w]），不用 Vulkan / WebGPU 的 [0, w]
4. **不内置灵敏度缩放**——Core 层只做纯变换，灵敏度归 Engine 层（OrbitInputController）

## 文档列表

| 文档 | 说明 |
|---|---|
| [coordinate-system.md](coordinate-system.md) | **必读**——坐标系约定、矩阵布局、投影公式、两套矩阵表示 |
| [orbit-camera.md](orbit-camera.md) | 轨道相机完整 API |
| [pmx-parser.md](pmx-parser.md) | PmxParser · PmxModel · SkeletalModel · SkeletalModelConverter |
| [mmd-math.md](mmd-math.md) | MmdMath（YXZ 欧拉角 ↔ 四元数）· QuatMath · Mat4 · ModelRootTransform |
| [model-transform.md](model-transform.md) | 模型变换（移动 / 旋转 / 缩放倍率）：全ての親 注入 · 渲染层根矩阵 · 缩放与物理解耦 |
| [animation/index.md](animation/index.md) | 动画子系统总览（VMD 加载 → 播放 → 混合 → 可见性 → 相机 → 外部親 → 生命周期） |

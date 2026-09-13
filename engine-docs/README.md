# MikuEngine 使用文档

> 跨平台原生 MMD（MikuMikuDance）引擎。主目标平台 Android（OpenGL ES 3.1），桌面使用 GLFW 作为测试宿主。

本文档面向**调用方**（游戏/应用开发者），描述如何安装、集成、使用 MikuEngine。  
开发日志 / 架构决策记录请参阅项目根目录下的 [docs/](../docs)。

---

## 模块分层

```
┌──────────────────────────────────────────────────────────────────────┐
│  平台层                                                               │
│  Demo (GLFW) · Android Activity · iOS UIView                         │
│  职责：窗口/上下文 + 原生输入 API → Engine 层一行转发                  │
├──────────────────────────────────────────────────────────────────────┤
│  Engine 层  MikuEngine.Engine                                         │
│  OrbitInputController（跨平台手势识别 + 灵敏度）                        │
├──────────────────────────────────────────────────────────────────────┤
│  Core 层  MikuEngine.Core                                            │
│  OrbitCamera · MmdMath · PmxParser · SkeletalModel ·                  │
│  Animation（VMD/混合/IK/付与）                                         │
│  职责：纯数学 / 纯数据，零平台依赖                                        │
├──────────────────────────────────────────────────────────────────────┤
│  Physics 层  MikuEngine.Physics                                       │
│  MMDPhysics（骨骼同步层） · World · RigidBodyStore ·                   │
│  ContactDetection · ConstraintSolver（reze 物理逐式移植）               │
├──────────────────────────────────────────────────────────────────────┤
│  Render 层  MikuEngine.Render.GLES                                    │
│  GlesDevice · GlesGridRenderer · GlesModelRenderer ·                  │
│  GlesShadowRenderer · GlesTextureLibrary · GlesSkinMatricesBuffer     │
│  职责：OpenGL ES 3.1 渲染（GLES 后端唯一，桌面自动加载 GL）             │
└──────────────────────────────────────────────────────────────────────┘
```

依赖方向（单向）：`Core ← Engine ← Render.GLES`，`Core ← Physics ← Render.GLES`，平台层引用 Engine。

---

## 快速导航

### ① 入门

| 文档 | 说明 |
|---|---|
| [安装与依赖](getting-started/installation.md) | NuGet 包引用、目标框架、平台依赖 |
| [跑通第一个 Demo](getting-started/first-window.md) | 最小可运行窗口 + 相机 + 网格地面 |

### ② Core 模块

| 文档 | 说明 |
|---|---|
| [坐标系约定](core/coordinate-system.md) | 左手 / Y-up / +Z-forward / 列主序矩阵，**必读** |
| [OrbitCamera API](core/orbit-camera.md) | 轨道相机的构造、属性、矩阵输出方法 |
| [PMX 模型解析](core/pmx-parser.md) | PmxParser · PmxModel · SkeletalModel · SkeletalModelConverter |
| [MMD 数学工具](core/mmd-math.md) | MmdMath（YXZ 欧拉角 ↔ 四元数）· QuatMath · Mat4 |
| [动画子系统](core/animation/index.md) | VMD 加载 · 播放 · 多轨混合 · 表示枠可见性 · CCD IK · 付与 · 生命周期 |
| [CCD IK 求解器](core/animation/ik.md) | MmdIkSolver · MmdIkChain · 角度限制 / ReverseClamp |
| [付与变换](core/animation/append-transform.md) | 付与（Append Transform）· 局部付与 · 物理后付与 |

### ③ Engine 模块

| 文档 | 说明 |
|---|---|
| [OrbitInputController API](engine/orbit-input-controller.md) | 跨平台手势识别：鼠标 / 触控 / 捏合，灵敏度调整 |

### ④ Render.GLES 模块

| 文档 | 说明 |
|---|---|
| [GlesDevice](render-gles/gles-device.md) | GL 上下文管理、帧缓冲状态、资源生命周期 |
| [GlesGridRenderer](render-gles/grid-renderer.md) | 网格地面渲染（雾色淡出） |
| [GlesModelRenderer](render-gles/gles-model-renderer.md) | PMX 模型渲染管线：蒙皮、材质段、轮廓线、自阴影 |
| [GlesShadowRenderer](render-gles/gles-shadow-renderer.md) | 自阴影 Z 图 + 床影：紧视锥 / texel snapping / 多风格软影 |
| [渲染辅助组件](render-gles/gles-support.md) | GlesTextureLibrary · GlesSkinMatricesBuffer · GlesDebugOverlay |

### ⑤ Physics 模块

| 文档 | 说明 |
|---|---|
| [物理引擎总览](physics/index.md) | 模块结构 · 快速上手 · 帧序 · 确定性契约 |
| [MMDPhysics 骨骼同步层](physics/mmd-physics.md) | Update 三相位 · 开关（S1/S2/S3）· Reset · 抖动阻尼 |
| [World 与内核类型](physics/world.md) | 确定性步进 · 风 · 刚体/关节定义 · 内置地面 · 碰撞与求解 |

### ⑥ 平台集成

| 文档 | 说明 |
|---|---|
| [桌面 GLFW 集成](platform-integration/desktop-glfw.md) | 窗口创建、GLFW 鼠标回调 → Engine 层转发 |
| [Android 集成](platform-integration/android.md) | Activity + View.OnTouchListener + EGL 上下文（伪代码骨架） |

### ⑦ 未来模块

| 模块 | 状态 |
|---|---|
| SDEF 球形变形 | 🚧 |
| 共享光空间 / 多模型 | 🚧 |

---

## 典型使用流程

### 流程 A：纯网格预览（入门）

```csharp
// 1. 创建相机
var camera = new OrbitCamera(alpha: π/4, beta: π/3, radius: 100, target: Vector3.Zero);

// 2. 创建输入控制器（调用方只传 enabled，平台层自动转发）
var input = new OrbitInputController(camera, enabled: true);

// 3. 每帧：
camera.Aspect = viewportWidth / (float)viewportHeight;
camera.ComputeViewProj(viewProjSpan);
grid.Draw(viewProjSpan);  // GlesGridRenderer
```

### 流程 B：加载并渲染 PMX 模型

```csharp
// 1. 从磁盘加载（一步完成 PMX 解析 + SkeletalModel 转换 + 纹理上传 + 自阴影预处理）
var modelRenderer = GlesModelRenderer.LoadFromFile(device, "path/to/model.pmx");

// 2. 每帧（简化版，完整流程见 model-renderer 文档）
camera.Aspect = width / (float)height;
Span<float> viewProj = stackalloc float[16];
camera.ComputeViewProj(viewProj);

// 自阴影（可选）
shadowRenderer.UpdateLight(modelRenderer.Model, lightDirection);
frame.LightViewProj = shadowRenderer.LightViewProj;

// 帧首统一准备
modelRenderer.PrepareFrame(in frame);

// Z pass（光照深度图）
shadowRenderer.RenderShadowMaps(device, modelRenderer, width, height);

// 主渲染
modelRenderer.Draw(in frame, toonMode: 1f);

// 床影（MMD 影模式 2）
if (shadowRenderer.Enabled)
    shadowRenderer.DrawFloor(device, frame.LightColor);
```

### 流程 C：VMD 动画 + IK + 物理（完整管线）

```csharp
// 1. 加载模型并解析动画
var modelRenderer = GlesModelRenderer.LoadFromFile(device, "model.pmx");
var model = modelRenderer.Model;
var anim = MmdAnimation.FromVmd(VmdParser.Parse(File.ReadAllBytes("motion.vmd")));
var timeline = new MmdTimeline();
timeline.AddLayer(new MmdAnimationLayer(anim));

// 2. 注入物理内核（PMX 刚体/关节 → 内核定义；S2/S3 开关默认开）
var pmx = PmxParser.Parse(File.ReadAllBytes("model.pmx"));
modelRenderer.Physics = new MMDPhysics(
    pmx.RigidBodies.Select(RigidBodyDef.FromPmx).ToArray(),
    pmx.Joints.Select(JointDef.FromPmx).ToArray());

// 3. 每帧：
timeline.Apply(model);                                  // 采样 + 混合写回
MmdMorphEvaluator.Evaluate(model);                      // 表情求值
modelRenderer.PhysicsFrame = timeline.CurrentFrame;     // 物理 tick 时钟随动画帧号
modelRenderer.PrepareFrame(in frame);                   // 内部依次：IK → 世界矩阵 →
                                                        // 物理写回 → 物理后付与 → 蒙皮/上传
modelRenderer.Draw(in frame);
```

详细用法见各子文档。

---

## 文档维护指南

- **新增模块文档** → 在对应子目录加 `.md` 文件，并在**本文件**和该子目录的 `index.md` 里追加一行链接
- **修改已有文档** → 直接编辑对应 `.md` 文件，本文件的链接无需改动（锚点不变）
- **标记未实现** → 在链接后加 `🚧`
- **命名约定** → 文件名小写 + 连字符（`orbit-camera.md`），标题可读（`# OrbitCamera API`）
- **图示** → 代码内联，坐标系等需要图示的地方用 ASCII art

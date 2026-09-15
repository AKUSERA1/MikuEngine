# MikuEngine 使用文档

> 跨平台原生 MMD（MikuMikuDance）引擎。主目标平台 Android（OpenGL ES 3.1），桌面使用 GLFW 作为测试宿主。

本文档面向**调用方**（游戏 / 应用开发者），只描述如何安装、集成、使用 MikuEngine，
以及使用时必须遵守的约定与需要注意的边界。

---

## 模块分层

```
┌──────────────────────────────────────────────────────────────────────┐
│  平台层                                                               │
│  桌面宿主 (GLFW) · Android Activity · iOS UIView                     │
│  职责：窗口 / 上下文 + 原生输入 API → Engine 层一行转发                │
├──────────────────────────────────────────────────────────────────────┤
│  Engine 层  MikuEngine.Engine                                        │
│  OrbitInputController（跨平台手势识别 + 灵敏度）                       │
├──────────────────────────────────────────────────────────────────────┤
│  Core 层  MikuEngine.Core                                            │
│  OrbitCamera · MmdMath · ModelRootTransform · PmxParser ·             │
│  SkeletalModel（姿势 / 模型变换 / 表情）                               │
│  Animation（VMD / 混合 / IK / 赋予 / 相机 / 外部親）                    │
│  职责：纯数学 / 纯数据，零平台依赖                                      │
├──────────────────────────────────────────────────────────────────────┤
│  Physics 层  MikuEngine.Physics                                      │
│  MMDPhysics（骨骼同步层）· World · RigidBodyStore ·                   │
│  ContactDetection · ConstraintSolver（MMD 刚体 / 关节模拟）            │
├──────────────────────────────────────────────────────────────────────┤
│  Render 层  MikuEngine.Render.GLES                                   │
│  GlesDevice · GlesGridRenderer · GlesModelRenderer ·                  │
│  GlesShadowRenderer · GlesTextureLibrary · GlesSkinMatricesBuffer ·   │
│  GlesMorphBuffer                                                      │
│  职责：OpenGL ES 3.1 渲染（GLES 后端唯一，桌面自动加载 GL）             │
└──────────────────────────────────────────────────────────────────────┘
```

依赖方向（单向）：`Core ← Physics ← Render.GLES ← Engine`，平台层引用 Engine。

---

## 快速导航

### 1. 入门

| 文档 | 说明 |
|---|---|
| [安装与依赖](getting-started/installation.md) | 包引用、目标框架、平台依赖 |
| [最小可运行窗口](getting-started/first-window.md) | 创建窗口 + 相机 + 网格地面 |

### 2. Core 模块

| 文档 | 说明 |
|---|---|
| [坐标系约定](core/coordinate-system.md) | 左手 / Y-up / +Z-forward / 两套矩阵表示，**必读** |
| [OrbitCamera API](core/orbit-camera.md) | 轨道相机的构造、属性、矩阵输出方法 |
| [PMX 模型解析](core/pmx-parser.md) | PmxParser · PmxModel · SkeletalModel · SkeletalModelConverter |
| [MMD 数学工具](core/mmd-math.md) | MmdMath（YXZ 欧拉角 ↔ 四元数）· QuatMath · Mat4 · ModelRootTransform |
| [模型变换与缩放](core/model-transform.md) | 移动 / 旋转 注入 全ての親 · 渲染层根矩阵 · 缩放倍率与物理解耦 |
| [动画子系统](core/animation/index.md) | VMD 加载 · 播放 · 多轨混合 · 显示帧可见性 · CCD IK · 赋予 · 生命周期 |
| [VMD 相机动画](core/animation/camera.md) | 相机轨道采样 · OrbitCamera 驱动模式 · 输入短路 · 视差视点 |
| [外部親绑定](core/animation/external-parent.md) | 跨模型挂载：绑定轨道 · 控制器 · 先亲后子帧序 |
| [CCD IK 求解器](core/animation/ik.md) | MmdIkSolver · MmdIkChain · 角度限制 / ReverseClamp |
| [赋予变换](core/animation/append-transform.md) | 赋予（Append Transform）· 局部赋予 · 物理后赋予 |

### 3. Engine 模块

| 文档 | 说明 |
|---|---|
| [OrbitInputController API](engine/orbit-input-controller.md) | 跨平台手势识别：鼠标 / 触控 / 捏合，灵敏度调整 |

### 4. Render.GLES 模块

| 文档 | 说明 |
|---|---|
| [GlesDevice](render-gles/gles-device.md) | GL 上下文管理、帧缓冲状态、资源生命周期 |
| [GlesGridRenderer](render-gles/grid-renderer.md) | 网格地面渲染（雾色淡出） |
| [GlesModelRenderer](render-gles/gles-model-renderer.md) | PMX 模型渲染管线：蒙皮、材质段、轮廓线、自阴影、模型变换根矩阵 |
| [GlesShadowRenderer](render-gles/gles-shadow-renderer.md) | 自阴影 Z 图 + 床影：紧视锥 / texel snapping / 多风格软影 |
| [渲染辅助组件](render-gles/gles-support.md) | GlesTextureLibrary · PmxFileResolver · GlesSkinMatricesBuffer · GlesMorphBuffer · GlesDebugOverlay |

### 5. Physics 模块

| 文档 | 说明 |
|---|---|
| [物理引擎总览](physics/index.md) | 模块结构 · 快速上手 · 帧序 · 确定性契约 |
| [MMDPhysics 骨骼同步层](physics/mmd-physics.md) | Update 三相位 · 开关（S1/S2/S3）· Reset · 抖动阻尼 |
| [World 与内核类型](physics/world.md) | 确定性步进 · 风 · 刚体 / 关节定义 · 内置地面 · 碰撞与求解 |

### 6. 平台集成

| 文档 | 说明 |
|---|---|
| [桌面 GLFW 集成](platform-integration/desktop-glfw.md) | 窗口创建、GLFW 鼠标回调 → Engine 层转发 |
| [Android 集成](platform-integration/android.md) | Activity + View.OnTouchListener + EGL 上下文（骨架） |

---

## 尚未实现的能力

以下能力当前版本不提供，调用方需自行规避或等待后续版本：

| 能力 | 现状 |
|---|---|
| SDEF 球形变形 | 解析支持，求值退化为 Bdef2 |
| QDEF 四元数变形 | 解析支持，求值退化为 Bdef4 |
| VMD 光照动画轨道 | 解析仅计数，不消费（相机动画已支持，见 [camera.md](core/animation/camera.md)） |
| VMD property 的 IK 开关接线 | 已解析保留，未接入求解器使能位 |
| PMX 骨的「外部親変形」标志（0x2000） | 解析保留，运行时未消费（跨模型绑定走 VMD 路径，见 [external-parent.md](core/animation/external-parent.md)） |
| 多模型共享光照 / 多 caster 阴影 | 未实现：每个模型各自的 `GlesModelRenderer` 与时间轴；子模型可共用主模型的自阴影 Z 图（只由主模型投射） |
| Android 集成 | 未验证 |

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

// 2. 每帧（简化版，完整流程见 gles-model-renderer 文档）
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

// 2. 注入物理内核（PMX 刚体 / 关节 → 内核定义；S2/S3 开关默认开）
var pmx = PmxParser.Parse(File.ReadAllBytes("model.pmx"));
modelRenderer.Physics = new MMDPhysics(
    pmx.RigidBodies.Select(RigidBodyDef.FromPmx).ToArray(),
    pmx.Joints.Select(JointDef.FromPmx).ToArray());

// 3. 每帧：
timeline.Apply(model);                                  // 采样 + 混合写回
MmdMorphEvaluator.Evaluate(model);                      // 表情求值
modelRenderer.ModelScale = new Vector3(1, 1, 1);        // 缩放倍率（可选，渲染层，与物理解耦）
modelRenderer.PhysicsFrame = timeline.CurrentFrame;     // 物理 tick 时钟随动画帧号
modelRenderer.PrepareFrame(in frame);                   // 内部依次：模型变换 → 根矩阵 →
                                                        // IK → 世界矩阵 → 物理写回 →
                                                        // 物理后赋予 → 蒙皮 / 上传
modelRenderer.Draw(in frame);
```

各环节的详细用法见对应子文档。

### 流程 D：相机动画 / 跨模型挂载（可选扩展）

```csharp
// VMD 相机动画（与模型动效共用同一帧号）
if (anim.CameraTrack is { IsEmpty: false } ct && ct.Sample(timeline.CurrentFrame, out var pose))
{
    camera.SetVmdDriven(true);                       // 进入驱动态（输入自动短路）
    camera.SetVmdPose(pose.Target, pose.RotationEuler, pose.Distance, pose.Fov);
}
frame.CameraPosition = new Vector4(camera.GetEyePosition(), 0f);   // 视差量取视点

// 外部親：把子模型挂到亲模型骨骼上（先亲后子）
var ext = new MmdExternalParentController();
ext.Register("角色", parentRenderer.Model);
ext.Register("道具", childRenderer.Model, childAnimation);
// 每帧：亲模型 PrepareFrame → ext.Update(timeline.CurrentFrame) → 子模型 PrepareFrame
```

详见 [camera.md](core/animation/camera.md) 与 [external-parent.md](core/animation/external-parent.md)。

---

## 文档约定

- 命名：文件名小写 + 连字符（`orbit-camera.md`），标题可读（`# OrbitCamera API`）
- 新增模块文档：在对应子目录加 `.md`，并在本文件与该子目录的 `index.md` 各追加一行链接
- 图示：代码内联；坐标系等需要图示的地方用 ASCII art
- 用词：除骨骼名（如 全ての親、足ＩＫ）与 PMX / VMD 字段名外，统一使用简体中文

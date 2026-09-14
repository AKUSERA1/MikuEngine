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
│  OrbitCamera · MmdMath · ModelRootTransform · PmxParser ·              │
│  SkeletalModel（姿势/面板变换/表情）· Animation（VMD/混合/IK/付与）      │
│  职责：纯数学 / 纯数据，零平台依赖                                        │
├──────────────────────────────────────────────────────────────────────┤
│  Physics 层  MikuEngine.Physics                                       │
│  MMDPhysics（骨骼同步层） · World · RigidBodyStore ·                   │
│  ContactDetection · ConstraintSolver（reze 物理逐式移植）               │
├──────────────────────────────────────────────────────────────────────┤
│  Render 层  MikuEngine.Render.GLES                                    │
│  GlesDevice · GlesGridRenderer · GlesModelRenderer ·                  │
│  GlesShadowRenderer · GlesTextureLibrary · GlesSkinMatricesBuffer ·    │
│  GlesMorphBuffer                                                      │
│  职责：OpenGL ES 3.1 渲染（GLES 后端唯一，桌面自动加载 GL）             │
└──────────────────────────────────────────────────────────────────────┘
```

依赖方向（单向）：`Core ← Physics ← Render.GLES ← Engine`，平台层引用 Engine。

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
| [坐标系约定](core/coordinate-system.md) | 左手 / Y-up / +Z-forward / 列主序矩阵 / 两套矩阵表示，**必读** |
| [OrbitCamera API](core/orbit-camera.md) | 轨道相机的构造、属性、矩阵输出方法 |
| [PMX 模型解析](core/pmx-parser.md) | PmxParser · PmxModel · SkeletalModel · SkeletalModelConverter |
| [MMD 数学工具](core/mmd-math.md) | MmdMath（YXZ 欧拉角 ↔ 四元数）· QuatMath · Mat4 · ModelRootTransform |
| [模型变换与缩放](core/model-transform.md) | MMD モデル操作：全ての親 注入 · 渲染层根矩阵 · 缩放与物理解耦 |
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
| [GlesModelRenderer](render-gles/gles-model-renderer.md) | PMX 模型渲染管线：蒙皮、材质段、轮廓线、自阴影、模型变换根矩阵 |
| [GlesShadowRenderer](render-gles/gles-shadow-renderer.md) | 自阴影 Z 图 + 床影：紧视锥 / texel snapping / 多风格软影 |
| [渲染辅助组件](render-gles/gles-support.md) | GlesTextureLibrary · PmxFileResolver · GlesSkinMatricesBuffer · GlesMorphBuffer · GlesDebugOverlay |

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
| SDEF 球形变形（真实现，当前退化为 Bdef2） | 🚧 |
| 共享光空间 / 多模型场景 | 🚧 |
| 渲染侧材质 morph uniform 逐材质应用 | 🚧 |
| VMD 相机 / 光照动画轨道 | 🚧 |
| Android 移植验证 | 🚧 |

### ⑧ Demo 交互速查（`samples/MikuEngine.Demo`）

```bash
dotnet run --project samples/MikuEngine.Demo                    # 自动找 Model/1/1.pmx
dotnet run --project samples/MikuEngine.Demo -- path/to/x.pmx   # 显式指定模型
```

| 输入 | 功能 |
|---|---|
| 鼠标 左键 / 右键 / 滚轮 | 轨道相机：旋转 / 平移 / 缩放 |
| `I`/`K` · `J`/`L` · `U`/`O` | 模型面板 **移動**（Y / X / Z 世界轴，0.5/步；`Shift` 微调 0.1） |
| `Alt` + 同上键 | 模型面板 **回転**（`I`/`K`=X · `J`/`L`=Y · `U`/`O`=Z，∓15°/步，MMD YXZ 序） |
| `.` / `,` | **拡大率** 全体放大 / 缩小（×1.1，渲染层，与物理解耦） |
| `Shift+.` / `Shift+,` | 只拉伸 / 只压扁 Z 轴（压纸片测试） |
| `R` | 重置全部模型变换（移動 / 回転 / 拡大率） |
| 空格 / `←→` / `↑↓` / `F` | 动画：暂停/播放 · ∓1 帧 · 帧率 ±6 · 回首帧 |
| `E` | 轮廓线开关 |
| `1` / `2` / `0` | 自阴影：开 / 自阴影+床影 / 关 |
| `S` | 自阴影风格循环（PE 标准 16-tap → 硬边本影 → 软影） |
| `P` / `G` / `H` | 物理：模拟总开关（S1）/ 地面碰撞（S2）/ 物理后付与（S3） |

> 自動加载 `Motion/動作+IK.vmd`（骨动效 + 足ＩＫ 目标 + 表情，单层）；
> 想换回「Motion + Lips/Eyes/Facial 四层」形态，改 `Program.cs` 里的 `motionFiles` 数组即可。

### 无人值守验收（CI 友好）

| 参数 | 验收内容 |
|---|---|
| `--smoke` | 渲染自检：Z 图统计 + 三种阴影风格与床影的**贡献象素**量化 |
| `--anim-smoke` | 多层动画播放：90 帧无异常 + 混合器状态 + 求值耗时 |
| `--ik-smoke[=N]` | IK 收敛：逐帧打印「目标(足ＩＫ) vs 被驱动端(足首)」距离，判定 < 1 模型单位 |
| `--xform-smoke` | 模型变换端到端：TR 注入 全ての親 / 操作中心不动 / 拡大率 不碰 `WorldMatrices` / 重置幂等 |

`--ik-smoke` 附带数据导出：`--ik-dump=<path>`（rig + FK + IK 结果 JSON，供 reze 求解器对拍）、
`--ik-bake-dump=<path>`（逐帧最终局部旋转，供 mmdbridge 烘焙对拍）、`--ik-frame=<N>`（导出帧号）。

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
modelRenderer.ModelScale = new Vector3(1, 1, 1);        // 拡大率（可选，渲染层，与物理解耦）
modelRenderer.PhysicsFrame = timeline.CurrentFrame;     // 物理 tick 时钟随动画帧号
modelRenderer.PrepareFrame(in frame);                   // 内部依次：面板变换 → 根矩阵 →
                                                        // IK → 世界矩阵 → 物理写回 →
                                                        // 物理后付与 → 蒙皮/上传
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

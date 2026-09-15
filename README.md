# MikuEngine

C# / .NET 10 的 MikuMikuDance（MMD）实时引擎，目标平台 **OpenGL ES 3.1**（桌面 GLFW 运行，可移植移动端）。
从零实现 PMX/VMD 解析、骨骼动画、IK、光照/自阴影渲染，行为语义以 MMD 本体和 PmxEditor 为基准。

## 项目结构

| 项目 | 说明 |
|---|---|
| `src/MikuEngine.Core` | PMX/VMD 解析、骨骼模型、动画/IK/混合器、MMD 数学 |
| `src/MikuEngine.Physics` | 刚体物理内核 |
| `src/MikuEngine.Render.GLES` | GLES 3.1 渲染器：模型 / 轮廓线 / 自阴影 / 床影 / 格网 / 调试预览 |
| `src/MikuEngine.Engine` | 跨平台输入层 |
| `samples/MikuEngine.Demo` | 可运行示例 |
| `tests/` | xUnit 单测 |
| `engine-docs/` | 各子系统设计文档 |

## 进度 CheckList

> 状态图例：✅ 已完成 · 🚧 进行中 · ⏳ 未开始 · 🔒 排除（本期不做）

### 资产解析

- ✅ PMX 2.0：顶点 / 骨骼 / 材质 / 表情 / 刚体 / 关节（`PmxParser`）
- ✅ VMD：骨轨道（bezier 插值）、表情轨道、property 可见性 / IK 开关、 morph 全类型
- ✅ 权重类型：BDEF1/2/4、QDEF（退化 BDEF4）、SDEF（退化 BDEF2），字节和恒 255 归一
- ⏳ SDEF 真实现（C/R0/R1 双线性插值）
- ⏳ VMD 相机动画轨道
- ⏳ VMD 光照动画轨道
- 🔒 PMX 2.1 的软体物理
- 🔒 PMX 2.1 新增的Flip与Impulse变形

### 骨骼 / 动画核心

- ✅ 骨骼层级 → 局部 T/R + 世界/蒙皮矩阵管线
- ✅ 变形求值顺序
- ✅ 付与变换（局部 + 世界两模式）
- ✅ 軸制限
- ✅ IK 求解器
- ✅ morph 组传播、骨 morph、材质 morph、顶点/UV morph 稀疏表
- ✅ 多层动画加权混合（Mixer + Layer 逐项归一）、播放器/时间轴（play / pause / loop / seek / 多层）
- ✅ 模型变换（MMD モデル操作）：旋转、平移，以及MMD未提供的缩放变换

### 物理模拟

- ✅ 刚体 / 关节内核
- ✅ 地面碰撞（模型空间 y=0，可开关）
- ✅ 物理后付与（MMD本体独有行为，PmxEditor都不支持，这类模型仅在MMD本体才有胸部物理）
- ✅ 模型缩放与物理解耦（骨架 / 刚体 / IK 全部运行在 bind 尺度，模型压成纸片也不影响模拟）

### GLES 3.1 渲染

- ✅ 交错 VBO / 索引缓冲 / 蒙皮矩阵 SSBO / morph SSBO（顶点 + UV）
- ✅ Phong + Toon + Sphere（加算/乘算）光照，严格复刻 PmxEditor 预览效果
- ✅ 绘制：单一队列、按 PMX 材质顺序（blend 恒开、深度写恒开、无 alpha test、默认剔除背面）
- ✅ 轮廓线（edge pass，per-顶点 EdgeScale / per-材质 EdgeSize）
- ✅ 自阴影：光照 Z 图 + 内联 PCF（三种风格：PE 标准 16-tap / 硬边缘阴影 / 软影）
- ✅ 床影（地面阴影）
- ⏳ 多模型纹理库去重 + 引用计数（跨实例重复上传待优化）
- ⏳ 材质 morph 的渲染侧 uniform 逐材质应用（Core 表已就绪）

### 多模型支持

- ✅ 核心层：`SkeletalModel` / `MmdAnimation.Bind` / Timeline 全部按实例自包含，零全局状态
- ✅ Demo 多模型：下拉框切活跃模型，每个模型各自的时间轴，共享相机 / 格网 / 阴影 / 主时钟游标

### 平台与工具

- ✅ 桌面 WinForms GUI（MMD 本体倒品字形布局）+ WGL 自建 GL 上下文视口
- ✅ 窗体用设计器风格布局（`MainForm.Designer.cs` + `InitializeComponent`），可在 VS 里直接打开设计器
- ✅ xUnit 回归（Core 175 通过 / Physics 70 通过）
- ✅ tools/ 取证脚本（PMX 骨表/刚体表、VMD 轨道扫描、PmxEditor 常量 dump、mmdbridge bake 对拍）
- ⏳ Android 移植验证（GLES 3.1 目标已预留）

## 构建与运行

```bash
# 构建（.NET 10 SDK）
dotnet build MikuEngine.slnx

# 运行 Demo（不自动载入任何文件，模型 / 动画由拖放或菜单打开）
dotnet run --project samples/MikuEngine.Demo
```

## Demo 界面与操作

界面按 MMD 本体的「倒品字形」排布：

```
┌────────────────────────────────────────────────────┐
│ 菜单栏（文件 / 显示 / 物理 / 动作 / 帮助）             │
├────────────────────┬───────────────────────────────┤
│ 时间线（当前帧号、   │ 3D 视口                        │
│  活跃模型轨道数）    │                    ┌────────┐ │
│ 消息（运行日志）     │                    │TRS 变换盘│ │
├────────────────────┴───────────────────────────────┤
│ 操作面板：模型（载入/活跃模型下拉/移除/IK）· 动画 · 模型变换 │
├────────────────────────────────────────────────────┤
│ 状态栏（fps / 模型数 / OpenGL 版本）                  │
└────────────────────────────────────────────────────┘
```

| 输入 | 功能 |
|---|---|
| 拖放 `.pmx` 到窗口 | 新增一个模型（活跃模型切到新模型） |
| 拖放 `.vmd` 到窗口 | 把动画载入到**当前活跃模型**（替换该模型的动画，别的模型不受影响） |
| 「载入模型…」/「载入动画…」 | 同上，走文件对话框 |
| 活跃模型下拉框 | 切换**当前操作对象**：TRS 作用对象 + 动画载入目标。**阴影由全部模型共同投射，与它无关** |
| 「模型」区块 IK 复选框 | 开关**活跃模型**的 IK 求解（逐模型保存）；关掉后停在「付与之后、IK 之前」 |
| 视口 左键 / 右键 / 滚轮 | 轨道相机：旋转 / 平移 / 缩放（左键拖 = **模型跟手**：拖右向右转、拖下露顶部） |
| 视口右下角变换盘 | 下排选 `移动`/`旋转`/`缩放`，上排按住 `X`/`Y`/`Z` **上下拖动**（向上=正向）——**全局模式** |
| 空格 / `←→` / `F` | 播放暂停 / 步进 1 帧 / 回首帧（**启动为暂停**；没载入动画时帧号不推进） |
| `R` | 重置活跃模型的全部变换（移動 / 回転 / 拡大率） |
| `E` / `S` | 轮廓线开关 / 自阴影风格循环 |
| `0` / `1` / `2` | 自阴影：关 / 自阴影 / 自阴影+床影 |
| `P` / `G` / `H` | 物理模拟 / 地面碰撞 / 物理后付与 开关 |
| `C` | 相机动画开关（开启后 VMD 独占视图、鼠标轨道失效） |

模型变换是 MMD「モデル操作」的全局模式：平移与旋转注入 `全ての親`（`操作中心` 留在原地、
子孙绕枢轴跟随），缩放只进渲染层根矩阵（与物理 / IK 解耦，`WorldMatrices` 逐位不变）。

### Demo 的结构与边界

`samples/MikuEngine.Demo` 是**纯界面/交互层**，只做「WinForms 组装 + 调用引擎」：

| 目录 | 内容 |
|---|---|
| `Program.cs` | 入口：`Application.Run(new MainForm())`，无命令行解析 |
| `MainForm.cs` / `.Designer.cs` | 主窗体：布局（设计器风格）、菜单、拖放、渲染循环、读数刷新 |
| `Rendering/` | `GlViewport`（WGL 视口控件）、`DemoScene`（模型集合 + 相机 + 主时钟 + 每帧管线）、`DemoModel`、`ModelTransform` |
| `Controls/` | `TransformPad`（TRS 变换盘）、`TimelineView`（简化时间线） |

它**不自动载入任何文件、不内置测试开关、也不附带 MMD 资源**：
启动后是空场景（只有格网），模型与动画一律由用户拖放或菜单打开；
渲染与动画逻辑全部在 `src/`，`samples/` 下不复制任何引擎代码。

## 测试

```bash
dotnet test MikuEngine.slnx          # Core + Physics 全量单测
```

单测里对真实 MMD 资源（PMX / VMD）的引用统一走 `tests/MikuEngine.Core.Tests/TestAssets.cs`：
它**不写死 Demo 目录名**，而是在仓库根的 `samples/*/` 下按相对路径查找 ——
Demo 项目目录改名或搬迁都不会打断测试。

> 注：`samples/MikuEngine.Demo.old/` 是改造前的控制台→GUI 版 Demo（含 `--smoke` / `--xform-smoke` /
> `--gui-shot` 等无人值守自检开关与 MMD 资源），目前**原样保留**，后续计划改造成自动化验证专用工程。

## 鸣谢

本项目参考了以下项目：
- [babylon-mmd](https://github.com/noname0310/babylon-mmd)
- [reze-engine](https://github.com/AmyangXYZ/reze-engine)
- PmxEditor
- MikuMikuDance
- [mmdbridge](https://github.com/rintrint/mmdbridge/)

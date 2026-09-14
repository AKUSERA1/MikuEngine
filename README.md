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
- ✅ Opaque / Cutout / Blended 三队列排序绘制
- ✅ 轮廓线（edge pass，per-顶点 EdgeScale / per-材质 EdgeSize）
- ✅ 自阴影：光照 Z 图 + 内联 PCF（三种风格：PE 标准 16-tap / 硬边缘阴影 / 软影）
- ✅ 床影（地面阴影）
- ⏳ 多模型纹理库去重 + 引用计数（跨实例重复上传待优化）
- ⏳ 材质 morph 的渲染侧 uniform 逐材质应用（Core 表已就绪）

### 多模型支持

- ✅ 核心层：`SkeletalModel` / `MmdAnimation.Bind` / Timeline 全部按实例自包含，零全局状态
- ⏳ Demo 多模型改造

### 平台与工具

- ✅ 桌面 GLFW 窗口 + 输入（OrbitInputController：鼠标 / 触控手势）
- ✅ xUnit 回归（Core 168 通过 / Physics 70 通过）+ 四类无人值守 smoke 验收（见下）
- ✅ tools/ 取证脚本（PMX 骨表/刚体表、VMD 轨道扫描、PmxEditor 常量 dump、mmdbridge bake 对拍）
- ⏳ Android 移植验证（GLES 3.1 目标已预留）

## 构建与运行

```bash
# 构建（.NET 10 SDK）
dotnet build MikuEngine.slnx

# 运行 Demo（自动寻找 samples/MikuEngine.Demo/Model/1/1.pmx，也可显式传 PMX 路径）
dotnet run --project samples/MikuEngine.Demo
```

## Demo 操作

| 输入 | 功能 |
|---|---|
| 鼠标 左键 / 右键 / 滚轮 | 轨道相机：旋转 / 平移 / 缩放 |
| `I`/`K` · `J`/`L` · `U`/`O` | 模型面板 移動（Y/X/Z 世界轴，0.5/步；`Shift` 微调 0.1） |
| `Alt` + 同上键 | 模型面板 回転（X=俯仰 / Y=偏航 / Z=滚转，∓15°/步，MMD YXZ 序） |
| `.` / `,` | 拡大率 全体放大 / 缩小（×1.1，与物理解耦） |
| `Shift+.` / `Shift+,` | 只拉伸 / 只压扁 Z 轴（压纸片测试） |
| `R` | 重置全部模型变换 |
| 空格 / `←→` / `↑↓` / `F` | 动画：暂停 / ∓1 帧 / 帧率 ±6 / 回首帧 |
| `E` | 轮廓线开关 |
| `1` / `2` / `0` | 自阴影：开 / 自阴影+床影 / 关 |
| `S` | 自阴影风格循环（PE 标准 → 硬边 → 软影） |
| `P` / `G` / `H` | 物理模拟 / 地面碰撞 / 物理后付与 开关 |

## 测试与无人值守验收

```bash
dotnet test MikuEngine.slnx                       # 全量单测

dotnet run --project samples/MikuEngine.Demo -- --smoke        # 渲染自检（Z 图统计 + 影贡献象素量化）
dotnet run --project samples/MikuEngine.Demo -- --anim-smoke   # 多层动画播放验收
dotnet run --project samples/MikuEngine.Demo -- --ik-smoke     # IK 收敛验收（目标-末端距离 < 1 单位）
dotnet run --project samples/MikuEngine.Demo -- --xform-smoke  # 模型变换端到端验收（TR 注入 / 操作中心不动 / 缩放解耦 / 重置幂等）
```

`--ik-smoke` 支持 `--ik-dump=<path>` 导出 IK 对拍 JSON、`--ik-bake-dump=<path>` 导出与 mmdbridge 烘焙逐帧对拍的数据。

## 鸣谢

本项目参考了以下项目：
- [babylon-mmd](https://github.com/noname0310/babylon-mmd)
- [reze-engine](https://github.com/AmyangXYZ/reze-engine)
- PmxEditor
- MikuMikuDance
- [mmdbridge](https://github.com/rintrint/mmdbridge/)

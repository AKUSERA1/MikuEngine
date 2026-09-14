# 动画子系统 — MikuEngine.Core.Animation

从 VMD 二进制到运行时骨骼 / 表情姿态的完整链路，全部位于 **Core 层**（纯数学 / 纯数据，零渲染依赖，可在单元测试、Android、WebAssembly 上直接跑）。

## 管线总览

```
VMD 二进制 (.vmd)
      │
      ▼
VmdParser.Parse(byte[])        ← 纯二进制解析，Shift-JIS(932) 解码名字
      │
      ▼
VmdMotion (原样关键帧 + property 表示枠 + 分区偏移)
      │
      ▼
MmdAnimation.FromVmd(vmd)      ← 按骨/表情名聚合成轨道；property 单独成轨
      │
      ▼
MmdAnimation.Bind(model)       ← 绑定到具体模型（数值数组与源轨道共享，不复制）
      │
      ├─ 单动效：MmdAnimationPlayer 游标 → Sample(model, frame)
      │
      └─ 多动效：MmdTimeline 游标 → MmdAnimationMixer.Evaluate(model, frame)
                     │             （逐层 SampleInto → 加权混合 → 可见性 AND → 整体写回）
                     ▼
                 SkeletalModel（局部 T/R + MorphRawWeights + Visible）
                     │
                     ▼
                 MmdMorphEvaluator.Evaluate(model)   ← Group 传播 + 骨 morph 折入局部 T/R
                     │
                     ▼
                 model.ApplyModelTransform()         ← 面板 移動/回転（全ての親 后乘因子）
                     │
                     ▼
                 MmdIkSolver.Solve(model)            ← CCD IK（迭代中含增量世界矩阵重算）
                     │
                     ▼
                 model.UpdateWorldMatrices(...)      ← 軸制限 → 付与 → IK 折进世界/蒙皮矩阵
                     │
                     ▼
                 MMDPhysics.Update(...)              ← 物理步进 + 姿态写回（MikuEngine.Physics）
                     │
                     ▼
                 model.ApplyPhysicsAppend()          ← 物理后付与（付与链消费模拟结果）
```

以上 IK → 物理段在渲染路径中由 `GlesModelRenderer.PrepareFrame` 统一驱动（详见
[ik.md](ik.md)、[append-transform.md](append-transform.md) 与 [物理模块](../../physics/index.md)）。
模型面板 拡大率 则**不在这条链上**——它只进渲染层根矩阵，见
[model-transform.md](../model-transform.md)。

## 设计原则

1. **帧号纯函数**：采样只取决于「帧号」，不依赖也不保留上一帧 / 上一层的任何状态。
   因此「连续播放到帧 N」≡「直接 seek 到帧 N」，跳帧 / 暂停 / 改帧率都只改游标，不动采样逻辑。
2. **左手坐标系 + Y-up**，与 MMD 原生约定一致（见 [coordinate-system.md](../coordinate-system.md)）。
3. **名字用 Shift-JIS(932) 解码**，与 VMD 规范一致；`NameRaw`（原始字节）作为解码器无关的权威标识，
   用于跨模型绑定的兜底（见 [vmd-loading.md](vmd-loading.md)）。
4. **采样先整体复位到绑定姿势再写回**：未覆盖的骨 / morph 自动回到绑定状态，
   调用方无需手动清理，移除层 / 清除动画后无残留。
5. **加权重采样（multi-track blending）采用 reze 的半球对齐 nlerp + 逐项归一**，
   而非 babylon-mmd 的全局归一（见 [blending.md](blending.md) 的偏离说明）。

## 文档列表

| 文档 | 说明 |
|---|---|
| [vmd-loading.md](vmd-loading.md) | VmdParser · VmdMotion · MmdAnimation（FromVmd/Bind）· 三类轨道 · property 表示枠 |
| [playback.md](playback.md) | MmdAnimationPlayer · MmdPoseBuffer · MmdAnimation.Sample · MmdTimeline 时间轴 |
| [blending.md](blending.md) | MmdAnimationLayer · MmdAnimationMixer（加权混合 / 可见性 AND） |
| [visibility.md](visibility.md) | SkeletalModel.Visible · 表示枠阶梯采样 · 渲染三 pass 门控 |
| [ik.md](ik.md) | MmdIkSolver（CCD）· MmdIkChain / MmdIkChainBuilder · 角度限制 / ReverseClamp |
| [append-transform.md](append-transform.md) | 付与变换（含局部付与 / 自付与 / 物理后付与 S3） |
| [lifecycle.md](lifecycle.md) | 绑定 / 层增删 / 清除 / GC 保证 · MmdMorphEvaluator 调用顺序 |
| [../model-transform.md](../model-transform.md) | 模型面板 移動/回転/拡大率（全ての親 注入 · 渲染层缩放解耦） |

## 源文件

| 文件 | 说明 |
|---|---|
| [VmdParser.cs](../../../src/MikuEngine.Core/Animation/VmdParser.cs) | VMD 二进制解析（骨 / 表情 / property 表示枠） |
| [MmdAnimation.cs](../../../src/MikuEngine.Core/Animation/MmdAnimation.cs) | 运行时轨道集合（骨 / 表情 / 表示枠）+ 采样 / 绑定 |
| [MmdPoseBuffer.cs](../../../src/MikuEngine.Core/Animation/MmdPoseBuffer.cs) | 一层采样输出缓冲（多层复用同一实例） |
| [MmdAnimationPlayer.cs](../../../src/MikuEngine.Core/Animation/MmdAnimationPlayer.cs) | 单动效帧号游标 |
| [MmdAnimationLayer.cs](../../../src/MikuEngine.Core/Animation/MmdAnimationLayer.cs) | 时间轴上的一层（Offset / Trim / Weight / Fade / Loop） |
| [MmdAnimationMixer.cs](../../../src/MikuEngine.Core/Animation/MmdAnimationMixer.cs) | 多动效混合器 |
| [MmdTimeline.cs](../../../src/MikuEngine.Core/Animation/MmdTimeline.cs) | 多层时间轴（播放控制外壳） |
| [MmdMorphEvaluator.cs](../../../src/MikuEngine.Core/Animation/MmdMorphEvaluator.cs) | 表情权重解算（Group 传播 / 骨 morph / 材质 morph） |
| [MmdIkSolver.cs](../../../src/MikuEngine.Core/Animation/MmdIkSolver.cs) | MMD 风格 CCD IK 求解器 |
| [MmdIkChain.cs](../../../src/MikuEngine.Core/Models/MmdIkChain.cs) | 运行时 IK 链 / 链节 / 固定轴 / Euler 顺序 |
| [MmdIkChainBuilder.cs](../../../src/MikuEngine.Core/Models/MmdIkChainBuilder.cs) | PMX 骨数据 → 运行时 IK 链 |

## 与方案文档的关系

动画混合的设计与逐条决策记录在 `docs/2026-09-11-anim-blend-plan.md`
（含 5d-1 ~ 5d-4 实施记录、逐项归一偏离说明、VMD 模型名乱码成因附录）。
本目录是**面向调用方**的 API 文档，不重复记录架构决策。

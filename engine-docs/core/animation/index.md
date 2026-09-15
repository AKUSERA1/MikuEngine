# 动画子系统 — MikuEngine.Core.Animation

从 VMD 二进制到运行时骨骼 / 表情 / 相机姿态的完整链路，全部位于 **Core 层**
（纯数学 / 纯数据，零渲染依赖，可在单元测试、Android、WebAssembly 上直接跑）。

## 管线总览

```
VMD 二进制 (.vmd)
      │
      ▼
VmdParser.Parse(byte[])        ← 纯二进制解析，Shift-JIS(932) 解码名字
      │
      ▼
VmdMotion (原样关键帧 + property 显示帧 + 相机键 + 外部親键 + 分区偏移)
      │
      ▼
MmdAnimation.FromVmd(vmd)      ← 按骨 / 表情名聚合成轨道；property / 相机 / 外部親各自成轨
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
                 model.ApplyModelTransform()         ← 移动 / 旋转（全ての親 后乘因子）
                     │
                     ▼
                 MmdIkSolver.Solve(model)            ← CCD IK（迭代中含增量世界矩阵重算）
                     │
                     ▼
                 model.UpdateWorldMatrices(...)      ← 轴限制 → 赋予 → IK → 外部親根 → 世界 / 蒙皮矩阵
                     │
                     ▼
                 MMDPhysics.Update(...)              ← 物理步进 + 姿态写回（MikuEngine.Physics）
                     │
                     ▼
                 model.ApplyPhysicsAppend()          ← 物理后赋予（赋予链消费模拟结果）
```

以上 IK → 物理段在渲染路径中由 `GlesModelRenderer.PrepareFrame` 统一驱动；
缩放变换则**不在这条链上**——它只进渲染层根矩阵，见
[model-transform.md](../model-transform.md)。

**两条不在姿态链上的轨道**：相机轨道驱动视图（[camera.md](camera.md)），
外部親轨道把模型挂到别的模型上（[external-parent.md](external-parent.md)）。
它们都是整场景量，由宿主在姿态链之外单独消费。

## 设计原则

1. **帧号纯函数**：采样只取决于「帧号」，不依赖也不保留上一帧 / 上一层的任何状态。
   因此「连续播放到帧 N」≡「直接 seek 到帧 N」，跳帧 / 暂停 / 改帧率都只改游标，不动采样逻辑。
2. **左手坐标系 + Y-up**，与 MMD 原生约定一致（见 [coordinate-system.md](../coordinate-system.md)）。
3. **名字用 Shift-JIS(932) 解码**，与 VMD 规范一致；`NameRaw`（原始字节）作为解码器无关的权威标识，
   用于跨模型绑定的兜底（见 [vmd-loading.md](vmd-loading.md)）。
4. **采样先整体复位到绑定姿势再写回**：未覆盖的骨 / morph 自动回到绑定状态，
   调用方无需手动清理，移除层 / 清除动画后无残留。
5. **加权重采样采用半球对齐 nlerp + 逐项归一**（见 [blending.md](blending.md)）。
6. **整场景量不做模型过滤**：property（显示帧）/ 相机 / 外部親轨道在 `Bind` 时原样透传，
   只有骨 / 表情轨道会被「该模型是否存在」筛掉。

## 文档列表

| 文档                                             | 说明                                                                         |
| ---------------------------------------------- | -------------------------------------------------------------------------- |
| [vmd-loading.md](vmd-loading.md)               | VmdParser · VmdMotion · MmdAnimation（FromVmd / Bind）· 轨道类型 · property 显示帧      |
| [playback.md](playback.md)                     | MmdAnimationPlayer · MmdPoseBuffer · MmdAnimation.Sample · MmdTimeline 时间轴 |
| [blending.md](blending.md)                     | MmdAnimationLayer · MmdAnimationMixer（加权混合 / 可见性 AND）                      |
| [visibility.md](visibility.md)                 | SkeletalModel.Visible · 显示帧阶梯采样 · 渲染三 pass 门控                              |
| [camera.md](camera.md)                         | VMD 相机轨道 · MmdCameraTrack 采样 · OrbitCamera 的 VMD 驱动模式与输入短路               |
| [external-parent.md](external-parent.md)       | 外部親绑定：跨模型挂载 · 离散轨道 · 控制器 · 先亲后子帧序契约                                       |
| [ik.md](ik.md)                                 | MmdIkSolver（CCD）· MmdIkChain / MmdIkChainBuilder · 角度限制 / ReverseClamp     |
| [append-transform.md](append-transform.md)     | 赋予变换（含局部赋予 / 自赋予 / 物理后赋予）                                                  |
| [lifecycle.md](lifecycle.md)                   | 绑定 / 层增删 / 清除 / GC 保证 · MmdMorphEvaluator 调用顺序                             |
| [../model-transform.md](../model-transform.md) | 模型变换（移动 / 旋转 / 缩放）（全ての親 注入 · 渲染层缩放解耦）                                      |

## 源文件

| 文件                                                                                             | 说明                                            |
| ---------------------------------------------------------------------------------------------- | --------------------------------------------- |
| [VmdParser.cs](../../../src/MikuEngine.Core/Animation/VmdParser.cs)                            | VMD 二进制解析（骨 / 表情 / property 显示帧 / 相机 / 外部親）    |
| [MmdAnimation.cs](../../../src/MikuEngine.Core/Animation/MmdAnimation.cs)                      | 运行时轨道集合（骨 / 表情 / 显示帧 / 相机 / 外部親）+ 采样 / 绑定     |
| [MmdPoseBuffer.cs](../../../src/MikuEngine.Core/Animation/MmdPoseBuffer.cs)                    | 一层采样输出缓冲（多层复用同一实例）                            |
| [MmdAnimationPlayer.cs](../../../src/MikuEngine.Core/Animation/MmdAnimationPlayer.cs)          | 单动效帧号游标                                       |
| [MmdAnimationLayer.cs](../../../src/MikuEngine.Core/Animation/MmdAnimationLayer.cs)            | 时间轴上的一层（Offset / Trim / Weight / Fade / Loop） |
| [MmdAnimationMixer.cs](../../../src/MikuEngine.Core/Animation/MmdAnimationMixer.cs)            | 多动效混合器                                        |
| [MmdTimeline.cs](../../../src/MikuEngine.Core/Animation/MmdTimeline.cs)                        | 多层时间轴（播放控制外壳）                                 |
| [MmdMorphEvaluator.cs](../../../src/MikuEngine.Core/Animation/MmdMorphEvaluator.cs)            | 表情权重解算（Group 传播 / 骨 morph / 材质 morph）         |
| [MmdCameraTrack.cs](../../../src/MikuEngine.Core/Animation/MmdCameraTrack.cs)                  | VMD 相机轨道 + `MmdCameraPose` 六通道贝塞尔采样           |
| [MmdExternalParentTrack.cs](../../../src/MikuEngine.Core/Animation/MmdExternalParentTrack.cs)  | 外部親绑定键 + 离散轨道                                 |
| [MmdExternalParentController.cs](../../../src/MikuEngine.Core/Animation/MmdExternalParentController.cs) | 跨模型外部親注册表 + 每帧绑定解析                            |
| [MmdIkSolver.cs](../../../src/MikuEngine.Core/Animation/MmdIkSolver.cs)                        | MMD 风格 CCD IK 求解器                             |
| [MmdIkChain.cs](../../../src/MikuEngine.Core/Models/MmdIkChain.cs)                             | 运行时 IK 链 / 链节 / 固定轴 / Euler 顺序                |
| [MmdIkChainBuilder.cs](../../../src/MikuEngine.Core/Models/MmdIkChainBuilder.cs)               | PMX 骨数据 → 运行时 IK 链                            |

## 调用顺序速查（自建帧循环时按此顺序）

单模型：

```
1. 采样写回：timeline.Apply(model)   或   anim.Sample(model, frame)
2. 表情求值：MmdMorphEvaluator.Evaluate(model)
3. 模型变换：model.ApplyModelTransform()
4. IK      ：MmdIkSolver.Solve(model)
5. 世界矩阵：model.UpdateWorldMatrices()
6. 物理    ：MMDPhysics.Update(...) → model.ApplyPhysicsAppend()
7. 蒙皮上传：渲染层完成
```

相机（与上述同帧号，不参与姿态链）：

```
采样：anim.CameraTrack.Sample(frame, out pose)
驱动：camera.SetVmdDriven(true) → camera.SetVmdPose(pose.Target, pose.RotationEuler,
                                                    pose.Distance, pose.Fov)
视差：frame.CameraPosition = camera.GetEyePosition()
```

多模型 + 外部親（**先亲后子**）：

```
亲模型：1 → 7（世界矩阵须在本帧定稿）
绑定  ：externalParent.Update(frame)
子模型：1 → 7
```

渲染层（`GlesModelRenderer.PrepareFrame`）已内置单模型的 1 → 7；
只有自建帧循环（离线渲染 / 测试）时才需要自己按序调用。
顺序打乱会得到错误姿态，其中最易错的是「表情求值必须早于 IK / 世界矩阵」
与「外部親必须先亲后子」。

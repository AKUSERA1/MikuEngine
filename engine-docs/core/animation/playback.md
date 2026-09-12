# 播放与采样

帧号驱动的播放控制，以及「采样」这一帧号纯函数的落地方式。

---

## 显示帧率与动画帧率解耦

渲染循环跑显示器的 vsync，动画只由**连续帧号** `CurrentFrame` 表达，两者仅通过游标耦合。
采样在连续帧号上插值（**不 floor 到整数帧**），所以 30fps 动效在 144Hz 屏上依旧平滑；
若错误取整会出现 30fps 步进抖动。

因为采样是帧号的纯函数（见 [MmdAnimation.SampleInto](../../../src/MikuEngine.Core/Animation/MmdAnimation.cs)），
任何改变游标的操作（跳帧 / seek / 暂停 / 改帧率）都不动采样逻辑。

---

## MmdAnimationPlayer（单动效游标）

轻量帧号游标，适合单条动效。

```csharp
using MikuEngine.Core.Animation;

var player = new MmdAnimationPlayer
{
    PlaybackFps = 30f,            // 每秒多少动画帧（默认 30）
    Loop = true,                  // 到尾帧回卷到首帧（默认 false：钳制在尾帧）
    Mode = MmdPlaybackMode.RealTime,   // 或 FrameLocked（每渲染帧精确 +1 帧，调试用）
};
player.Configure(startFrame, endFrame);   // 设定区间并把游标置于首帧

// 每渲染帧：
player.Advance(deltaSeconds);       // 推进游标（Paused 时无操作）
anim.Sample(model, player.CurrentFrame);   // 采样写回模型
```

### 成员

| 成员 | 类型 | 说明 |
|---|---|---|
| `CurrentFrame` | `double` | 连续帧号游标，可任意 seek |
| `PlaybackFps` | `float` | 每秒动画帧数（默认 30） |
| `Mode` | `MmdPlaybackMode` | `RealTime`（按真实时间） / `FrameLocked`（每帧 +1） |
| `Loop` | `bool` | 到尾帧回卷（默认 false） |
| `Paused` | `bool` | 暂停时不推进 |
| `StartFrame` / `EndFrame` | `double` | 播放区间 |
| `Configure(s, e)` | 方法 | 设区间并把游标置于首帧 |
| `Advance(dt)` | 方法 | 推进游标（区间 / 回卷语义同上） |
| `Seek(frame)` | 方法 | 直接定位（钳制到区间） |
| `Step(frames)` | 方法 | 相对当前帧步进（钳制到区间） |

---

## MmdPoseBuffer（采样输出缓冲）

一层采样的结果容器。**多动效混合时所有层复用同一个实例**（采样是纯函数，每层采样前都会整体复位）。

```csharp
var buffer = MmdPoseBuffer.ForModel(model);   // 尺寸 = 模型骨数 / morph 数
buffer.Reset();                                // 复位到「绑定姿势 + 无表情」
```

| 字段 | 类型 | 语义 |
|---|---|---|
| `Rotations` | `Quaternion[]` | 局部旋转本体（复位 = Identity） |
| `Translations` | `Vector3[]` | 相对绑定姿势的父空间偏移（复位 = 0；写回时加 `LocalPositions`） |
| `MorphWeights` | `float[]` | VMD 原始权重（复位 = 0） |
| `CoveredBones` | `List<int>` | 本次采样覆盖到的骨索引 |
| `CoveredMorphs` | `List<int>` | 本次采样覆盖到的 morph 索引 |

未覆盖的项保持复位值；「层只覆盖一部分骨 / morph」靠 `CoveredBones` / `CoveredMorphs` 显式记录，
混合器用它算「覆盖该项的权重和」（残差混回绑定姿势）。

---

## MmdAnimation.SampleInto / Sample

```csharp
// 纯函数：把 frame 处的姿态采样进 buffer（先整体复位到绑定姿势）
anim.SampleInto(frame, buffer);

// 单动效便捷入口：SampleInto + 直接写回模型
anim.Sample(model, frame);
```

`SampleInto` 的语义（帧号纯函数）：

1. `buffer.Reset()` 整体复位到绑定姿势 + 无表情；
2. 逐骨骼轨道写 `Rotations[index]` / `Translations[index]`，并记录进 `CoveredBones`；
3. 逐表情轨道写 `MorphWeights[index]`（同名 morph 全部槽位），并记录进 `CoveredMorphs`；
4. **只写局部 T/R 与原始 morph 权重，不重算世界矩阵**——世界矩阵由调用方的 `PrepareFrame` 负责。

`Sample` 在其基础上把缓冲写回模型：

- `LocalRotations[i] = buffer.Rotations[i]`
- `LocalTranslations[i] = LocalPositions[i] + buffer.Translations[i]`
- `MorphRawWeights[i] = buffer.MorphWeights[i]`

> 单动效路径下，可见性（`Visible`）由调用方按 `anim.SampleVisible(frame)` 写回；
> 走混合器时可见性由 `MmdAnimationMixer.Evaluate` 统一写回（见 [visibility.md](visibility.md)）。

---

## MmdTimeline（多层时间轴）

`MmdAnimationMixer` 的播放控制外壳：层集合管理 + 时间游标（语义与 `MmdAnimationPlayer` 一致）。

```csharp
using MikuEngine.Core.Animation;

var timeline = new MmdTimeline { Loop = true };   // 自建混合器

// 加载层（可指定「在第 N 帧导入」）
var motion = MmdAnimation.Bind(VmdParser.Parse(File.ReadAllBytes("Motion.vmd")), model);
timeline.AddLayer(new MmdAnimationLayer(motion));                 // 默认 Offset=0
timeline.AddLayer(new MmdAnimationLayer(motion2), importAt: 30); // 第 0 帧落在时间轴第 30 帧

// 每渲染帧：
timeline.Advance(deltaSeconds);   // 推进游标
timeline.Apply(model);            // == mixer.Evaluate(model, CurrentFrame)，写回模型
```

### 成员

| 成员 | 类型 | 说明 |
|---|---|---|
| `CurrentFrame` | `double` | 连续帧号游标 |
| `PlaybackFps` | `float` | 每秒动画帧数（默认 30） |
| `Mode` | `MmdPlaybackMode` | 真实时间 / 帧锁定 |
| `Loop` | `bool` | 到末端回卷（默认 false） |
| `Paused` | `bool` | 暂停时游标不动 |
| `StartFrame` / `EndFrame` | `double` | 播放区间 = 各层活跃区间并集（结构变化后自动刷新） |
| `Layers` | `IReadOnlyList<MmdAnimationLayer>` | 全部层（转发自混合器） |
| `Mixer` | `MmdAnimationMixer` | 被驱动的混合器 |
| `AddLayer(layer, importAt?)` | 方法 | 添加层；`importAt` 非空时设 `Offset`（层结构变化后自动刷新区间） |
| `RemoveLayer(layer)` | 方法 | 移除一层（返回是否移除）；下一帧该层轨道自动回绑定姿势 |
| `ClearLayers()` | 方法 | 移除全部层；下一帧整模型回绑定姿势、恒可见 |
| `Advance(dt)` | 方法 | 推进游标（区间 / 回卷语义与 `MmdAnimationPlayer.Advance` 逐行一致） |
| `Seek(frame)` / `Step(frames)` | 方法 | 定位 / 相对步进（钳制到区间） |
| `Apply(model)` | 方法 | 在当前游标处求值并写回模型 |

区间外（各层都不活跃）求值即整模型绑定姿势——**空白保留**。

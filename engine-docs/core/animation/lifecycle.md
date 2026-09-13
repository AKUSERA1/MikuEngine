# 生命周期与状态清除

动画对象的绑定、层增删、清除，以及对应的垃圾回收保证；附表情求值器（MmdMorphEvaluator）的调用顺序。

---

## 绑定（展开 → 绑定）

```
VmdMotion ──FromVmd──▶ MmdAnimation（未绑定，数值数组共享）
                         │
                         └──Bind(model)──▶ MmdAnimation（已绑定）
```

- `FromVmd` 产出的轨道与具体模型无关，同一份可绑定到多个模型。
- `Bind` 只保留「模型存在对应骨 / 表情」的轨道；数值数组**与源轨道共享**（不复制），跨模型复用零额外内存。
- 同名表情会写入全部同名槽位（MMD 允许重名，权重必须写给每一个）。
- 绑定前的轨道（`BoneIndex = -1` / `MorphIndices = []`）在 `SampleInto` / `Sample` 中会被跳过。

---

## 层增删与清除

```csharp
var timeline = new MmdTimeline();

// ① 导入：把另一条动效的第 0 帧落在时间轴当前帧
var imp = MmdAnimation.Bind(VmdParser.Parse(File.ReadAllBytes("test1.vmd")), model);
timeline.AddLayer(new MmdAnimationLayer(imp), importAt: timeline.CurrentFrame);

// ② 移除：该层轨道下一帧自动回绑定姿势、表示枠投票自动解除（整体写回语义，无残留）
timeline.RemoveLayer(imp);

// ③ 清除全部：整模型回绑定姿势、恒可见
timeline.ClearLayers();
```

关键不变量：**采样是帧号纯函数 + 整体写回**。

- 任何未被活跃层覆盖的骨 / morph，求值后会回到绑定姿势 / morph 0；
- 所以移除层或清除后**无需任何手动清理**——下帧 `Evaluate` / `Apply` 天然无残留。

---

## 垃圾回收保证

`MmdAnimation` / `MmdAnimationLayer` / `MmdAnimationMixer` / `MmdTimeline` 全是**纯托管对象图**：
模型 (`SkeletalModel`) 不反向持有这些动效对象。清除动画只需：

```csharp
timeline.ClearLayers();   // 混合器内的层列表清空
// 上层若还持有着 layer 引用，置 null / 让其离开作用域
```

一旦调用方不再持有层 / 混合器 / 时间轴的引用，整条对象链（动效 → 轨道 → 共享数值数组）即成为
不可达对象，下一轮 GC 整体回收。**没有隐藏根、没有非托管资源需要手动释放、没有事件订阅需要反注册。**

> 验证：`tests/MikuEngine.Core.Tests` 中 `MmdTimelineTests.ClearLayers_ReleasesAnimationGraph_ForGc`
> 用 `WeakReference` + 双 `GC.Collect()` 断言清除后动效图可被回收。

---

## 表情求值器（MmdMorphEvaluator）

动画写回的是**原始** morph 权重（`MorphRawWeights`），几何 / 材质落地前必须过一遍 `MmdMorphEvaluator`：

```csharp
anim.Sample(model, frame);          // ① 复位骨骼 + 写骨轨道；② 复位并写 MorphRawWeights
MmdMorphEvaluator.Evaluate(model);  // ③ Group 传播 → MorphWeights；④ 骨 morph 写入局部 T/R
model.PrepareFrame(in frame);       // ⑤ IK → UpdateWorldMatrices（軸制限 → 付与）→ 物理 → 蒙皮
```

`Evaluate` 分三步（**幂等**：有效权重每次从原始值整体重算）：

1. **Group 传播**：`w[child] += ratio * w[group]`，按 `GroupOrder`（引用者先于被引用者）遍历，
   组里套组自然级联，多个组指向同一子项时累加。
2. **骨 morph**：对目标骨叠加父空间平移与局部旋转 `own * Slerp(Identity, morph, w)`（右乘），
   必须落在 `PrepareFrame` 的 `UpdateWorldMatrices`（軸制限 / 付与）**之前**。
3. **材质 morph**：`ResolveMaterial(model, materialIndex)` 产出叠加了所有活跃材质 morph 的
   「有效材质」（Multiply `v + (v*m − v)*w` / Add `v + m*w`，与 babylon-mmd 同式）。
   `MaterialIndex == -1` 表示全部材质。

> 不支持 Flip(9) / Impulse(10) / 附加 UV1~4(4~7)：转换器不为它们建表，求值器天然跳过
> （`MorphKinds` 仍保留原类型值，便于诊断）。顶点 / UV morph 只解算权重，几何与材质落地由渲染层消费。

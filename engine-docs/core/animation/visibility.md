# 可见性（显示帧）

MMD 的「显示帧」控制整模型显示 / 不显示。本标准路径：`SkeletalModel.Visible` 布尔量，
由动画求值统一写回，渲染端据此门控三个 pass。

***

## 数据来源：property 阶梯轨道

`MmdPropertyTrack` 是离散状态轨，键与键之间**保持、绝不插值**：

```csharp
bool visible = anim.SampleVisible(frame);
// 早于首键 ⇒ true（未声明「不显示」的动效应照常渲染）
// 晚于末键 ⇒ 保持末键状态
// 空轨道   ⇒ 恒 true
```

单动效走 `MmdAnimation.SampleVisible`；多动效由 `MmdAnimationLayer.IsVisibleAt(localFrame)`
取每层本地帧的布尔，再由混合器做 **AND**（见下）。

***

## 合并规则（多层）

`MmdAnimationMixer.Evaluate` 对**每个活跃层**取阶梯布尔，AND 合并写回 `model.Visible`：

```
visible = true
for each active layer:
    visible &= layer.IsVisibleAt(ToLocal(timelineFrame))
model.Visible = (activeCount > 0) ? visible : true
```

- **无活跃层 ⇒ 恒可见**（时间轴空白区间整模型照常显示）。
- 可见性是布尔量，**不进数值混合**：即便某层 `Weight = 0.5`（数值上只贡献一半姿态），
  它的「不显示」依旧一票否决。

***

## 渲染门控

`SkeletalModel.Visible` 由 `GlesModelRenderer.PrepareFrame` 在帧首采样进 `ModelVisible` 快照，
随后三个 pass 据此早退：

| Pass                                      | 行为                           |
| ----------------------------------------- | ---------------------------- |
| 主渲染 `Draw`                                | `if (!ModelVisible) return;` |
| 轮廓线 `DrawEdges`                           | 同快照早退（防御性重复）                 |
| 自阴影 `GlesShadowRenderer.RenderShadowMaps` | 跳过该 caster，**并清空本帧深度**       |

> **共享 Z 图的坑**：自阴影接收侧与床影共用同一张 Z 图。只是早退 caster 会留下上一帧的深度，
> 屏幕上仍会出现一个已经不可见模型的影子。因此 `GlesShadowRenderer` 在跳过前显式
> `glClear(DepthBufferBit)`（空处 z = 1，采样即「不在影里」）。
> **隐藏模型不能只早退——必须清深度。**

***

## 状态复位

动效求值写回 `Visible`，但「关闭动画驱动」时应显式复位，否则会停在隐藏窗口一直看不见：

```csharp
// 宿主关掉动画驱动时
model.ResetPose();            // 局部 T/R 回绑定
model.ResetMorphWeights();    // 表情权重回 0
model.Visible = true;         // 显示帧也复位
```

混合器 `ClearLayers()` / 所有层都不活跃时，`Evaluate` 写回 `Visible = true`（整模型恒可见），
下一帧自动解除隐藏，无残留。

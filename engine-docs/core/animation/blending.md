# 动画混合（多轨加权）

把多条已绑定到同一模型的动效混合成一份姿态：`MmdAnimationLayer` 描述「一条动效在时间轴上的摆放」，
`MmdAnimationMixer` 负责逐层加权累加。

---

## MmdAnimationLayer — 时间轴上的一层

把一条动效放到时间轴的任意位置，并裁剪 / 加权 / 淡入淡出 / 循环。坐标映射：

```
local     = timeline − Offset
活跃区间  = [Offset + TrimStart, Offset + TrimEnd]        // 闭区间
```

- `Offset` = 「动画帧 0 落在时间轴哪一帧」：在第 30 帧导入 ⇒ `Offset = 30`。
- `TrimStart` / `TrimEnd` 默认 = 动效内容起止（`MmdAnimation.StartFrame` / `EndFrame`，即首 / 末键）。
  因此 VMD 自身的前导空帧会如实表现为「这段时间轴区间上该层不贡献」（**空白保留**）；
  显式把 `TrimStart` 设到内容起点之前才等价于「首键外推」——那是显式选择，不是默认行为。

```csharp
var layer = new MmdAnimationLayer(anim)
{
    Offset    = 0,      // 动画第 0 帧落在时间轴第几帧
    Weight    = 1f,     // 层权重（≤ 0 ⇒ 完全不贡献）
    Loop      = false,  // 越过活跃区间末端后回卷
    FadeIn    = 0,      // 层内淡入帧数（自区间起点起算）
    FadeOut   = 0,      // 层内淡出帧数（自区间终点往前算）
};
```

| 成员 | 类型 | 说明 |
|---|---|---|
| `Animation` | `MmdAnimation` | 该层驱动的动效（应已 `Bind` 到目标模型） |
| `Offset` | `double` | 动画帧 0 落在时间轴哪一帧 |
| `TrimStart` / `TrimEnd` | `double` | 裁剪区间（动画本地帧），默认 = 内容起止 |
| `Weight` | `float` | 层权重（≤ 0 ⇒ 不贡献） |
| `Loop` | `bool` | 区间末端回卷 |
| `FadeIn` / `FadeOut` | `double` | 层内淡入 / 淡出帧数（0 = 不淡） |
| `ActiveStart` / `ActiveEnd` | `double` | 活跃区间（时间轴帧，闭） |
| `IsActive(timelineFrame)` | 方法 | 该帧是否贡献（看 `Weight > 0` + 落在区间，与 fade 无关） |
| `ToLocal(timelineFrame)` | 方法 | 时间轴帧 → 该动效本地帧（Loop 时回卷） |
| `FadeAt(timelineFrame)` | 方法 | 层内淡入淡出包络 ∈ [0,1]（当前线性斜坡） |
| `IsVisibleAt(localFrame)` | 方法 | 该层本地帧处的表示枠（显示 / 非表示） |

> `IsActive` 判定**与淡入淡出包络无关**：淡入首帧的包络为 0，但该层仍算活跃。
> 原因——可见性是布尔量，不参与数值混合，表示枠的一票否决不因 fade 而失效。

---

## MmdAnimationMixer — 混合器

每帧对时间轴帧号做一次 `Evaluate`：

```
1. 收集活跃层（Weight > 0 且落在区间内）
2. 逐层 SampleInto 到共享 scratch
3. 按 3.4 公式累加：逐项「覆盖权重和」归一 + 残差混回绑定姿势
4. 可见性：逐活跃层取阶梯布尔，AND 合并写回 SkeletalModel.Visible
5. 整体写回局部 T/R 与原始 morph 权重（含未覆盖项）
   ⇒ 帧号纯函数、幂等、seek 等价于连续播放
```

```csharp
var mixer = new MmdAnimationMixer();
mixer.AddLayer(new MmdAnimationLayer(motion));      // 骨动效
mixer.AddLayer(new MmdAnimationLayer(lips));        // 口型
mixer.AddLayer(new MmdAnimationLayer(eyes));        // 眼神
mixer.AddLayer(new MmdAnimationLayer(facial));      // 表情

mixer.Evaluate(model, timelineFrame);   // 写回模型（局部 T/R + MorphRawWeights + Visible）
bool visible = mixer.Visible;           // 本帧可见性合并结果（活跃层 AND；无活跃层 ⇒ true）
```

| 成员 | 类型 | 说明 |
|---|---|---|
| `Layers` | `IReadOnlyList<MmdAnimationLayer>` | 全部层（含当前不活跃的），顺序即累加顺序 |
| `Visible` | `bool` | 本帧可见性合并结果（活跃层阶梯布尔的 AND；无活跃层 ⇒ true） |
| `AddLayer(layer)` | 方法 | 添加一层并返回它（便于链式设 Offset / Weight） |
| `RemoveLayer(layer)` | 方法 | 移除一层；下帧该层轨道自动回绑定姿势、可见性投票自动少一票 |
| `ClearLayers()` | 方法 | 移除全部层；下帧整模型回绑定姿势、恒可见 |
| `ActiveRange` | `(double, double)` | 各层活跃区间的并集（时间轴帧） |
| `Evaluate(model, frame)` | 方法 | 在 frame 处求值并写回模型（帧号纯函数） |

### 与参考实现的取舍

- **旋转**：用 reze 的半球对齐 nlerp。累加前若与已累计方向 dot < 0 取负（走最短弧），
  保证结果与累加顺序无关。
- **残差**：按「覆盖该项的权重和」混回绑定姿势，而不是全局权重。
- **可见性**：布尔 AND，**不做数值加权**（规避 babylon 的 0.5 半透明 bug）。IK 开关解析保留、不消费。

### 权重归一：逐项（对方案 3.4 的偏离）

方案 §3.4 原写「所有权重和的 `norm = 1/Σw` 全局因子」。本引擎改为**逐项**归一——
仅当同一骨 / morph 上的覆盖权重和 `W(item) > 1` 时才除以 `W(item)`；`W ≤ 1` 走残差混回绑定。

理由（实测验证）：

- **MMD 双槽位（モーション + 表情槽）各层轨道互不重叠**，全局 `Σw` 归一会把四层各压到 1/4 强度，
  与 MMD 的并集行为相悖；逐项归一在槽位不重叠时保持满强度。
- **层间竞争**（多个层驱动同一项）时与 babylon / reze 的全局归一**严格等价**——那正是两家的设计场景。
- **单层 `Weight == 1` 直通**：`boneN == 1 && w == 1` 时跳过 `Normalize`，与单动效 `MmdAnimation.Sample`
  **位级一致**（回归基线，[playback.md](playback.md)）。

累加器的中性元是**零向量**（不是 Identity）：Identity 初始化会把贡献叠成 2 倍；绑定姿势由写回时的
残差项 `(1 − W)·Identity` 统一补足。

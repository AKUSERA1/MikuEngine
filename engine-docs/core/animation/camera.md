# VMD 相机动画

VMD 的相机区段（camera）驱动整个视图，而不是模型。它与骨骼 / 表情轨道一样挂在
`MmdAnimation` 上，由宿主每帧采样后喂给 `OrbitCamera`。

```
VmdParser.Parse           → VmdMotion.CameraKeys（VmdCameraKey，文件顺序）
MmdAnimation.FromVmd      → MmdAnimation.CameraTrack（MmdCameraTrack，六通道贝塞尔）
MmdCameraTrack.Sample     → MmdCameraPose（target / rotation / distance / fov）
OrbitCamera.SetVmdPose    → 视图与投影（VmdDriven 模式）
```

## 源文件

| 文件 | 说明 |
|---|---|
| [MmdCameraTrack.cs](../../../src/MikuEngine.Core/Animation/MmdCameraTrack.cs) | 相机轨道 + `MmdCameraPose` 采样（贝塞尔） |
| [OrbitCamera.cs](../../../src/MikuEngine.Core/Camera/OrbitCamera.cs) | `VmdDriven` 模式：视图矩阵、投影、视点 |

---

## 1. MMD 相机的语义（先读这个）

MMD 相机是**注视点模型**，不是「位置 + 朝向」模型：

| 字段 | 含义 |
|---|---|
| `Target` | 注视点（MMD 世界空间，左手 / Y-up） |
| `RotationEuler` | 相机朝向欧拉角，**弧度，VMD 原始值（未取负）**——取负在相机侧做 |
| `Distance` | 与注视点的距离，**负值 = 相机在注视点后方**（VMD 原始语义） |
| `Fov` | 视角（弧度）。VMD 原始存的是 u32 **度数**，构建轨道时转弧度 |

视点由「注视点 + 朝向 × 距离」推出：

```
forward = q · (0, 0, 1)        // q = Euler(-ry, -rx, -rz)
eye     = Target + forward · Distance
```

> 朝向由 `RotationEuler` 决定，**不依赖 eye → target 连线**。
> 位置烘焙的轨道里 `Distance = 0`（eye 与 target 重合），因此视图矩阵不能走 lookAt
> ——归一化会退化成全零基；引擎直接铺四元数旋转基（见 §3）。

---

## 2. 轨道与采样

```csharp
// 轨道（由 FromVmd 构建，或取自 anim.CameraTrack）
MmdCameraTrack track = anim.CameraTrack!;      // 可能为 null / 空轨道

if (track.Sample(frame, out MmdCameraPose pose))
{
    camera.SetVmdDriven(true);
    camera.SetVmdPose(pose.Target, pose.RotationEuler, pose.Distance, pose.Fov);
}
else
{
    camera.SetVmdDriven(false);                // 无相机键 ⇒ 回到 orbit 自由视角
}
```

| 成员 | 说明 |
|---|---|
| `InterpolationStride` | 每键插值字节数 = **24**（6 通道 × (x1, x2, y1, y2)） |
| `Frames` / `Targets` / `Rotations` / `Distances` / `Fovs` | 逐键数值数组 |
| `Interpolation` | 每键 24 B 贝塞尔控制点，通道顺序 = targetX, targetY, targetZ, rotation, distance, fov |
| `IsEmpty` | 无键 |
| `EndFrame` | 末键帧号（空轨道为 0） |
| `Sample(frame, out pose)` | 采样（帧号的纯函数）。空轨道返回 `false`；区间外钳制到端键 |
| `FromVmd(keys)` | 由 `VmdCameraKey` 列表构建（稳定升序、同帧去重保留最后一次出现） |

三条与骨骼轨道不同的性质，接线时容易踩：

1. **插值字节布局不同**：相机每键 24 B、**按通道连续排布**（通道 c 占 `[4c, 4c+3]`）；
   骨骼是 64 B 的「每通道 4 份 16 B 副本」结构。两者不可互换读取。
2. **rotation 三轴共用一条贝塞尔通道**（通道 3）——MMD 相机旋转是单通道动画，
   三个轴的插值权重相同。
3. **无模型绑定**：相机是整场景量，`MmdAnimation.Bind` 原样透传轨道，
   不做「模型是否存在对应骨」的过滤。

区间曲线取自**后键**（与骨骼轨道一致，MMD 语义）。

---

## 3. OrbitCamera 的 VMD 驱动模式

```csharp
camera.SetVmdDriven(true);                                   // 进入驱动态（备份当前 fov）
camera.SetVmdPose(pose.Target, pose.RotationEuler, pose.Distance, pose.Fov);  // 每帧
camera.SetVmdDriven(false);                                  // 退出，恢复 orbit 的 fov
```

| 成员 | 说明 |
|---|---|
| `VmdDriven` | true = 视图由 VMD 相机姿态驱动 |
| `SetVmdDriven(bool)` | 进入 / 退出驱动态。**幂等**；进入时备份 `Fov`，退出时恢复 |
| `SetVmdPose(target, rotationEuler, distance, fov)` | 喂入下一帧的采样姿态；`fov` 直接驱动投影 |
| `GetEyePosition()` | 视差量（高光 / rim / 球面贴图用的相机位置）：驱动态取 VMD 视点，否则取 `Position` |

驱动态下的行为差异：

| 项 | 行为 |
|---|---|
| View 矩阵 | `View = Rᵀ · T(−eye)`，**不走 lookAt**（由四元数旋转基直接铺出，对 `Distance = 0` 稳健）。euler **三轴取负**后构建四元数 |
| Projection | `Fov` 被 VMD 逐帧改写；`SetVmdDriven(false)` 恢复进入前的 orbit fov |
| 近 / 远裁剪面 | 改用 `|Distance|` 推算（orbit 的 `Radius` 与取景无关），其余公式不变 |
| `Alpha` / `Beta` / `Radius` / `Target` | **不被触碰**——退出驱动态后 orbit 视角原样恢复 |
| `Position` | 仍是 orbit 位置，**与真实拍摄点无关**；着色用的相机位置必须取 `GetEyePosition()` |

> **硬约束**：`Alpha` / `Beta` / `Radius` 这些字段由 `SetVmdPose` 不进，也不该进。
> 需要记录 VMD 状态时不要写进 orbit 参数，否则退出驱动态后视角会跳变。

---

## 4. 输入短路

`OrbitInputController` 在相机被 VMD 驱动时**自动短路**：

```csharp
public bool InputAllowed => Enabled && !_camera.VmdDriven;
```

四个输入入口（`OnPointerDown` / `OnPointerMove` / `OnPointerUp` / `OnScroll`）
都以 `InputAllowed` 为闸门。原因：驱动态下 orbit / pan / zoom 操作的是一组
**与取景无关**的参数，改它既看不见也没有意义。

宿主不需要自己判断，也不需要额外禁用控制器；如需强制按下不响应，
`Enabled = false` 依旧是总开关。

---

## 5. 播放区间与时间轴

- 相机键**参与** `MmdAnimation.StartFrame` / `EndFrame` 的计算
  （与骨 / 表情轨道取并）：**只有相机键的 VMD 也要能播**。
- 采样用的是同一个时间轴游标（`MmdTimeline.CurrentFrame`），因此相机与舞蹈天然同步；
  跳帧 / seek / 暂停行为与动画一致，因为它同样是帧号的纯函数。

---

## 6. 最小用法

```csharp
// 加载：把相机轨与模型轨绑到同一个时间轴
var vmd = VmdParser.Parse(File.ReadAllBytes("camera.vmd"));
var anim = MmdAnimation.FromVmd(vmd);

// 每帧（在渲染之前）
timeline.Apply(model);                       // 模型姿态（若该 VMD 同时含骨 / 表情键）
if (anim.CameraTrack is { IsEmpty: false } ct && ct.Sample(timeline.CurrentFrame, out var pose))
{
    camera.SetVmdDriven(true);
    camera.SetVmdPose(pose.Target, pose.RotationEuler, pose.Distance, pose.Fov);
}
else
{
    camera.SetVmdDriven(false);
}

// 帧数据里的相机位置必须用视点，不是 orbit 的 Position
frame.CameraPosition = new Vector4(camera.GetEyePosition(), 0f);
```

> 相机与模型必须挂在**同一个 `MmdTimeline`（或至少同一个帧号）**上，
> 否则对不上拍。相机轨道本身不依赖模型，可与任意模型组合。

---

## 7. 边界

| 项 | 说明 |
|---|---|
| `CameraTrack` 可为 null | 未调用 `FromVmd` 的手工构造实例可能为 null；判定顺序为 `null` → `IsEmpty` → `Sample` |
| 空轨道 / 区间外 | `Sample` 返回 `false` 或钳制到端键；宿主应据此决定是否维持 `VmdDriven` |
| 光照动画 | **未实现**：VMD 的 light 区段仅计数，不驱动引擎光照 |
| 相机区缺失 | 老 VMD 可能没有 camera 区，`CameraKeys` 为空、`CameraSectionOffset = 0` |

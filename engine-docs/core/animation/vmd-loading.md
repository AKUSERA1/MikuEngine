# VMD 加载与动效构建

把 VMD 二进制解析为运行时可采样的轨道集合（[MmdAnimation](../../../src/MikuEngine.Core/Animation/MmdAnimation.cs)）。

```
VMD 二进制 (.vmd)
      │  VmdParser.Parse(byte[])
      ▼
VmdMotion                ← 骨 / 表情 / property（显示帧，VMD 规范中称 表示枠）/
      │                     相机 / 外部親 关键帧原样数据
      │  MmdAnimation.FromVmd(vmd)
      ▼
MmdAnimation             ← 按骨 / 表情名聚合成轨道；property / 相机 / 外部親各自成轨
      │  MmdAnimation.Bind(model)
      ▼
MmdAnimation（已绑定）    ← 骨 / 表情轨道按模型过滤；整场景轨道原样透传；数值数组与源共享
```

***

## VmdParser

### 基本用法

```csharp
using MikuEngine.Core.Animation;

byte[] data = File.ReadAllBytes("Motion.vmd");
VmdMotion vmd = VmdParser.Parse(data);   // 签名不符 / 区段越界 / 文件截断抛 VmdParseException

Console.WriteLine(vmd.ModelName);        // 解码后的模型名（Shift-JIS 932）
// vmd.BoneKeys / vmd.MorphKeys / vmd.PropertyKeys / vmd.CameraKeys  —— 文件顺序关键帧
// vmd.ExternalParentKeys —— 外部親键（来自骨键中「名字含冒号」的键）
// vmd.PropertyKeyCount  —— 显示帧键数量
// vmd.LeftoverBytes     —— 解析完所有区段后的剩余字节（非 0 = 文件有非规范尾巴）
```

光照 / 自阴影区段**只计数不解码**（本引擎不消费）；相机区段解码为 `CameraKeys`。

### VmdMotion 结构

| 属性                                                        | 类型                     | 说明                                    |
| --------------------------------------------------------- | ---------------------- | ------------------------------------- |
| `ModelName` / `ModelNameRaw`                              | `string` / `byte[]`    | 解码后模型名 / 20B 原始字节（截断到首个 `0x00`）       |
| `BoneKeys`                                                | `List<VmdBoneKey>`     | 骨骼关键帧，文件顺序（排序在轨道构建时做）                 |
| `MorphKeys`                                               | `List<VmdMorphKey>`    | 表情关键帧，文件顺序                            |
| `PropertyKeys`                                            | `List<VmdPropertyKey>` | property 区段（显示帧，VMD 规范中称 表示枠）关键帧，文件顺序 |
| `CameraKeys`                                              | `List<VmdCameraKey>`   | 相机关键帧，文件顺序（无 camera 区时为空）              |
| `ExternalParentKeys`                                      | `List<MmdExternalParentKey>` | 外部親绑定键，文件顺序（来源见下）                |
| `LightKeyCount` / `SelfShadowKeyCount`                    | `int`                  | 仅计数的区段                                    |
| `PropertyKeyCount`                                        | `int`                  | `= PropertyKeys.Count`                |
| `CameraSectionOffset`                                     | `int`                  | camera 分区起点（键数据首字节，不含 count 字段）；无该区为 0 |
| `PropertySectionOffset` / `PropertySectionBytes`          | `int`                  | property 分区的字节位置 / 长度（含 IK 开关列表）      |
| `LeftoverBytes`                                           | `int`                  | 非规范尾巴字节数（诊断用）                         |

### 关键帧类型

```csharp
public readonly record struct VmdBoneKey(
    string BoneName, byte[] NameRaw, uint Frame,
    Vector3 Position, Quaternion Rotation, byte[] Interpolation);

public readonly record struct VmdMorphKey(string MorphName, byte[] NameRaw, uint Frame, float Weight);

public readonly record struct VmdPropertyKey(int Frame, bool Visible, VmdIkState[] IkStates);

public readonly record struct VmdIkState(string BoneName, byte[] NameRaw, bool Enabled);

// 相机键（61 B）。FovDegrees 是 VMD 原始的 u32 度数，转弧度在轨道构建时做。
public readonly record struct VmdCameraKey(
    uint Frame, float Distance, Vector3 Target, Vector3 RotationEuler,
    float FovDegrees, byte[] Interpolation);

// 外部親键：不是独立区段，来自骨键中「名字含冒号」的键（见下）
public readonly record struct MmdExternalParentKey(
    int Frame, string ParentModel, string ParentBone,
    Vector3 OffsetTranslation, Quaternion OffsetRotation);
```

- **`NameRaw`**：名字的原始字节，是解码器无关的权威标识。跨模型绑定时若解码名对不上，
  可用原始字节兜底（见下方 `MmdAnimation.Bind`）。
- **`VmdPropertyKey.Visible`**：可见性极性为 `byte != 0 ⇒ 可见`。
- **`VmdIkState`**：property 键附带的 IK 开关。IK 求解器与服务端使能位
  （`SkeletalModel.IkEnabled`）都已就位，但**当前版本尚未把 property 的** **`IkStates`** **接过去**——
  目前只在 `MmdAnimation.IkStates` 里解析保留，`IkEnabled` 在转换期统一置 `true`。
- **`VmdCameraKey.Interpolation`**：24 B，**按通道连续排布**（6 通道 × (x1,x2,y1,y2)）。
  与骨键 64 B 的「每通道 4 份 16 B 副本」布局不同，两者不可互换读取（见 [camera.md](camera.md)）。

### 外部親键的拦截

MMD 的外部親绑定藏在**骨键轨道「外部親」**里：这类骨键的 15 B 骨名字段存的是
`親モデル名:親ボーン名`（优先全角冒号「：」，半角「:」兜底）。解析器据此拦截：

- 名字含冒号的骨键**不进 `BoneKeys`**，而是转成 `ExternalParentKeys`；
- 键的 `pos` / `rot` 成为 `OffsetTranslation` / `OffsetRotation`（叠加在亲骨世界变换之后）；
- 冒号任一侧为空 = 解除绑定（`ParentModel` 为空串）。

详见 [external-parent.md](external-parent.md)。

### 编码约定（Shift-JIS 932）

VMD 规范用 Shift-JIS 解码所有名字字段，`.NET` 侧用代码页 932。两条实现约定：

1. **非法字节**：输出 best-fit `'?'`。合法 Shift-JIS 名字不受影响，只可能出现在脏名字里。
2. **截断语义**：在第一个 `0x00` 字节处截断再解码（不是按 20 字节全解）。

> **模型名乱码警示**：若VMD创作者用非UTF-8环境的中文，则模型名可能会被 Windows 系统以 ANSI(CP936) 写入，按 Shift-JIS 读出时可能乱码。
> 模型名不参与任何绑定，仅显示层需要兜底；解析端契约维持 932 不变。

***

## MmdAnimation — 运行时轨道集合

骨 / 表情 / 显示帧（以及相机 / 外部親）在「展开」（`FromVmd`）与「绑定」（`Bind`）两步分离：
轨道数据与模型无关，可跨模型复用。

### 构建与绑定

```csharp
// 一步展开 + 绑定（最常见）
MmdAnimation anim = MmdAnimation.Bind(vmd, model);

// 或分步：同一条 VmdMotion 可展开一次、绑定到多个模型
MmdAnimation expanded = MmdAnimation.FromVmd(vmd);
MmdAnimation boundA  = expanded.Bind(modelA);
MmdAnimation boundB  = expanded.Bind(modelB);
```

- **`FromVmd`**：按名字聚合关键帧成轨道。骨骼轨道做**最短弧**处理（相邻键四元数 dot < 0 取负，
  保证同半球、短弧 slerp）；插值字节从 VMD 原 64 B 去冗余为每键 16 B
  （按通道分读，避开 MMD 复用 `raw[2]` / `raw[3]` 存物理开关的写法）。
  相机轨道由 `CameraKeys` 构建为 `CameraTrack`（六通道贝塞尔，键 24 B）；
  外部親轨道由 `ExternalParentKeys` 构建为 `ExternalParentTrack`（离散状态机）。
- **`Bind`**：为 `model` 产出一份绑定实例——找不到对应骨 / 表情的轨道**丢弃**
  （不同模型共用动效是常态）；**数值数组与源轨道共享**（不复制）。
  同名表情会写入全部同名槽位（MMD 允许表情重名）。
  **property / 相机 / 外部親是整场景量，不做模型过滤，原样透传**。
- **播放区间**：`StartFrame` / `EndFrame` 由骨 / 表情 / 相机 / 外部親轨道取并，
  **不受 property 影响**（显示帧是叠加在姿态上的布尔量，不是姿态来源）。
  相机与外部親键纳入区间：只有相机的 VMD 也要能播、只有绑定键的动效也该有区间。

### 轨道类型

| 类型                        | 说明                                | 采样                                                 |
| ------------------------- | --------------------------------- | -------------------------------------------------- |
| `MmdBoneTrack`            | 单骨旋转 + 父空间平移偏移；贝塞尔缓动              | `Sample(frame)` → `(Quaternion, Vector3)`，纯函数，端键钳制 |
| `MmdMorphTrack`           | 单表情权重；**线性**插值（VMD 不存 morph 插值曲线） | `SampleWeight(frame)`                              |
| `MmdPropertyTrack`        | 整模型显示 / 不显示；**阶梯**保持、绝不插值         | `SampleVisible(frame)`                             |
| `MmdCameraTrack`          | 整场景相机（target / rotation / distance / fov） | `Sample(frame, out MmdCameraPose)`，六通道贝塞尔          |
| `MmdExternalParentTrack`  | 外部親绑定；**阶梯**保持、绝不插值               | `TrySample(frame, out MmdExternalParentKey)`        |

`MmdBoneTrack` 关键点：

- 帧号严格升序；同帧重复键去重保留文件顺序的最后一次出现。
- 平移是**相对绑定姿势的父空间偏移**（0 = 绑定），写回时由调用方加 `SkeletalModel.LocalPositions`。
- 贝塞尔：`Bezier01(bx1, bx2, by1, by2, t)` 走 MMD 三次曲线，控制点落对角线时退化为恒等（默认线性键）。

`MmdPropertyTrack` 关键点：

- 显示帧是**离散状态**：键间保持、绝不插值；早于首键返回 `true`（未声明「不显示」的动效应照常渲染）；
  晚于末键保持末键状态；空轨道恒 `true`。
- 是**整模型**量，不参与「按模型绑定」的过滤，原样透传给绑定实例。
- IK 开关随键保留（`IkStates`），但尚未接到 `SkeletalModel.IkEnabled`（见上）。

`MmdCameraTrack` / `MmdExternalParentTrack` 关键点：

- 两者都是**整场景量**：`Bind` 原样透传，实体共享（不复制）。
- 相机轨道：`Sample` 是帧号纯函数，区间外钳制到端键，空轨道返回 `false`；
  rotation 三轴共用一条贝塞尔通道。用法见 [camera.md](camera.md)。
- 外部親轨道：与显示帧同族的**离散状态机**，`TrySample` 返回「≤ frame 的最后一键」，
  无键覆盖返回 `false`（未绑定）；**不做同帧去重**（同帧多键合法，后者覆盖前者）。
  用法见 [external-parent.md](external-parent.md)。

***

## 完整示例

```csharp
using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;

byte[] data = File.ReadAllBytes("Motion.vmd");
VmdMotion vmd = VmdParser.Parse(data);

MmdAnimation anim = MmdAnimation.Bind(vmd, model);

// 单动效直接写回模型（先整体复位到绑定姿势）
anim.Sample(model, frame: 120);

// 或喂混合器（见 blending.md）
var layer = new MmdAnimationLayer(anim) { Weight = 1f };
mixer.AddLayer(layer);
mixer.Evaluate(model, frame: 120);
```

> `MmdAnimation.Sample` 会**直接改写模型**且复用一个内部 scratch 缓冲，**不承诺线程安全**。
> 多动效不要经此入口，改用 `SampleInto` + 混合器（[playback.md](playback.md) / [blending.md](blending.md)）。


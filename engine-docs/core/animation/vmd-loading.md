# VMD 加载与动效构建

把 VMD 二进制解析为运行时可采样的轨道集合（[MmdAnimation](../../../src/MikuEngine.Core/Animation/MmdAnimation.cs)）。

```
VMD 二进制 (.vmd)
      │  VmdParser.Parse(byte[])
      ▼
VmdMotion                ← 骨 / 表情 / property（表示枠）关键帧原样数据
      │  MmdAnimation.FromVmd(vmd)
      ▼
MmdAnimation             ← 按骨/表情名聚合成轨道；property 单独成轨
      │  MmdAnimation.Bind(model)
      ▼
MmdAnimation（已绑定）    ← 只保留模型存在的轨道；数值数组与源共享
```

---

## VmdParser

### 基本用法

```csharp
using MikuEngine.Core.Animation;

byte[] data = File.ReadAllBytes("Motion.vmd");
VmdMotion vmd = VmdParser.Parse(data);   // 签名不符 / 区段越界 / 文件截断抛 VmdParseException

Console.WriteLine(vmd.ModelName);        // 解码后的模型名（Shift-JIS 932）
// vmd.BoneKeys / vmd.MorphKeys / vmd.PropertyKeys  —— 文件顺序关键帧
// vmd.PropertyKeyCount  —— 表示枠键数量
// vmd.LeftoverBytes     —— 解析完所有区段后的剩余字节（非 0 = 文件有非规范尾巴）
```

`VmdParser.Parse` 的结构校验与字段偏移逐行对照 babylon-mmd 的 `vmdObject.js`，
测试以它的解析结果为权威基准。相机 / 光照 / 自阴影区段只计数不解码（本引擎不消费）。

### VmdMotion 结构

| 属性 | 类型 | 说明 |
|---|---|---|
| `ModelName` / `ModelNameRaw` | `string` / `byte[]` | 解码后模型名 / 20B 原始字节（截断到首个 `0x00`） |
| `BoneKeys` | `List<VmdBoneKey>` | 骨骼关键帧，文件顺序（排序在轨道构建时做） |
| `MorphKeys` | `List<VmdMorphKey>` | 表情关键帧，文件顺序 |
| `PropertyKeys` | `List<VmdPropertyKey>` | 表示枠（显示 / 非表示）关键帧，文件顺序 |
| `CameraKeyCount` / `LightKeyCount` / `SelfShadowKeyCount` | `int` | 仅计数的区段 |
| `PropertyKeyCount` | `int` | `= PropertyKeys.Count` |
| `PropertySectionOffset` / `PropertySectionBytes` | `int` | property 分区的字节位置 / 长度（含 IK 开关列表） |
| `LeftoverBytes` | `int` | 非规范尾巴字节数（诊断用） |

### 关键帧类型

```csharp
public readonly record struct VmdBoneKey(
    string BoneName, byte[] NameRaw, uint Frame,
    Vector3 Position, Quaternion Rotation, byte[] Interpolation);

public readonly record struct VmdMorphKey(string MorphName, byte[] NameRaw, uint Frame, float Weight);

public readonly record struct VmdPropertyKey(int Frame, bool Visible, VmdIkState[] IkStates);

public readonly record struct VmdIkState(string BoneName, byte[] NameRaw, bool Enabled);
```

- **`NameRaw`**：名字的原始字节，是解码器无关的权威标识。跨模型绑定时若解码名对不上，
  可用原始字节兜底（见下方 `MmdAnimation.Bind`）。
- **`VmdPropertyKey.Visible`**：可见性极性为 `byte != 0 ⇒ 可见`（与 babylon-mmd 解析层一致，已用 test.vmd 实测）。
- **`VmdIkState`**：property 键附带的 IK 开关。IK 求解器与服务端使能位
  （`SkeletalModel.IkEnabled`，`MmdIkSolver.Solve` 会读）都已就位，但**本引擎尚未把
  property 的 `IkStates` 接过去**——目前只在 `MmdAnimation.IkStates` 里解析保留，
  `IkEnabled` 由转换期统一置 `true`。接线时的落点很明确：按帧取阶梯布尔写进逐链使能位。

### 编码约定（Shift-JIS 932）

VMD 规范用 Shift-JIS 解码所有名字字段。与 babylon-mmd（JS `TextDecoder`）的两处已知差异：

1. **非法字节**：JS 输出 `U+FFFD`，.NET 932 输出 best-fit `'?'`——只可能出现在脏名字里，合法 Shift-JIS 名两者逐字符一致。
2. **截断语义相同**：都在第一个 `0x00` 字节处截断再解码。

> **模型名乱码警示**：写入端若用中文 Windows 的 ANSI(CP936) 写出模型名字段，会被错误当 Shift-JIS 读，
> 表现为半角片假名（`U+FF61`–`U+FF9F`）。模型名不参与任何绑定，仅显示层需兜底；详见
> `docs/2026-09-11-anim-blend-plan.md` 末尾附录。本引擎解析端契约维持 932 不变。

---

## MmdAnimation — 运行时轨道集合

骨 / 表情 / 表示枠在「展开」（`FromVmd`）与「绑定」（`Bind`）两步分离：轨道数据与模型无关，可跨模型复用。

### 构建与绑定

```csharp
// 一步展开 + 绑定（最常见）
MmdAnimation anim = MmdAnimation.Bind(vmd, model);

// 或分步：同一条 VmdMotion 可展开一次、绑定到多个模型
MmdAnimation expanded = MmdAnimation.FromVmd(vmd);
MmdAnimation boundA  = expanded.Bind(modelA);
MmdAnimation boundB  = expanded.Bind(modelB);
```

- **`FromVmd`**：按名字聚合关键帧成轨道。骨骼轨道做**最短弧**处理（相邻键四元数 dot < 0 取负，保证同半球、短弧 slerp）；
  插值字节从 VMD 原 64B 去冗余为每键 16B（按通道分读，避开 MMD 复用 `raw[2]/raw[3]` 存物理开关的坑）。
- **`Bind`**：为 `model` 产出一份绑定实例——找不到对应骨 / 表情的轨道**丢弃**（不同模型共用动效是常态）；
  **数值数组与源轨道共享**（不复制）。同名表情会写入全部同名槽位（MMD 允许重名）。
- **播放区间**：`StartFrame` / `EndFrame` 由骨 / 表情轨道取并，**不受 property 影响**（表示枠是叠加在姿态上的布尔量，不是姿态来源）。

### 轨道类型

| 类型 | 说明 | 采样 |
|---|---|---|
| `MmdBoneTrack` | 单骨旋转 + 父空间平移偏移；贝塞尔缓动 | `Sample(frame)` → `(Quaternion, Vector3)`，纯函数，端键钳制 |
| `MmdMorphTrack` | 单表情权重；**线性**插值（VMD 不存 morph 插值曲线） | `SampleWeight(frame)` |
| `MmdPropertyTrack` | 整模型显示 / 非表示；**阶梯**保持、绝不插值 | `SampleVisible(frame)` |

`MmdBoneTrack` 关键点：

- 帧号严格升序；同帧去重保留文件顺序最后一次出现（与 babylon 一致）。
- 平移是**相对绑定姿势的父空间偏移**（0 = 绑定），写回时由调用方加 `SkeletalModel.LocalPositions`。
- 贝塞尔：`Bezier01(bx1, bx2, by1, by2, t)` 走 MMD 三次曲线，控制点落对角线时退化为恒等（默认线性键）。

`MmdPropertyTrack` 关键点：

- 表示枠是**离散状态**：键间保持、绝不插值；早于首键返回 `true`（未声明「非表示」的动效应照常渲染）。
- 是**整模型**量，不参与「按模型绑定」的过滤，原样透传给绑定实例。
- IK 开关随键保留（`IkStates`），但尚未接到 `SkeletalModel.IkEnabled`（见上）。

---

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

> 注意：`MmdAnimation.Sample` 会**直接改写模型**且复用一个内部 scratch 缓冲，不承诺线程安全。
> 多动效不要经此入口，改用 `SampleInto` + 混合器（[playback.md](playback.md) / [blending.md](blending.md)）。

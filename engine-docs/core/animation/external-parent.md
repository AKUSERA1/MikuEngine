# 外部親绑定（跨模型挂载）

MMD 的「外部親」把一个模型的根挂到**另一个模型**的指定骨骼上（典型场景：角色手里的扇子、
道具、挂件）。绑定生效后子模型的根骨跟随亲骨移动，同时子模型自身的动效继续叠加。

```
VMD（子模型动作文件）→ 骨键里「名字含冒号」的键 → MmdExternalParentKey
                       MmdExternalParentTrack（离散状态机，放在子模型的 MmdAnimation 上）
                       MmdExternalParentController.Update(frame)
                       → SkeletalModel.SetRootParent(亲骨世界矩阵 × 键偏移)
                       → 子模型 FK 时把无父骨挂到该矩阵下
```

## 源文件

| 文件 | 说明 |
|---|---|
| [MmdExternalParentTrack.cs](../../../src/MikuEngine.Core/Animation/MmdExternalParentTrack.cs) | 绑定键 + 离散轨道（采样、构建） |
| [MmdExternalParentController.cs](../../../src/MikuEngine.Core/Animation/MmdExternalParentController.cs) | 跨模型注册表 + 每帧绑定解析 |
| [SkeletalModel.cs](../../../src/MikuEngine.Core/Models/SkeletalModel.cs) | `RootParent` / `SetRootParent`：模型侧的挂载点 |

---

## 1. VMD 里的存储形式

外部親**不是独立区段**，而是藏在子模型动作文件的**骨键轨道「外部親」**里：

- 骨键的 15 B 骨名字段存的是 **`親モデル名:親ボーン名`**（优先全角冒号「：」，半角「:」兜底，
  切在**第一个**冒号上，两侧会去掉首尾空白）；
- 键的 `pos` / `rot` 是叠加在亲骨世界变换**之后**的偏移；
- **名字含冒号的骨键会被解析器拦截**，不进 `VmdMotion.BoneKeys`，而是转成外部親键；
- 任一侧为空串（如 `:`） = **解除绑定**键。

```csharp
public readonly record struct MmdExternalParentKey(
    int Frame,
    string ParentModel,      // 空串 = 解除绑定
    string ParentBone,
    Vector3 OffsetTranslation,
    Quaternion OffsetRotation);
```

> 解析后可用 `VmdMotion.ExternalParentKeys` 取出（文件顺序），
> `MmdAnimation.FromVmd` 会把它构建成 `MmdAnimation.ExternalParentTrack`。

---

## 2. 轨道：离散状态机

`MmdExternalParentTrack` 与表示枠（可见性）同族——**键与键之间保持、绝不插值**：

```csharp
bool bound = track.TrySample(frame, out MmdExternalParentKey key);
// 返回「≤ frame 的最后一键」；区间之前（无键覆盖）返回 false = 未绑定
```

| 成员 | 说明 |
|---|---|
| `Keys` | 按 `Frame` 升序（同帧保持文件顺序） |
| `IsEmpty` | 无键 |
| `EndFrame` | 末键帧号（空轨道为 0） |
| `TrySample(frame, out key)` | 二分上界采样；无覆盖返回 `false` |
| `FromVmd(keys)` | 由键列表构建（稳定升序） |

与骨骼轨道不同的三点：

1. **不做同帧去重**——同一帧的多条键是合法数据（每条一个绑定对，**后者覆盖前者**）；
2. **换亲 = 直接切换**（新键生效即换，无过渡）；解除 = `ParentModel` 为空的键；
3. **无模型绑定**：轨道挂在 `MmdAnimation` 上原样透传（`Bind` 不过滤），
   亲模型按**名字**由控制器解析。

**换模型不需要重建轨道**：亲模型是名字字符串，与 `SkeletalModel` 实例解耦。

---

## 3. 控制器：跨模型绑定

```csharp
var controller = new MmdExternalParentController();

controller.Register("角色",  parentModel);                    // 亲模型（不带动效也能被挂）
controller.Register("扇子",  childModel, childAnimation);      // 子模型 + 其外部親轨道来源

// 每帧（顺序见 §4）
controller.Update(timeline.CurrentFrame);
```

| 成员 | 说明 |
|---|---|
| `Register(name, model, animation = null)` | 注册模型。`animation` = 该模型的外部親轨道来源；亲模型传 `null`（只被挂） |
| `Unregister(name)` | 注销；挂在其上的子模型下一帧自动解绑 |
| `Clear()` | 清空注册表 |
| `Update(frame)` | 对每个注册了动效的模型采样其外部親轨道，写 / 摘根父矩阵 |

`Update` 的判定链（任一不满足 ⇒ 解绑为普通骨架）：

1. 轨道为空 / 采样无键 / `ParentModel` 为空串；
2. 亲模型**未注册**，或 `ParentModel` 指向自己（自绑定）；
3. 亲模型**缺指定骨** ⇒ 不失败，而是挂到**亲模型根**（亲根矩阵恒为单位阵）；
4. 其余情况：`SetRootParent(键偏移 × 亲骨世界矩阵)`。

> `Update` 只做「写 / 摘根父矩阵」，**不触发 FK**——姿态重算由调用方按顺序驱动。

---

## 4. 帧序契约（先亲后子）

子模型读到的是亲骨**本帧**的世界矩阵，因此每帧必须：

```
1. 亲模型：采样 → MmdMorphEvaluator.Evaluate → UpdateWorldMatrices（或 PrepareFrame）
2. controller.Update(frame)                     ← 此时亲骨世界矩阵已定稿
3. 子模型：采样 → MmdMorphEvaluator.Evaluate → UpdateWorldMatrices（或 PrepareFrame）
4. 子模型绘制
```

- **顺序颠倒会让子模型慢一帧**（跟着亲骨上一帧的姿态走），表现为高速运动时的轻微错位。
- 链式绑定（A → B → C）按 `Update` 内条目遍历顺序天然成立；
  深层链请按拓扑序逐帧驱动，或整体排序后再驱动。
- 与渲染层 `PrepareFrame` 的关系：亲模型 `PrepareFrame` 内部已含物理与 FK，
  在第 2 步之前调用即可；子模型在第 2 步之后再 `PrepareFrame`。

---

## 5. 模型侧语义（SkeletalModel.SetRootParent）

```csharp
public Matrix4x4? RootParent { get; }         // null = 普通骨架
public void SetRootParent(Matrix4x4? matrix); // 下一轮 UpdateWorldMatrices 生效
```

绑定生效时：

- **所有无父骨**（含 全ての親）挂到该矩阵下：`WorldMatrices[root] = local' × RootParent`，
  由 `UpdateWorldMatrices` 在 FK 时消费；
- `local'` 的平移会先减去**首个无父骨的绑定姿势世界位置**（从 `InverseBind` 反推）。
  减掉后该骨正好「坐」在亲骨上、其余无父骨保持与它的相对布局；
  不减则模型原点会跑到亲骨上，绑在网格中心的道具会整体偏移；
- 进入路径是**骨骼**而不是模型变换（与 MMD 一致）⇒ **物理仍在模型空间运行**，
  绑在手上的扇子重力依旧朝下；
- 模型变换（移动 / 旋转）的注入因子在 `RootParent` 之后**照常后乘**，两者互不干扰。

> **挂载矩阵是模型空间量**：亲骨的世界矩阵已含亲模型自己的模型变换与物理结果，
> 不要把场景级变换再乘一遍。

---

## 6. 程序化绑定

VMD 里没有外部親键时（MMD 中由用户在 MMD 里手动打键），可直接构造单键轨道，
语义与打键一致：

```csharp
anim.ExternalParentTrack = MmdExternalParentTrack.FromVmd(
[
    new MmdExternalParentKey(
        Frame: 30,
        ParentModel: "角色",
        ParentBone: "左手首",
        OffsetTranslation: Vector3.Zero,     // 叠加在亲骨世界变换之后
        OffsetRotation: Quaternion.Identity),
]);
```

解除绑定：追加一条 `ParentModel = ""` 的键；或 `model.SetRootParent(null)` 直接摘除。

---

## 7. 边界

| 项 | 说明 |
|---|---|
| PMX 的「外部親変形」骨标志（0x2000） | `PmxBone.ExternalParentIndex` 已解析保留，**运行时未消费**；跨模型绑定走本文的 VMD 路径 |
| 亲模型未注册 / 自绑定 | 按未绑定处理（等价于解绑） |
| 亲骨缺失 | 挂亲模型根（不是错误，也不是跳过） |
| 多道具挂同一骨 | 各子模型各自注册一个条目，各自维护自己的轨道 |
| `Update` 开销 | 骨索引按「亲模型名 : 骨名」缓存，`Register` / `Unregister` / `Clear` 会清缓存 |
| 与模型变换 | 两者可同时存在：先挂亲骨，再叠加子模型自己的移动 / 旋转（缩放倍率仍只在渲染层） |

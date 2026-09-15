# 渲染辅助组件

支撑性组件，服务于 GlesModelRenderer 和 GlesShadowRenderer：

- [GlesTextureLibrary + PmxTexturePath + PmxFileResolver](#glestexturelibrary--pmxtexturepath--pmxfileresolver)：纹理加载 / 缓存 / 白色兜底 → 路径归一化 / 磁盘查找
- [GlesSkinMatricesBuffer](#glesskinmatricesbuffer)：蒙皮矩阵 SSBO
- [GlesMorphBuffer](#glesmorphbuffer)：顶点 / UV 表情偏移 SSBO，稀疏脏区上传
- [GlesDebugOverlay](#glesdebugoverlay)：Z 图屏幕预览

## 源文件

| 文件 | 说明 |
|---|---|
| [GlesTextureLibrary.cs](../../src/MikuEngine.Render.GLES/GlesTextureLibrary.cs) | 纹理加载 / 按路径缓存 / 白色兜底 + `PmxFileResolver` |
| [GlesSkinMatricesBuffer.cs](../../src/MikuEngine.Render.GLES/GlesSkinMatricesBuffer.cs) | 蒙皮矩阵 SSBO |
| [GlesMorphBuffer.cs](../../src/MikuEngine.Render.GLES/GlesMorphBuffer.cs) | 顶点 / UV morph 偏移 SSBO（稀疏脏区上传） |
| [GlesDebugOverlay.cs](../../src/MikuEngine.Render.GLES/GlesDebugOverlay.cs) | 屏幕空间调试预览 + [debug_quad.vert.glsl](../../src/MikuEngine.Render.GLES/Shaders/debug_quad.vert.glsl) / [debug_quad.frag.glsl](../../src/MikuEngine.Render.GLES/Shaders/debug_quad.frag.glsl) |

---

## GlesTextureLibrary + PmxTexturePath + PmxFileResolver

### 分工

PMX 里的纹理路径是相对路径，常混用 `\` 与 `/`，部分文件带尾部 NUL，Linux / Android 大小写敏感。
三步分工：

```
PmxTexturePath.Normalize        // 路径清洗（Core 层，纯字符串）
PmxFileResolver.Resolve         // 路径 → 磁盘绝对路径（含大小写不敏感兜底；Render.GLES 层）
GlesTextureLibrary.Load         // 从磁盘文件 → GL 纹理（Render 层）
```

### GlesTextureLibrary

```csharp
var textures = new GlesTextureLibrary(device);

// 加载（失败返回 None = -1，调用方按「无纹理」处理）
int id = textures.Load("C:/model/textures/diffuse.png");
int toonId = textures.Load("C:/model/toon01.png", toon: true);

// 取 GL 句柄
uint glTex = textures.Get(id);     // id 无效时返回 1×1 白色兜底（永远不会返回 0）
bool ok = textures.IsValid(id);    // 显式判有效性
int n = textures.Count;            // 已加载纹理数

// 加载器里按路径自动缓存（同一路径只上传一次）
```

#### 缓存范围（多模型时要注意）

- 去重键是**传入的路径字符串**，比较方式**大小写不敏感**（`OrdinalIgnoreCase`）。
- 缓存是**实例级**的：去重只在同一个 `GlesTextureLibrary` 内生效。
- `GlesModelRenderer.LoadFromFile` 每加载一个模型就 `new GlesTextureLibrary(device)`，
  即**每个模型渲染器各持一份纹理库**。同一 PMX 加载两次，同一套贴图会被上传两次
  （显存重复占用）。
- 需要的跨模型共享需自行处理：把库提升为 per-`GlesDevice` 共享，或由宿主把外部库传给
  渲染器。**注意引用计数**——当前 `Dispose` 会删除库内全部纹理，
  共享后必须先确认没有别的模型仍在用。

#### toon 图 vs 普通图

| | toon 图 | 普通贴图 |
|---|---|---|
| mipmap | 不生成 | 生成 |
| Wrap | CLAMP_TO_EDGE | REPEAT |
| Min/Mag filter | LINEAR | LINEAR_MIPMAP_LINEAR / LINEAR |

#### V 轴处理（重要）

PMX 的 UV 按 **D3D9 左上原点**约定书写。GL 上传时**不能翻转图像**——
D3D9 的行序与 GL 一致，翻转反而让 toon 明暗颠倒。

### PmxFileResolver

```csharp
// 普通纹理：modelDir + 相对路径 → 磁盘绝对路径（失败返回 null）
string? diskPath = PmxFileResolver.Resolve("C:/model", "textures/cloth/01.png");

// 共享 toon（toon01..toon10）：在模型目录及一级子目录里找
string? toonPath = PmxFileResolver.ResolveSharedToon("C:/model", sharedIndex: 0);
// → 先直接找 toon01.png / .bmp / .jpg / .tga，再找子目录
```

内部调用 `PmxTexturePath.Normalize`，然后先精确匹配，再大小写不敏感兜底遍历目录。

---

## GlesSkinMatricesBuffer

蒙皮矩阵 SSBO（shader binding 1）。数百至上千根骨骼的蒙皮矩阵达数十 KB，
会超出 UBO 的最小 16 KB 保证；SSBO 的最小保证是 128 MB。

### Layout

```glsl
// shader:
layout(std430, binding = 1) buffer { mat4 uSkinMatrices[]; }
```

矩阵按 `System.Numerics.Matrix4x4` 内存序原样打包（行主序 row-vector ≡ GLSL 列主序 column-vector），
与 `mat4` 内存一致，**无需 transpose**。

### 每渲染器一份（实际接线）

`GlesModelRenderer` 在构造时创建自己的 `GlesSkinMatricesBuffer`，
`PrepareFrame` 内部完成整段上传：

```csharp
_skin.BeginFrame();                              // 游标归零
SkinMatrixBaseOffset = _skin.Append(model.SkinMatrices);   // 写入本模型矩阵
_skin.Flush();                                   // 整段上传
_skin.Bind(1);
```

⇒ **宿主不需要手动管理它**。`SkinMatrixBaseOffset` 由渲染器自己写入（只读属性），
每个模型渲染器各自持有一份缓冲，多模型互不干扰。

### 类的通用接口（自管理缓冲时用）

```csharp
public uint BufferId { get; }              // GL 缓冲句柄
public int CapacityBoneCount { get; }      // 当前容量（骨数）
public int UsedBoneCount { get; }          // 本帧已写入的骨数
public void EnsureCapacity(int required);
public void BeginFrame();                  // 归零游标
public int Append(ReadOnlySpan<Matrix4x4> skinMatrices);   // 返回该段的 baseOffset
public void Flush();                       // 上传已写入区间
public void Bind(uint binding = 1);
```

`Append` 自动扩容（×2 增长，64 起步），旧缓冲直接丢弃——每帧 `Flush` 都是整段重传，
没有保留旧内容的必要。多个模型的矩阵可以 Append 进同一份缓冲，
用返回的 `baseOffset` 在 shader 里做偏移寻址。

### 与 UBO 的对比

| | UBO | SSBO |
|---|---|---|
| 最小保证大小 | 16 KB | 128 MB |
| 单模型上千骨 | 超出限制 | 可用 |
| std430 vs std140 | 必须 std140 | 推荐 std430 |
| 多模型共存 | 很难 | 天然支持（可 Append 累积） |

---

## GlesMorphBuffer

顶点 / UV 表情偏移 SSBO 的通用实现，一个类实例对应一种元素尺寸：

| 用途 | 绑定 | 元素 | 元素尺寸 |
|---|---|---|---|
| 顶点 morph | binding 2 | `vec4 uMorphOffsets[]`（用 xyz） | 16 B（std430 stride） |
| UV morph | binding 3 | `vec2 uMorphUvs[]` | 8 B |

```csharp
var morph   = new GlesMorphBuffer(device, model.VertexCount, components: 4);   // 顶点
var morphUv = new GlesMorphBuffer(device, model.VertexCount, components: 2);   // UV

// 每帧（PrepareFrame 内）
morph.Update(model, model.MorphWeights);      // 顶点：重算 + 上传脏区
morphUv.UpdateUv(model, model.MorphWeights);  // UV
morph.Bind(2);                                // 顶点 = 2，UV = 3
```

### 内存模型

只有**一份** O(顶点数) 的缓冲，大小与 morph 数量**无关**；每条 morph 的偏移数据仍留在
Core 侧的稀疏表里。不要为每条 morph 复制一份与顶点数等长的稠密数组（那是 O(顶点数 × morph 数)）。

每帧开销同样是稀疏的：

1. 只把「上一帧动过的顶点」清零（不整段 O(V) 清零）；
2. 只遍历「权重非 0」的 morph 的受影响顶点累加；
3. 只上传「上一帧 ∪ 本帧」脏区那一段（`BufferSubData` 带偏移）。

索引一律用 `gl_VertexID`（`glDrawElements` 下它等于**顶点索引**，不是元素序号），
因此不需要额外的索引缓冲。

### 诊断

| 成员 | 说明 |
|---|---|
| `BufferId` | GL 缓冲句柄 |
| `VertexCapacity` | 容量（`max(模型顶点数, 1)`） |
| `LastTouchedVertexCount` | 最近一次 Update 碰过的顶点数 |
| `LastUploadVertexCount` | 最近一次实际上传的区间长度（0 = 本帧无需上传） |

> 即便本帧没有活跃 morph，也必须把上一帧的脏区以「已清零」的版本传一遍，
> 否则 GPU 上会残留上一层表情的偏移（`Update` 内部已处理）。

---

## GlesDebugOverlay

屏幕空间调试预览——把光照深度图直接画到屏幕上。

### 为什么需要

自阴影这类多 pass 功能「没生效」时，肉眼看最终画面无法区分是哪一段断了
（纹理没绑定 / FBO 没附件 / 矩阵错——三种都是「画面没变化」，都不报 GL 错）。
把中间 RT 直接画出来就能快速定位。

### 用法

```csharp
var overlay = new GlesDebugOverlay(device);

// 每帧最后（所有 3D 绘制之后）
overlay.Current = GlesDebugOverlay.View.ZMap;   // Off ↔ ZMap 切换
overlay.Render(shadowRenderer.ZTexture);

// 调节显示增益（深度差很小时临时放大看梯度）
overlay.Gain = 5f;

// 或循环切换视图：Off → ZMap → Off
overlay.Current = overlay.Cycle();
```

### Z 图预览的纹理比较模式切换

Z 图纹理对象上开着 `COMPARE_REF_TO_TEXTURE`（主渲染采样需要）。
`GlesDebugOverlay.Render` 在预览前临时关掉比较模式，画完立刻恢复——成对调用不会泄漏状态。

```
gl.TexParameter(TEXTURE_COMPARE_MODE, NONE);     // 关掉，普通 sampler2D 可采
gl.Uniform4(uRect, -1, -1, 1, 1);                // 全屏覆盖
gl.BindTexture(ZTex);
gl.DrawArrays(TRIANGLES, 0, 6);                  // 空 VAO（program 不读顶点属性）
gl.TexParameter(TEXTURE_COMPARE_MODE, COMPARE_REF_TO_TEXTURE);  // 恢复
```

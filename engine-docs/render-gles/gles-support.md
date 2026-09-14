# 渲染辅助组件

五个支撑性组件，服务于 GlesModelRenderer 和 GlesShadowRenderer：
- [GlesTextureLibrary + PmxTexturePath + PmxFileResolver](#glestexturelibrary--pmxtexturepath--pmxfileresolver)：纹理加载 / 缓存 / 白色兜底 → 路径归一化 / 磁盘查找
- [GlesSkinMatricesBuffer](#glesskinmatricesbuffer)：蒙皮矩阵 SSBO，多模型共用缓冲
- [GlesMorphBuffer](#glesmorphbuffer)：顶点 / UV 表情偏移 SSBO，稀疏脏区上传
- [GlesDebugOverlay](#glesdebugoverlay)：Z 图屏幕预览

---

## GlesTextureLibrary + PmxTexturePath + PmxFileResolver

### 为什么需要这三者

PMX 里的纹理路径是相对路径，常混用 `\` 与 `/`，部分文件带 `.png` 尾的 NUL 填充，Linux/Android 大小写敏感。三步分工：

```
PmxTexturePath.Normalize        // 路径清洗（Core 层，纯字符串）
PmxFileResolver.Resolve         // 路径 → 磁盘绝对路径（含大小写不敏感兜底；Render.GLES 层）
GlesTextureLibrary.Load         // 从磁盘文件 → GL 纹理（Render 层）
```

### GlesTextureLibrary

```csharp
var textures = new GlesTextureLibrary(device);

// 加载（失败返回 None = -1，调用方按"无纹理"处理）
int id = textures.Load("C:/model/textures/diffuse.png");
int toonId = textures.Load("C:/model/toon01.png", toon: true);

// 取 GL 句柄
uint glTex = textures.Get(id);     // id 无效时返回 1×1 白色兜底（永远不会返回 0）

// 加载器里按路径自动缓存（同一路径只上传一次）
```

#### toon 图 vs 普通图

| | toon 图 | 普通贴图 |
|---|---|---|
| mipmap | 不生成 | 生成 |
| Wrap | CLAMP_TO_EDGE | REPEAT |
| Min/Mag filter | LINEAR | LINEAR_MIPMAP_LINEAR / LINEAR |

#### V 轴处理（重要！）

PMX 的 UV 按 **D3D9 左上原点** 约定写的。GL 上传时**不能翻转图像**——D3D9 的行序与 GL 一致，翻转反而让 toon 明暗颠倒。

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

整帧所有模型共用的蒙皮矩阵 SSBO。解决"单模型骨数（536~1099 量级）= 数十 KB 撑爆 UBO 最小 16 KB"的问题——SSBO 最小保证 128 MB。

### Layout

```glsl
// shader:
layout(std430, binding = 1) buffer { mat4 uSkinMatrices[]; }
```

矩阵按 `System.Numerics.Matrix4x4` 内存序原样打包（行主序 row-vector ≡ GLSL 列主序 column-vector），与 `mat4` 内存一致，**无需 transpose**。

### 多模型累积

```csharp
// 多模型场景：BeginFrame 清空游标 → 依次 Append → Flush 一次上传
skin.BeginFrame();

int baseA = skin.Append(modelA.SkinMatrices);
int baseB = skin.Append(modelB.SkinMatrices);

skin.Flush();

// 各 model 把自己的 baseOffset 告诉 shader
modelA.SkinMatrixBaseOffset = baseA;
modelB.SkinMatrixBaseOffset = baseB;
```

`Append` 自动扩容（×2 增长，64 起步），旧缓冲直接丢弃——每帧 `Flush` 都是从 CPU 侧整段重传，没有拷贝必要。

### 与 UBO 的对比

| | UBO | SSBO |
|---|---|---|
| 最小保证大小 | 16 KB | 128 MB |
| 单个模型 1099 骨 | 70 KB → **爆** | 70 KB → OK |
| std430 vs std140 | 必须 std140 | 推荐 std430 |
| 多模型共用 | 很难 | 天然支持（Append 累积） |

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
morph.Bind(2);                                 // 顶点 = 2，UV = 3
```

### 内存模型（与 babylon-mmd 的分野）

只有**一份** O(顶点数) 的缓冲，大小与 morph 数量**无关**；每条 morph 的偏移数据仍留在 Core 侧的稀疏表里。
babylon-mmd 给每条 morph 复制一份与顶点数等长的稠密数组 ⇒ O(顶点数 × morph 数)。

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

屏幕空间调试预览——把光照深度图直接画到屏幕上，作为每步修复的验收判据。

### 为什么需要

自阴影这类多 pass 功能"没生效"时，肉眼看最终画面无法区分是哪一段断了（纹理没绑定 / FBO 没附件 / 矩阵错——三种都是"画面没变化"，都不报 GL 错）。把中间 RT 直接画出来就能快速定位。

### 用法

```csharp
var overlay = new GlesDebugOverlay(device);

// 每帧最后（所有 3D 绘制之后）
overlay.Current = GlesDebugOverlay.View.ZMap;   // Off ↔ ZMap 切换
overlay.Render(shadowRenderer.ZTexture);

// 调节显示增益（深度差很小时临时放大看梯度）
overlay.Gain = 5f;
```

### Z 图预览的纹理比较模式切换

Z 图纹理对象上开着 `COMPARE_REF_TO_TEXTURE`（主渲染采样需要）。`GlesDebugOverlay.Render` 在预览前临时关掉比较模式，画完立刻恢复——成对调用不会泄漏状态。

```
gl.TexParameter(TEXTURE_COMPARE_MODE, NONE);     // 关掉，普通 sampler2D 可采
gl.Uniform4(uRect, -1, -1, 1, 1);                // 全屏覆盖
gl.BindTexture(ZTex);
gl.DrawArrays(TRIANGLES, 0, 6);                  // 空 VAO（program 不读顶点属性）
gl.TexParameter(TEXTURE_COMPARE_MODE, COMPARE_REF_TO_TEXTURE);  // 恢复
```

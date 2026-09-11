# GlesShadowRenderer

自阴影 Z 图 + 床影系统。对齐 PmxEditor 的光源视角 Z 图，但删除了 PE 的屏幕空间影强度图（mask）——主渲染改为内联 PCF 直接采样深度纹理。

## 源文件

[GlesShadowRenderer.cs](../../../src/MikuEngine.Render.GLES/GlesShadowRenderer.cs)  
[shadow.vert.glsl](../../../src/MikuEngine.Render.GLES/Shaders/shadow.vert.glsl)  
[shadow_z.frag.glsl](../../../src/MikuEngine.Render.GLES/Shaders/shadow_z.frag.glsl)  
[shadow_mask.frag.glsl](../../../src/MikuEngine.Render.GLES/Shaders/shadow_mask.frag.glsl)（mask 已删除，保留作为参考）  
[floor_shadow.vert.glsl](../../../src/MikuEngine.Render.GLES/Shaders/floor_shadow.vert.glsl)  
[floor_shadow.frag.glsl](../../../src/MikuEngine.Render.GLES/Shaders/floor_shadow.frag.glsl)

## 三 pass 流程

```
┌─ Pass 1 ──────────────────────────────────────────────────┐
│ Z 图（光照深度图）                                         │
│  FBO: depth-only (DEPTH_COMPONENT24 直挂 depth attachment) │
│  着色器: shadow.vert + shadow_z.frag（输出颜色恒定，只有深度有值）│
│  光栅化: PolygonOffset(factor=1.5, units=2) 消 acne        │
│  绑定: 模型的 VAO / FrameUbo / SkinSsbo                    │
└───────────────────────────────────────────────────────────┘
            │
            ▼
┌─ Pass 2 ──────────────────────────────────────────────────┐
│ 主渲染（modelRenderer.Draw）                               │
│  在 model.frag 里 sampler2DShadow（UNIT 3） 内联 PCF 采  │
│  Z 图，直接算出 lit 分数 → 乘进 diffuse / toon             │
└───────────────────────────────────────────────────────────┘
            │
            ▼
┌─ Pass 3 ──────────────────────────────────────────────────┐
│ 床影（仅 MMD 影模式 2）                                    │
│  四边形 y=0 平面，floor_shadow.vert/frag                  │
│  采 Z 图算出地面上的影区域 → 输出 alpha 到屏幕             │
│  渲染状态: blend 开 / DepthTest 开 / DepthMask = false    │
└───────────────────────────────────────────────────────────┘
```

## 基本用法

```csharp
var shadow = new GlesShadowRenderer(device);
shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadow;  // 或 SelfShadowAndFloor

// 每帧
shadow.UpdateLight(modelRenderer.Model, lightDirection);   // 紧视锥 + texel snapping
frame.LightViewProj = shadow.LightViewProj;

modelRenderer.PrepareFrame(in frame);                      // 帧首统一上传 UBO + SSBO

shadow.RenderShadowMaps(device, modelRenderer, width, height);   // Z pass

modelRenderer.ShadowZTexture = shadow.ZTexture;
modelRenderer.Draw(in frame);                              // 主渲染采 Z 图

if (shadow.Mode == ShadowMode.SelfShadowAndFloor)
    shadow.DrawFloor(device, frame.LightColor);            // 床影
```

---

## 光照深度图

### 为什么是 DEPTH_COMPONENT24（方案 B）

旧版用 RGBA32F 颜色图 + 独立 renderbuffer 深度（双份），显存 28 B/px。新方案只用 `DEPTH_COMPONENT24` 直挂 depth attachment，显存 4 B/px。精度从 32f 变 24 bit 定点——对 0.003 偏置仍绰绰有余。

### 纹理采样参数（方案 B 的核心）

| 参数 | 值 | 作用 |
|---|---|---|
| `TEXTURE_MIN/MAG_FILTER` | `LINEAR` | 比较模式下硬件对 2×2 texel 深度比较结果做双线性插值 → 免费 2×2 PCF |
| `TEXTURE_COMPARE_MODE` | `COMPARE_REF_TO_TEXTURE` | shader 里 `texture(shadowSampler, vec3(uv, ref))` 返回 `ref <= z` 的插值分数 |
| `TEXTURE_COMPARE_FUNC` | `LEQUAL` | 深度比较函数 |
| `TEXTURE_WRAP_S/T` | `CLAMP_TO_EDGE` | 越界返回边缘深度（空处深度=1 → 永远受光，安全） |

---

## UpdateLight：紧视锥 + texel snapping

### 紧视锥（密度目标 ≈ reze 近级联 64 texels/unit）

半宽不是用包围球而是用 AABB 在光空间 right/up 平面上的投影——正交投影下与深度无关，比包围球紧得多：

```
er = h.X * |r.X| + h.Y * |r.Y| + h.Z * |r.Z|     // AABB 在 right 轴上的投影
eu = h.X * |v.X| + h.Y * |v.Y| + h.Z * |v.Z|     // AABB 在 up 轴上的投影
_e = ceil(max(er, eu) / 0.85) + 1                 // 0.85 给淡出带留位 + 1 单位余量 + ceil 稳定量子
texel = 2 * _e / resolution
```

`_e` 整数稳定 → texel 量子稳定 → snapping 才有意义。

### texel snapping（消除影边呼吸）

把目标点吸附到本级 texel 网格：

```
tr = round(dot(center, r) / texel) * texel    // 量化 right 分量
tu = round(dot(center, v) / texel) * texel    // 量化 up 分量
td = dot(center, f)                           // 沿光方向不量化（量化它只会让深度跳变）
target = r * tr + v * tu + f * td
```

动画 / 移动时影边不再"呼吸"。

---

## 深度偏置体系

三侧分工，单一职责避免相互干涉：

| 侧 | 位置 | 作用 | 参数 |
|---|---|---|---|
| Caster（Z pass） | 光栅化斜率偏置 | `PolygonOffset(1.5, 2)` | factor=斜率、units=常数 |
| Receiver（主渲染） | 片元深度比较 | 常数底 + 按面朝向的斜率缩放 | `ShadowBias=0.0005`, `ShadowSlopeBias=2.0`, 封顶 `ShadowBiasMax=0.003` |
| Receiver（法线偏移） | 世界空间位置 | 沿世界法线推离表面再算光空间坐标 | `ShadowNormalOffset`（按世界 texel 接线） |

- **常数底**很小（0.0005），只吃正对面的近距阴影
- **斜率缩放**（默认 2）：掠射面的 `tan(入射角)` 大，斜率项自动增大抑制 acne；正对面 ≈ 0，不误伤近距离阴影
- **封顶**（0.003 = `ShadowMargin`）：陡面偏置不会回退到之前的全屏常数
- 法线偏移专门处理**弧面**（脸 / 裙内）——这些地方 Z pass 的 PolygonOffset 不太够用

---

## MMD 影模式

| 模式 | 说明 |
|---|---|
| 0（Off） | 关闭，主渲染里所有材质都不采影 |
| 1（SelfShadow） | 只自阴影（Z pass + 主渲染内联 PCF） |
| 2（SelfShadowAndFloor） | 自阴影 + 床影（Z pass + 主渲染 + 床影 pass） |

切换：

```csharp
shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadowAndFloor;
```

---

## 调试：GlesDebugOverlay 预览 Z 图

```csharp
var overlay = new GlesDebugOverlay(device);

// 每帧（最后调用）
overlay.Current = GlesDebugOverlay.View.ZMap;   // 或 View.Off
overlay.Render(shadow.ZTexture);
```

预览时临时关掉 Z 图纹理的 `COMPARE_REF_TO_TEXTURE`（主渲染采样需要它），画完立刻恢复——成对调用不会泄漏比较状态。

---

## 常量（对齐 PmxEditor）

| 常量 | 值 | 来源 |
|---|---|---|
| `SelfStrength` | 0.6 | PE fxd L57 |
| `FloorStrength` | 0.7 | PE fxd L58 |
| `ShadowMargin` | 0.003 | PE fxd L59 — 与 `ShadowBiasMax` 一致 |

## 资源清理

```csharp
shadow.Dispose();
// 释放 ZTex / ZFbo / 两个 program / floorVao / floorVbo
```

using System.Numerics;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// 自阴影 / 床影
///
/// 对齐 PmxEditor 的光源视角 Z 图，但已删掉 PE 的屏幕空间影强度图（mask）：
///   1. 光照深度图（tec_z / ZSampler）—— DEPTH_COMPONENT24 深度纹理直挂 depth attachment，
///      开 LINEAR + COMPARE_REF_TO_TEXTURE + LEQUAL：shader 每次 texture() 硬件对 2×2 texel
///      做二值深度比较后双线性插值（免费 2×2 PCF）。Z pass 不再写颜色（省整张 32f 颜色图）。
///   2. （已删除）屏幕空间影强度图 —— 主渲染为内联采样直接用比较采样器消费 1.：
///      硬边本影 3×3 高斯（阈值提取）、标准 16-tap PCF / 软影 9-tap PCF。
/// 另有床影（tec_floorShadow），对应 MMD 影模式 2。
///
/// 删 mask 的理由：
///   不需要 MME 补正型阴影兼容，mask 的唯一价值（供 ExcellentShadow 类后处理读取）不复存在；
///   内联 PCF 每片元用自己的位置比深度，多模型下不存在 mask 的"读到别人判定"串扰。
///   收益：每模型 3 pass → 2 pass、省 mask FBO/纹理、省 uCameraSpace 双视角分支。
///
/// MMD 影模式：0=关（默认）/ 1=自阴影 / 2=自阴影+床影
///
/// TODO："共享光空间图"的多模型共用 + "稳定区域"（量化/贴 texel 网格）需等
///   场景/多模型支持落地后再做；当前单模型下 UpdateLight 仍按单个模型包围盒拟合。
/// TODO：stage/prop 特权化、按光源视锥剔除整模型 —— 同样依赖场景支持。
/// </summary>
public sealed unsafe class GlesShadowRenderer : IDisposable
{
    /// <summary>MMD 影模式。</summary>
    public enum ShadowMode
    {
        Off = 0,
        SelfShadow = 1,
        SelfShadowAndFloor = 2,
    }

    /// <summary>影图分辨率档位。改档触发 FBO 延迟重建。</summary>
    public enum ShadowResolution
    {
        R512 = 512,
        R1024 = 1024,
        R2048 = 2048,
        R4096 = 4096,
    }

    /// <summary>PE g_selfStrength（fxd L57）。</summary>
    public const float SelfStrength = 0.6f;

    /// <summary>PE g_floorStrength（fxd L58）。</summary>
    public const float FloorStrength = 0.7f;

    /// <summary>PE g_shadowMargin（fxd L59）。</summary>
    public const float ShadowMargin = 0.003f;

    private readonly GlesDevice _device;
    private int _size = 2048;               // 影图分辨率（正方形）
    private float _e = 16f;                 // 当前正交半宽（UpdateLight 算出；TexelWorld 用）

    private uint _zTex, _zFbo;              // 光照深度图（DEPTH_COMPONENT24）
    private uint _zProg, _floorProg;
    private uint _floorVao, _floorVbo;

    private readonly Dictionary<string, int> _u = new();

    /// <summary>当前影模式，默认关闭。</summary>
    public ShadowMode Mode { get; set; } = ShadowMode.Off;

    /// <summary>本帧算出的光照 ViewProj（Demo 要把它填进 FrameUniforms.LightViewProj）。</summary>
    public Matrix4x4 LightViewProj { get; private set; } = Matrix4x4.Identity;

    public bool Enabled => Mode != ShadowMode.Off;

    /// <summary>
    /// 光照深度图（DEPTH_COMPONENT24，比较采样器用）。主渲染绑到 unit 3：
    /// model.frag / floor_shadow 用 sampler2DShadow 采样（硬件双线性深度比较）。
    /// 注意：纹理对象上开着 COMPARE_REF_TO_TEXTURE —— 普通 sampler2D 不能采它；
    /// 调试预览（GlesDebugOverlay）会临时关掉比较模式再恢复。
    /// </summary>
    public uint ZTexture => _zTex;

    /// <summary>1/影图边长。主渲染与床影的 PCF 核缩放（单一来源，不再硬编码 1/1024）。</summary>
    public float Texel => 1f / _size;

    /// <summary>一个影图 texel 覆盖的世界尺寸（= 2 半宽/分辨率）。法线偏移等"按世界 texel 接线"用。</summary>
    public float TexelWorld => 2f * _e / _size;

    /// <summary>影图分辨率（正方形）。写入触发 FBO 延迟重建。</summary>
    public int Resolution
    {
        get => _size;
        set
        {
            int v = Math.Clamp(value, 512, 4096);
            if (v == _size) return;
            _size = v;
            RecreateTargets();
        }
    }

    public GlesShadowRenderer(GlesDevice device)
    {
        _device = device;
        var gl = device.Gl;

        _zProg = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.shadow.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.shadow_z.frag.glsl");
        _floorProg = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.floor_shadow.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.floor_shadow.frag.glsl");

        CreateTargets();

        // 地面矩形（MMD 地面 = y=0 平面），足够大以覆盖光照视锥
        float e = 300f;
        float[] quad =
        {
            -e, 0, -e,   e, 0, -e,   -e, 0,  e,    // tri 1
             e, 0, -e,   e, 0,  e,   -e, 0,  e,    // tri 2
        };
        _floorVbo = gl.CreateBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _floorVbo);
        fixed (float* p = quad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        _floorVao = gl.CreateVertexArray();
        gl.BindVertexArray(_floorVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _floorVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        gl.BindVertexArray(0);
    }

    private void CreateTargets()
    {
        var gl = _device.Gl;
        int s = _size;

        // 光照深度图（方案 B）：DEPTH_COMPONENT24 深度纹理直挂 depth attachment，
        // Z pass 不再写颜色。相比旧版（RGBA32F 颜色 + renderbuffer 深度双份）：
        //   1. 显存省 16 MB/s（2048²：旧 16(color)+12(rbo)=28 B/px → 新 4 B/px）
        //   2. 深度精度从 32f 变 24bit 定点 —— 对 0.003 偏置（≈1.5e-4 NDC/级）仍绰绰有余
        _zTex = gl.CreateTexture(TextureTarget.Texture2D);
        gl.BindTexture(TextureTarget.Texture2D, _zTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)s, (uint)s, 0,
            PixelFormat.DepthComponent, PixelType.UnsignedInt, (void*)0);
        SetShadowCompareClamp();

        _zFbo = gl.CreateFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _zFbo);
        // 只挂深度附件 —— ES 3.0+ 允许 depth-only FBO（draw buffer 默认 GL_NONE）
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _zTex, 0);
        CheckFramebuffer("Z 图");

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void DestroyTargets()
    {
        var gl = _device.Gl;
        if (_zTex != 0) { gl.DeleteTexture(_zTex); _zTex = 0; }
        if (_zFbo != 0) { gl.DeleteFramebuffer(_zFbo); _zFbo = 0; }
    }

    private void RecreateTargets()
    {
        DestroyTargets();
        CreateTargets();
    }

    /// <summary>
    /// 诊断：FBO complete 检查。缺附件/尺寸不匹配时 GL 只会静默丢弃全部绘制。
    /// </summary>
    private void CheckFramebuffer(string name)
    {
        var st = _device.Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (st != GLEnum.FramebufferComplete)
            Console.WriteLine($"[GlesShadowRenderer] {name} FBO 不完整：{st}");
    }

    /// <summary>
    /// Z 图的采样参数（方案 B 的核心）：
    ///   1. LINEAR —— 比较模式下硬件对 2×2 texel 的比较结果做双线性插值（免费 2×2 PCF）
    ///   2. COMPARE_REF_TO_TEXTURE + LEQUAL —— shader 里 texture(shadowSampler, vec3(uv, ref))
    ///     返回 (ref <= s) 的插值结果，即"lit 分数"，与旧版手写 (s >= z) 同义
    ///   1. CLAMP_TO_EDGE —— 越界采样返回边缘深度（背景=1 ⇒ 永远受光，安全）
    /// </summary>
    private void SetShadowCompareClamp()
    {
        var gl = _device.Gl;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
    }

    /// <summary>
    /// 由光源方向 + 模型包围盒算出光照 ViewProj（正交投影，复用 OrbitCamera 的左手系约定）。
    ///
    /// reze两件套（锐度 + 时间平滑）：
    ///   1. 紧视锥：半宽 = AABB 半尺寸在光空间 right/up 上的投影（比包围球紧得多），
    ///      除以 0.85 给淡出带留位、再 ceil 到整数单位 —— e 稳定 ⇒ texel 量子稳定 ⇒
    ///      snapping 才有意义。密度目标 ≈ reze 近级联的 64 texels/unit。
    ///   2. texel snapping：目标点吸附到本级 texel 网格（只量化 right/up 平面，
    ///      沿光方向不量化 —— 量化它只会让深度跳变）。动画/移动时影边不再"呼吸"。
    /// 注意：包围盒是绑定姿势的（converter 算好即静态），故每模型调一次即可；
    /// 若未来改为逐帧姿态包围盒，须每帧调用并保持 1. 的量化稳定。
    /// </summary>
    public void UpdateLight(MikuEngine.Core.Models.SkeletalModel model, Vector3 lightDirection)
    {
        var center = model.BoundsCenter;
        var h = model.BoundsSize * 0.5f;                          // AABB 半尺寸
        float sphereR = MathF.Max(model.BoundsSize.Length() * 0.6f, 15f);

        Vector3 dir = Vector3.Normalize(lightDirection);          // 光传播方向
        Vector3 up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.95f
            ? Vector3.UnitZ
            : Vector3.UnitY;

        // 基向量（与 LookAt 内部一致），供紧视锥投影与 snapping 使用
        Vector3 f = dir;
        Vector3 r = Vector3.Normalize(Vector3.Cross(up, f));
        Vector3 v = Vector3.Normalize(Vector3.Cross(f, r));

        // ── 1. 紧视锥：AABB 在光空间平面上的投影半径（正交投影下与深度无关）──────
        float er = h.X * MathF.Abs(r.X) + h.Y * MathF.Abs(r.Y) + h.Z * MathF.Abs(r.Z);
        float eu = h.X * MathF.Abs(v.X) + h.Y * MathF.Abs(v.Y) + h.Z * MathF.Abs(v.Z);
        // 模型边缘 ≤ 0.85（淡出带 0.88→0.96 在模型外），+1 单位余量，ceil 整数稳定量子
        _e = MathF.Ceiling(MathF.Max(er, eu) / 0.85f) + 1f;
        float texel = 2f * _e / _size;

        // ── 2. texel snapping：目标点吸附到 texel 网格 ─────────────────────
        float tr = MathF.Round(Vector3.Dot(center, r) / texel) * texel;
        float tu = MathF.Round(Vector3.Dot(center, v) / texel) * texel;
        float td = Vector3.Dot(center, f);                        // 沿光方向不量化
        var target = r * tr + v * tu + f * td;

        // 深度范围按 AABB 沿光方向的投影收紧（z 精度更好），床影地面深度也覆盖在内
        float ef = h.X * MathF.Abs(f.X) + h.Y * MathF.Abs(f.Y) + h.Z * MathF.Abs(f.Z);
        float dist = sphereR * 4f;                                // 眼到模型中心（沿用旧距离）
        float near = MathF.Max(dist - ef * 2f - 4f, 0.1f);
        float far = dist + ef * 2f + 4f;

        // 注意：顺序必须是 view * proj，不能写 proj * view（已经数值验证）。
        var view = LookAt(target - f * dist, target, up);
        var proj = Orthographic(_e, near, far);
        LightViewProj = view * proj;
    }

    /// <summary>左手系正交投影：z_view ∈ [near, far] → z_ndc ∈ [-1, 1]。列主序内存。</summary>
    private static Matrix4x4 Orthographic(float halfExtent, float near, float far)
    {
        float ri = 1f / (far - near);
        return new Matrix4x4(
            1f / halfExtent, 0f, 0f, 0f,
            0f, 1f / halfExtent, 0f, 0f,
            0f, 0f, 2f * ri, 0f,
            0f, 0f, -(near + far) * ri, 1f);
    }

    private static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up)
    {
        Vector3 f = target - eye;
        float fl = f.Length();
        f = fl > 0f ? f / fl : Vector3.Zero;
        Vector3 r = Vector3.Normalize(Vector3.Cross(up, f));
        Vector3 v = Vector3.Normalize(Vector3.Cross(f, r));

        return new Matrix4x4(
            r.X, v.X, f.X, 0f,
            r.Y, v.Y, f.Y, 0f,
            r.Z, v.Z, f.Z, 0f,
            -Vector3.Dot(r, eye), -Vector3.Dot(v, eye), -Vector3.Dot(f, eye), 1f);
    }

    /// <summary>
    /// 渲染光照深度图（Z 图）。必须在 model.PrepareFrame 之后、model.Draw 之前调用
    /// （它复用的 UBO/SSBO 与主渲染一致；帧首统一上传由 PrepareFrame 完成，见前述）。
    /// </summary>
    public void RenderShadowMaps(GlesDevice device, GlesModelRenderer model, int viewW, int viewH)
    {
        if (Mode == ShadowMode.Off) return;
        var gl = device.Gl;

        // 表示枠「非表示」：跳过 caster（模型不可见就不该投影）。
        // 注意 Z 图是自阴影接收侧与床影<b>共享</b>的资源，只早退会留下上一帧的深度 ——
        // 屏幕上仍会出现一个已经不可见的模型的影子。因此这里清空深度再退
        // （reze-engine 是整 pass 跳过；本引擎有床影共用同一张图，故多一步 clear）。
        if (!model.ModelVisible)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _zFbo);
            gl.Viewport(0, 0, (uint)_size, (uint)_size);
            gl.DepthMask(true);                 // glClear 受深度写掩码影响，显式置位
            gl.ClearDepth(1f);                  // 空处 z=1（采样即「不在影里」）
            gl.Clear(ClearBufferMask.DepthBufferBit);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.Viewport(0, 0, (uint)viewW, (uint)viewH);
            return;
        }

        var m = model.Model;
        int baseOffset = model.SkinMatrixBaseOffset;
        AttachModelBuffers(model.FrameUbo, model.SkinSsbo);
        gl.BindVertexArray(model.Vao);            // 顶点/索引来自模型的 VAO

        // ── 1. 光照深度图（depth-only：无颜色附件，只需清深度）──────────
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _zFbo);
        gl.Viewport(0, 0, (uint)_size, (uint)_size);
        gl.ClearDepth(1f);                             // 空处 z=1（PE: ZSampler 默认 1 → 不在影里）
        gl.Clear(ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);                // 影图不剔除，避免单面材质投不出影

        gl.UseProgram(_zProg);
        BindCommon(_zProg);
        gl.Uniform1(U(_zProg, "uSkinMatBase"), (float)baseOffset);
        // 顶点 morph：Z pass 与主渲染共用同一份偏移，否则表情变形后影子会对不上。
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, model.MorphSsbo);
        gl.Uniform1(U(_zProg, "uMorphEnabled"), model.MorphEnabled ? 1f : 0f);
        int texUnit = 0; gl.Uniform1(U(_zProg, "uDiffuseTex"), texUnit);

        // 光栅化斜率偏置：factor=斜率 1.5 / units=常数 2。
        // 正值把 caster 深度推离光源 ⇒ 受光判定更容易 ⇒ 消斜面 acne；与法线偏移（接收侧）、
        // 比较侧余量（「常数底 + 按面朝向的斜率缩放」，见 model.frag.glsl）分工。
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1.5f, 2f);

        foreach (var seg in m.Segments)
        {
            if (seg.IndexCount <= 0) continue;
            if (!seg.CastsShadow) continue;            // PMX 材质旗标 bit2（EnabledDrawShadow）
            gl.Uniform1(U(_zProg, "uEnableTexture"), seg.EnableTexture ? 1f : 0f);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, model.Textures.Get(seg.DiffuseTextureId));
            gl.DrawElements(PrimitiveType.Triangles, (uint)seg.IndexCount, DrawElementsType.UnsignedInt,
                (void*)(seg.IndexStart * sizeof(uint)));
        }

        gl.Disable(EnableCap.PolygonOffsetFill);

        // ── 还原 ───────────────────────────────────────────────────────
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)viewW, (uint)viewH);
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void BindCommon(uint prog)
    {
        var gl = _device.Gl;
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _modelFrameUbo);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, _modelSkinSsbo);
        // 注意：顶点数据仍来自模型的 VAO，由调用方先绑好
    }

    /// <summary>
    /// 读回光照深度图并打印统计量（只有 Z 图，影强度图已删除）。
    /// 判读：覆盖率应≈模型在光源视角下的剪影占比（本仓模型约 3~5%），
    /// z 应明显小于 1.000（1.000 = Clear 空值）。覆盖 0% / z 恒 1.000 ⇒ Z pass 无产出。
    /// </summary>
    public void DumpMapStats()
    {
        if (Mode == ShadowMode.Off)
        {
            Console.WriteLine("[Shadow] 影模式 = 0（关），Z 图未渲染，统计无意义");
            return;
        }

        var gl = _device.Gl;
        int n = _size * _size;

        // 深度纹理读回：DEPTH_COMPONENT 格式（旧版读颜色的 RGBA 已不适用）
        var z = new float[n];
        fixed (float* p = z)
        {
            gl.BindTexture(TextureTarget.Texture2D, _zTex);
            gl.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.DepthComponent, PixelType.Float, p);
        }
        int covered = 0;
        float zmin = float.MaxValue, zmax = float.MinValue;
        double zsum = 0;
        for (int i = 0; i < n; i++)
        {
            float v = z[i];
            if (v < 0.99f)
            {
                covered++;
                zsum += v;
                if (v < zmin) zmin = v;
            }
            if (v > zmax) zmax = v;
        }
        float zmean = covered > 0 ? (float)(zsum / covered) : 1f;

        Console.WriteLine($"[Shadow] ① Z 图     : 覆盖 {covered * 100.0 / n:F1}%  z∈[{zmin:F3}, {zmax:F3}]  覆盖区均值 {zmean:F3}" +
                          "   （1.000 = Clear 空值；覆盖 0% 即该 pass 无产出）");
    }

    private uint _modelFrameUbo;
    private uint _modelSkinSsbo;

    /// <summary>由 model renderer 注入，影 pass 复用同一份 UBO/SSBO。</summary>
    public void AttachModelBuffers(uint frameUbo, uint skinSsbo)
    {
        _modelFrameUbo = frameUbo;
        _modelSkinSsbo = skinSsbo;
    }

    /// <summary>
    /// 床影（MMD 影模式 2）。必须**自己设置渲染状态**，不沿用上一个 pass 的残留
    /// （PE tec_floorShadow 的 AlphaBlend/SRCALPHA/INVSRCALPHA + DrawModel_Shadow 的 ZWRITEENABLE=false）。
    /// </summary>
    public void DrawFloor(GlesDevice device, Vector3 lightColor)
    {
        if (Mode != ShadowMode.SelfShadowAndFloor) return;
        var gl = _device.Gl;

        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);      // PE 保留 ZEnable：影要被模型正确遮挡
        gl.DepthMask(false);                     // PE: ZWRITEENABLE = false
        gl.Disable(EnableCap.CullFace);          // PE: 地板四边形 CULL_NONE

        gl.UseProgram(_floorProg);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _modelFrameUbo);
        int tu = 3; gl.Uniform1(U(_floorProg, "uShadowZMap"), tu);
        gl.ActiveTexture(TextureUnit.Texture3);
        gl.BindTexture(TextureTarget.Texture2D, _zTex);
        gl.Uniform1(U(_floorProg, "uShadowTexel"), Texel);   // 单一来源
        gl.Uniform4(U(_floorProg, "uLightColor"), lightColor.X, lightColor.Y, lightColor.Z, 1f);
        gl.Uniform1(U(_floorProg, "uFloorStrength"), FloorStrength);

        gl.BindVertexArray(_floorVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.BindVertexArray(0);

        // 还原：不要把"不写深度 + 开混合"泄漏给后续 pass / 下一帧
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
    }

    private int U(uint prog, string name) =>
        _u.TryGetValue(prog + name, out int v) ? v : _u[prog + name] = _device.Gl.GetUniformLocation(prog, name);

    public void Dispose()
    {
        var gl = _device.Gl;
        DestroyTargets();
        gl.DeleteProgram(_zProg);
        gl.DeleteProgram(_floorProg);
        gl.DeleteVertexArray(_floorVao);
        gl.DeleteBuffer(_floorVbo);
    }
}

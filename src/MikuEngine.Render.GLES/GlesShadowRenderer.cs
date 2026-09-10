using System.Numerics;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// 自阴影 / 床影。对齐 PmxEditor 的三段式：
///   ① 光照深度图（tec_z / ZSampler）—— 从【光源】视角渲染 z/w，带深度附件
///   ② 影强度图（tec_selfShadow / ShadowSampler）—— 从【相机】视角光栅化（只有深度用光源的），
///      每个相机可见片元 16 点采样 ① 判断自己是否被挡住，写出一张【屏幕空间】影强度图
///   ③ 主渲染按片元自己的屏幕坐标取回 ② ，替换 toon 分支（PS1 L563-586）
/// 另有床影（tec_floorShadow），对应 MMD 影模式 2。
///
/// ① 与 ② 的光栅化视角【必须不同】，这是 PE 实现的核心（见 shadow.vert.glsl 的 uCameraSpace）：
/// 若 ② 也从光源光栅化，只有离光最近的面写得进去，而它们按定义就是受光的，
/// 相机看得见但被遮挡的面永远读不到自己的判定 → 投影阴影信息整体丢失。
///
/// MMD 影模式：0=关（默认）/ 1=自阴影 / 2=自阴影+床影
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

    /// <summary>PE g_selfStrength（fxd L57）。</summary>
    public const float SelfStrength = 0.6f;

    /// <summary>PE g_floorStrength（fxd L58）。</summary>
    public const float FloorStrength = 0.7f;

    /// <summary>PE g_shadowMargin（fxd L59）。</summary>
    public const float ShadowMargin = 0.003f;

    private readonly GlesDevice _device;
    private readonly int _size = 1024;          // 影图分辨率（正方形）

    private uint _zTex, _zFbo;                  // 光照深度图（R 通道存 z/w）
    private uint _zDepthRbo;                    // 光照深度图的深度附件（P0-4）
    private uint _maskTex, _maskFbo;            // 影强度图
    private uint _maskDepthRbo;                 // 影强度图的深度附件（P0-5）
    private uint _zProg, _maskProg, _floorProg;
    private uint _floorVao, _floorVbo;

    private readonly Dictionary<string, int> _u = new();

    /// <summary>当前影模式，默认关闭。</summary>
    public ShadowMode Mode { get; set; } = ShadowMode.Off;

    /// <summary>本帧算出的光照 ViewProj（Demo 要把它填进 FrameUniforms.Lights）。</summary>
    public Matrix4x4 LightViewProj { get; private set; } = Matrix4x4.Identity;

    public bool Enabled => Mode != ShadowMode.Off;

    /// <summary>① 光照深度图（R 通道存 z/w）。调试预览用。</summary>
    public uint ZTexture => _zTex;

    /// <summary>
    /// ② 影强度图（R 通道：0 受光 ~ 1 全影）。调试预览用。
    /// ⚠️ 主渲染侧的 <c>uShadowMap</c> 目前【没有】绑定它（P0-1，待第 5 步修复），
    ///    本属性当前只服务于 <see cref="GlesDebugOverlay"/>。
    /// </summary>
    public uint MaskTexture => _maskTex;

    public GlesShadowRenderer(GlesDevice device)
    {
        _device = device;
        var gl = device.Gl;

        _zProg = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.shadow.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.shadow_z.frag.glsl");
        _maskProg = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.shadow.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.shadow_strength.frag.glsl");
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

        // 光照深度图（RGBA32F；桌面 GL 可渲染，Android 需 EXT_color_buffer_float）
        _zTex = gl.CreateTexture(TextureTarget.Texture2D);
        gl.BindTexture(TextureTarget.Texture2D, _zTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)s, (uint)s, 0,
            PixelFormat.Rgba, PixelType.Float, (void*)0);
        SetNearestClamp();
        _zFbo = gl.CreateFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _zFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _zTex, 0);

        // 深度附件（P0-4 修复）。
        // GL 规定「FBO 没有深度缓冲时，深度测试视为恒通过」—— 也就是说缺了这个 renderbuffer，
        // Z pass 里的 Enable(DepthTest)/DepthFunc(Lequal)/DepthMask(true) 全是空操作，
        // Z 图会退化成"按绘制顺序后写覆盖"的最后一层三角面，而不是深度图（且不报任何错）。
        // PE 侧对应物是 m_ZStencil = Surface.CreateDepthStencil(...) + ChangeZRenderTarget()。
        _zDepthRbo = gl.CreateRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _zDepthRbo);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)s, (uint)s);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _zDepthRbo);
        CheckFramebuffer("Z 图");

        // 影强度图
        _maskTex = gl.CreateTexture(TextureTarget.Texture2D);
        gl.BindTexture(TextureTarget.Texture2D, _maskTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)s, (uint)s, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
        SetNearestClamp();
        _maskFbo = gl.CreateFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _maskTex, 0);

        // 影强度图也要深度附件（P0-5 的一半）：它现在从【相机】光栅化，
        // 必须让"离相机最近的那个片元"胜出 —— 也就是主渲染真正看得见的那张皮。
        // 没有深度附件时该 pass 是"后写覆盖"，写进去的是绘制顺序决定的随机面。
        // PE 侧对应物：m_shadowRenderTexture.TargetStencil（DrawSelfShadow 里 ZEnable=true）。
        _maskDepthRbo = gl.CreateRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _maskDepthRbo);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)s, (uint)s);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _maskDepthRbo);
        CheckFramebuffer("影强度图");

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// 诊断：FBO complete 检查。缺附件/尺寸不匹配时 GL 只会静默丢弃全部绘制，
    /// 不查的话表现和"功能没生效"一模一样。
    /// </summary>
    private void CheckFramebuffer(string name)
    {
        var st = _device.Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (st != GLEnum.FramebufferComplete)
            Console.WriteLine($"[GlesShadowRenderer] ⚠️ {name} FBO 不完整：{st}");
        else
            Console.WriteLine($"[GlesShadowRenderer] {name} FBO complete（{_size}²）");
    }

    private void SetNearestClamp()
    {
        var gl = _device.Gl;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        // ⚠️ 必须 CLAMP_TO_EDGE：越界采样会返回 0（= 永远在影里），整个模型会变黑
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    /// <summary>
    /// 由光源方向 + 模型包围盒算出光照 ViewProj。
    /// 用【正交】投影，直接复用 OrbitCamera 的左手系约定。
    /// </summary>
    public void UpdateLight(MikuEngine.Core.Models.SkeletalModel model, Vector3 lightDirection)
    {
        var center = model.BoundsCenter;
        float radius = MathF.Max(model.BoundsSize.Length() * 0.6f, 15f);

        Vector3 dir = Vector3.Normalize(lightDirection);          // 光传播方向
        Vector3 eye = center - dir * radius * 4f;                 // 退到光的反方向
        Vector3 up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.95f
            ? Vector3.UnitZ
            : Vector3.UnitY;

        // 用【正交】投影，且视锥必须完全包住模型：
        // 之前用 fov 15° 的窄角透视，在 6r 距离处视锥半高只有 0.79r，比模型半径还小，
        // 模型出界部分采不到有效深度（Z 图是 1.0），会导致影判定错乱。
        float e = MathF.Max(radius * 1.4f, 15f);                  // 正交半宽
        float dist = radius * 4f;                                 // 眼到模型中心
        float near = MathF.Max(dist - 2.5f * e, 0.1f);
        float far = dist + 2.5f * e;

        // 约定与 OrbitCamera 完全一致（列主序 / LH / z_ndc ∈ [-1,1]）
        var view = LookAt(eye, center, up);
        var proj = Orthographic(e, near, far);

        // ⚠️ 顺序必须是 view * proj，不能写 proj * view（P0-2）。
        //
        // 原因：Matrix4x4 在本仓库是"列主序 GL 布局的【容器】"，而 System.Numerics 的
        // operator* 是【行向量语义】—— (p·A)·B == p·(A*B)，即乘号右边后作用。
        // GLSL 端按列主序读这 16 个 float，等价于用 A^T 做 M·p，于是
        //     容器里存 proj*view  ⇒ GLSL 实际矩阵 = (P·V)^T = V·P   ← 顺序反了
        // 数值实测（模型中心 (0,12,0)、e=21、near=7.5、far=112.5）：
        //     用 proj*view → NDC = (-0.808, -5.938, 69.798)  → 整个模型被裁掉
        //     用 view*proj → NDC = ( 0.000,  0.000,  ...  )  → 中心归位
        // OrbitCamera.ComputeViewProj 用自写的 MultiplyColumnMajor(proj, view) 才是等价的
        // 列主序写法；这里沿用 System.Numerics 语义，两种写法各写各的算法，不要互相照抄注释。
        LightViewProj = view * proj;
    }

    /// <summary>
    /// 左手系正交投影：z_view ∈ [near, far] → z_ndc ∈ [-1, 1]。
    /// 列主序内存布局（与 <see cref="OrbitCamera"/> / model.vert 的 mat4 约定一致）。
    ///
    /// ⚠️ M33 是 +2/(far-near)，不是 GL 教科书里的 -2/(f-n)（P0-3）。
    ///    那个负号配的是【右手系】视空间（相机看向 -Z，前方 z_view &lt; 0）；
    ///    而本仓 LookAt（以及 MMD/PE）是【左手系】，前方 z_view &gt; 0。
    ///    照抄负号会让 z_ndc = -2z/(f-n) - (n+f)/(f-n)，解 z_ndc ∈ [-1,1] 得
    ///    z_view ∈ [-far, -near] —— 裁剪体整个落在光源相机【背后】，
    ///    可见几何（z_view ≈ 60）全部被近/远平面裁掉，Z 图与影强度图都光栅化不出任何东西。
    /// </summary>
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

        // 列主序语义；System.Numerics 的字段序与 GLSL mat4 内存一致
        return new Matrix4x4(
            r.X, v.X, f.X, 0f,
            r.Y, v.Y, f.Y, 0f,
            r.Z, v.Z, f.Z, 0f,
            -Vector3.Dot(r, eye), -Vector3.Dot(v, eye), -Vector3.Dot(f, eye), 1f);
    }

    /// <summary>
    /// 渲染 Z 图与影强度图。必须在 model.Draw 之前调用（它绑定的 UBO/SSBO 与主渲染一致）。
    /// </summary>
    public void RenderShadowMaps(GlesDevice device, GlesModelRenderer model, int viewW, int viewH)
    {
        if (Mode == ShadowMode.Off) return;
        var gl = device.Gl;
        var m = model.Model;
        int baseOffset = model.SkinMatrixBaseOffset;
        AttachModelBuffers(model.FrameUbo, model.SkinSsbo);
        gl.BindVertexArray(model.Vao);            // 顶点/索引来自模型的 VAO

        // ── ① 光照深度图 ────────────────────────────────────────────────
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _zFbo);
        gl.Viewport(0, 0, (uint)_size, (uint)_size);
        gl.ClearColor(1f, 1f, 1f, 1f);                 // 空处 z=1（PE: ZSampler 默认 1 → 不在影里）
        gl.ClearDepth(1f);                             // 显式，不依赖驱动默认值（GL 默认也是 1，但别赌）
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);                // 影图不剔除，避免单面材质投不出影
                                                       // （有深度测试后，光背面即使被光栅化也会输给近面）

        gl.UseProgram(_zProg);
        BindCommon(_zProg);
        gl.Uniform1(U(_zProg, "uCameraSpace"), 0f);   // ① 从光源光栅化
        gl.Uniform1(U(_zProg, "uSkinMatBase"), (float)baseOffset);
        int texUnit = 0; gl.Uniform1(U(_zProg, "uDiffuseTex"), texUnit);
        foreach (var seg in m.Segments)
        {
            if (seg.IndexCount <= 0) continue;
            gl.Uniform1(U(_zProg, "uEnableTexture"), seg.EnableTexture ? 1f : 0f);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, model.Textures.Get(seg.DiffuseTextureId));
            gl.DrawElements(PrimitiveType.Triangles, (uint)seg.IndexCount, DrawElementsType.UnsignedInt,
                (void*)(seg.IndexStart * sizeof(uint)));
        }

        // ── ② 影强度图 ──────────────────────────────────────────────────
        // 从【相机】光栅化（P0-5 修复），写出的是一张【屏幕空间】的影强度图。
        // 主渲染按片元自己的屏幕坐标取回它 —— 与 PE PS1 的
        //   uv = getShadowTexPos(p_in.Depth)   // Depth = 相机裁剪坐标
        // 是同一件事。
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
        gl.ClearColor(0f, 0f, 0f, 1f);                 // 空处 0 = 受光
        gl.ClearDepth(1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(true);

        gl.UseProgram(_maskProg);
        BindCommon(_maskProg);
        gl.Uniform1(U(_maskProg, "uCameraSpace"), 1f);   // ② 从相机光栅化
        gl.Uniform1(U(_maskProg, "uSkinMatBase"), (float)baseOffset);
        texUnit = 0; gl.Uniform1(U(_maskProg, "uDiffuseTex"), texUnit);
        int tu = 3; gl.Uniform1(U(_maskProg, "uShadowZMap"), tu);
        gl.Uniform1(U(_maskProg, "uShadowTexel"), 1f / _size);
        gl.ActiveTexture(TextureUnit.Texture3);
        gl.BindTexture(TextureTarget.Texture2D, _zTex);

        foreach (var seg in m.Segments)
        {
            if (seg.IndexCount <= 0) continue;
            gl.Uniform1(U(_maskProg, "uEnableTexture"), seg.EnableTexture ? 1f : 0f);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, model.Textures.Get(seg.DiffuseTextureId));
            gl.DrawElements(PrimitiveType.Triangles, (uint)seg.IndexCount, DrawElementsType.UnsignedInt,
                (void*)(seg.IndexStart * sizeof(uint)));
        }

        // ── 还原 ───────────────────────────────────────────────────────
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)viewW, (uint)viewH);
        gl.ClearColor(GlesDevice.DefaultClearColor.r, GlesDevice.DefaultClearColor.g,
                      GlesDevice.DefaultClearColor.b, GlesDevice.DefaultClearColor.a);
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
    /// 读回两张中间 RT 并打印统计量（修复计划 · 步骤 1 的量化部分）。
    ///
    /// 为什么要做：自阴影这类多 pass 功能一旦"没生效"，最终画面只是"没变暗"，
    /// 无法区分是哪一段断了 —— 纹理没绑定 / FBO 没附件 / 矩阵把几何裁光了，
    /// 三种症状完全一样，而且都不报 GL 错。所以必须把中间结果量化出来。
    ///
    /// 判读方法：
    ///   · ① Z 图：覆盖率应≈模型在光源视角下的剪影占比（本仓模型约 3~5%），
    ///     z 应明显小于 1.000（1.000 = Clear 的空值）。覆盖 0% / z 恒 1.000 ⇒ 该 pass 什么都没画。
    ///   · ② 影强度图：非零比例应与"模型在屏幕上的覆盖"同量级，且应有连续灰度。
    ///     远小于 Z 图覆盖 ⇒ 只写了预期的一小部分（典型：在光源空间光栅化，见 P0-5）。
    /// </summary>
    public void DumpMapStats()
    {
        if (Mode == ShadowMode.Off)
        {
            Console.WriteLine("[Shadow] 影模式 = 0（关），中间 RT 未渲染，统计无意义");
            return;
        }

        var gl = _device.Gl;
        int n = _size * _size;

        var z = new float[n * 4];
        fixed (float* p = z)
        {
            gl.BindTexture(TextureTarget.Texture2D, _zTex);
            gl.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, p);
        }
        int covered = 0;
        float zmin = float.MaxValue, zmax = float.MinValue;
        double zsum = 0;
        for (int i = 0; i < z.Length; i += 4)
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

        var m = new byte[n * 4];
        fixed (byte* p = m)
        {
            gl.BindTexture(TextureTarget.Texture2D, _maskTex);
            gl.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        int nz = 0, peak = 0;
        for (int i = 0; i < m.Length; i += 4)
        {
            if (m[i] > 0) nz++;
            if (m[i] > peak) peak = m[i];
        }

        Console.WriteLine($"[Shadow] ① Z 图     : 覆盖 {covered * 100.0 / n:F1}%  z∈[{zmin:F3}, {zmax:F3}]  覆盖区均值 {zmean:F3}" +
                          "   （1.000 = Clear 空值；覆盖 0% 即该 pass 无产出）");
        Console.WriteLine($"[Shadow] ② 影强度图 : 非零 {nz * 100.0 / n:F2}%  峰值 {peak}/255" +
                          "   （0 = 受光，255 = 全影；应覆盖模型屏幕投影的大部分）");
    }

    private uint _modelFrameUbo;
    private uint _modelSkinSsbo;

    /// <summary>由 model renderer 注入，影 pass 复用同一份 UBO/SSBO。</summary>
    public void AttachModelBuffers(uint frameUbo, uint skinSsbo)
    {
        _modelFrameUbo = frameUbo;
        _modelSkinSsbo = skinSsbo;
    }

    /// <summary>床影（MMD 影模式 2）。</summary>
    /// <remarks>
    /// 必须**自己设置渲染状态**，不能沿用上一个 pass 的残留 —— 这正是 PE 的做法：
    /// PE 的 <c>tec_floorShadow</c> 在 fxd 里写明了
    ///     <c>AlphaBlendEnable = True; SrcBlend = SRCALPHA; DestBlend = INVSRCALPHA;</c>
    /// 而 C# 侧 <c>DrawModel_Shadow</c> 另外做了 <c>SetRenderState(ZWRITEENABLE, false)</c>。
    /// 两者合起来才是一块"半透明贴地 overlay"：只混合、不写深度。
    ///
    /// 之前本方法一个状态都没设，继承了 <c>model.Draw</c> 收尾时的「Blend 关 / DepthMask 开」，
    /// 于是有两处错：
    ///   ① 混合关闭 ⇒ 床影被当成**不透明**块写进去（alpha 被忽略），
    ///      画面上是一整块 <c>LightColor*0.5</c> 的灰板而不是影；
    ///   ② 深度写入 ⇒ 与同样位于 y=0 的坐标格网**共面争深度**，
    ///      后画的格网用 Lequal 与床影互相覆盖，出现闪烁斑纹（z-fight）。
    /// 关掉深度写入后，床影不参与任何共面深度比较，z-fight 从机制上消失。
    /// （调用顺序也要配合：格网先画、床影后画，见 Demo 的 Render。）
    /// </remarks>
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
        gl.DeleteTexture(_zTex);
        gl.DeleteFramebuffer(_zFbo);
        gl.DeleteRenderbuffer(_zDepthRbo);
        gl.DeleteTexture(_maskTex);
        gl.DeleteFramebuffer(_maskFbo);
        gl.DeleteRenderbuffer(_maskDepthRbo);
        gl.DeleteProgram(_zProg);
        gl.DeleteProgram(_maskProg);
        gl.DeleteProgram(_floorProg);
        gl.DeleteVertexArray(_floorVao);
        gl.DeleteBuffer(_floorVbo);
    }
}

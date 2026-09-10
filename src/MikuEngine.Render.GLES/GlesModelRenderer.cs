using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// per-frame UBO（std140），与 <c>Shaders/model.vert.glsl</c> 的 FrameBlock 一一对应。
/// 所有 uniform 都用 float 声明，避免 glUniform1i / 1f 的重载歧义。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FrameUniforms
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 View;

    /// <summary>xyz = 相机世界坐标。</summary>
    public Vector4 CameraPosition;

    /// <summary>xyz = 光线传播方向（PmxEditor 的 LightDirect），会被着色器取反当 ln。</summary>
    public Vector4 LightDirection;

    public Vector4 LightColor;
    public Vector4 AmbientColor;

    /// <summary>PmxEditor fxd L25：(0.07, 0.07, 0.07, 1)。</summary>
    public Vector4 BaseAmbient;

    public static FrameUniforms Default() => new()
    {
        ViewProj = Matrix4x4.Identity,
        View = Matrix4x4.Identity,
        CameraPosition = new Vector4(0f, 0f, 1f, 0f),
        // ⚠️ 以下三个是 PmxEditor 的默认值（反编译 PmxEditorCore L12531-12535，InitializeDevice）：
        //   m_manager.Ambient = System.Drawing.Color.White;
        //   m_manager.SetLightDirection(new Vector3(-0.5f, -1f, 0.5f));   ← Z 是 +0.5
        //   m_manager.SetLightColor(new Color4(0.5f, 0.5f, 0.5f));        ← 0.5 灰，不是 1
        // 之前自定的 (1,1,1) / (-0.5,-1,-0.5) / AmbientColor=0.5 会让整体偏暗、
        // 且被照亮的朝向与 PE 不同（Z 分量符号相反）。
        // 代入 PE 公式：diff = Diffuse * (0.25*ly + 0.753) * 2.0，背光面也 ×1.5 —— PE 默认观感就是偏白。
        LightDirection = new Vector4(Vector3.Normalize(new Vector3(-0.5f, -1f, 0.5f)), 0f),
        LightColor = new Vector4(0.5f, 0.5f, 0.5f, 1f),
        AmbientColor = new Vector4(1f, 1f, 1f, 1f),
        BaseAmbient = new Vector4(0.07f, 0.07f, 0.07f, 1f),
    };
}

/// <summary>
/// PMX 模型渲染器（Phase 0 / 0.5：静态预览）。
///
/// 职责：VAO/VBO/EBO + 蒙皮矩阵 SSBO + 纹理 + 按 Opaque / Cutout / Blended 三队列排序绘制。
/// 未实现：Morph、Edge/Outline pass、自阴影、SDEF 真实现（见 docs/shader-design.md §4）。
/// </summary>
public sealed unsafe class GlesModelRenderer : IDisposable
{
    private readonly GlesDevice _device;
    private readonly uint _program;
    private readonly uint _vao, _vbo, _ebo;
    private readonly uint _frameUbo;
    private readonly GlesSkinMatricesBuffer _skin;
    private readonly GlesTextureLibrary _textures;
    private readonly MikuEngine.Core.Models.SkeletalModel _model;
    private bool _disposed;

    // uniform locations
    private readonly int _locSkinMatBase;
    private readonly int _locDiffuse, _locSpecular, _locShininess, _locAmbient;
    private readonly int _locEnableTexture, _locEnableSphere, _locEnableToon, _locSphereMode;
    private readonly int _locDiffuseTex, _locSphereTex, _locToonTex;

    public Core.Models.SkeletalModel Model => _model;
    public GlesTextureLibrary Textures => _textures;
    public int SkinMatrixBaseOffset { get; private set; }

    private GlesModelRenderer(GlesDevice device, Core.Models.SkeletalModel model, GlesTextureLibrary textures)
    {
        _device = device;
        _model = model;
        _textures = textures;
        var gl = device.Gl;

        _program = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.model.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.model.frag.glsl");

        _locSkinMatBase = gl.GetUniformLocation(_program, "uSkinMatBase");
        _locDiffuse = gl.GetUniformLocation(_program, "uMaterialDiffuse");
        _locSpecular = gl.GetUniformLocation(_program, "uMaterialSpecular");
        _locShininess = gl.GetUniformLocation(_program, "uMaterialShininess");
        _locAmbient = gl.GetUniformLocation(_program, "uMaterialAmbient");
        _locEnableTexture = gl.GetUniformLocation(_program, "uEnableTexture");
        _locEnableSphere = gl.GetUniformLocation(_program, "uEnableSphere");
        _locEnableToon = gl.GetUniformLocation(_program, "uEnableToon");
        _locSphereMode = gl.GetUniformLocation(_program, "uSphereMode");
        _locDiffuseTex = gl.GetUniformLocation(_program, "uDiffuseTex");
        _locSphereTex = gl.GetUniformLocation(_program, "uSphereTex");
        _locToonTex = gl.GetUniformLocation(_program, "uToonTex");

        // ⚠️ 位置为 -1 意味着着色器里没有这个 uniform（名字打错 / 类型不匹配被优化掉），
        //    后续 glUniform* 会静默失效 —— 曾因此导致整体贴图丢失，这里显式暴露。
        foreach (var (name, loc) in new (string, int)[]
        {
            ("uSkinMatBase", _locSkinMatBase), ("uMaterialDiffuse", _locDiffuse),
            ("uMaterialSpecular", _locSpecular), ("uMaterialShininess", _locShininess),
            ("uMaterialAmbient", _locAmbient), ("uEnableTexture", _locEnableTexture),
            ("uEnableSphere", _locEnableSphere), ("uEnableToon", _locEnableToon),
            ("uSphereMode", _locSphereMode),
            ("uDiffuseTex", _locDiffuseTex), ("uSphereTex", _locSphereTex),
            ("uToonTex", _locToonTex),
        })
        {
            if (loc < 0)
                Console.WriteLine($"[GlesModelRenderer] ⚠️ uniform 未找到：{name}");
        }

        // ── 顶点缓冲 ────────────────────────────────────────────────────
        _vbo = gl.CreateBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (byte* p = model.VertexData)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)model.VertexData.Length, p, BufferUsageARB.StaticDraw);

        _ebo = gl.CreateBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = model.IndexData)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(model.IndexData.Length * sizeof(uint)), p,
                BufferUsageARB.StaticDraw);

        int stride = Core.Models.SkeletalModel.VertexStride;
        _vao = gl.CreateVertexArray();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);   // EBO 绑定属于 VAO 状态

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);

        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)12);

        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)24);

        // 骨骼索引：UNSIGNED_SHORT ×4（本模型 1099 骨，UByte 装不下）
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribIPointer(3, 4, VertexAttribIType.UnsignedShort, (uint)stride, (void*)40);

        // 权重：UNSIGNED_BYTE ×4，normalized → 硬件自动 ÷255
        gl.EnableVertexAttribArray(4);
        gl.VertexAttribPointer(4, 4, VertexAttribPointerType.UnsignedByte, true, (uint)stride, (void*)48);

        gl.BindVertexArray(0);

        _frameUbo = device.CreateUbo((nuint)sizeof(FrameUniforms));
        _skin = new GlesSkinMatricesBuffer(device, model.BoneCount);
    }

    /// <summary>从磁盘加载 PMX 并建立渲染资源（纹理按 PMX 内的相对路径在模型目录解析）。</summary>
    public static GlesModelRenderer LoadFromFile(GlesDevice device, string pmxPath)
    {
        var pmx = Core.Models.PmxParser.Parse(File.ReadAllBytes(pmxPath));
        var model = Core.Models.SkeletalModelConverter.Convert(pmx);

        string dir = Path.GetDirectoryName(Path.GetFullPath(pmxPath)) ?? ".";
        var textures = new GlesTextureLibrary(device);

        int missing = 0;
        foreach (var seg in model.Segments)
        {
            seg.DiffuseTextureId = LoadTexture(pmx, dir, textures, seg.DiffuseTextureId, false, ref missing);
            seg.SphereTextureId = LoadTexture(pmx, dir, textures, seg.SphereTextureId, false, ref missing);
            seg.EnableTexture = seg.DiffuseTextureId >= 0;
            seg.EnableSphere = seg.SphereTextureId >= 0 && seg.SphereMode != Core.Models.PmxMaterialSphereMode.Off;

            // Toon：共享 toon（toon01..10）走特殊解析，否则按纹理表索引
            var mat = pmx.Materials[seg.MaterialIndex];
            if (mat.IsSharedToonTexture)
            {
                string? path = PmxFileResolver.ResolveSharedToon(dir, mat.ToonTextureIndex);
                seg.ToonTextureId = path is null ? GlesTextureLibrary.None : textures.Load(path, toon: true);
                if (seg.ToonTextureId < 0) missing++;
            }
            else
            {
                seg.ToonTextureId = LoadTexture(pmx, dir, textures, seg.ToonTextureId, true, ref missing);
            }
            seg.EnableToon = seg.ToonTextureId >= 0;
        }

        if (missing > 0)
            Console.WriteLine($"[GlesModelRenderer] {missing} 个纹理引用未能解析，已按「无纹理」处理。");

        return new GlesModelRenderer(device, model, textures);
    }

    private static int LoadTexture(Core.Models.PmxModel pmx, string dir, GlesTextureLibrary lib,
        int textureIndex, bool toon, ref int missing)
    {
        if (textureIndex < 0 || textureIndex >= pmx.Textures.Length) return GlesTextureLibrary.None;

        string? path = PmxFileResolver.Resolve(dir, pmx.Textures[textureIndex]);
        if (path is null)
        {
            missing++;
            Console.WriteLine($"[GlesModelRenderer] 纹理缺失：{pmx.Textures[textureIndex]}");
            return GlesTextureLibrary.None;
        }
        return lib.Load(path, toon);
    }

    /// <summary>每帧调用。</summary>
    public void Draw(in FrameUniforms frame)
    {
        var gl = _device.Gl;

        _model.UpdateWorldMatrices();

        gl.UseProgram(_program);

        var uniforms = frame;
        _device.UpdateUbo(_frameUbo, ref uniforms);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _frameUbo);

        _skin.BeginFrame();
        SkinMatrixBaseOffset = _skin.Append(_model.SkinMatrices);
        _skin.Flush();
        _skin.Bind(1);
        gl.Uniform1(_locSkinMatBase, (float)SkinMatrixBaseOffset);

        gl.BindVertexArray(_vao);

        // 采样器单元：0=主纹理 1=球贴图 2=toon
        int unit;
        unit = 0; gl.Uniform1(_locDiffuseTex, unit);
        unit = 1; gl.Uniform1(_locSphereTex, unit);
        unit = 2; gl.Uniform1(_locToonTex, unit);

        // ── 单一队列、按 PMX 材质顺序（对齐 PmxEditor，2026-09-10 修订）────────
        // 反编译结论：fxd 只有 tec_model 一个 technique、单一 pass，且恒开
        //   AlphaBlendEnable=True / SrcBlend=SRCALPHA / DestBlend=INVSRCALPHA
        // fxd 与 C# 都**没有** ZWriteEnable / AlphaTestEnable —— D3D9 的
        // ZWRITEENABLE 默认 TRUE，即 PE 对半透明材质也写深度、按材质顺序画、不排序。
        //
        // 之前的三队列（Opaque/Cutout/Blended + blended 关深度写 + 按距离排序）
        // 是自创结构：a=1 的材质 SRCALPHA/INVSRCALPHA 本来就是恒等混合，
        // 拆队列毫无收益，还会改变绘制顺序、破坏连续 alpha 渐变。
        DrawSegments(_model.Segments);

        gl.BindVertexArray(0);

        // 还原全局状态，避免污染下一帧的 grid / 其它渲染器
        // （grid 是单面四边形，若继承了剔除状态且缠绕方向不合适会被整块剔掉）
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
    }

    private void DrawSegments(ReadOnlySpan<Core.Models.DrawSegment> segments)
    {
        var gl = _device.Gl;
        foreach (ref readonly var seg in segments)
        {
            if (seg.IndexCount <= 0) continue;

            var m = _model.Materials[seg.MaterialIndex];

            gl.Uniform4(_locDiffuse, m.Diffuse.X, m.Diffuse.Y, m.Diffuse.Z, m.Diffuse.W);
            gl.Uniform4(_locSpecular, m.Specular.X, m.Specular.Y, m.Specular.Z, 1f);
            gl.Uniform1(_locShininess, m.Shininess);
            gl.Uniform4(_locAmbient, m.Ambient.X, m.Ambient.Y, m.Ambient.Z, 1f);

            gl.Uniform1(_locEnableTexture, seg.EnableTexture ? 1f : 0f);
            gl.Uniform1(_locEnableSphere, seg.EnableSphere ? 1f : 0f);
            gl.Uniform1(_locEnableToon, seg.EnableToon ? 1f : 0f);
            gl.Uniform1(_locSphereMode, (float)(int)seg.SphereMode);

            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _textures.Get(seg.DiffuseTextureId));
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, _textures.Get(seg.SphereTextureId));
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, _textures.Get(seg.ToonTextureId));

            // 逐材质设置剔除状态（PE 对每个材质都可能切一次）
            ApplyRenderState(gl, seg.IsDoubleSided);

            gl.DrawElements(PrimitiveType.Triangles, (uint)seg.IndexCount, DrawElementsType.UnsignedInt,
                (void*)(seg.IndexStart * sizeof(uint)));
        }
    }

    /// <summary>
    /// 对齐 PmxEditor（2026-09-10 修订）：
    ///   · blend 恒开 SRC_ALPHA/INV_SRC_ALPHA（a=1 时是恒等混合，对不透明零副作用）
    ///   · 深度写恒开、无 alpha test
    ///   · <b>默认剔除背面</b>；材质带 PMX 両面（IsDoubleSided）flag 时关闭剔除
    ///
    /// 剔除这块的反编译证据（PmxEditorCore L25914-25934）：
    /// <code>
    ///   Cull renderState3 = d.GetRenderState&lt;Cull&gt;((RenderState)22);   // 保存
    ///   for (int i = 0; i &lt; num; i++) {                                 // 逐材质
    ///       if (m_both[i]) d.SetRenderState&lt;Cull&gt;((RenderState)22, (Cull)1);  // D3DCULL_NONE
    ///       ef.BeginPass(0); d.DrawIndexedPrimitives(...); ef.EndPass();
    ///       if (m_both[i]) d.SetRenderState&lt;Cull&gt;((RenderState)22, renderState3);  // 恢复
    ///   }
    /// </code>
    /// 即：PE 默认用 D3D9 设备默认值 D3DCULL_CCW（正面为顺时针、剔除背面），
    /// 仅当材质标了両面才临时切成 D3DCULL_NONE，画完立即恢复。
    ///
    /// 之前"恒不剔除"是错的：单面材质（本模型的 衣+/外套+/袖+/肌+，flag=0x1e）
    /// 的背光内表面会被画出来 —— 它们法线背光 → ly=0 → 只剩环境色，叠在正面材质上就偏暗。
    /// </summary>
    private static void ApplyRenderState(GL gl, bool doubleSided)
    {
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);

        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.CW);   // PMX/MMD 是 D3D9 LH 约定，正面为顺时针
        if (doubleSided)
            gl.Disable(EnableCap.CullFace);

        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(true);
    }

    public void Dispose()    {
        if (_disposed) return;
        _disposed = true;
        var gl = _device.Gl;
        gl.DeleteVertexArray(_vao);
        gl.DeleteBuffer(_vbo);
        gl.DeleteBuffer(_ebo);
        gl.DeleteBuffer(_frameUbo);
        gl.DeleteProgram(_program);
        _skin.Dispose();
        _textures.Dispose();
    }
}

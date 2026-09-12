using System.Numerics;
using System.Runtime.InteropServices;
using MikuEngine.Core.Animation;
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

    /// <summary>光源 ViewProj（WVP_Light），自阴影 Z 图 / 影强度图 / 床影采样用。</summary>
    public Matrix4x4 LightViewProj;

    public static FrameUniforms Default() => new()
    {
        ViewProj = Matrix4x4.Identity,
        View = Matrix4x4.Identity,
        CameraPosition = new Vector4(0f, 0f, 1f, 0f),
        // 注意：以下三个是 PmxEditor 的默认值（反编译 PmxEditorCore L12531-12535，InitializeDevice）：
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

/// <summary>自阴影风格。三者消费同一张 Z 图，只差采样与注入方式。</summary>
public enum SelfShadowStyle
{
    /// <summary>标准本影：16-tap PCF + PE 二选一注入（ToonMode 0 压亮度 / 否则 lerp 到 toon 角点）。</summary>
    Standard = 1,
    /// <summary>硬边本影：连续场高斯模糊 + smoothstep 阈值提取 + 影色乘性暗度（硬核心 + 平滑边界）。</summary>
    Threshold = 2,
    /// <summary>普通阴影：PCF 软影 + 直接遮蔽乘子（≈ PBR 软影观感）。</summary>
    Soft = 3,
}

/// <summary>
/// PMX 模型渲染器。
///
/// 职责：VAO/VBO/EBO + 蒙皮矩阵 SSBO + 纹理 + 按 Opaque / Cutout / Blended 三队列排序绘制。
/// 未实现：Morph、SDEF。
/// </summary>
public sealed unsafe class GlesModelRenderer : IDisposable
{
    private readonly GlesDevice _device;
    private readonly uint _program;
    private readonly uint _edgeProgram;
    private readonly uint _vao, _vbo, _ebo;
    private readonly uint _frameUbo;
    private readonly GlesSkinMatricesBuffer _skin;
    private readonly GlesMorphBuffer _morph;       // 顶点 morph（binding 2，vec4）
    private readonly GlesMorphBuffer _morphUv;     // UV morph（binding 3，vec2）
    private readonly bool _hasVertexMorph;
    private readonly bool _hasUvMorph;
    private readonly GlesTextureLibrary _textures;
    private readonly MikuEngine.Core.Models.SkeletalModel _model;
    private readonly bool _hasEdge;
    private float uToonMode = 1f;
    private bool _disposed;

    // uniform locations
    private readonly int _locSkinMatBase;
    private readonly int _locMorphEnabled;
    private readonly int _locMorphUvEnabled;
    private readonly int _locDiffuse, _locSpecular, _locShininess, _locAmbient;
    private readonly int _locEnableTexture, _locEnableSphere, _locEnableToon, _locSphereMode;
    private readonly int _locDiffuseTex, _locSphereTex, _locToonTex;
    private readonly int _locTextureCoeff, _locSphereCoeff, _locToonCoeff;

    // edge program 的 uniform
    private readonly int _locEdgeSkinMatBase, _locEdgeColor, _locEdgeSize, _locEdgeMorphEnabled;
    private readonly int _locSelfShadow, _locToonMode;
    private readonly int _locShadowZMap, _locShadowTexel, _locSelfShadowStrength;
    private readonly int _locShadowStyle, _locShadowColor, _locShadowBias, _locShadowBiasMax, _locShadowSlopeBias, _locShadowSoftness, _locShadowEdge;
    private readonly int _locNormalOffset;

    public Core.Models.SkeletalModel Model => _model;

    /// <summary>轮廓线开关（对应 PmxEditor 的 chkEdge）。默认开。</summary>
    public bool EdgeVisible { get; set; } = true;

    /// <summary>
    /// 本帧的模型可见性 —— 在 <see cref="PrepareFrame"/> 从 <c>SkeletalModel.Visible</c> 采样，
    /// 供 <see cref="Draw"/> / <see cref="DrawEdges"/> / 影图 caster 共用同一份「本帧快照」。
    /// false 时三个 pass 全部早退（表示枠「非表示」）。
    /// </summary>
    public bool ModelVisible { get; private set; } = true;

    /// <summary>自阴影开关（对应 MMD 影模式 1/2，0=关）。默认关闭。</summary>
    public int SelfShadowMode { get; set; } = 0;

    /// <summary>
    /// 光照深度图（<see cref="GlesShadowRenderer.ZTexture"/>），采样器单元 3。
    /// 自阴影改为内联 PCF直接采样这张图，不再是屏幕空间影强度图。
    /// 0 表示未提供：此时不绑定，采样不完整纹理恒返回 (0,0,0,1)（= 不在影里），安全降级。
    /// </summary>
    public uint ShadowZTexture { get; set; }

    /// <summary>1/影图边长（= <see cref="GlesShadowRenderer.Texel"/>），PCF 核缩放。</summary>
    public float ShadowTexel { get; set; } = 1f / 1024f;

    /// <summary>影强度（默认 1；0 = 全受光，隔离测试用）。三模式共享的全局参数。</summary>
    public float SelfShadowStrength { get; set; } = 1f;

    /// <summary>自阴影风格：标准本影 / 硬边本影（阈值提取）/ 普通阴影。</summary>
    public SelfShadowStyle ShadowStyle { get; set; } = SelfShadowStyle.Standard;

    /// <summary>
    /// 硬边本影的影色—— 语义是<b>乘性暗度</b>而非替换色：
    /// 影内 <c>col.rgb *= mix(1, uShadowColor.rgb, 影强度)</c>（1 = 不变，0.5 = 压暗一半，0 = 全黑）。
    /// 默认 0.5 灰 = MMD 式暗化：影区仍是原色的一半，贴图 / 球面 / toon 都能透出来。
    /// </summary>
    public System.Numerics.Vector4 ShadowColor { get; set; } = new(0.5f, 0.5f, 0.5f, 1f);

    /// <summary>
    /// 深度比较偏置的<b>常数底</b>（默认 0.0005）：所有片元都至少拿到这么多余量。
    /// 原来的全屏常数 0.003 ≈ 掠射到 82° 才需要的量，对正对面的近距离阴影属于过量偏置，
    /// 会把背光侧眉这类「遮挡余量小」的影整片推掉（实测 0.003 时左右眉 11.5% vs 2.6%）。
    /// 现在超出常数底的部分交给 <see cref="ShadowSlopeBias"/> 按面朝向自适应。
    /// </summary>
    public float ShadowBias { get; set; } = 0.0005f;

    /// <summary>
    /// 偏置<b>上限</b>（默认 0.003 =  5 的全屏常数，也是
    /// <see cref="GlesShadowRenderer.ShadowMargin"/>）：斜率项封顶在这里，
    /// 保证掠射面拿到的余量不超过原行为，陡面 acne 不会回退。
    /// </summary>
    public float ShadowBiasMax { get; set; } = 0.003f;

    /// <summary>
    /// 斜率缩放系数（默认 2）：偏置 = <c>常数底 + 系数 × 一个影图 texel 内自身深度的变化量</c>，
    /// 再被 <see cref="ShadowBiasMax"/> 封顶。正对面该项≈0（只吃常数底，不误伤近距离阴影）；
    /// 掠射面该项随 tan(入射角) 增大（继续压住 acne）。调大 ⇒ 更保险但更容易吃掉薄影；
    /// 调小 ⇒ 薄影更完整但 acne 风险回升。
    /// </summary>
    public float ShadowSlopeBias { get; set; } = 2f;

    /// <summary>硬边本影的核宽 / 普通阴影的 PCF 核宽（>1 更软）。</summary>
    public float ShadowSoftness { get; set; } = 1f;

    /// <summary>
    /// 硬边本影（style 2）的阈值带宽 w：连续场模糊后 smoothstep 的半宽，
    /// 越小边界越硬（0.3~0.5 ≈ MMD 的硬边观感），越大边界越平滑。
    /// </summary>
    public float ShadowEdge { get; set; } = 0.35f;

    /// <summary>
    /// 法线偏移偏置（MMD 世界单位）：接收位置沿世界法线推离表面再算光空间坐标，
    /// 消弧面（脸/裙内）acne。按 1.5 × 世界 texel 接线（demo），随影图密度自适应。
    /// </summary>
    public float ShadowNormalOffset { get; set; } = 0.00f;

    // 供自阴影 pass 复用（同一份蒙皮 SSBO 与 per-frame UBO）
    public uint Vao => _vao;
    public uint FrameUbo => _frameUbo;
    public uint SkinSsbo => _skin.BufferId;

    /// <summary>顶点 morph 偏移 SSBO（binding 2）；始终有效（无 morph 时是 1 元素哑缓冲）。</summary>
    public uint MorphSsbo => _morph.BufferId;

    /// <summary>模型是否有顶点 morph（决定 <c>uMorphEnabled</c> 与是否可安全读 binding 2）。</summary>
    public bool MorphEnabled => _hasVertexMorph;

    /// <summary>模型是否有 UV morph（决定 <c>uMorphUvEnabled</c>）。</summary>
    public bool MorphUvEnabled => _hasUvMorph;

    /// <summary>最近一帧累加过的顶点数（诊断用；0 = 本帧没有活跃表情）。</summary>
    public int MorphTouchedVertexCount => _morph.LastTouchedVertexCount;

    public GlesTextureLibrary Textures => _textures;
    public int SkinMatrixBaseOffset { get; private set; }

    private GlesModelRenderer(GlesDevice device, Core.Models.SkeletalModel model, GlesTextureLibrary textures)
    {
        _device = device;
        _model = model;
        _hasEdge = HasAnyEdge(model);
        _textures = textures;
        var gl = device.Gl;

        _program = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.model.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.model.frag.glsl");

        _locSkinMatBase = gl.GetUniformLocation(_program, "uSkinMatBase");
        _locMorphEnabled = gl.GetUniformLocation(_program, "uMorphEnabled");
        _locMorphUvEnabled = gl.GetUniformLocation(_program, "uMorphUvEnabled");
        _locDiffuse = gl.GetUniformLocation(_program, "uMaterialDiffuse");
        _locSpecular = gl.GetUniformLocation(_program, "uMaterialSpecular");
        _locShininess = gl.GetUniformLocation(_program, "uMaterialShininess");
        _locAmbient = gl.GetUniformLocation(_program, "uMaterialAmbient");
        _locEnableTexture = gl.GetUniformLocation(_program, "uEnableTexture");
        _locEnableSphere = gl.GetUniformLocation(_program, "uEnableSphere");
        _locEnableToon = gl.GetUniformLocation(_program, "uEnableToon");
        _locSphereMode = gl.GetUniformLocation(_program, "uSphereMode");
        _locSelfShadow = gl.GetUniformLocation(_program, "uEnableSelfShadow");
        _locToonMode = gl.GetUniformLocation(_program, "uToonMode");
        _locShadowZMap = gl.GetUniformLocation(_program, "uShadowZMap");
        _locShadowTexel = gl.GetUniformLocation(_program, "uShadowTexel");
        _locSelfShadowStrength = gl.GetUniformLocation(_program, "uSelfShadowStrength");
        _locShadowStyle = gl.GetUniformLocation(_program, "uShadowStyle");
        _locShadowColor = gl.GetUniformLocation(_program, "uShadowColor");
        _locShadowBias = gl.GetUniformLocation(_program, "uShadowBias");
        _locShadowBiasMax = gl.GetUniformLocation(_program, "uShadowBiasMax");
        _locShadowSlopeBias = gl.GetUniformLocation(_program, "uShadowSlopeBias");
        _locShadowSoftness = gl.GetUniformLocation(_program, "uShadowSoftness");
        _locShadowEdge = gl.GetUniformLocation(_program, "uShadowEdge");
        _locNormalOffset = gl.GetUniformLocation(_program, "uNormalOffset");
        _locDiffuseTex = gl.GetUniformLocation(_program, "uDiffuseTex");
        _locSphereTex = gl.GetUniformLocation(_program, "uSphereTex");
        _locToonTex = gl.GetUniformLocation(_program, "uToonTex");
        _locTextureCoeff = gl.GetUniformLocation(_program, "uTextureCoeff");
        _locSphereCoeff = gl.GetUniformLocation(_program, "uSphereCoeff");
        _locToonCoeff = gl.GetUniformLocation(_program, "uToonCoeff");

        // 注意：位置为 -1 意味着着色器里没有这个 uniform（名字打错 / 类型不匹配被优化掉），
        // 后续 glUniform* 会静默失效 —— 曾因此导致整体贴图丢失，这里显式暴露。
        foreach (var (name, loc) in new (string, int)[]
        {
            ("uSkinMatBase", _locSkinMatBase), ("uMorphEnabled", _locMorphEnabled),
            ("uMorphUvEnabled", _locMorphUvEnabled),
            ("uMaterialDiffuse", _locDiffuse),
            ("uMaterialSpecular", _locSpecular), ("uMaterialShininess", _locShininess),
            ("uMaterialAmbient", _locAmbient), ("uEnableTexture", _locEnableTexture),
            ("uEnableSphere", _locEnableSphere), ("uEnableToon", _locEnableToon),
            ("uSphereMode", _locSphereMode), ("uEnableSelfShadow", _locSelfShadow),
            ("uToonMode", _locToonMode), ("uShadowZMap", _locShadowZMap),
            ("uShadowTexel", _locShadowTexel), ("uSelfShadowStrength", _locSelfShadowStrength),
            ("uShadowStyle", _locShadowStyle), ("uShadowColor", _locShadowColor),
            ("uShadowBias", _locShadowBias), ("uShadowBiasMax", _locShadowBiasMax),
            ("uShadowSlopeBias", _locShadowSlopeBias),
            ("uShadowSoftness", _locShadowSoftness),
            ("uShadowEdge", _locShadowEdge),
            ("uNormalOffset", _locNormalOffset),
            ("uDiffuseTex", _locDiffuseTex), ("uSphereTex", _locSphereTex),
            ("uToonTex", _locToonTex),
            ("uTextureCoeff", _locTextureCoeff), ("uSphereCoeff", _locSphereCoeff),
            ("uToonCoeff", _locToonCoeff),
        })
        {
            if (loc < 0)
                Console.WriteLine($"[GlesModelRenderer] uniform 未找到：{name}");
        }

        // ── 轮廓线 program（PE: technique tec_edge，独立于主渲染）──────────
        _edgeProgram = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.edge.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.edge.frag.glsl");
        _locEdgeSkinMatBase = gl.GetUniformLocation(_edgeProgram, "uSkinMatBase");
        _locEdgeColor = gl.GetUniformLocation(_edgeProgram, "uMaterialEdgeColor");
        _locEdgeSize = gl.GetUniformLocation(_edgeProgram, "uMaterialEdgeSize");
        _locEdgeMorphEnabled = gl.GetUniformLocation(_edgeProgram, "uMorphEnabled");
        foreach (var (name, loc) in new (string, int)[]
        {
            ("uSkinMatBase", _locEdgeSkinMatBase),
            ("uMaterialEdgeColor", _locEdgeColor),
            ("uMaterialEdgeSize", _locEdgeSize),
            ("uMorphEnabled", _locEdgeMorphEnabled),
        })
        {
            if (loc < 0)
                Console.WriteLine($"[GlesModelRenderer] edge uniform 未找到：{name}");
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

        // 骨骼索引：UNSIGNED_SHORT ×4（测试模型 1099 骨，UByte 装不下）
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribIPointer(3, 4, VertexAttribIType.UnsignedShort, (uint)stride, (void*)40);

        // 权重：UNSIGNED_BYTE ×4，normalized → 硬件自动 ÷255
        gl.EnableVertexAttribArray(4);
        gl.VertexAttribPointer(4, 4, VertexAttribPointerType.UnsignedByte, true, (uint)stride, (void*)48);

        gl.BindVertexArray(0);

        _frameUbo = device.CreateUbo((nuint)sizeof(FrameUniforms));
        _skin = new GlesSkinMatricesBuffer(device, model.BoneCount);

        // 顶点 morph：没有顶点 morph 的模型也建一条 1 元素哑缓冲，保证 shader 里 binding 2
        // 始终有缓冲可绑；是否读它由 uMorphEnabled 决定（见 GlesMorphBuffer 注释）。
        _hasVertexMorph = model.VertexMorphs.Length > 0;
        _morph = new GlesMorphBuffer(device, _hasVertexMorph ? model.VertexCount : 0, components: 4);

        // UV morph：同上，占用 binding 3（vec2）。
        _hasUvMorph = model.UvMorphs.Length > 0;
        _morphUv = new GlesMorphBuffer(device, _hasUvMorph ? model.VertexCount : 0, components: 2);
    }

    private static bool HasAnyEdge(Core.Models.SkeletalModel model)
    {
        foreach (var s in model.Segments)
            if (s.EnableEdge) return true;
        return false;
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

    /// <summary>
    /// 上传 per-frame UBO。自阴影的影图 pass 复用这份 UBO，
    /// 因此必须先于<see cref="RenderShadowMaps"/> 调用，否则影图会用到上一帧的矩阵。
    /// </summary>
    public void UploadFrame(in FrameUniforms frame)
    {
        var uniforms = frame;
        _device.UpdateUbo(_frameUbo, ref uniforms);
    }

    /// <summary>
    /// 帧首统一准备：重算世界/蒙皮矩阵 + 上传 UBO + 上传蒙皮矩阵 SSBO。
    ///
    /// 注意：必须先于影图 pass 调用。修复前蒙皮矩阵在 <see cref="Draw"/> 里才上传，
    ///    而 Draw 在 RenderShadowMaps 之后 ⇒ 影图读的是上一帧的骨架（1 帧滞后；
    ///    运行时增删模型时 baseOffset 会位移 ⇒ 甚至读错模型）。UBO 之前修过、SSBO 只修了一半，
    ///    这里把两者都抽到帧首，一次补齐。
    /// </summary>
    public void PrepareFrame(in FrameUniforms frame)
    {
        // 表示枠（VMD property）结果在本帧内固定：先取快照，后续 Draw / 轮廓线 / 影图 caster
        // 都读这一份，保证同帧内三个 pass 的可见性判定一致。
        ModelVisible = _model.Visible;

        // IK 求解：必须早于世界矩阵重算 —— 求解器内部先跑一次 FK 全量更新（拿到被驱动端/目标的世界
        // 坐标）并导出链骨基旋转，迭代中只在链骨子树增量重算，最终把结果写进 IkRotations；
        // 下面这次 UpdateWorldMatrices 才把 IK 折进世界/蒙皮矩阵。顺序与 PmxEditor 的
        // `UpdateLocalMatrix(ik:true)`（在骨骼矩阵推进途中顺手解 IK）语义等价。
        MmdIkSolver.Solve(_model);

        _model.UpdateWorldMatrices();
        UploadFrame(in frame);
        _skin.BeginFrame();
        SkinMatrixBaseOffset = _skin.Append(_model.SkinMatrices);
        _skin.Flush();

        // 顶点 / UV morph 偏移：只重算「活跃 morph 的受影响顶点」并上传脏区（内存/带宽都是
        // O(顶点数)，与 morph 数量无关）。必须与蒙皮矩阵同帧提交 —— 影图 pass 与主渲染读同一份。
        _morph.Update(_model, _model.MorphWeights);
        _morphUv.UpdateUv(_model, _model.MorphWeights);
    }

    /// <summary>每帧调用。必须先调用 <see cref="PrepareFrame"/>。</summary>
    /// <param name="toonMode">0=无 1=MMD toon 2=固有（PE 的 ToonMode）</param>
    public void Draw(in FrameUniforms frame, float toonMode = 1f)
    {
        // 表示枠「非表示」：主渲染与轮廓线一起跳过。此处尚无任何 GL 状态改动，
        // 直接返回不会污染 grid / 其它渲染器（Draw 末尾的状态还原也无需执行）。
        if (!ModelVisible) return;

        uToonMode = toonMode;
        var gl = _device.Gl;

        gl.UseProgram(_program);

        gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _frameUbo);
        _skin.Bind(1);
        gl.Uniform1(_locSkinMatBase, (float)SkinMatrixBaseOffset);

        // 顶点 morph：偏移 SSBO 绑 binding 2，并用 uMorphEnabled 区分「模型无顶点 morph」
        // （此时缓冲里全是 0，但 shader 干脆不读）。
        _morph.Bind(2);
        gl.Uniform1(_locMorphEnabled, _hasVertexMorph ? 1f : 0f);

        // UV morph：binding 3（只影响主纹理 UV；edge / shadow 不采样主纹理，无需偏移）
        _morphUv.Bind(3);
        gl.Uniform1(_locMorphUvEnabled, _hasUvMorph ? 1f : 0f);

        gl.BindVertexArray(_vao);

        // 采样器单元：0=主纹理 1=球贴图 2=toon 3=光照深度图（内联 PCF）
        int unit;
        unit = 0; gl.Uniform1(_locDiffuseTex, unit);
        unit = 1; gl.Uniform1(_locSphereTex, unit);
        unit = 2; gl.Uniform1(_locToonTex, unit);
        unit = 3; gl.Uniform1(_locShadowZMap, unit);
        gl.Uniform1(_locShadowTexel, ShadowTexel);
        gl.Uniform1(_locSelfShadowStrength, SelfShadowStrength);
        gl.Uniform1(_locShadowStyle, (float)(int)ShadowStyle);
        gl.Uniform4(_locShadowColor, ShadowColor.X, ShadowColor.Y, ShadowColor.Z, ShadowColor.W);
        gl.Uniform1(_locShadowBias, ShadowBias);
        gl.Uniform1(_locShadowBiasMax, ShadowBiasMax);
        gl.Uniform1(_locShadowSlopeBias, ShadowSlopeBias);
        gl.Uniform1(_locShadowSoftness, ShadowSoftness);
        gl.Uniform1(_locShadowEdge, ShadowEdge);
        gl.Uniform1(_locNormalOffset, ShadowNormalOffset);

        // 注意：ShadowZTexture == 0 时必须显式解绑：GL 纹理绑定是粘滞状态，
        //    "跳过 bind"摘不掉上一帧的绑定（踩过，会得到 A/B 校验和相同的假结果）。
        gl.ActiveTexture(TextureUnit.Texture3);
        gl.BindTexture(TextureTarget.Texture2D, ShadowZTexture);
        gl.ActiveTexture(TextureUnit.Texture0);

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

        // 轮廓线（PE 是独立 technique tec_edge，在主渲染之后跑）
        if (_hasEdge && EdgeVisible)
            DrawEdges();

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

            // 材质 morph：基础材质 + 全部活跃材质 morph 混合后的「有效材质」。
            // 无活跃材质 morph 时逐字段等于基础材质（TextureCoeff 恒等 (1,1,1,1)）。
            var m = MmdMorphEvaluator.ResolveMaterial(_model, seg.MaterialIndex);

            gl.Uniform4(_locDiffuse, m.Diffuse.X, m.Diffuse.Y, m.Diffuse.Z, m.Diffuse.W);
            gl.Uniform4(_locSpecular, m.Specular.X, m.Specular.Y, m.Specular.Z, 1f);
            gl.Uniform1(_locShininess, m.Shininess);
            gl.Uniform4(_locAmbient, m.Ambient.X, m.Ambient.Y, m.Ambient.Z, 1f);
            gl.Uniform4(_locTextureCoeff, m.TextureColor.X, m.TextureColor.Y, m.TextureColor.Z, m.TextureColor.W);
            gl.Uniform4(_locSphereCoeff, m.SphereColor.X, m.SphereColor.Y, m.SphereColor.Z, m.SphereColor.W);
            gl.Uniform4(_locToonCoeff, m.ToonColor.X, m.ToonColor.Y, m.ToonColor.Z, m.ToonColor.W);

            gl.Uniform1(_locEnableTexture, seg.EnableTexture ? 1f : 0f);
            gl.Uniform1(_locEnableSphere, seg.EnableSphere ? 1f : 0f);
            gl.Uniform1(_locEnableToon, seg.EnableToon ? 1f : 0f);
            gl.Uniform1(_locSphereMode, (float)(int)seg.SphereMode);
            // 收影侧旗标（PMX bit3 = EnabledReceiveShadow）。
            // 材质未标"收影"时强制关掉自阴影（走 col *= toonCol 分支），行为对齐 MMD。
            gl.Uniform1(_locSelfShadow, (SelfShadowMode > 0 && seg.ReceivesShadow) ? (float)SelfShadowMode : 0f);
            gl.Uniform1(_locToonMode, uToonMode);

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
    /// 轮廓线 pass（PE: technique tec_edge）。
    ///
    /// 要点：
    ///  1. 主渲染全部画完之后再画 —— edge 外扩壳与本体共享深度，后画才能被正确遮挡
    ///  2. 只画 (flag & EnabledToonEdge) != 0 且 EdgeSize &gt; 0 的材质（本模型 42 个里 23 个）
    ///  3. 复用同一个 VAO：edge VS 用的 attribute 槽位与主 VS 完全一致
    ///  4. 剔除状态与主渲染同规则（逐材质按双面 flag）；blend 恒开，
    ///    因此 EdgeColor.w &lt; 1 的边缘是半透明的（本模型脸/肌/足 = 0.60）
    /// </summary>
    private void DrawEdges()
    {
        if (!ModelVisible) return;   // 与 Draw 同一份本帧快照（Draw 已早退，这里是防御性重复）

        var gl = _device.Gl;
        gl.UseProgram(_edgeProgram);
        gl.Uniform1(_locEdgeSkinMatBase, (float)SkinMatrixBaseOffset);
        // 轮廓线必须用与主渲染同一份顶点 morph 偏移，否则外扩壳会与本体错位。
        _morph.Bind(2);
        gl.Uniform1(_locEdgeMorphEnabled, _hasVertexMorph ? 1f : 0f);

        int unit = 0; _ = unit;   // edge 不采样纹理

        foreach (var seg in _model.Segments)
        {
            if (!seg.EnableEdge || seg.IndexCount <= 0) continue;

            // 材质 morph 可以改描边颜色 / 宽度；宽度被乘算到 0 时该材质的描边应消失。
            var m = MmdMorphEvaluator.ResolveMaterial(_model, seg.MaterialIndex);
            if (m.EdgeSize <= 0f) continue;

            gl.Uniform4(_locEdgeColor, m.EdgeColor.X, m.EdgeColor.Y, m.EdgeColor.Z, m.EdgeColor.W);
            gl.Uniform1(_locEdgeSize, m.EdgeSize);

            ApplyRenderState(gl, seg.IsDoubleSided);

            // 注意：inverted hull 必须剔除正面，否则外扩壳的正面比本体更靠近相机、
            //    深度测试必然通过，会把整个本体盖成轮廓色（已实测踩坑）。
            //    剔掉正面后只剩壳体的背面，恰好只在轮廓边缘露出一条边。
            //
            //    注意：这里**不**沿用材质的双面 flag —— 両面说的是「本体两面都画」，
            //    不是「壳体两面都画」。双面材质若不剔除正面同样会盖住本体。
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Front);
            gl.FrontFace(FrontFaceDirection.CW);

            gl.DrawElements(PrimitiveType.Triangles, (uint)seg.IndexCount, DrawElementsType.UnsignedInt,
                (void*)(seg.IndexStart * sizeof(uint)));
        }
    }

    /// <summary>
    /// 对齐 PmxEditor（2026-09-10 修订）：
    ///   1. blend 恒开 SRC_ALPHA/INV_SRC_ALPHA（a=1 时是恒等混合，对不透明零副作用）
    ///   2. 深度写恒开、无 alpha test
    ///   3. <b>默认剔除背面</b>；材质带 PMX 両面（IsDoubleSided）flag 时关闭剔除
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
        gl.DeleteProgram(_edgeProgram);
        _skin.Dispose();
        _morph.Dispose();
        _morphUv.Dispose();
        _textures.Dispose();
    }
}

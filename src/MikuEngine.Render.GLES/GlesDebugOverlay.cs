using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// 只读调试预览。
///
/// 自阴影这类多 pass 功能"没生效"时，肉眼看最终画面无法区分是哪一段断了
/// （纹理没绑定 / FBO 没附件 / 矩阵错，三种症状都是"画面没变化"，都不报 GL 错）。
/// 所以先把中间 RT 直接画到屏幕上，作为每一步修复的验收判据。
///
/// 只有 Z 图（光照深度图）：屏幕空间影强度图已删除、主渲染改为内联 PCF。
/// 因此这里只预览 Z 图，Off 时完全不产生绘制调用。
/// </summary>
public sealed class GlesDebugOverlay : IDisposable
{
    public enum View
    {
        Off = 0,
        ZMap = 1,
    }

    private readonly GlesDevice _device;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly int _locRect, _locTex, _locChannel, _locGain;

    /// <summary>当前预览内容。Demo 按 Z 循环。</summary>
    public View Current { get; set; } = View.Off;

    /// <summary>Z 图的显示增益。深度差很小时可以临时放大看梯度。</summary>
    public float Gain { get; set; } = 1f;

    public GlesDebugOverlay(GlesDevice device)
    {
        _device = device;
        var gl = device.Gl;

        _program = device.BuildProgram(
            "MikuEngine.Render.GLES.Shaders.debug_quad.vert.glsl",
            "MikuEngine.Render.GLES.Shaders.debug_quad.frag.glsl");

        _locRect = gl.GetUniformLocation(_program, "uRect");
        _locTex = gl.GetUniformLocation(_program, "uTex");
        _locChannel = gl.GetUniformLocation(_program, "uChannel");
        _locGain = gl.GetUniformLocation(_program, "uGain");

        // 空 VAO：本 program 不读任何顶点属性，但 GLES 下绘制需要绑定一个 VAO
        _vao = gl.CreateVertexArray();
    }

    /// <summary>Off → ZMap → Off。</summary>
    public View Cycle()
    {
        Current = Current == View.Off ? View.ZMap : View.Off;
        return Current;
    }

    /// <summary>
    /// 把光照深度图（Z 图）画到屏幕。必须在所有 3D 绘制（模型 / 影图 / 格网）之后调用。
    /// </summary>
    public void Render(uint zTex)
    {
        if (Current == View.Off) return;

        var gl = _device.Gl;

        // 覆盖式绘制：不受深度/剔除影响，也不需要混合
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);

        gl.UseProgram(_program);
        gl.BindVertexArray(_vao);
        gl.Uniform1(_locTex, 0);
        gl.Uniform1(_locChannel, 0f);      // Z 图只取 R
        gl.Uniform1(_locGain, Gain);
        gl.ActiveTexture(TextureUnit.Texture0);

        // 方案 B：Z 图是 DEPTH_COMPONENT24 且纹理对象上开着 COMPARE_REF_TO_TEXTURE，
        // 普通 sampler2D 采比较纹理是无效操作。预览时临时关掉比较模式（LINEAR 保留，
        // 深度值照常可滤波），画完立刻恢复 —— 恢复与关闭在同一函数内成对，不会泄漏。
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
            (int)TextureCompareMode.None);

        gl.Uniform4(_locRect, -1f, -1f, 1f, 1f);
        gl.BindTexture(TextureTarget.Texture2D, zTex);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
            (int)TextureCompareMode.CompareRefToTexture);

        gl.BindVertexArray(0);
        gl.ActiveTexture(TextureUnit.Texture0);

        // 还原成"深度测试开"的默认态，避免污染下一帧的 grid/其它 renderer
        gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        var gl = _device.Gl;
        gl.DeleteProgram(_program);
        gl.DeleteVertexArray(_vao);
    }
}

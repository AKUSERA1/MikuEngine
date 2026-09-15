using System.ComponentModel;
using System.Runtime.InteropServices;
using MikuEngine.Render.GLES;
using Silk.NET.OpenGL;

namespace MikuEngine.Demo.Rendering;

/// <summary>
/// WinForms 里的 OpenGL 视口控件：控件窗口带 <c>CS_OWNDC</c>，用 GDI 选像素格式后由
/// WGL（opengl32.dll）自建渲染上下文。不走「GLFW 顶层窗口 SetParent 进容器」那条路，
/// 因此没有 z-order / 焦点 / 缩放同步的一堆麻烦；所有 GL 调用都在 UI 线程。
///
/// 上下文是驱动能给出的最高版本的**兼容 profile**（与桌面 GLFW 默认路径同源），
/// GLES 风格的 <c>#version 310 es</c> 着色器照常编译。
///
/// 出帧时机由宿主决定：消息队列空闲时调用 <see cref="DrawFrame"/>，
/// <c>SwapBuffers</c> 在本控件内完成，宿主只往 <see cref="Render"/> 上挂逻辑（见 <c>MainForm.OnIdle</c>）。
/// </summary>
public sealed class GlViewport : Control
{
    private const int CsOwnDc = 0x00000020;
    private const int PfdDrawToWindow = 0x00000004;
    private const int PfdSupportOpenGl = 0x00000020;
    private const int PfdDoubleBuffer = 0x00000001;

    private static IntPtr _opengl32;

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cRedShift;
        public byte cGreenBits;
        public byte cGreenShift;
        public byte cBlueBits;
        public byte cBlueShift;
        public byte cAlphaBits;
        public byte cAlphaShift;
        public byte cAccumBits;
        public byte cAccumRedBits;
        public byte cAccumGreenBits;
        public byte cAccumBlueBits;
        public byte cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask;
        public uint dwVisibleMask;
        public uint dwDamageMask;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int ChoosePixelFormat(IntPtr hdc, ref PixelFormatDescriptor pfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PixelFormatDescriptor pfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SwapBuffers(IntPtr hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern IntPtr wglCreateContext(IntPtr hdc);

    [DllImport("opengl32.dll")]
    private static extern bool wglDeleteContext(IntPtr hrc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hrc);

    [DllImport("opengl32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr wglGetProcAddress(string procName);

    private delegate int SwapIntervalProc(int interval);

    private IntPtr _hwnd;
    private IntPtr _hdc;
    private IntPtr _hrc;
    private bool _vsyncApplied;
    private int _pendingWidth = -1;
    private int _pendingHeight = -1;

    /// <summary>Silk.NET 的 GL 入口（上下文就绪后非空）。</summary>
    public GL? Gl { get; private set; }

    /// <summary>引擎 GL 设备封装（上下文就绪后非空）。</summary>
    public GlesDevice? Device { get; private set; }

    /// <summary>上下文与 <see cref="Device"/> 都已就绪。</summary>
    public bool IsReady => _hrc != IntPtr.Zero && Device is not null;

    /// <summary>上下文创建完成（宿主在此初始化场景与资源）。</summary>
    public event EventHandler? ContextReady;

    /// <summary>每帧渲染逻辑（在一个已 current 的上下文里调用；返回后本控件负责 SwapBuffers）。</summary>
    public event Action<double>? Render;

    /// <summary>上下文创建 / 渲染过程中抛出的异常。</summary>
    public event EventHandler<Exception>? RenderFailed;

    public GlViewport()
    {
        SetStyle(
            ControlStyles.Opaque |
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.Selectable,
            true);
        BackColor = Color.FromArgb(28, 36, 56);
        TabStop = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CsOwnDc;   // DC 与窗口绑定，像素格式不会在多次 GetDC 之间漂移
            return cp;
        }
    }

    // GL 自己画：不擦背景、不响应 WM_PAINT，避免闪一下再被 GL 覆盖。
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) { }

    protected override void WndProc(ref Message m)
    {
        const int WmEraseBkgnd = 0x0014;
        if (m.Msg == WmEraseBkgnd)
        {
            m.Result = 1;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            CreateContext();
        }
        catch (Exception ex)
        {
            RenderFailed?.Invoke(this, ex);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        DestroyContext();
        base.OnHandleDestroyed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // 这里未必有 current 上下文 —— 只记下尺寸，下一帧开头再落地到 glViewport。
        _pendingWidth = Math.Max(1, ClientSize.Width);
        _pendingHeight = Math.Max(1, ClientSize.Height);
    }

    /// <summary>出帧：由宿主在消息循环空闲时调用（见 <c>MainForm.OnIdle</c>）。</summary>
    public void DrawFrame(double deltaSeconds)
    {
        if (_hrc == IntPtr.Zero) return;
        if (!wglMakeCurrent(_hdc, _hrc)) return;

        if (!_vsyncApplied)
        {
            _vsyncApplied = true;
            TryEnableVsync();
        }

        var device = Device;
        if (device is not null && _pendingWidth > 0 &&
            (_pendingWidth != device.Width || _pendingHeight != device.Height))
        {
            device.Resize(_pendingWidth, _pendingHeight);
            _pendingWidth = -1;
            _pendingHeight = -1;
        }

        try
        {
            Render?.Invoke(deltaSeconds);
        }
        catch (Exception ex)
        {
            RenderFailed?.Invoke(this, ex);
            return;
        }

        SwapBuffers(_hdc);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DestroyContext();
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ 上下文

    private void CreateContext()
    {
        int pfdSize = Marshal.SizeOf<PixelFormatDescriptor>();
        if (pfdSize != 40)
            throw new InvalidOperationException($"PIXELFORMATDESCRIPTOR 布局异常：{pfdSize} 字节（应为 40）");

        _hwnd = Handle;
        _hdc = GetDC(_hwnd);
        if (_hdc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC 失败");

        var pfd = new PixelFormatDescriptor
        {
            nSize = (ushort)pfdSize,
            nVersion = 1,
            dwFlags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer,
            iPixelType = 0,       // PFD_TYPE_RGBA
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = 0,       // PFD_MAIN_PLANE
        };

        int format = ChoosePixelFormat(_hdc, ref pfd);
        if (format == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ChoosePixelFormat 失败（无可用像素格式）");
        if (!SetPixelFormat(_hdc, format, ref pfd))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetPixelFormat 失败");

        _hrc = wglCreateContext(_hdc);
        if (_hrc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "wglCreateContext 失败");
        if (!wglMakeCurrent(_hdc, _hrc))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "wglMakeCurrent 失败");

        Gl = GL.GetApi(ResolveGlProc);
        Device = new GlesDevice(Gl, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        ContextReady?.Invoke(this, EventArgs.Empty);
    }

    private void DestroyContext()
    {
        if (_hrc == IntPtr.Zero) return;
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        wglDeleteContext(_hrc);
        _hrc = IntPtr.Zero;

        // 用缓存的窗口句柄 ReleaseDC：此刻 Control.Handle 可能已被销毁，
        // 再读一次 Handle 会把窗口重新建出来。
        if (_hdc != IntPtr.Zero && _hwnd != IntPtr.Zero)
        {
            ReleaseDC(_hwnd, _hdc);
            _hdc = IntPtr.Zero;
        }

        Device = null;
        Gl = null;
        _vsyncApplied = false;
    }

    /// <summary>
    /// 解析 GL 函数地址：驱动（GL 1.2+ 与扩展）只通过 <c>wglGetProcAddress</c> 暴露，
    /// GL 1.1 那几个（glClear / glViewport / glDrawElements…）只导在 opengl32.dll 里，需回退模块导出表。
    /// </summary>
    private static IntPtr ResolveGlProc(string name)
    {
        IntPtr p = wglGetProcAddress(name);
        if (p != IntPtr.Zero && p != (IntPtr)1 && p != (IntPtr)2 && p != (IntPtr)3 && p != new IntPtr(-1))
            return p;

        _opengl32 = _opengl32 == IntPtr.Zero ? NativeLibrary.Load("opengl32.dll") : _opengl32;
        return NativeLibrary.TryGetExport(_opengl32, name, out IntPtr q) ? q : IntPtr.Zero;
    }

    private void TryEnableVsync()
    {
        try
        {
            IntPtr p = wglGetProcAddress("wglSwapIntervalEXT");
            if (p == IntPtr.Zero) return;
            Marshal.GetDelegateForFunctionPointer<SwapIntervalProc>(p)(1);
        }
        catch
        {
            // 没有该扩展时靠宿主侧的帧率闸门限速
        }
    }
}

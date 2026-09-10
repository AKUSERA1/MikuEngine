using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using Silk.NET.GLFW;
using MikuEngine.Core.Camera;
using MikuEngine.Core.Models;
using MikuEngine.Render.GLES;
using MikuEngine.Engine;

// ─────────────────────────────────────────────────────────────────────────
// MikuEngine GLES 3.1 Demo —— 静态模型预览（Phase 0 / 0.5）
//
// 自动加载 samples/MikuEngine.Demo/Model/Model.pmx，用于验证
//   ① PMX 解析 → SkeletalModel 转换管线
//   ② 交错 VBO / 索引缓冲 / 蒙皮矩阵 SSBO
//   ③ model.vert / model.frag（Phong + Toon + Sphere + 三队列 alpha）
//
// 操作：
//   鼠标：左键=旋转 | 右键=平移 | 滚轮=缩放
//   B    ：给"上半身"骨骼加 30° 旋转 —— 用于确认蒙皮链路真的生效
//          （静态绑定姿势下蒙皮矩阵是单位阵，不足以证明 skinning 正确）
//   R    ：重置姿势
// ─────────────────────────────────────────────────────────────────────────

// --smoke：无人值守自检。隐藏窗口跑 20 帧 → 强制开影模式 1 → 打印中间 RT 统计 → 退出。
// 自阴影/轮廓线这类多 pass 功能的"静默失效"没法靠肉眼看画面定位，靠这个把中间结果量化。
bool smoke = Array.Exists(args, a => a == "--smoke");
string? pmxPath = Array.Find(args, a => !a.StartsWith("--", StringComparison.Ordinal)) ?? FindDefaultModel();
if (pmxPath is null || !File.Exists(pmxPath))
{
    Console.Error.WriteLine("[Demo] 找不到模型文件。请把路径作为第一个参数传入，");
    Console.Error.WriteLine("      或确保仓库里存在 samples/MikuEngine.Demo/Model/Model.pmx");
    return 1;
}

var options = WindowOptions.Default;
options.Size = new Silk.NET.Maths.Vector2D<int>(1100, 760);
options.Title = $"MikuEngine GLES — {Path.GetFileName(pmxPath)}";
options.WindowState = WindowState.Normal;
options.VSync = true;
options.IsVisible = !smoke;   // smoke 模式不弹窗

using var window = Window.Create(options);

GL? gl = null;
GlesDevice? device = null;
GlesGridRenderer? grid = null;
GlesModelRenderer? model = null;
GlesShadowRenderer? shadow = null;
GlesDebugOverlay? debugView = null;

// 初始视角先给个占位，模型加载后按包围盒重新定位
// PE 默认视野角 25°（设置对话框里的「视野角」，用户实测确认）
const float PeViewAngleDeg = 25f;
var camera = new OrbitCamera(
    alpha: MathF.PI / 4f,
    beta: MathF.PI / 3f,
    radius: 40f,
    target: System.Numerics.Vector3.Zero,
    fov: PeViewAngleDeg * MathF.PI / 180f);

var input = new OrbitInputController(camera, enabled: true);

// 骨骼测试用（按 B 切换）
int testBoneIndex = -1;
bool poseApplied = false;

window.Load += () =>
{
    gl = GL.GetApi((Silk.NET.Core.Contexts.IGLContext)window.GLContext!);
    var size = window.FramebufferSize;
    device = new GlesDevice(gl, size.X, size.Y);
    grid = new GlesGridRenderer(device);

    Console.WriteLine($"[Demo] GL version : {gl.GetStringS(StringName.Version)}");
    Console.WriteLine($"[Demo] GL renderer: {gl.GetStringS(StringName.Renderer)}");
    Console.WriteLine($"[Demo] 加载模型：{pmxPath}");

    model = GlesModelRenderer.LoadFromFile(device, pmxPath);
    shadow = new GlesShadowRenderer(device);
    debugView = new GlesDebugOverlay(device);
    // 影强度图 → 主渲染的 unit 4（P0-1：不接这一步，sampler 会静默采样到不完整纹理 → 恒受光）
    model.ShadowMaskTexture = shadow.MaskTexture;
    shadow.UpdateLight(model.Model, new System.Numerics.Vector3(FrameUniforms.Default().LightDirection.X, FrameUniforms.Default().LightDirection.Y, FrameUniforms.Default().LightDirection.Z));

    // ── 诊断输出 ────────────────────────────────────────────────────────
    var m = model.Model;
    int opaque = 0, cutout = 0, blended = 0;
    foreach (var s in m.Segments)
    {
        if (s.Type == MaterialRenderType.Opaque) opaque++;
        else if (s.Type == MaterialRenderType.Cutout) cutout++;
        else blended++;
    }

    Console.WriteLine($"[Demo] 顶点 {m.VertexCount} | 三角形 {m.IndexData.Length / 3} | 骨骼 {m.BoneCount} | 材质 {m.Segments.Length}");
    Console.WriteLine($"[Demo] 队列分类：Opaque={opaque} Cutout={cutout} Blended={blended}");
    int edgeCount = 0;
    foreach (var seg in m.Segments) if (seg.EnableEdge) edgeCount++;
    Console.WriteLine($"[Demo] 轮廓线：{edgeCount}/{m.Segments.Length} 个材质（按 E 开关）");
    Console.WriteLine("[Demo] 自阴影：0=关 1=自阴影 2=自阴影+床影（默认 0）");
    Console.WriteLine("[Demo] 中间 RT 调试预览：按 Z 循环 关 → Z图 → 影强度图 → 左右并排");
    Console.WriteLine($"[Demo] 蒙皮矩阵 {m.BoneCount * 64 / 1024.0:F1} KB → SSBO" +
                      $"（UBO 最小保证仅 16 KB，这里必须用 SSBO）");
    Console.WriteLine($"[Demo] 纹理库：{model.Textures.Count - 1} 张已加载");
    Console.WriteLine($"[Demo] 包围盒 min={Fmt(m.BoundsMin)} max={Fmt(m.BoundsMax)} size={Fmt(m.BoundsSize)}");

    // ── 按包围盒定位相机 ────────────────────────────────────────────────
    camera.Target = m.BoundsCenter;
    camera.Radius = MathF.Max(m.BoundsSize.Y * 2.0f, m.BoundsSize.Length() * 1.1f);
    camera.Beta = MathF.PI / 2.2f;

    // ── 骨骼测试目标：优先上半身 ────────────────────────────────────────
    foreach (var name in new[] { "上半身", "上半身2", "首", "頭", "センター", "center" })
    {
        testBoneIndex = m.FindBone(name);
        if (testBoneIndex >= 0)
        {
            Console.WriteLine($"[Demo] 蒙皮测试骨骼：#{testBoneIndex} \"{name}\"（按 B 切换）");
            break;
        }
    }
    if (testBoneIndex < 0)
        Console.WriteLine("[Demo] 未找到常用骨骼名，按 B 将旋转 #0 骨骼");

    // ── 输入：平台层只做"原生事件 → 控制器方法"转发 ──────────────────────
    unsafe
    {
        var glfw = GlfwWindowing.GetExistingApi(window);
        var hwnd = GlfwWindowing.GetHandle(window);
        var inputLocal = input;

        glfw.SetMouseButtonCallback(hwnd, (w, button, action, mods) =>
        {
            var btn = button switch
            {
                MouseButton.Left => OrbitInputController.PointerButton.Left,
                MouseButton.Right => OrbitInputController.PointerButton.Right,
                MouseButton.Middle => OrbitInputController.PointerButton.Middle,
                _ => OrbitInputController.PointerButton.None,
            };

            glfw.GetCursorPos(hwnd, out double x, out double y);
            if (action == InputAction.Press)
                inputLocal.OnPointerDown(0, (float)x, (float)y, btn);
            else if (action == InputAction.Release)
                inputLocal.OnPointerUp(0);
        });

        glfw.SetCursorPosCallback(hwnd, (w, x, y) => inputLocal.OnPointerMove(0, (float)x, (float)y));
        glfw.SetScrollCallback(hwnd, (w, xOff, yOff) => inputLocal.OnScroll((float)yOff));

        glfw.SetKeyCallback(hwnd, (w, key, scan, action, mods) =>
        {
            if (action != InputAction.Press) return;
            var target = model;
            if (target is null) return;

            if (key == Keys.B)
            {
                int bone = testBoneIndex >= 0 ? testBoneIndex : 0;
                poseApplied = !poseApplied;
                target.Model.SetBoneLocalRotation(bone, poseApplied
                    ? System.Numerics.Quaternion.CreateFromAxisAngle(
                        System.Numerics.Vector3.UnitY, MathF.PI / 6f)   // 30°
                    : System.Numerics.Quaternion.Identity);
                string boneName = bone < target.Model.BoneNames.Length ? target.Model.BoneNames[bone] : "?";
                Console.WriteLine($"[Demo] 姿势：{boneName} 旋转 {(poseApplied ? "30°" : "0°")}");
            }
            else if (key == Keys.E)
            {
                target.EdgeVisible = !target.EdgeVisible;
                Console.WriteLine($"[Demo] 轮廓线：{(target.EdgeVisible ? "开" : "关")}");
            }
            else if (key == Keys.Number1 || key == Keys.Number2 || key == Keys.Number0)
            {
                int mode = (key == Keys.Number2) ? 2 : (key == Keys.Number1) ? 1 : 0;
                if (target is null) return;
                target.SelfShadowMode = mode;
                if (shadow != null)
                    shadow.Mode = (GlesShadowRenderer.ShadowMode)mode;
                Console.WriteLine($"[Demo] 自阴影模式：{mode}（{(mode == 0 ? "关" : mode == 1 ? "自阴影" : "自阴影+床影")}）");
            }
            else if (key == Keys.R)
            {
                poseApplied = false;
                target.Model.ResetPose();
                Console.WriteLine("[Demo] 姿势已重置为绑定姿势");
            }
            else if (key == Keys.Z)
            {
                if (debugView is null) return;
                var v = debugView.Cycle();
                string desc = v switch
                {
                    GlesDebugOverlay.View.ZMap => "① 光照深度图（应：模型轮廓可见、越近越暗；全白=该 pass 什么都没画）",
                    GlesDebugOverlay.View.ShadowMask => "② 影强度图（应：该暗处有白斑；全黑=没判定出任何影）",
                    GlesDebugOverlay.View.Both => "左右并排 左=Z图 右=影强度图",
                    _ => "关",
                };
                Console.WriteLine($"[Demo] 中间 RT 预览：{desc}");
                // 影图只在模式 1/2 才渲染，看预览时顺带把影模式打开更直观
                if (v != GlesDebugOverlay.View.Off && shadow != null && !shadow.Enabled)
                    Console.WriteLine("[Demo]   提示：当前影模式为 0（关），影图不会被渲染 —— 按 1 或 2 打开");
            }
        });
    }

    Console.WriteLine("[Demo] 鼠标: 左键=旋转 | 右键=平移 | 滚轮=缩放 | B=弯曲测试 | E=轮廓线 | 1/2/0=自阴影 | Z=中间RT预览 | R=重置");

    if (smoke)
    {
        shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadow;
        model.SelfShadowMode = 1;
        debugView.Current = GlesDebugOverlay.View.Both;   // 顺便走一遍预览绘制路径
        Console.WriteLine("[Demo] --smoke：跑 20 帧 → 输出中间 RT 统计 + 影开/影关的画面差异，然后退出");
    }
};

window.FramebufferResize += size => device?.Resize(size.X, size.Y);

int smokeFrame = 0;
byte[]? smokeMaskOn = null;    // 影模式 1 + 影强度图【已绑定】
byte[]? smokeMaskOff = null;   // 影模式 1 + 影强度图【已断开】（cc≡0）
byte[]? smokeFloorOn = null;   // 影模式 2（含床影）
window.Render += dt =>
{
    if (smoke && ++smokeFrame > 26) { window.Close(); return; }
    if (device == null || grid == null) return;

    device.BeginFrame();

    int w = window.FramebufferSize.X;
    int h = window.FramebufferSize.Y;
    camera.Aspect = h > 0 ? (float)w / h : 1f;

    Span<float> viewProj = stackalloc float[16];
    Span<float> view = stackalloc float[16];
    camera.ComputeViewProj(viewProj);
    camera.WriteViewMatrix(view);

    var frame = FrameUniforms.Default();
    frame.ViewProj = FromColumnMajor(viewProj);
    frame.View = FromColumnMajor(view);
    frame.CameraPosition = new System.Numerics.Vector4(camera.Position, 0f);

    if (model != null && shadow != null && shadow.Enabled)
    {
        // ⚠️ 影图 pass 复用模型的 per-frame UBO，必须先上传再渲影图，
        //    否则影图拿到的是上一帧的矩阵（首帧更是单位阵 → 全屏影）。
        frame.LightViewProj = shadow.LightViewProj;
        model.UploadFrame(in frame);
        shadow.RenderShadowMaps(device, model, w, h);
        model.Draw(in frame);
    }
    else if (model != null)
    {
        frame.LightViewProj = shadow?.LightViewProj ?? System.Numerics.Matrix4x4.Identity;
        model.Draw(in frame);
    }

    grid.Draw(viewProj);

    // 床影是"贴地半透明 overlay"（只混合、不写深度），必须**最后**画：
    // 放在格网之前会被格网地面盖掉。顺序 + 双方都不写深度 ⇒ 与格网再无共面深度争用。
    if (model != null && shadow != null && shadow.Enabled)
        shadow.DrawFloor(device, new System.Numerics.Vector3(
            frame.LightColor.X, frame.LightColor.Y, frame.LightColor.Z));

    // 中间 RT 只读预览（修复计划 · 步骤 1）：必须放在所有 3D 绘制之后
    if (shadow != null && debugView != null && debugView.Current != GlesDebugOverlay.View.Off)
        debugView.Render(shadow.ZTexture, shadow.MaskTexture);

    if (smoke && shadow != null && debugView != null)
    {
        switch (smokeFrame)
        {
            case 5:
                Console.WriteLine($"[Demo] 预览绘制后 GL error: {device.Gl.GetError()}");
                debugView.Current = GlesDebugOverlay.View.Off;   // 后面要读画面，先撤掉预览
                break;

            case 6:
                shadow.DumpMapStats();
                smokeMaskOn = ReadViewport(device, w, h);
                Console.WriteLine($"[Demo] A) 影模式1 + 影强度图已绑定：校验和 {Checksum(smokeMaskOn)}");
                break;

            case 10:
                // 隔离测试：保持影模式 1（即仍走 PE 的"不做 col*=toonCol"分支），
                // 只把影强度图断开 → sampler 采样不完整纹理恒返回 (0,0,0,1) → cc≡0。
                // 这样 A/B 的唯一差别就只剩【自阴影本身】，排除 toon 分支切换带来的整体明暗变化。
                if (model != null) model.ShadowMaskTexture = 0;
                Console.WriteLine("[Demo] B) 仍为影模式1，但断开影强度图（等价 cc≡0）");
                break;

            case 12:
                smokeMaskOff = ReadViewport(device, w, h);
                Console.WriteLine($"[Demo] B) 校验和 {Checksum(smokeMaskOff)}");
                if (smokeMaskOn != null)
                {
                    int d = CountDiff(smokeMaskOn, smokeMaskOff);
                    Console.WriteLine($"[Demo] ⇒ 自阴影本身的贡献：{d} 象素（{d * 100.0 / (w * h):F3}%）" +
                        (d == 0 ? "  ❌ 影强度图对画面没有影响" : "  ✅ 影强度图确实在影响画面"));
                }
                break;

            case 14:
                shadow.Mode = GlesShadowRenderer.ShadowMode.Off;
                if (model != null) model.SelfShadowMode = 0;
                Console.WriteLine("[Demo] C) 切到影模式 0（会走 col *= toonCol 分支）");
                break;

            case 16:
                if (smokeMaskOff != null)
                {
                    var off = ReadViewport(device, w, h);
                    int d = CountDiff(smokeMaskOff, off);
                    Console.WriteLine($"[Demo] C) 校验和 {Checksum(off)}");
                    Console.WriteLine($"[Demo] ⇒ 影模式1 vs 影模式0（含 PE「二选一」结构导致的整体明暗差）：" +
                        $"{d} 象素（{d * 100.0 / (w * h):F3}%）");
                }
                break;

            case 18:
                // 床影验收：模式 2 与模式 1 的唯一差别就是"贴地 overlay 有没有画"。
                // 修复前它被后画的格网盖掉/与格网 z-fight，贡献接近 0。
                shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadowAndFloor;
                if (model != null) model.SelfShadowMode = 2;
                Console.WriteLine("[Demo] D) 影模式 2（自阴影 + 床影）");
                break;

            case 20:
                smokeFloorOn = ReadViewport(device, w, h);
                Console.WriteLine($"[Demo] D) 校验和 {Checksum(smokeFloorOn)}");
                break;

            case 22:
                shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadow;
                if (model != null) model.SelfShadowMode = 1;
                Console.WriteLine("[Demo] E) 影模式 1（关掉床影，其余不变）");
                break;

            case 24:
                if (smokeFloorOn != null)
                {
                    var noFloor = ReadViewport(device, w, h);
                    int d = CountDiff(smokeFloorOn, noFloor);
                    Console.WriteLine($"[Demo] E) 校验和 {Checksum(noFloor)}");
                    Console.WriteLine($"[Demo] ⇒ 床影的贡献：{d} 象素（{d * 100.0 / (w * h):F3}%）" +
                        (d == 0 ? "  ❌ 床影完全看不到（被格网盖掉？）" : "  ✅ 床影画在地面上了"));
                }
                break;
        }
    }
};

window.Closing += () =>
{
    model?.Dispose();
    shadow?.Dispose();
    debugView?.Dispose();
    grid?.Dispose();
    device?.Dispose();
};

window.Run();
return 0;

// ─────────────────────────────────────────────────────────────────────────

    // OrbitCamera 输出列主序 float[16]；Matrix4x4 的字段顺序（M11,M12,M13,M14,M21...）
    // 与 GLSL mat4 的列主序内存在字节上完全一致，因此**按顺序直抄**，不能转置。
    static System.Numerics.Matrix4x4 FromColumnMajor(ReadOnlySpan<float> c) => new(
        c[0], c[1], c[2], c[3],
        c[4], c[5], c[6], c[7],
        c[8], c[9], c[10], c[11],
        c[12], c[13], c[14], c[15]);

static string Fmt(System.Numerics.Vector3 v) => $"({v.X:F1}, {v.Y:F1}, {v.Z:F1})";

// ── --smoke 用：读回默认帧缓冲，做校验和 / 差异统计 ──────────────────────
// 自阴影的最终验收只能看"画面到底变了没有"：中间 RT 有数据 ≠ 主渲染用上了它
// （sampler 没绑定、uv 取错、分支写错，都会让中间 RT 完好而画面毫无变化）。

static unsafe byte[] ReadViewport(GlesDevice dev, int w, int h)
{
    var px = new byte[w * h * 4];
    fixed (byte* p = px)
        dev.Gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    return px;
}

static long Checksum(byte[] px)
{
    ulong h = 14695981039346656037UL;
    for (int i = 0; i < px.Length; i += 4)
    {
        h = (h ^ px[i]) * 1099511628211UL;
        h = (h ^ px[i + 1]) * 1099511628211UL;
        h = (h ^ px[i + 2]) * 1099511628211UL;
    }
    return (long)(h & 0x7FFFFFFFFFFFFFFFUL);
}

static int CountDiff(byte[] a, byte[] b)
{
    int pixels = Math.Min(a.Length, b.Length) / 4;
    int diff = 0;
    for (int i = 0; i < pixels; i++)
    {
        int o = i * 4;
        if (Math.Abs(a[o] - b[o]) > 2 || Math.Abs(a[o + 1] - b[o + 1]) > 2 || Math.Abs(a[o + 2] - b[o + 2]) > 2)
            diff++;
    }
    return diff;
}

static string? FindDefaultModel()
{
    const string Relative = "samples/MikuEngine.Demo/Model/Model.pmx";

    // 从输出目录往上找仓库根（bin/Debug/net10.0 → ... → 仓库根）
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, Relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }

    // 兜底：当前工作目录
    string fromCwd = Path.Combine(Directory.GetCurrentDirectory(), Relative.Replace('/', Path.DirectorySeparatorChar));
    return File.Exists(fromCwd) ? fromCwd : null;
}

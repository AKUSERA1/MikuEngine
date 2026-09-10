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

string? pmxPath = args.Length > 0 ? args[0] : FindDefaultModel();
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

using var window = Window.Create(options);

GL? gl = null;
GlesDevice? device = null;
GlesGridRenderer? grid = null;
GlesModelRenderer? model = null;

// 初始视角先给个占位，模型加载后按包围盒重新定位
var camera = new OrbitCamera(
    alpha: MathF.PI / 4f,
    beta: MathF.PI / 3f,
    radius: 40f,
    target: System.Numerics.Vector3.Zero,
    fov: MathF.PI / 4f);

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
                input.OnPointerDown(0, (float)x, (float)y, btn);
            else if (action == InputAction.Release)
                input.OnPointerUp(0);
        });

        glfw.SetCursorPosCallback(hwnd, (w, x, y) => input.OnPointerMove(0, (float)x, (float)y));
        glfw.SetScrollCallback(hwnd, (w, xOff, yOff) => input.OnScroll((float)yOff));

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
            else if (key == Keys.R)
            {
                poseApplied = false;
                target.Model.ResetPose();
                Console.WriteLine("[Demo] 姿势已重置为绑定姿势");
            }
        });
    }

    Console.WriteLine("[Demo] 鼠标: 左键=旋转 | 右键=平移 | 滚轮=缩放 | B=弯曲测试 | R=重置");
};

window.FramebufferResize += size => device?.Resize(size.X, size.Y);

window.Render += dt =>
{
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

    grid.Draw(viewProj);
    model?.Draw(in frame);
};

window.Closing += () =>
{
    model?.Dispose();
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

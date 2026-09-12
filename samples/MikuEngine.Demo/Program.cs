using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using Silk.NET.GLFW;
using MikuEngine.Core.Animation;
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
//   动画（Mixer 多动效，Motion/ 下存在对应 VMD 时自动加载）：
//     Motion.vmd=骨动效 | Lips.vmd=口型 | Eyes.vmd=视线（両目）| Facial.vmd=表情
//   空格 ：暂停/播放 | ←/→ ：∓1 帧 | ↑/↓ ：帧率 ±6 | F ：回首帧 | V ：动画驱动开关
//   [    ：把 test1.vmd 导入当前帧（任意帧导入演示） | ] ：移除导入层 | ; ：导入层权重循环 | ' ：层列表
//
// --smoke     ：无人值守自检。隐藏窗口跑 40 帧 → 强制开影模式 1 → 打印中间 RT 统计 → 退出。
//               （跳过动画加载：自阴影/轮廓线这类多 pass 功能的"静默失效"没法靠肉眼看画面定位，
//               靠这个把中间结果量化；动画会让模型动起来污染帧间差异统计）
// --anim-smoke：无人值守播放验收。加载四层动效、隐藏窗口跑 90 帧、定期打印混合器状态后退出 ——
//               验收标准是「全程无异常 + 各层确实在驱动模型」。
// ─────────────────────────────────────────────────────────────────────────

// --smoke：无人值守自检。隐藏窗口跑 40 帧 → 强制开影模式 1 → 打印中间 RT 统计 → 退出。
// 自阴影/轮廓线这类多 pass 功能的"静默失效"没法靠肉眼看画面定位，靠这个把中间结果量化。
bool smoke = Array.Exists(args, a => a == "--smoke");
bool animSmoke = Array.Exists(args, a => a == "--anim-smoke");
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
options.IsVisible = !smoke && !animSmoke;   // smoke 模式不弹窗

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
    alpha: 0f,
    beta: MathF.PI / 3f,
    radius: 40f,
    target: System.Numerics.Vector3.Zero,
    fov: PeViewAngleDeg * MathF.PI / 180f);

var input = new OrbitInputController(camera, enabled: true);

// 骨骼测试用（按 B 切换）
int testBoneIndex = -1;
bool poseApplied = false;

// VMD 动画（Step 5d-4 MmdTimeline）：Motion/ 下的骨动效 + 三个表情槽位动效自动加载；
// [ 把另一条 VMD 导入当前帧 / ] 移除 / ; 循环权重 / ' 打印层列表
MmdTimeline? timeline = null;
var baseLayers = new List<(string Desc, string File, MmdAnimationLayer Layer)>();
MmdAnimationLayer? importedLayer = null;   // [ 键导入的层（演示任意帧导入与清除）
string importedFile = "";
float importedWeight = 1f;                 // ; 键循环 1 → 0.5 → 0
bool animationEnabled = true;
bool morphEnabled = true;      // M 键切换：关时把表情权重全清零（回归基线用）
bool morphDiagPrinted = false; // 首次有表情真正生效时打一条诊断
long applyTicksTotal = 0; int applyCount = 0; long applyTicksMax = 0;   // --anim-smoke 耗时统计

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
    // 阶段 3：主渲染直接采样光照深度图做内联 PCF（不再是屏幕空间影强度图）。
    model.ShadowZTexture = shadow.ZTexture;
    model.ShadowTexel = shadow.Texel;
    shadow.UpdateLight(model.Model, new System.Numerics.Vector3(FrameUniforms.Default().LightDirection.X, FrameUniforms.Default().LightDirection.Y, FrameUniforms.Default().LightDirection.Z));
    // 法线偏移偏置（reze §2 #5）：按 1.5 × 世界 texel 接线，随紧视锥密度自适应
    //（须在 UpdateLight 之后取，半宽在那里定）。缩放模型或换密度档后需重接。
    model.ShadowNormalOffset = shadow.TexelWorld * 1.5f;

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
    Console.WriteLine("[Demo] 光照深度图调试预览：按 Z 开关（全白=Z pass 无产出）");
    Console.WriteLine($"[Demo] 蒙皮矩阵 {m.BoneCount * 64 / 1024.0:F1} KB → SSBO" +
                      $"（UBO 最小保证仅 16 KB，这里必须用 SSBO）");
    Console.WriteLine($"[Demo] 纹理库：{model.Textures.Count - 1} 张已加载");
    Console.WriteLine($"[Demo] 表情（morph）：{m.MorphNames.Length} 条 | 顶点 {m.VertexMorphs.Length} / UV {m.UvMorphs.Length} / " +
                      $"骨 {m.BoneMorphs.Length} / 材质 {m.MaterialMorphs.Count(e => e is not null)} / " +
                      $"组 {m.GroupMorphs.Count(g => g is not null)}" +
                      $" | 上 GPU：顶点 {(model.MorphEnabled ? "开" : "关（该模型无顶点 morph）")}" +
                      $"/ UV {(model.MorphUvEnabled ? "开" : "关")}" +
                      "（Flip/Impulse 按设计不支持，见 docs/2026-09-11-anim-morph-plan.md §0.2）");
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

    // ── VMD 动画（Step 5d-4：MmdTimeline 多层时间轴）───────────────────
    // 帧号驱动：渲染帧只推进游标，采样是帧号的纯函数 —— 跳帧 / seek / 暂停都不动采样逻辑。
    // smoke 模式跳过（自阴影 A/B 校验和比较的是相邻帧画面，动画会让模型动起来污染差异统计）；
    // --anim-smoke 反其道行之：专门为「多层播放无异常」的无人值守验收而设。
    if (smoke && !animSmoke)
    {
        Console.WriteLine("[Demo] --smoke：跳过 VMD 动画加载（避免污染自阴影帧间差异）");
    }
    else
    {
        timeline = new MmdTimeline { Loop = true };     // 舞曲播完回卷

        // (文件名, 层说明)。Motion 是骨动效主体；Lips/Eyes/Facial 对应 MMD 的「表情」槽位。
        // 四层的帧区间天然重合（≈0..833），全 Offset=0 同步播放。
        var motionFiles = new (string File, string Desc)[]
        {
            ("Motion.vmd", "骨动效"),
            ("Lips.vmd",   "口型"),
            ("Eyes.vmd",   "视线（両目）"),
            ("Facial.vmd", "表情"),
        };

        int loadedLayers = 0;
        foreach (var (file, desc) in motionFiles)
        {
            string? motionPath = FindMotionFile(file);
            if (motionPath is null)
            {
                Console.WriteLine($"[Demo] 未找到 Motion/{file}，跳过{desc}层");
                continue;
            }

            try
            {
                var vmd = VmdParser.Parse(File.ReadAllBytes(motionPath));
                var expanded = MmdAnimation.FromVmd(vmd);
                var bound = expanded.Bind(m);
                var layer = timeline.AddLayer(new MmdAnimationLayer(bound));
                baseLayers.Add((desc, file, layer));
                loadedLayers++;

                string slots = bound.BoneTracks.Length > 0 && bound.MorphTracks.Length > 0
                    ? $"骨 {bound.BoneTracks.Length}/{expanded.BoneTracks.Length} + morph {bound.MorphTracks.Length}/{expanded.MorphTracks.Length}"
                    : bound.BoneTracks.Length > 0
                        ? $"骨 {bound.BoneTracks.Length}/{expanded.BoneTracks.Length}"
                        : $"morph {bound.MorphTracks.Length}/{expanded.MorphTracks.Length}";
                Console.WriteLine($"[Demo] 层 {loadedLayers}（{desc}）：{file} | 帧 {layer.ActiveStart:F0}..{layer.ActiveEnd:F0} | " +
                                  $"{slots} | 表示枠键 {bound.PropertyKeyCount}");
                if (bound.BoneTracks.Length == 0 && bound.MorphTracks.Length == 0)
                    Console.WriteLine($"[Demo]   ⚠ {file} 没有能与该模型匹配的轨道，此层不会有可见效果");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Demo] {file} 加载失败：{ex.Message}");
            }
        }

        if (loadedLayers == 0)
        {
            Console.WriteLine("[Demo] 没有任何可用的 VMD 层，动画不可用");
            timeline = null;
            baseLayers.Clear();
        }
        else
        {
            Console.WriteLine($"[Demo] 时间轴：{loadedLayers} 层 | 区间 {timeline.StartFrame:F0}..{timeline.EndFrame:F0}（各层活跃区间并集）" +
                              " | 空格暂停 ←/→单步 ↑/↓帧率");
            Console.WriteLine("[Demo] 层管理：[=把 test1.vmd 导入当前帧 | ]=移除导入层 | ;=导入层权重 1/0.5/0 | '=层列表");
        }
    }

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
            else if (timeline != null && key == Keys.Space)
            {
                timeline.Paused = !timeline.Paused;
                Console.WriteLine($"[Demo] 时间轴：{(timeline.Paused ? "暂停" : "播放")}（帧 {timeline.CurrentFrame:F1}）");
            }
            else if (timeline != null && (key == Keys.Left || key == Keys.Right))
            {
                timeline.Step(key == Keys.Left ? -1 : 1);
                Console.WriteLine($"[Demo] 时间轴帧：{timeline.CurrentFrame:F1}");
            }
            else if (timeline != null && (key == Keys.Up || key == Keys.Down))
            {
                timeline.PlaybackFps = MathF.Max(1f, timeline.PlaybackFps + (key == Keys.Up ? 6f : -6f));
                Console.WriteLine($"[Demo] 时间轴帧率：{timeline.PlaybackFps:F0} fps");
            }
            else if (timeline != null && key == Keys.F)
            {
                timeline.Seek(timeline.StartFrame);
                Console.WriteLine($"[Demo] 时间轴帧：{timeline.CurrentFrame:F1}（首帧）");
            }
            else if (timeline != null && key == Keys.LeftBracket)
            {
                // 任意帧导入：把 test1.vmd 的第 0 帧落在时间轴当前位置（演示空白保留 + 区间并集 + 表示枠 AND）
                if (importedLayer != null)
                {
                    Console.WriteLine($"[Demo] 已有导入层 {importedFile}（帧 {importedLayer.ActiveStart:F0} 起），按 ] 先移除");
                }
                else
                {
                    string? path = FindMotionFile("test1.vmd");
                    if (path is null)
                    {
                        Console.WriteLine("[Demo] 未找到 Motion/test1.vmd，无法导入");
                    }
                    else
                    {
                        try
                        {
                            var vmd = VmdParser.Parse(File.ReadAllBytes(path));
                            var bound = MmdAnimation.FromVmd(vmd).Bind(target.Model);
                            importedFile = Path.GetFileName(path);
                            importedWeight = 1f;
                            importedLayer = timeline.AddLayer(new MmdAnimationLayer(bound) { Weight = importedWeight },
                                importAt: timeline.CurrentFrame);
                            Console.WriteLine($"[Demo] 导入 {importedFile}：第 0 帧落在时间轴 {timeline.CurrentFrame:F1} | " +
                                              $"区间 {importedLayer.ActiveStart:F0}..{importedLayer.ActiveEnd:F0} | " +
                                              $"表示枠键 {bound.PropertyKeyCount}（含非表示窗口，可观察 AND 合并）");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Demo] 导入失败：{ex.Message}");
                        }
                    }
                }
            }
            else if (timeline != null && key == Keys.RightBracket)
            {
                if (importedLayer == null)
                {
                    Console.WriteLine("[Demo] 没有导入层可移除（按 [ 先导入）");
                }
                else if (timeline.RemoveLayer(importedLayer))
                {
                    // 下一帧 Apply：该层轨道自动回绑定姿势、表示枠投票解除（整体写回语义，无残留）
                    Console.WriteLine($"[Demo] 已移除导入层 {importedFile}（区间缩为 {timeline.StartFrame:F0}..{timeline.EndFrame:F0}）");
                    importedLayer = null;
                    importedFile = "";
                }
            }
            else if (timeline != null && key == Keys.Semicolon)
            {
                if (importedLayer == null)
                {
                    Console.WriteLine("[Demo] 没有导入层（按 [ 先导入）");
                }
                else
                {
                    importedWeight = importedWeight switch { 1f => 0.5f, 0.5f => 0f, _ => 1f };
                    importedLayer.Weight = importedWeight;
                    Console.WriteLine($"[Demo] {importedFile} 权重 → {importedWeight}" +
                                      (importedWeight == 0f ? "（不贡献：该层轨道回绑定姿势）" : ""));
                }
            }
            else if (timeline != null && key == Keys.Apostrophe)
            {
                Console.WriteLine($"[Demo] 层列表（区间 {timeline.StartFrame:F0}..{timeline.EndFrame:F0} | 游标 {timeline.CurrentFrame:F1} | 可见 {timeline.Mixer.Visible}）：");
                foreach (var (desc, file, layer) in baseLayers)
                    PrintLayer(desc, file, layer, timeline);
                if (importedLayer != null)
                    PrintLayer("导入", importedFile, importedLayer, timeline);
            }
            else if (key == Keys.M)
            {
                morphEnabled = !morphEnabled;
                if (!morphEnabled) model?.Model.ResetMorphWeights();
                int active = 0;
                if (model != null)
                    foreach (float weight in model.Model.MorphWeights)
                        if (weight != 0f) active++;
                Console.WriteLine($"[Demo] 表情驱动：{(morphEnabled ? "开" : "关")}" +
                                  $" | 模型 {model?.Model.MorphNames.Length ?? 0} 条" +
                                  $" | 当前活跃 {active} 条" +
                                  $" | 本帧脏区顶点 {model?.MorphTouchedVertexCount ?? 0}");
            }
            else if (key == Keys.V)
            {
                animationEnabled = !animationEnabled;
                if (!animationEnabled)
                {
                    target.Model.ResetPose();
                    target.Model.ResetMorphWeights();   // 表情也一并复位，避免残留
                    target.Model.Visible = true;        // 表示枠也复位：否则停在隐藏窗口会一直看不见
                }
                Console.WriteLine($"[Demo] 动画驱动：{(animationEnabled ? "开" : "关（B/R 静态姿势测试可用）")}");
            }
            else if (key == Keys.Z)
            {
                if (debugView is null) return;
                var v = debugView.Cycle();
                string desc = v == GlesDebugOverlay.View.ZMap
                    ? "光照深度图（应：模型轮廓可见、越近越暗；全白=该 pass 什么都没画）"
                    : "关";
                Console.WriteLine($"[Demo] 光照深度图预览：{desc}");
                if (v == GlesDebugOverlay.View.ZMap && shadow != null && !shadow.Enabled)
                    Console.WriteLine("[Demo]   提示：当前影模式为 0（关），Z 图不会被渲染 —— 按 1 或 2 打开");
            }
            else if (key == Keys.S)
            {
                // 循环自阴影风格：标准 → 硬边（阈值提取）→ 软影 → 标准
                target.ShadowStyle = target.ShadowStyle switch
                {
                    SelfShadowStyle.Standard => SelfShadowStyle.Threshold,
                    SelfShadowStyle.Threshold => SelfShadowStyle.Soft,
                    _ => SelfShadowStyle.Standard,
                };
                string name = target.ShadowStyle switch
                {
                    SelfShadowStyle.Threshold => "硬边本影（阈值提取：连续场模糊 + smoothstep 硬化）",
                    SelfShadowStyle.Soft => "普通阴影（PCF 软影）",
                    _ => "标准本影（PE 16-tap）",
                };
                Console.WriteLine($"[Demo] 自阴影风格：{name}");
            }
        });
    }

    Console.WriteLine("[Demo] 鼠标: 左键=旋转 | 右键=平移 | 滚轮=缩放 | B=弯曲测试 | E=轮廓线 | 1/2/0=自阴影 | S=影风格 | Z=中间RT预览 | R=重置");
    Console.WriteLine("[Demo] 动画: 空格=暂停 | ←/→=∓1帧 | ↑/↓=帧率±6 | F=首帧 | [=导入VMD到当前帧 | ]=移除导入层 | ;=导入层权重 | '=层列表 | V=动画驱动开关 | M=表情驱动开关");

    if (smoke)
    {
        shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadow;
        model.SelfShadowMode = 1;
        debugView.Current = GlesDebugOverlay.View.ZMap;   // 顺便走一遍预览绘制路径
        Console.WriteLine("[Demo] --smoke：跑 40 帧 → 输出 Z 图统计 + 影开/影关的画面差异，然后退出");
    }
};

window.FramebufferResize += size => device?.Resize(size.X, size.Y);

int smokeFrame = 0;
int animSmokeFrame = 0;
bool animSmokeDiagPrinted = false;
byte[]? smokeOn = null;        // 标准本影，影强度 1
byte[]? smokeIsolated = null;  // 标准本影，影强度 0（cc≡0，隔离测试）
byte[]? smokeHard = null;      // 硬边本影（阈值提取），影强度 1
byte[]? smokeSoft = null;      // 普通阴影，影强度 1
byte[]? smokeFloorOn = null;   // 影模式 2（含床影）
window.Render += dt =>
{
    if (smoke && ++smokeFrame > 40) { window.Close(); return; }
    if (animSmoke && ++animSmokeFrame > 90)
    {
        // 混合求值耗时统计（验收「播放无异常」之外的效率观测）
        if (applyCount > 0)
            Console.WriteLine($"[anim-smoke] Apply ×{applyCount} 帧：平均 {applyTicksTotal / applyCount * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency:F1} μs | " +
                              $"最大 {applyTicksMax * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency:F1} μs");
        window.Close();
        return;
    }
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

    if (model != null)
    {
        frame.LightViewProj = shadow?.LightViewProj ?? System.Numerics.Matrix4x4.Identity;
        // 里程碑 A：先推进动画帧号 → 采样写入局部 T/R（纯函数，先整体复位到绑定姿势）→
        //   再由 PrepareFrame 重算世界/蒙皮矩阵并上传。影图 pass 与主渲染读同一份本帧姿态。
        // 里程碑 B（Step 5a-1）：采样只写「原始」表情权重，随后由 MmdMorphEvaluator 做 Group
        //   传播并把骨 morph 折进局部 T/R —— 必须早于 PrepareFrame 的 UpdateWorldMatrices。
        if (timeline != null && animationEnabled)
        {
            timeline.Advance(dt);
            bool wasVisible = model.Model.Visible;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Step 5d-4：时间轴推进 + 混合求值（活跃层采样 → 加权混合 → 可见性 AND → 整体写回）。
            // 采样是帧号的纯函数，seek ≡ 连续播放；未激活/已移除的层不贡献（空白保留、无残留）。
            timeline.Apply(model.Model);
            long ticks = sw.ElapsedTicks;
            applyTicksTotal += ticks;
            applyTicksMax = Math.Max(applyTicksMax, ticks);
            applyCount++;
            if (timeline.Mixer.Visible != wasVisible)
                Console.WriteLine($"[Demo] 表示枠：{(timeline.Mixer.Visible ? "显示" : "非表示")}（帧 {timeline.CurrentFrame:F1}）");
            // M 键关掉表情驱动时把权重清零（VMD 的 morph 轨道不再生效）：
            // 用于「权重全 0 ⇒ 画面与 Step 4 完全一致」的回归基线。
            if (!morphEnabled) model.Model.ResetMorphWeights();
            MmdMorphEvaluator.Evaluate(model.Model);
        }
        // 阶段 0：帧首统一上传（重算蒙皮矩阵 + UBO + 蒙皮 SSBO），
        // 影图 pass 与主渲染都读同一份、且是本帧的最新值（修复了上一帧滞后的坑）。
        model.PrepareFrame(in frame);

        // 表情管线首次真正动起来时打一条诊断（证明「权重 → 稀疏累加 → 脏区上传」是活的）。
        if (!morphDiagPrinted && morphEnabled && model.MorphTouchedVertexCount > 0)
        {
            int active = 0;
            foreach (float weight in model.Model.MorphWeights)
                if (weight != 0f) active++;
            Console.WriteLine($"[Demo] 表情首次激活：活跃 {active} 条 | 本帧脏区顶点 {model.MorphTouchedVertexCount} | " +
                              $"顶点 morph 上传 {(model.MorphEnabled ? "开" : "关")}");
            morphDiagPrinted = true;
        }

        // --anim-smoke：定期打印时间轴/混合器状态（帧游标 / 可见性 / 活跃 morph 数 / 中心骨姿态），
        // 证明四层混合真的在驱动模型；跑满 90 帧后由循环开头的计数器退出。
        if (animSmoke && timeline != null &&
            (animSmokeFrame == 1 || animSmokeFrame % 30 == 0))
        {
            int activeMorphs = 0;
            foreach (float weight in model.Model.MorphRawWeights)
                if (weight != 0f) activeMorphs++;
            int centerIndex = model.Model.FindBone("センター");
            float angleDeg = -1f, offsetLen = -1f;
            if (centerIndex >= 0)
            {
                angleDeg = 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(model.Model.LocalRotations[centerIndex].W), 0f, 1f)) * 180f / MathF.PI;
                offsetLen = System.Numerics.Vector3.Distance(
                    model.Model.LocalPositions[centerIndex], model.Model.LocalTranslations[centerIndex]);
            }
            Console.WriteLine($"[anim-smoke] 渲染帧 {animSmokeFrame:D3} | 时间轴帧 {timeline.CurrentFrame:F1} | " +
                              $"Visible={timeline.Mixer.Visible} | 活跃 morph {activeMorphs} | " +
                              $"センター 位移 {offsetLen:F2} / 旋转 {angleDeg:F1}°");
        }

        if (shadow != null && shadow.Enabled)
            shadow.RenderShadowMaps(device, model, w, h);

        model.Draw(in frame);
    }

    grid.Draw(viewProj);

    // 床影是"贴地半透明 overlay"（只混合、不写深度），必须**最后**画：
    // 放在格网之前会被格网地面盖掉。顺序 + 双方都不写深度 ⇒ 与格网再无共面深度争用。
    if (model != null && shadow != null && shadow.Enabled)
        shadow.DrawFloor(device, new System.Numerics.Vector3(
            frame.LightColor.X, frame.LightColor.Y, frame.LightColor.Z));

    // 光照深度图只读预览：必须放在所有 3D 绘制之后
    if (shadow != null && debugView != null && debugView.Current != GlesDebugOverlay.View.Off)
        debugView.Render(shadow.ZTexture);

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
                smokeOn = ReadViewport(device, w, h);
                Console.WriteLine($"[Demo] A) 标准本影 + 影强度 1：校验和 {Checksum(smokeOn)}");
                break;

            case 8:
                // 隔离测试：保持影模式 1，把影强度置 0 → cc≡0。
                if (model != null) model.SelfShadowStrength = 0f;
                Console.WriteLine("[Demo] B) 标准本影，影强度=0（等价 cc≡0）");
                break;

            case 10:
                smokeIsolated = ReadViewport(device, w, h);
                Console.WriteLine($"[Demo] B) 校验和 {Checksum(smokeIsolated)}");
                if (smokeOn != null)
                {
                    int d = CountDiff(smokeOn, smokeIsolated);
                    Console.WriteLine($"[Demo] ⇒ 标准本影贡献：{d} 象素（{d * 100.0 / (w * h):F3}%）" +
                        (d == 0 ? "  ❌ 无影响" : "  ✅ 有影响"));
                }
                if (model != null) model.SelfShadowStrength = 1f;
                break;

            case 12:
                shadow.Mode = GlesShadowRenderer.ShadowMode.Off;
                if (model != null) model.SelfShadowMode = 0;
                Console.WriteLine("[Demo] C) 切到影模式 0（走 col *= toonCol 分支）");
                break;

            case 14:
                if (smokeIsolated != null)
                {
                    var off = ReadViewport(device, w, h);
                    int d = CountDiff(smokeIsolated, off);
                    Console.WriteLine($"[Demo] C) 校验和 {Checksum(off)}");
                    Console.WriteLine($"[Demo] ⇒ 标准本影(cc=0) vs 影模式0（PE「二选一」明暗差）：" +
                        $"{d} 象素（{d * 100.0 / (w * h):F3}%）");
                }
                shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadow;
                if (model != null) { model.SelfShadowMode = 1; model.ShadowStyle = SelfShadowStyle.Threshold; }
                Console.WriteLine("[Demo] D) 硬边本影（阈值提取）");
                break;

            case 16:
                smokeHard = ReadViewport(device, w, h);
                if (model != null) model.SelfShadowStrength = 0f;
                break;

            case 18:
                if (smokeHard != null)
                {
                    var off = ReadViewport(device, w, h);
                    int d = CountDiff(smokeHard, off);
                    Console.WriteLine($"[Demo] ⇒ 硬边本影贡献：{d} 象素（{d * 100.0 / (w * h):F3}%）" +
                        (d == 0 ? "  ❌ 无影响" : "  ✅ 有影响"));
                }
                if (model != null) { model.SelfShadowStrength = 1f; model.ShadowStyle = SelfShadowStyle.Soft; }
                Console.WriteLine("[Demo] E) 普通阴影（软影）");
                break;

            case 20:
                smokeSoft = ReadViewport(device, w, h);
                if (model != null) model.SelfShadowStrength = 0f;
                break;

            case 22:
                if (smokeSoft != null)
                {
                    var off = ReadViewport(device, w, h);
                    int d = CountDiff(smokeSoft, off);
                    Console.WriteLine($"[Demo] ⇒ 普通阴影贡献：{d} 象素（{d * 100.0 / (w * h):F3}%）" +
                        (d == 0 ? "  ❌ 无影响" : "  ✅ 有影响"));
                }
                if (model != null) { model.SelfShadowStrength = 1f; model.ShadowStyle = SelfShadowStyle.Standard; }
                Console.WriteLine("[Demo] F) 回到标准本影");
                break;

            case 24:
                // 床影验收：模式 2 与模式 1 的唯一差别就是"贴地 overlay 有没有画"。
                shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadowAndFloor;
                if (model != null) model.SelfShadowMode = 2;
                Console.WriteLine("[Demo] G) 影模式 2（自阴影 + 床影）");
                break;

            case 26:
                smokeFloorOn = ReadViewport(device, w, h);
                Console.WriteLine($"[Demo] G) 校验和 {Checksum(smokeFloorOn)}");
                break;

            case 28:
                shadow.Mode = GlesShadowRenderer.ShadowMode.SelfShadow;
                if (model != null) model.SelfShadowMode = 1;
                Console.WriteLine("[Demo] H) 影模式 1（关掉床影，其余不变）");
                break;

            case 30:
                if (smokeFloorOn != null)
                {
                    var noFloor = ReadViewport(device, w, h);
                    int d = CountDiff(smokeFloorOn, noFloor);
                    Console.WriteLine($"[Demo] H) 校验和 {Checksum(noFloor)}");
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

// ── ' 键用：打印一层的导入点 / 区间 / 权重 / 当前活跃性与表示枠 ──────────────
static void PrintLayer(string desc, string file, MmdAnimationLayer layer, MmdTimeline timeline)
{
    double local = timeline.CurrentFrame - layer.Offset;
    Console.WriteLine($"[Demo]   {desc}（{file}）| 导入帧 {layer.Offset:F0} | 区间 {layer.ActiveStart:F0}..{layer.ActiveEnd:F0}" +
                      $" | 权重 {layer.Weight} | 当前{(layer.IsActive(timeline.CurrentFrame) ? "活跃" : "不活跃")}" +
                      $" | 本地帧 {local:F1} | 该层表示枠 {(layer.IsVisibleAt(layer.Loop ? layer.ToLocal(timeline.CurrentFrame) : local) ? "显示" : "非表示")}");
}

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
    const string Relative = "samples/MikuEngine.Demo/Model/1/1.pmx";

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

static string? FindMotionFile(string fileName)
{
    string Relative = $"samples/MikuEngine.Demo/Motion/{fileName}";

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, Relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }

    string fromCwd = Path.Combine(Directory.GetCurrentDirectory(), Relative.Replace('/', Path.DirectorySeparatorChar));
    return File.Exists(fromCwd) ? fromCwd : null;
}

using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using Silk.NET.GLFW;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Camera;
using MikuEngine.Core.Models;
using MikuEngine.Physics;
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
//   鼠标 ：左键=旋转 | 右键=平移 | 滚轮=缩放
//   动画（Motion/ 下存在对应 VMD 时自动加载）：
//     动作+IK.vmd（骨动效 + 足ＩＫ/つま先ＩＫ 目标 + 表情，单层播放）
//     —— 原 Motion.vmd + Lips/Eyes/Facial 四层组合保留在 motionFiles 的注释里，需要时一行切回
//   空格 ：暂停/播放 | ←/→ ：∓1 帧 | ↑/↓ ：帧率 ±6 | F ：回首帧
//   E    ：轮廓线开关 | 1/2/0 ：自阴影 关/自阴影/自阴影+床影 | S ：自阴影风格循环
//   P    ：物理模拟开关（Stage 1 S1；裙/发/胸随动效摆动，OFF 回纯动画，OFF→ON 自动 snap）
//   G    ：地面碰撞开关（Stage 1 S2；模型空间 y=0 内置地面，默认开）
//   H    ：物理后付与开关（Stage 1 S3；付与链消费模拟结果，OFF 回物理前姿态，A/B 对照）
//   IJKL/UO：模型面板 移動（Y/X/Z 世界轴，Shift 微调）| Alt+同键：面板 回転（YXZ 序 ±15°）
//   ./,  ：モデル操作 拡大率（渲染层根矩阵，与物理解耦；Shift+, 只缩 Y 压纸片）| R：重置
//
// --smoke     ：无人值守自检。隐藏窗口跑 40 帧 → 强制开影模式 1 → 打印中间 RT 统计 → 退出。
//               （跳过动画加载：自阴影/轮廓线这类多 pass 功能的"静默失效"没法靠肉眼看画面定位，
//               靠这个把中间结果量化；动画会让模型动起来污染帧间差异统计）
// --anim-smoke：无人值守播放验收。加载动效、隐藏窗口跑 90 帧、定期打印混合器状态后退出 ——
//               验收标准是「全程无异常 + 各层确实在驱动模型」。
// --xform-smoke：无人值守模型变换验收（MMD モデル操作端到端：TR 注入 全ての親 / 操作中心不动 /
//               拡大率物理解耦 / 重置幂等）。
// ─────────────────────────────────────────────────────────────────────────

// --smoke：无人值守自检。隐藏窗口跑 40 帧 → 强制开影模式 1 → 打印中间 RT 统计 → 退出。
// 自阴影/轮廓线这类多 pass 功能的"静默失效"没法靠肉眼看画面定位，靠这个把中间结果量化。
bool smoke = Array.Exists(args, a => a == "--smoke");
bool animSmoke = Array.Exists(args, a => a == "--anim-smoke");
// --ik-smoke：无人值守 IK 验收。只加载 Motion/动作+IK.vmd，隐藏窗口跑 N 帧，逐帧打印
//   「目标(足ＩＫ) vs 被驱动端(足首) 的距离/高度差」——脚悬空会直接体现为这个距离下不去。
//   --ik-dump=<path> 时，在第 --ik-frame=<N> 帧导出一份 JSON（rig + 姿势 + FK 世界 + IK 结果），
//   供 tools/ik-oracle 的 reze-engine 求解器对拍。
bool ikSmoke = Array.Exists(args,
    a => a == "--ik-smoke" || a.StartsWith("--ik-smoke=", StringComparison.Ordinal));
string? ikDumpPath = null;
{
    string? dumpArg = Array.Find(args, a => a.StartsWith("--ik-dump=", StringComparison.Ordinal));
    if (dumpArg is not null) ikDumpPath = dumpArg["--ik-dump=".Length..];
}
int ikDumpFrame = 120;
{
    string? fArg = Array.Find(args, a => a.StartsWith("--ik-frame=", StringComparison.Ordinal));
    if (fArg is not null && int.TryParse(fArg["--ik-frame=".Length..], out int f)) ikDumpFrame = f;
}
int ikSmokeFrames = 240;
{
    string? nArg = Array.Find(args, a => a.StartsWith("--ik-smoke=", StringComparison.Ordinal));
    if (nArg is not null && int.TryParse(nArg["--ik-smoke=".Length..], out int n)) ikSmokeFrames = n;
}
// --xform-smoke：无人值守模型变换验收（MMD モデル操作）。
//   隐藏窗口跑 N 帧，验证端到端链路：
//     ① 移動/回転 注入 全ての親 → センター 等子孙跟随、绕枢轴距离不变；操作中心原地不动；
//     ② 拡大率 只进渲染层根矩阵 → 画面变化但 WorldMatrices（物理消费）逐位不变（物理解耦）；
//     ③ 重置 → 画面回到基准（校验和一致）。
bool xformSmoke = Array.Exists(args, a => a == "--xform-smoke");
// --ik-bake-dump=<path>：随 --ik-smoke 逐动画帧记录「IK 相关骨的最终局部旋转」
//   （= FinalRotations，已含 IK 叠加；与 MMD bake 语义一致），结束时写一份 JSON，
//   供 tools/ik-oracle/cmp_baked.py 与 mmdbridge 烘焙出的 baked.vmd 逐帧对拍。
string? ikBakeDumpPath = null;
{
    string? bArg = Array.Find(args, a => a.StartsWith("--ik-bake-dump=", StringComparison.Ordinal));
    if (bArg is not null) ikBakeDumpPath = bArg["--ik-bake-dump=".Length..];
}
bool headless = smoke || animSmoke || ikSmoke || xformSmoke;
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
options.IsVisible = !headless;   // smoke / IK 验收模式不弹窗

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

// VMD 动画（Step 5d-4 MmdTimeline）：Motion/ 下的 VMD 自动加载为单层时间轴
MmdTimeline? timeline = null;

// Stage 1 S1 物理总开关（P 键切换）。默认开（MMD 行为：物理常开）；
// --ik-smoke 隔离验收时默认关，避免物理写回污染 IK 诊断读数；
// --xform-smoke 也默认关（保证校验和逐帧确定）。
bool physicsEnabled = !ikSmoke && !xformSmoke;
// Stage 1 S2 地面碰撞开关（G 键切换）。默认开（MMD 本体默认有地面碰撞）。
bool groundCollisionEnabled = true;
long applyTicksTotal = 0; int applyCount = 0; long applyTicksMax = 0;   // --anim-smoke 耗时统计

window.Load += () =>
{
    gl = GL.GetApi((Silk.NET.Core.Contexts.IGLContext)window.GLContext!);
    var size = window.FramebufferSize;
    device = new GlesDevice(gl, size.X, size.Y);
    grid = new GlesGridRenderer(device);

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

    // ── Stage 1 S1：物理内核（RezePhysics 移植，B5/B6）───────────────────
    // PMX 刚体/关节 → 内核 def（reze pmx-loader 的模式映射），内核自带模型空间
    // 地面体（顶面 y=0，让裙摆/头发停在地面）。骨骼索引与 PMX 保序一致，
    // RigidBodyDef.BoneIndex 直接对应 model.Model 的骨骼下标。
    try
    {
        var pmxData = PmxParser.Parse(File.ReadAllBytes(pmxPath));
        var rbDefs = pmxData.RigidBodies.Select(RigidBodyDef.FromPmx).ToArray();
        var jDefs = pmxData.Joints.Select(JointDef.FromPmx).ToArray();
        model.Physics = new MMDPhysics(rbDefs, jDefs);
        int dyn = rbDefs.Count(d => d.Type == RigidbodyType.Dynamic);
        int aligned = rbDefs.Count(d => d.Aligned);
        Console.WriteLine($"[Demo] 物理内核：刚体 {rbDefs.Length}（动态 {dyn} / mode2 对齐 {aligned}）| 关节 {jDefs.Length} | 内置地面（模型空间 y=0）");
        // S3 拓扑在 Physics 注入器内预计算；这里只报规模（0 = 该模型付与链没挂物理驱动骨，S3 无感）
        if (model.Model.HasPostPhysicsAppend)
            Console.WriteLine($"[Demo] 物理后付与：{model.Model.PhysicsAppendBoneCount} 骨需在物理步后重算（H 键开关）");
        else
            Console.WriteLine("[Demo] 物理后付与：无拓扑（付与链未挂物理驱动骨，S3 不生效）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Demo] 物理初始化失败（本模型不接物理）：{ex.Message}");
    }

    var m = model.Model;

    // ── 按包围盒定位相机 ────────────────────────────────────────────────
    camera.Target = m.BoundsCenter;
    camera.Radius = MathF.Max(m.BoundsSize.Y * 2.0f, m.BoundsSize.Length() * 1.1f);
    camera.Beta = MathF.PI / 2.2f;

    // ── VMD 动画（Step 5d-4：MmdTimeline 多层时间轴）───────────────────
    // 帧号驱动：渲染帧只推进游标，采样是帧号的纯函数 —— 跳帧 / seek / 暂停都不动采样逻辑。
    // smoke 模式跳过（自阴影 A/B 校验和比较的是相邻帧画面，动画会让模型动起来污染差异统计）；
    // --anim-smoke 反其道行之：专门为「多层播放无异常」的无人值守验收而设。
    if ((smoke || xformSmoke) && !animSmoke && !ikSmoke)
    {
        Console.WriteLine("[Demo] --smoke：跳过 VMD 动画加载（避免污染自阴影帧间差异）");
    }
    else
    {
        timeline = new MmdTimeline { Loop = true };     // 舞曲播完回卷

        // (文件名, 层说明)。默认加载「动作+IK.vmd」：骨动效 + 足ＩＫ/つま先ＩＫ 目标 + 表情
        // 全在这一份里（IK 骨的位置轨道就是足ＩＫ 目标，Mixer 直接把它写进 IK 骨的局部平移）。
        // 原四层组合保留备用 —— 想切回"Motion + 三个表情槽位分文件"的形态，把数组换回去即可：
        //   ("Motion.vmd","骨动效"), ("Lips.vmd","口型"), ("Eyes.vmd","视线（両目）"), ("Facial.vmd","表情")
        var motionFiles = new (string File, string Desc)[]
        {
            ("动作+IK.vmd", "骨动效 + IK + 表情"),
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
                loadedLayers++;
                Console.WriteLine($"[Demo] 动画层：{file}（{desc}）| 帧 {layer.ActiveStart:F0}..{layer.ActiveEnd:F0}");
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

            if (key == Keys.E)
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
            else if (key == Keys.G)
            {
                groundCollisionEnabled = !groundCollisionEnabled;
                Console.WriteLine($"[Demo] 地面碰撞：{(groundCollisionEnabled ? "开" : "关")}（S2；模型空间 y=0 内置地面，关闭后裙摆/发梢可穿地）");
            }
            else if (key == Keys.P)
            {
                physicsEnabled = !physicsEnabled;
                Console.WriteLine($"[Demo] 物理模拟：{(physicsEnabled ? "开" : "关")}（S1；OFF 期间骨骼保持纯动画，OFF→ON 自动 snap 无跳变）");
            }
            else if (key == Keys.H)
            {
                target.PostPhysicsAppendEnabled = !target.PostPhysicsAppendEnabled;
                Console.WriteLine($"[Demo] 物理后付与：{(target.PostPhysicsAppendEnabled ? "开" : "关")}（S3；OFF 时付与链保持物理前的动画姿态，A/B 对照）");
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
            else if (key == Keys.R)
            {
                // 模型变换重置（R 键）：面板 TR / 拡大率 全部归零 / 归一
                target.Model.ModelTranslationOffset = System.Numerics.Vector3.Zero;
                target.Model.ModelRotationAngles = System.Numerics.Vector3.Zero;
                target.ModelScale = System.Numerics.Vector3.One;
                Console.WriteLine("[Demo] 模型变换重置：移動=0 | 回転=0 | 拡大率=1");
            }
            else
            {
                // ── 模型面板变换（MMD モデル操作）──────────────────────
                //   移動：I/K=Y∓  J/L=X∓  U/O=Z∓（步长 0.5；Shift=±0.1 微调）
                //   回転：Alt+I/K=X（俯仰）∓  Alt+J/L=Y（偏航）∓  Alt+U/O=Z（滚转）∓，±15°，
                //         MMD YXZ 欧拉序，状态为绝对弧度（每帧由 ApplyModelTransform 重建，天然幂等）
                //   拡大率： ./,=全体 ×1.1 / ÷1.1；Shift+,=只缩 Y 轴（压成纸片）
                bool shift = (mods & Silk.NET.GLFW.KeyModifiers.Shift) != 0;
                bool alt = (mods & Silk.NET.GLFW.KeyModifiers.Alt) != 0;
                float moveStep = shift ? 0.1f : 0.5f;
                float rotStep = MathF.PI / 12f;   // 15°
                float scaleFactor = shift ? 1.05f : 1.1f;
                var m2 = target.Model;
                bool handled = false;

                switch (key)
                {
                    // ── 移動（面板 移動，世界轴）──
                    case Keys.J when !alt: m2.ModelTranslationOffset.X -= moveStep; handled = true; break;
                    case Keys.L when !alt: m2.ModelTranslationOffset.X += moveStep; handled = true; break;
                    case Keys.I when !alt: m2.ModelTranslationOffset.Y += moveStep; handled = true; break;
                    case Keys.K when !alt: m2.ModelTranslationOffset.Y -= moveStep; handled = true; break;
                    case Keys.U when !alt: m2.ModelTranslationOffset.Z -= moveStep; handled = true; break;
                    case Keys.O when !alt: m2.ModelTranslationOffset.Z += moveStep; handled = true; break;

                    // ── 回転（面板 回転，MMD YXZ 序）── Alt 修饰时对应轴 ∓15°
                    case Keys.I when alt: m2.ModelRotationAngles.X -= rotStep; handled = true; break;
                    case Keys.K when alt: m2.ModelRotationAngles.X += rotStep; handled = true; break;
                    case Keys.J when alt: m2.ModelRotationAngles.Y -= rotStep; handled = true; break;
                    case Keys.L when alt: m2.ModelRotationAngles.Y += rotStep; handled = true; break;
                    case Keys.U when alt: m2.ModelRotationAngles.Z -= rotStep; handled = true; break;
                    case Keys.O when alt: m2.ModelRotationAngles.Z += rotStep; handled = true; break;

                    // ── 拡大率（与物理解耦，只进渲染层根矩阵）──
                    case Keys.Period:          // .  = 放大全体
                        target.ModelScale *= scaleFactor; handled = true; break;
                    case Keys.Comma:           // ,  = 缩小全体
                        target.ModelScale /= scaleFactor; handled = true; break;
                    case Keys.Y when shift:    // Shift+Y = 只缩 Y 轴（压纸片测试）
                        target.ModelScale = target.ModelScale with { Y = MathF.Max(target.ModelScale.Y / scaleFactor, 0.01f) };
                        handled = true; break;
                }

                if (handled)
                {
                    var t = m2.ModelTranslationOffset; var r = m2.ModelRotationAngles; var s = target.ModelScale;
                    Console.WriteLine($"[Demo] 模型变换：移動=({t.X:F2},{t.Y:F2},{t.Z:F2}) | " +
                                      $"回転=({r.X * 180f / MathF.PI:F1}°,{r.Y * 180f / MathF.PI:F1}°,{r.Z * 180f / MathF.PI:F1}°) | " +
                                      $"拡大率=({s.X:F2},{s.Y:F2},{s.Z:F2})");
                }
            }
        });
    }

    Console.WriteLine("[Demo] 操作: 左键=旋转 | 右键=平移 | 滚轮=缩放 | E=轮廓线 | 1/2/0=自阴影模式 | S=自阴影风格");
    Console.WriteLine("[Demo] 播放: 空格=暂停/播放 | ←/→=∓1帧 | ↑/↓=帧率±6 | F=首帧");
    Console.WriteLine("[Demo] 物理: P=物理模拟开关 | G=地面碰撞开关 | H=物理后付与开关（P/G 默认开）");
    Console.WriteLine("[Demo] 模型变换（MMD モデル操作，全ての親 为基准，操作中心留在原地）:");
    Console.WriteLine("[Demo]   移動: I/K=Y∓ | J/L=X∓ | U/O=Z∓（0.5/步，Shift=0.1 微调）");
    Console.WriteLine("[Demo]   回転: Alt+I/K=X | Alt+J/L=Y | Alt+U/O=Z（∓15°/步，YXZ 序）");
    Console.WriteLine("[Demo]   拡大率: .=放大 | ,=缩小（×1.1，Shift+,=只缩Y压纸片；与物理解耦）");
    Console.WriteLine("[Demo]   R=重置全部模型变换");

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
int ikSmokeFrame = 0;
double ikSmokeMaxErr = 0;
string ikSmokeWorst = "-";
// bake 对拍记录：骨名表 + 每帧 (动画帧号, 四元数组)。仅在 --ik-bake-dump 时填充。
string[] ikBakeNames = Array.Empty<string>();
int[] ikBakeIdx = Array.Empty<int>();
var ikBakeFrames = new List<(int F, System.Numerics.Quaternion[] Q, System.Numerics.Vector3[] P)>();
byte[]? smokeOn = null;        // 标准本影，影强度 1
byte[]? smokeIsolated = null;  // 标准本影，影强度 0（cc≡0，隔离测试）
byte[]? smokeHard = null;      // 硬边本影（阈值提取），影强度 1
byte[]? smokeSoft = null;      // 普通阴影，影强度 1
byte[]? smokeFloorOn = null;   // 影模式 2（含床影）

// --xform-smoke 状态：基准/TR/拡大率 三份校验和 + 快照（见文件头验收项）
int xformFrame = 0;
long xformC0 = 0, xformC1 = 0, xformC2 = 0;
System.Numerics.Matrix4x4[]? xformWorldTr = null;   // TR 注入后的 WorldMatrices 快照（拡大率不得改变它）
System.Numerics.Vector3 xformOc0 = default, xformCenter0 = default;   // 操作中心 / センター 基准世界位置
window.Render += dt =>
{
    if (smoke && ++smokeFrame > 40) { window.Close(); return; }
    if (xformSmoke && ++xformFrame > 12) { window.Close(); return; }
    if (ikSmoke && ++ikSmokeFrame > ikSmokeFrames)
    {
        Console.WriteLine($"[ik-smoke] 结束：{ikSmokeFrames} 帧 | 全程最大末端误差 {ikSmokeMaxErr:F4}（{ikSmokeWorst}）");
        Console.WriteLine(ikSmokeMaxErr < 1.0
            ? "[ik-smoke] 判定：IK 收敛（最大误差 < 1 模型单位）—— 未出现「腿脚全程悬空」"
            : "[ik-smoke] 判定：存在明显不收敛的链，需检查（见上方逐帧输出）");
        if (ikBakeDumpPath is not null)
        {
            DumpBakeJson(ikBakeDumpPath, ikBakeNames, ikBakeFrames);
            Console.WriteLine($"[ik-smoke] 已导出 bake 对拍数据：{ikBakeDumpPath}（{ikBakeFrames.Count} 帧 × {ikBakeNames.Length} 骨）");
        }
        window.Close();
        return;
    }
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
        if (timeline != null)
        {
            // --ik-smoke：按渲染帧号硬推进（不走 dt），保证导出帧可复现 —— 采样是帧号的纯函数，
            // 帧号必须由外部给定，否则同一"渲染帧 120"在不同机器/负载下落在不同动画帧上。
            if (ikSmoke) timeline.Seek(ikSmokeFrame - 1);
            else timeline.Advance(dt);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Step 5d-4：时间轴推进 + 混合求值（活跃层采样 → 加权混合 → 可见性 AND → 整体写回）。
            // 采样是帧号的纯函数，seek ≡ 连续播放；未激活/已移除的层不贡献（空白保留、无残留）。
            timeline.Apply(model.Model);
            long ticks = sw.ElapsedTicks;
            applyTicksTotal += ticks;
            applyTicksMax = Math.Max(applyTicksMax, ticks);
            applyCount++;
            MmdMorphEvaluator.Evaluate(model.Model);
        }
        // 阶段 0：帧首统一上传（重算蒙皮矩阵 + UBO + 蒙皮 SSBO），
        // 影图 pass 与主渲染都读同一份、且是本帧的最新值（修复了上一帧滞后的坑）。
        // Stage 1 S1：物理 tick 时钟由动画帧号驱动（timeline.CurrentFrame）——暂停/未加载
        // 时帧号不推进 → advance=0 → 物理冻结（MMD 行为）；帧率变化同步给内核（tick 时长
        // = 1/(fps×k)，teleport 阈值同源）。
        if (model.Physics != null)
        {
            model.Physics.PlaybackFps = timeline?.PlaybackFps ?? 30f;
            model.PhysicsFrame = timeline?.CurrentFrame ?? 0;
            model.PhysicsEnabled = physicsEnabled;
            model.GroundCollisionEnabled = groundCollisionEnabled;
        }
        model.PrepareFrame(in frame);

        // --ik-smoke：IK 验收。量化「目标（足ＩＫ）与实际被驱动端（足首）的距离」——
        // 腿脚悬空会直接体现为这个距离下不去；Δy 为负说明踝低于目标（过冲/腿被压）。
        if (ikSmoke && (ikSmokeFrame == 1 || ikSmokeFrame % 30 == 0 || ikSmokeFrame == ikDumpFrame))
        {
            double err = PrintIkDiagnostics(model.Model, timeline?.CurrentFrame ?? 0);
            if (err > ikSmokeMaxErr) { ikSmokeMaxErr = err; ikSmokeWorst = $"渲染帧 {ikSmokeFrame}"; }
        }
        if (ikSmoke && ikDumpPath is not null && ikSmokeFrame == ikDumpFrame)
        {
            DumpIkJson(ikDumpPath, model.Model, timeline?.CurrentFrame ?? 0);
            Console.WriteLine($"[ik-smoke] 已导出对拍数据：{ikDumpPath}");
        }

        // --ik-bake-dump：PrepareFrame 之后（世界矩阵已含 IK 叠加）记录最终局部旋转。
        // 最终局部旋转 = FinalRotations（动画+軸制限+付与+IK 叠加，RecomputeBone 内
        // base *= IkRotations 后写入），与 MMD 烘焙进 VMD 的「最终状态」同一语义
        // （PMX 骨绑定局部旋转恒为单位阵）。
        if (ikSmoke && ikBakeDumpPath is not null && model.Model.BoneCount > 0)
        {
            if (ikBakeIdx.Length == 0) (ikBakeNames, ikBakeIdx) = SelectIkBakeBones(model.Model);
            var qs = new System.Numerics.Quaternion[ikBakeIdx.Length];
            var ps = new System.Numerics.Vector3[ikBakeIdx.Length];
            for (int b = 0; b < ikBakeIdx.Length; b++)
            {
                int bi = ikBakeIdx[b];
                // FinalRotations 已含 IkRotations（RecomputeBone 内 base *= ik 后写入），
                // 直接记录即为最终局部旋转 —— 不可再乘一次 IkRotations（会把链骨角度记成 2 倍）。
                qs[b] = System.Numerics.Quaternion.Normalize(model.Model.FinalRotations[bi]);
                ps[b] = model.Model.WorldMatrices[bi].Translation;
            }
            ikBakeFrames.Add((ikSmokeFrame - 1, qs, ps));
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

    // --xform-smoke：MMD モデル操作端到端验收（时序见各 case 注释）
    if (xformSmoke && model != null)
    {
        var mm = model.Model;
        switch (xformFrame)
        {
            case 2:  // 基准帧（恒等变换）：采校验和 + 骨骼基准位置，随后施加 TR（Y+30°，移動 (2,1,0)）
            {
                xformC0 = Checksum(ReadViewport(device!, w, h));
                if (mm.OperationCenterBoneIndex >= 0)
                    xformOc0 = mm.WorldMatrices[mm.OperationCenterBoneIndex].Translation;
                int c = mm.FindBone("センター");
                if (c >= 0) xformCenter0 = mm.WorldMatrices[c].Translation;
                Console.WriteLine($"[xform-smoke] 基准：校验和 {xformC0} | 全ての親={(mm.RootTransformBoneIndex >= 0 ? mm.BoneNames[mm.RootTransformBoneIndex] : "无")}({mm.RootTransformBoneIndex}) | " +
                                  $"操作中心={(mm.OperationCenterBoneIndex >= 0 ? $"{mm.BoneNames[mm.OperationCenterBoneIndex]}({mm.OperationCenterBoneIndex}) @ {xformOc0}" : "无")}");
                mm.ModelRotationAngles = new System.Numerics.Vector3(0f, 30f * MathF.PI / 180f, 0f);
                mm.ModelTranslationOffset = new System.Numerics.Vector3(2f, 1f, 0f);
                break;
            }

            case 3:  // TR 生效帧：画面变化 / 操作中心不动 / 子孙绕枢轴旋转 / 世界轴平移
            {
                xformC1 = Checksum(ReadViewport(device!, w, h));
                bool pictureChanged = xformC1 != xformC0;
                bool ocFixed = mm.OperationCenterBoneIndex < 0 ||
                    System.Numerics.Vector3.Distance(mm.WorldMatrices[mm.OperationCenterBoneIndex].Translation, xformOc0) < 1e-4f;

                int c = mm.FindBone("センター");
                bool subtreeMoved = c >= 0 &&
                    System.Numerics.Vector3.Distance(mm.WorldMatrices[c].Translation, xformCenter0) > 1f;
                // |v' - (pivot+t)| == |v - pivot|：旋转绕枢轴 + 世界轴平移的几何不变量
                float dist0 = 0, dist1 = 0;
                if (c >= 0 && mm.RootTransformBoneIndex >= 0 &&
                    System.Numerics.Matrix4x4.Invert(mm.InverseBind[mm.RootTransformBoneIndex], out var bindWorld))
                {
                    var pivot = bindWorld.Translation;
                    dist0 = (xformCenter0 - pivot).Length();
                    dist1 = (mm.WorldMatrices[c].Translation - (pivot + mm.ModelTranslationOffset)).Length();
                }
                bool distKept = MathF.Abs(dist0 - dist1) < 1e-3f;

                xformWorldTr = (System.Numerics.Matrix4x4[])mm.WorldMatrices.Clone();
                Console.WriteLine($"[xform-smoke] A) TR 注入 全ての親：画面变化={(pictureChanged ? "✅" : "❌")} | " +
                                  $"操作中心不动={(ocFixed ? "✅" : "❌")} | 子孙跟随旋转={(subtreeMoved ? "✅" : "❌")} | " +
                                  $"枢轴距离不变 {dist0:F3} vs {dist1:F3}{(distKept ? " ✅" : " ❌")}");
                // 拡大率：非均匀（压纸片方向）——必须只影响渲染，不碰 WorldMatrices
                model.ModelScale = new System.Numerics.Vector3(1.2f, 0.3f, 1.0f);
                break;
            }

            case 4:  // 拡大率生效帧：画面变化（视觉缩放）+ WorldMatrices 逐位不变（物理解耦）
            {
                xformC2 = Checksum(ReadViewport(device!, w, h));
                bool pictureChanged = xformC2 != xformC1;
                bool worldUntouched = xformWorldTr != null &&
                    xformWorldTr.AsSpan().SequenceEqual(mm.WorldMatrices.AsSpan());
                Console.WriteLine($"[xform-smoke] B) 拡大率 (1.2,0.3,1.0)：画面变化={(pictureChanged ? "✅" : "❌")} | " +
                                  $"WorldMatrices 逐位不变（物理/IK 不受影响）={(worldUntouched ? "✅" : "❌")}");
                break;
            }

            case 6:  // 重置（模拟 R 键）
            {
                mm.ModelTranslationOffset = System.Numerics.Vector3.Zero;
                mm.ModelRotationAngles = System.Numerics.Vector3.Zero;
                model.ModelScale = System.Numerics.Vector3.One;
                break;
            }

            case 7:  // 重置生效帧：画面必须回到基准校验和（幂等 / 无残留）
            {
                long c3 = Checksum(ReadViewport(device!, w, h));
                Console.WriteLine($"[xform-smoke] C) 重置回基准：{(c3 == xformC0 ? "✅ 校验和一致（无残留）" : $"❌ {c3} ≠ {xformC0}")}");
                break;
            }
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

// ── --ik-smoke 用：逐链打印「目标 vs 被驱动端」的距离与高度差 ────────────────
// 判定"腿脚悬空"的直接指标：足ＩＫ 目标与足首之间的世界距离。IK 正常时它会收敛到 ~0；
// 若全程停在模型身高量级（几十个单位），就是"腿够不到"（悬空）。
static double PrintIkDiagnostics(SkeletalModel m, double frame)
{
    var chains = m.IkChains;
    if (chains.Length == 0)
    {
        Console.WriteLine($"[ik-smoke] 帧 {frame,6:F1} | 该模型没有 IK 链（PmxIk 为空）");
        return 0;
    }

    var sb = new System.Text.StringBuilder();
    double maxErr = 0;
    for (int c = 0; c < chains.Length; c++)
    {
        var ch = chains[c];
        System.Numerics.Vector3 goal = m.WorldMatrices[ch.Goal].Translation;
        System.Numerics.Vector3 driven = m.WorldMatrices[ch.Driven].Translation;
        double err = System.Numerics.Vector3.Distance(goal, driven);
        if (err > maxErr) maxErr = err;

        // 链骨上累积的 IK 旋转角（度）。恒为 0 说明求解结果没留在 IkRotations 里，
        // 那么打印出来的 err 其实是"FK 未解算"的距离（早期版本就是这样误报的）。
        double rotDeg = 0;
        foreach (var lk in ch.Links)
        {
            var q = m.IkRotations[lk.BoneIndex];
            double d = 2.0 * System.Math.Acos(System.Math.Clamp(System.Math.Abs(q.W), 0.0, 1.0)) * 180.0 / System.Math.PI;
            if (d > rotDeg) rotDeg = d;
        }

        if (c > 0) sb.Append(" | ");
        sb.Append($"{m.BoneNames[ch.Goal]}→{m.BoneNames[ch.Driven]} {err:F4}(Δy {driven.Y - goal.Y,7:F3}, rot {rotDeg,5:F1}°)");
    }
    Console.WriteLine($"[ik-smoke] 帧 {frame,6:F1} | {sb}");
    return maxErr;
}

// ── --ik-smoke --ik-dump= 用：导出对拍数据 ────────────────────────────────
// 内容 = rig（名字/父/局部 T/R）+ 付与后的有效局部变换 + FK 世界 + IK 结果。
// 「有效局部变换」是关键：它让 Node 侧不必复刻付与/軸制限，直接重放同一骨架即可对拍 IK 算法本身。
static void DumpIkJson(string path, SkeletalModel m, double frame)
{
    static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    static string V3(System.Numerics.Vector3 v) => $"[{F(v.X)},{F(v.Y)},{F(v.Z)}]";
    static string V4(System.Numerics.Quaternion q) => $"[{F(q.X)},{F(q.Y)},{F(q.Z)},{F(q.W)}]";

    // ① FK 基准：关掉 IK 跑一次全量更新 → 得到「付与之后、IK 之前」的有效局部变换与 FK 世界位置。
    bool saved = m.IkSolverEnabled;
    m.IkSolverEnabled = false;
    MmdIkSolver.Solve(m);
    m.UpdateWorldMatrices();
    var effRot = m.FinalRotations.ToArray();
    var effTrans = m.FinalTranslations.ToArray();
    var fkWorld = new System.Numerics.Vector3[m.BoneCount];
    for (int i = 0; i < m.BoneCount; i++) fkWorld[i] = m.WorldMatrices[i].Translation;

    // ② 开 IK 求解 → 记录每条链的结果与最终世界位置。
    m.IkSolverEnabled = saved;
    var results = new List<IkChainSolveResult>();
    MmdIkSolver.Solve(m, results);
    m.UpdateWorldMatrices();
    var ikRot = (System.Numerics.Quaternion[])m.IkRotations.Clone();
    var ikWorld = new System.Numerics.Vector3[m.BoneCount];
    for (int i = 0; i < m.BoneCount; i++) ikWorld[i] = m.WorldMatrices[i].Translation;

    // 求值序位置（诊断付与排序问题用）：deformPos[i] = DeformOrder 中的名次。
    var deformPos = new int[m.BoneCount];
    for (int k = 0; k < m.DeformOrder.Length; k++) deformPos[m.DeformOrder[k]] = k;

    var sb = new System.Text.StringBuilder();
    sb.Append("{\n\"meta\":{");
    sb.Append($"\"source\":\"MikuEngine\",\"frame\":{F((float)frame)},\"boneCount\":{m.BoneCount},");
    sb.Append($"\"chainCount\":{m.IkChains.Length},\"solver\":\"PmxEditor IKTransform port\"}},\n");

    sb.Append("\"bones\":[\n");
    for (int i = 0; i < m.BoneCount; i++)
    {
        if (i > 0) sb.Append(",\n");
        sb.Append($"{{\"i\":{i},\"name\":\"{m.BoneNames[i].Replace("\\", "\\\\").Replace("\"", "\\\"")}\",");
        sb.Append($"\"parent\":{m.ParentIndices[i]},\"bindPos\":{V3(m.LocalPositions[i])},");
        sb.Append($"\"localPos\":{V3(m.LocalTranslations[i])},\"localRot\":{V4(m.LocalRotations[i])},");
        sb.Append($"\"effPos\":{V3(effTrans[i])},\"effRot\":{V4(effRot[i])},");
        sb.Append($"\"fkWorld\":{V3(fkWorld[i])},\"ikWorld\":{V3(ikWorld[i])},");
        sb.Append($"\"ikRot\":{V4(ikRot[i])},");
        sb.Append($"\"deformPos\":{deformPos[i]},\"appendSrc\":{m.AppendSources[i]},");
        sb.Append($"\"axisLim\":{V3(m.AxisLimits[i])}}}");
    }
    sb.Append("\n],\n");

    sb.Append("\"ikChains\":[\n");
    for (int c = 0; c < m.IkChains.Length; c++)
    {
        var ch = m.IkChains[c];
        if (c > 0) sb.Append(",\n");
        sb.Append($"{{\"index\":{c},\"goal\":{ch.Goal},\"driven\":{ch.Driven},");
        sb.Append($"\"iteration\":{ch.Iteration},\"limitAngle\":{F(ch.LimitAngle)},");
        sb.Append($"\"enabled\":{(m.IkEnabled[c] ? "true" : "false")},");
        sb.Append($"\"iterationsUsed\":{(c < results.Count ? results[c].IterationsUsed : -1)},");
        sb.Append($"\"errorSquared\":{(c < results.Count ? F(results[c].ErrorSquared) : "null")},");
        sb.Append("\"links\":[");
        for (int l = 0; l < ch.Links.Length; l++)
        {
            var lk = ch.Links[l];
            if (l > 0) sb.Append(",");
            sb.Append($"{{\"bone\":{lk.BoneIndex},\"hasLimit\":{(lk.HasLimitation ? "true" : "false")},");
            sb.Append($"\"min\":{V3(lk.MinAngle)},\"max\":{V3(lk.MaxAngle)},");
            sb.Append($"\"order\":{(int)lk.EulerOrder},\"fixAxis\":{(int)lk.FixAxis}}}");
        }
        sb.Append("]}");
    }
    sb.Append("\n]\n}\n");

    File.WriteAllText(path, sb.ToString());
}

// ── --ik-bake-dump 用：选骨 + 写 bake 对拍 JSON ──────────────────────────────
// 选骨集合 = 所有 IK 链的 Goal/Driven/Link 骨 ∪ 名字含 足/ひざ/つま先/高跟鞋/鉤 的骨
// （后者覆盖 MMD 解析式足 IK 会写、但不在任何 PMX 链里的骨，如 つま先/足D —— 诊断用）。
static (string[] Names, int[] Idx) SelectIkBakeBones(SkeletalModel m)
{
    var set = new SortedSet<int>();
    foreach (var ch in m.IkChains)
    {
        set.Add(ch.Goal);
        set.Add(ch.Driven);
        foreach (var lk in ch.Links) set.Add(lk.BoneIndex);
    }
    for (int i = 0; i < m.BoneCount; i++)
    {
        string n = m.BoneNames[i];
        if (n.Contains('足') || n.Contains("ひざ") || n.Contains("つま先") || n.Contains("高跟鞋") || n.Contains('鉤'))
            set.Add(i);
    }
    int[] idx = set.ToArray();
    return (idx.Select(i => m.BoneNames[i]).ToArray(), idx);
}

static void DumpBakeJson(string path, string[] names, List<(int F, System.Numerics.Quaternion[] Q, System.Numerics.Vector3[] P)> frames)
{
    static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    var sb = new System.Text.StringBuilder(1 << 22);
    sb.Append("{\"meta\":{\"source\":\"MikuEngine\",\"kind\":\"final-local-rotations\",");
    sb.Append("\"compose\":\"FinalRotations (already includes IkRotations)\",\"boneCount\":").Append(names.Length);
    sb.Append(",\"frameCount\":").Append(frames.Count).Append("},\n\"bones\":[");
    for (int i = 0; i < names.Length; i++)
    {
        if (i > 0) sb.Append(',');
        sb.Append('"').Append(names[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
    }
    sb.Append("],\n\"frames\":[\n");
    for (int f = 0; f < frames.Count; f++)
    {
        if (f > 0) sb.Append(",\n");
        sb.Append("{\"f\":").Append(frames[f].F).Append(",\"r\":[");
        var qs = frames[f].Q;
        for (int b = 0; b < qs.Length; b++)
        {
            if (b > 0) sb.Append(',');
            var q = qs[b];
            sb.Append('[').Append(F(q.X)).Append(',').Append(F(q.Y)).Append(',')
              .Append(F(q.Z)).Append(',').Append(F(q.W)).Append(']');
        }
        sb.Append("],\"p\":[");
        var p3 = frames[f].P;
        for (int b = 0; b < p3.Length; b++)
        {
            if (b > 0) sb.Append(',');
            sb.Append('[').Append(F(p3[b].X)).Append(',').Append(F(p3[b].Y)).Append(',').Append(F(p3[b].Z)).Append(']');
        }
        sb.Append("]}");
    }
    sb.Append("\n]}\n");
    File.WriteAllText(path, sb.ToString());
}

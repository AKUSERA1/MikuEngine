using System.Collections.Generic;
using System.Numerics;
using MikuEngine.Core.Camera;

namespace MikuEngine.Engine;

/// <summary>
/// 轨道相机输入控制器。
/// 跨平台（不依赖任何平台 API），调用方只需：
///   1. 构造时传入 OrbitCamera 和 bool enabled
///   2. 把平台原始输入事件转发到 OnPointerDown/Move/Up、OnScroll
///
/// 手势映射：
///   鼠标左键拖拽        → Orbit（旋转）
///   鼠标右键拖拽        → Pan（平移 target）
///   鼠标滚轮向上/向下   → Zoom in / Zoom out
///   单指拖拽            → Orbit
///   双指拖拽（中心平移） → Pan
///   双指捏合/张开       → Zoom
/// </summary>
public sealed class OrbitInputController
{
    private readonly OrbitCamera _camera;

    /// <summary>是否启用输入响应。关闭后所有 OnXXX 调用都是 no-op。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 是否允许输入响应（<see cref="Enabled"/> 且相机未被 VMD 姿态驱动）。
    /// VMD 相机动画独占视图时，orbit/pan/zoom 操作的是一组与取景无关的参数，一律短路。
    /// </summary>
    public bool InputAllowed => Enabled && !_camera.VmdDriven;

    /// <summary>
    /// Orbit 旋转灵敏度（弧度 / 像素）。默认 0.0025 ≈ 0.14°/px。
    /// 360° 旋转约需 2513px 水平拖拽。
    /// </summary>
    public float RotationSensitivity { get; set; } = 0.0025f;

    /// <summary>
    /// Pan 平移灵敏度（最终会乘以 Radius，即相机距离）。
    /// 默认 0.003 → Radius=100 时 1 像素平移 0.3 单位。
    /// </summary>
    public float PanSensitivity { get; set; } = 0.003f;

    /// <summary>滚轮缩放灵敏度。正值 = 向上滚拉近，负值 = 向下滚推远。默认 1.0 单位/格。</summary>
    public float WheelZoomSensitivity { get; set; } = 1.0f;

    /// <summary>触控捏合灵敏度：两指距离每变化 1% 对应 Radius 变化量。默认 50。</summary>
    public float PinchZoomSensitivity { get; set; } = 50.0f;

    // ── 指针状态 ──
    // 记录所有活跃指针的最新位置（pointerId → (x, y)）
    private readonly Dictionary<int, Vector2> _pointers = new();

    // 双指捏合初始距离（用于计算 pinch 比例变化）
    private float _initialPinchDistance;

    // 上一帧的双指中心点（用于双指 Pan 计算 delta）
    private Vector2 _lastTwoFingerCenter;

    // 单指旋转起点
    private (int id, Vector2 pos)? _singleTouch;

    // 鼠标按钮状态
    private bool _leftMouseDown;
    private bool _rightMouseDown;
    private Vector2 _lastMousePos;

    public OrbitInputController(OrbitCamera camera, bool enabled = true)
    {
        _camera = camera;
        Enabled = enabled;
    }

    // ─────────────────────────────────────────────────────────
    // 平台层调用的原始输入事件入口
    // ─────────────────────────────────────────────────────────

    public enum PointerButton
    {
        None,      // 触控 / 未指定
        Left,      // 鼠标左键
        Right,     // 鼠标右键
        Middle,    // 鼠标中键
    }

    /// <summary>指针按下（鼠标按键 / 触控点）。</summary>
    public void OnPointerDown(int pointerId, float x, float y, PointerButton button = PointerButton.None)
    {
        if (!InputAllowed) return;

        _pointers[pointerId] = new Vector2(x, y);

        if (_pointers.Count == 1)
        {
            // 第一个指针 → 进入单指旋转模式
            _singleTouch = (pointerId, new Vector2(x, y));
        }
        else if (_pointers.Count == 2)
        {
            // 第二指按下 → 进入双指捏合 + 双指平移模式
            _singleTouch = null;
            var (a, b) = GetTwoFingers();
            _initialPinchDistance = Vector2.Distance(a, b);
            _lastTwoFingerCenter = (a + b) * 0.5f;
        }

        // 鼠标按钮单独跟踪（pointerId=0 约定为鼠标）
        if (pointerId == 0)
        {
            if (button == PointerButton.Left) _leftMouseDown = true;
            if (button == PointerButton.Right) _rightMouseDown = true;
            _lastMousePos = new Vector2(x, y);
        }
    }

    /// <summary>指针移动。</summary>
    public void OnPointerMove(int pointerId, float x, float y)
    {
        if (!InputAllowed) return;
        if (!_pointers.TryGetValue(pointerId, out var prev)) return;

        var current = new Vector2(x, y);
        _pointers[pointerId] = current;

        // ── 鼠标（pointerId=0）单独处理左键/右键拖拽 ──
        if (pointerId == 0 && (_leftMouseDown || _rightMouseDown))
        {
            float dx = current.X - prev.X;
            float dy = current.Y - prev.Y;

            if (_leftMouseDown)
                _camera.Orbit(-dx * RotationSensitivity, -dy * RotationSensitivity);
            if (_rightMouseDown)
                _camera.Pan(dx * PanSensitivity, dy * PanSensitivity);

            _lastMousePos = current;
            return;
        }

        // ── 触控（pointerId >= 1）处理 ──
        if (_pointers.Count == 1 && _singleTouch.HasValue && _singleTouch.Value.id == pointerId)
        {
            // 单指 → Orbit
            float dx = current.X - prev.X;
            float dy = current.Y - prev.Y;
            _camera.Orbit(-dx * RotationSensitivity, -dy * RotationSensitivity);
        }
        else if (_pointers.Count == 2)
        {
            // 双指 → 同时做 Pan（中心平移）+ Zoom（捏合）
            var (a, b) = GetTwoFingers();
            var center = (a + b) * 0.5f;

            // 平移 delta
            float pdx = center.X - _lastTwoFingerCenter.X;
            float pdy = center.Y - _lastTwoFingerCenter.Y;
            if (MathF.Abs(pdx) + MathF.Abs(pdy) > 0.1f)
                _camera.Pan(pdx * PanSensitivity, pdy * PanSensitivity);

            // 捏合比例变化 → Zoom
            float dist = Vector2.Distance(a, b);
            if (_initialPinchDistance > 0.1f)
            {
                float ratio = dist / _initialPinchDistance;  // >1 张开，<1 捏合
                float zoomDelta = (1f - ratio) * PinchZoomSensitivity;
                _camera.Zoom(zoomDelta);
                _initialPinchDistance = dist;  // 累积缩放
            }

            _lastTwoFingerCenter = center;
        }
    }

    /// <summary>指针抬起。</summary>
    public void OnPointerUp(int pointerId)
    {
        if (!InputAllowed) return;

        _pointers.Remove(pointerId);

        // 鼠标按钮
        if (pointerId == 0)
        {
            _leftMouseDown = false;
            _rightMouseDown = false;
        }

        // 重新评估手势状态
        if (_pointers.Count == 1)
        {
            var (id, pos) = _pointers.First();
            _singleTouch = (id, pos);
        }
        else if (_pointers.Count == 0)
        {
            _singleTouch = null;
        }
        // 2 指 → 无需重置状态，下一次 move 会自动继续 Pan/Pinch
    }

    /// <summary>滚轮事件。deltaY > 0 向上滚（拉近），deltaY < 0 向下滚（推远）。</summary>
    public void OnScroll(float deltaY)
    {
        if (!InputAllowed) return;
        _camera.Zoom(-deltaY * WheelZoomSensitivity);
    }

    // ─────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────

    private (Vector2 a, Vector2 b) GetTwoFingers()
    {
        using var e = _pointers.GetEnumerator();
        e.MoveNext();
        var a = e.Current.Value;
        e.MoveNext();
        var b = e.Current.Value;
        return (a, b);
    }
}

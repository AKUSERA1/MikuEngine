using System.Numerics;

namespace MikuEngine.Core.Camera;

/// <summary>
/// 轨道相机，移植自 reze-engine 的 <c>Camera</c>。
///
/// 采用 **左手坐标系（+Z 正向）**，使用球坐标（alpha/beta/radius + 注视点 target）驱动。
/// 矩阵以 **列主序 float[16]** 输出（与 GLSL 的 uniform mat4 内存布局一致，无需转置）。
/// 本类为纯逻辑，不依赖任何平台或渲染 API；输入（鼠标/触摸）由平台层调用
/// <see cref="Orbit"/>/<see cref="Pan"/>/<see cref="Zoom"/> 等方法驱动。
/// </summary>
public sealed class OrbitCamera
{
    /// <summary>远裁剪面上限，足够容纳大范围的场景而不裁掉远处地面。</summary>
    private const float FarCap = 8000f;

    /// <summary>远裁剪面下限。</summary>
    private const float FarMin = 200f;

    /// <summary>近裁剪面的下限/上限（UNORM 深度缓冲）。本项目暂未启用 reversed-Z。</summary>
    private const float NearMin = 1.0f;
    private const float NearMax = 10f;

    /// <summary>俯仰角的上下限，防止相机翻转。</summary>
    public const float MinPitch = 0.001f;
    public const float MaxPitch = MathF.PI - 0.001f;

    /// <summary>水平环绕角（yaw）。</summary>
    public float Alpha;

    /// <summary>垂直俯仰角（pitch），值域 [MinPitch, MaxPitch]。</summary>
    public float Beta;

    /// <summary>相机到注视点的距离（缩放）。</summary>
    public float Radius;

    /// <summary>注视点。平移（Pan）移动的就是它。</summary>
    public Vector3 Target;

    /// <summary>垂直视场角（弧度）。</summary>
    public float Fov;

    /// <summary>宽高比（viewWidth / viewHeight），影响投影矩阵。</summary>
    public float Aspect = 1f;

    /// <summary>近/远裁剪面，随缩放半径动态更新。</summary>
    public float Near;
    public float Far;

    // ── 输入灵敏度 ──
    public float AngularSensitivity = 0.005f;  // 旋转灵敏度（弧度/像素）
    public float PanSensitivity = 0.0002f;     // 平移灵敏度（与半径成正比）
    public float WheelPrecision = 0.01f;       // 滚轮缩放系数

    public float MinZ = 0.05f;
    public float MaxZ = FarCap;

    public OrbitCamera(float alpha, float beta, float radius, Vector3 target, float fov = MathF.PI / 4f)
    {
        Alpha = alpha;
        Beta = beta;
        Radius = radius;
        Target = target;
        Fov = fov;
        UpdateFarFromRadius();
        UpdateNearFromRadius();
    }

    /// <summary>
    /// 把球坐标转换为笛卡尔坐标，得到相机位置。
    /// reze-engine 约定：y 轴向上分量由 cos(beta) 给出，xz 平面由 sin(beta)·(sin,cos)(alpha) 展开。
    /// </summary>
    public Vector3 Position => new(
        Target.X + Radius * MathF.Sin(Beta) * MathF.Sin(Alpha),
        Target.Y + Radius * MathF.Cos(Beta),
        Target.Z + Radius * MathF.Sin(Beta) * MathF.Cos(Alpha));

    /// <summary>
    /// 环绕旋转。水平移动像素 -> alpha，垂直移动像素 -> beta（向上拖使视角上仰）。
    /// </summary>
    public void Orbit(float deltaX, float deltaY)
    {
        Alpha += deltaX * AngularSensitivity;
        Beta -= deltaY * AngularSensitivity;
        // 钳制 beta，防止相机翻转
        Beta = MathF.Max(MinPitch, MathF.Min(MaxPitch, Beta));
    }

    /// <summary>
    /// 平移注视点（相当于拖拽场景）。
    /// 平移量 = 半径 × 灵敏度，保证不同缩放级别下手感一致。
    /// 横向：拖动向右则场景向左（反向）；纵向：向上拖则场景向上。
    /// </summary>
    public void Pan(float deltaX, float deltaY)
    {
        GetCameraVectors(out Vector3 right, out Vector3 up);
        float panDistance = Radius * PanSensitivity;
        Target += right * (-deltaX * panDistance) + up * (deltaY * panDistance);
    }

    /// <summary>
    /// 滚轮缩放相机距离。
    /// </summary>
    public void Zoom(float deltaY)
    {
        Radius += deltaY * WheelPrecision;
        Radius = MathF.Max(MinZ, MathF.Min(MaxZ, Radius));
        UpdateFarFromRadius();
    }

    /// <summary>
    /// 计算相机的 right / up 世界向量，用于平移。
    /// </summary>
    private void GetCameraVectors(out Vector3 right, out Vector3 up)
    {
        Vector3 eye = Position;
        Vector3 forward = Target - eye;
        float forwardLen = forward.Length();

        // 相机几乎与目标重叠的退化情况
        if (forwardLen < 0.0001f)
        {
            right = Vector3.UnitX;
            up = Vector3.UnitY;
            return;
        }

        forward /= forwardLen;
        Vector3 worldUp = Vector3.UnitY;

        // right = worldUp × forward
        right = Vector3.Cross(worldUp, forward);
        float rightLen = right.Length();
        if (rightLen < 0.0001f)
        {
            // 相机正对正上方/正下方时退化，用 X 轴作为 right
            right = Vector3.UnitX;
        }
        else
        {
            right /= rightLen;
        }

        // up = forward × right，确保正交
        up = Vector3.Cross(forward, right);
        float upLen = up.Length();
        up = upLen < 0.0001f ? Vector3.UnitY : up / upLen;
    }

    /// <summary>远裁剪面随缩放半径增长，保证拉远时大地面/远处几何仍可见。</summary>
    private void UpdateFarFromRadius()
    {
        const float margin = 600f;
        Far = MathF.Min(FarCap, MathF.Max(FarMin, Radius * 12f + margin));
    }

    /// <summary>
    /// 近裁剪面随取景距离缩放。UNORM 深度缓冲精度集中在近平面附近，
    /// 取景越远，near/far 比值越大，远处表面越容易共享同一深度值导致闪烁。
    /// 把 near 与取景距离绑定可恢复精度（见 reze-engine 注释）。
    /// </summary>
    private void UpdateNearFromRadius()
    {
        Near = MathF.Min(NearMax, MathF.Max(NearMin, Radius / 50f));
    }

    /// <summary>
    /// 把视图矩阵写入 <paramref name="m"/>（列主序，左手 lookAt）。
    /// </summary>
    public void WriteViewMatrix(float[] m)
    {
        Vector3 eye = Position;
        WriteLookAt(m, eye, Target, Vector3.UnitY);
    }

    /// <summary>
    /// 把投影矩阵写入 <paramref name="m"/>（列主序）。
    /// 左手透视，把 z ∈ [near, far] 线性映射到裁剪 z ∈ [0, w]（reze-engine / WebGPU 约定）。
    /// </summary>
    public void WriteProjectionMatrix(float[] m)
    {
        UpdateFarFromRadius();
        UpdateNearFromRadius();

        float f = 1f / MathF.Tan(Fov / 2f);
        float rangeInv = 1f / (Far - Near);

        m[0] = f / Aspect; m[1] = 0f; m[2] = 0f; m[3] = 0f;
        m[4] = 0f;         m[5] = f;  m[6] = 0f; m[7] = 0f;
        m[8] = 0f;         m[9] = 0f; m[10] = Far * rangeInv; m[11] = 1f;  // Z+ 正向（左手）
        m[12] = 0f;        m[13] = 0f; m[14] = -Near * Far * rangeInv; m[15] = 0f;
    }

    /// <summary>
    /// 左手坐标系 lookAt，等价于 reze-engine 的 Mat4.lookAtInto。
    /// forward = norm(target - eye)，right = norm(up × forward)，up2 = norm(forward × right)。
    /// 输出为列主序。
    /// </summary>
    private static void WriteLookAt(float[] o, Vector3 eye, Vector3 target, Vector3 up)
    {
        Vector3 f = target - eye;
        float fl = f.Length();
        f = fl > 0f ? f / fl : Vector3.Zero;

        Vector3 r = Vector3.Cross(up, f);
        float rl = r.Length();
        r = rl > 0f ? r / rl : Vector3.Zero;

        Vector3 v = Vector3.Cross(f, r);
        float vl = v.Length();
        v = vl > 0f ? v / vl : Vector3.Zero;

        o[0] = r.X; o[1] = v.X; o[2] = f.X; o[3] = 0f;
        o[4] = r.Y; o[5] = v.Y; o[6] = f.Y; o[7] = 0f;
        o[8] = r.Z; o[9] = v.Z; o[10] = f.Z; o[11] = 0f;
        o[12] = -(r.X * eye.X + r.Y * eye.Y + r.Z * eye.Z);
        o[13] = -(v.X * eye.X + v.Y * eye.Y + v.Z * eye.Z);
        o[14] = -(f.X * eye.X + f.Y * eye.Y + f.Z * eye.Z);
        o[15] = 1f;
    }
}
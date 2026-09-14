using System.Numerics;

namespace MikuEngine.Core.Camera;

/// <summary>
/// 轨道相机。
///
/// 坐标系约定：**左手坐标系（+Z 正向）+ Y-up**，与 MikuMikuDance 原生约定一致。
/// 矩阵输出 **列主序 float[16]**（GLSL mat4 直接读取，无需转置）。
/// 投影使用 **GLES 3.1 默认风格**：z ∈ [near, far] → z_clip ∈ [-w, +w]。
///
/// 本类为纯逻辑，不依赖任何平台或渲染 API。
/// </summary>
public sealed class OrbitCamera
{
    /// <summary>远裁剪面上限。MMD 地面雾色淡出在 150~200 单位完成，8000 够覆盖物理刚体散布。</summary>
    private const float FarCap = 8000f;

    private const float FarMin = 200f;

    /// <summary>
    /// 近裁剪面下限/上限。
    /// MMD 单位 ≈ 8cm，角色身高 ~25 单位，正常取景 Radius≈60。
    /// NearMin=0.3 让 Near = max(Radius/50, 0.3) = 1.2，near/far ≈ 0.0004 安全。
    /// </summary>
    private const float NearMin = 0.3f;
    private const float NearMax = 10f;

    public const float MinPitch = 0.001f;
    public const float MaxPitch = MathF.PI - 0.001f;

    public float Alpha;            // yaw
    public float Beta;             // pitch
    public float Radius;
    public Vector3 Target;
    public float Fov;
    public float Aspect = 1f;
    public float Near;
    public float Far;

    /// <summary>
    /// 以下三个字段是直接调用 Orbit/Pan/Zoom 时的内置缩放因子。
    /// OrbitInputController 作为 Engine 层唯一灵敏度入口，会传入已经乘好的 delta，
    /// 因此这里设为 1.0 保证 no-op。其他直接调用方可以按需调整。
    /// </summary>
    public float AngularSensitivity = 1.0f;
    public float PanSensitivity = 1.0f;
    public float WheelPrecision = 1.0f;

    public float MinZ = 0.05f;
    public float MaxZ = FarCap;

    // ─────────────────────────────────────────────────────────
    // VMD 相机驱动
    //
    // MMD 相机是「注视点」模型：看向 target，朝向由 euler 决定，沿自身 forward 后退 distance
    // （distance 为负）。驱动期间 orbit 的 alpha/beta/radius 不被触碰，fov 会被 VMD 轨道
    // 逐帧改写（进出驱动态时备份/恢复），输入控制器应短路 orbit/pan/zoom。
    // ─────────────────────────────────────────────────────────

    /// <summary>true = 由 VMD 相机姿态驱动视图（<see cref="SetVmdPose"/> 喂入）。
    /// 切换时备份 / 恢复 orbit 的 fov（VMD 相机动画会改写 fov）。</summary>
    public bool VmdDriven { get; private set; }

    private Vector3 _vmdTarget;
    private Vector3 _vmdRotation;   // euler 弧度（VMD 原始值）
    private float _vmdDistance;
    private float _savedFov = MathF.PI / 4f;

    /// <summary>进入 / 退出 VMD 驱动态。幂等：重复设置同一状态为 no-op。</summary>
    public void SetVmdDriven(bool enabled)
    {
        if (enabled == VmdDriven) return;
        if (enabled) _savedFov = Fov;
        else Fov = _savedFov;
        VmdDriven = enabled;
    }

    /// <summary>喂入下一帧采样的 VMD 相机姿态（fov 直接驱动投影）。</summary>
    public void SetVmdPose(Vector3 target, Vector3 rotationEuler, float distance, float fov)
    {
        _vmdTarget = target;
        _vmdRotation = rotationEuler;
        _vmdDistance = distance;
        Fov = fov;
    }

    /// <summary>
    /// VMD 姿态的视点：eye = target + q·(0,0,1)·distance。
    ///
    /// euler 三轴<b>取负</b>后构建四元数（babylon-mmd 用 RotationYawPitchRoll(-ry,-rx,-rz)；
    /// System.Numerics 的 CreateFromYawPitchRoll 与其 yaw/pitch/roll 语义一致）。
    /// forward = q·(0,0,1)（旋转矩阵第 3 列），distance 为负 ⇒ eye 落在注视点后方。
    /// </summary>
    private Vector3 VmdEye()
    {
        var q = Quaternion.CreateFromYawPitchRoll(-_vmdRotation.Y, -_vmdRotation.X, -_vmdRotation.Z);
        var m = Matrix4x4.CreateFromQuaternion(q);
        var forward = new Vector3(m.M13, m.M23, m.M33);
        return _vmdTarget + forward * _vmdDistance;
    }

    /// <summary>
    /// 视差量（高光 / rim / 球面贴图等着色项的相机位置）：
    /// VMD 驱动时必须取 <see cref="VmdEye"/>，orbit 的 <see cref="Position"/> 与真实拍摄点无关。
    /// </summary>
    public Vector3 GetEyePosition() => VmdDriven ? VmdEye() : Position;

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

    public Vector3 Position => new(
        Target.X + Radius * MathF.Sin(Beta) * MathF.Sin(Alpha),
        Target.Y + Radius * MathF.Cos(Beta),
        Target.Z + Radius * MathF.Sin(Beta) * MathF.Cos(Alpha));

    public void Orbit(float deltaX, float deltaY)
    {
        Alpha += deltaX * AngularSensitivity;
        Beta -= deltaY * AngularSensitivity;
        Beta = MathF.Max(MinPitch, MathF.Min(MaxPitch, Beta));
    }

    public void Pan(float deltaX, float deltaY)
    {
        GetCameraVectors(out Vector3 right, out Vector3 up);
        float panDistance = Radius * PanSensitivity;
        Target += right * (-deltaX * panDistance) + up * (deltaY * panDistance);
    }

    public void Zoom(float deltaY)
    {
        Radius += deltaY * WheelPrecision;
        Radius = MathF.Max(MinZ, MathF.Min(MaxZ, Radius));
        UpdateFarFromRadius();
    }

    private void GetCameraVectors(out Vector3 right, out Vector3 up)
    {
        Vector3 eye = Position;
        Vector3 forward = Target - eye;
        float forwardLen = forward.Length();

        if (forwardLen < 0.0001f)
        {
            right = Vector3.UnitX;
            up = Vector3.UnitY;
            return;
        }

        forward /= forwardLen;
        Vector3 worldUp = Vector3.UnitY;

        right = Vector3.Cross(worldUp, forward);
        float rightLen = right.Length();
        right = rightLen < 0.0001f ? Vector3.UnitX : right / rightLen;

        up = Vector3.Cross(forward, right);
        float upLen = up.Length();
        up = upLen < 0.0001f ? Vector3.UnitY : up / upLen;
    }

    private void UpdateFarFromRadius()
    {
        const float margin = 600f;
        float r = EffectiveRadius();
        Far = MathF.Min(FarCap, MathF.Max(FarMin, r * 12f + margin));
    }

    private void UpdateNearFromRadius()
    {
        Near = MathF.Min(NearMax, MathF.Max(NearMin, EffectiveRadius() / 50f));
    }

    /// <summary>近/远裁剪面的推算基准：VMD 驱动时 orbit 的 Radius 与取景无关，改用 |distance|。</summary>
    private float EffectiveRadius() => VmdDriven ? MathF.Abs(_vmdDistance) : Radius;

    // ─────────────────────────────────────────────────────────
    // 矩阵输出：列主序 float[16]，直接喂给 GLSL mat4
    // ─────────────────────────────────────────────────────────

    public void WriteViewMatrix(float[] m) => WriteViewMatrix(m.AsSpan());

    public void WriteViewMatrix(Span<float> m)
    {
        if (VmdDriven)
        {
            WriteVmdViewMatrix(m);
            return;
        }
        Vector3 eye = Position;
        WriteLookAt(m, eye, Target, Vector3.UnitY);
    }

    /// <summary>
    /// VMD 相机视图：View = Rᵀ · T(−eye)，直接由四元数旋转基铺出，<b>不走 lookAt</b>。
    ///
    /// 朝向本来就由 euler 决定、不依赖 eye→target 连线；position-baked 轨道全帧 distance=0
    /// （eye == target），lookAt 的归一化会退化成全零基，而 Rᵀ·T 形式对 d=0 天然稳健。
    /// 列主序输出，与 <see cref="WriteLookAt"/> 同一约定（col0=right、col1=up、col2=forward）。
    /// </summary>
    private void WriteVmdViewMatrix(Span<float> o)
    {
        var q = Quaternion.CreateFromYawPitchRoll(-_vmdRotation.Y, -_vmdRotation.X, -_vmdRotation.Z);
        var r = Matrix4x4.CreateFromQuaternion(q);
        Vector3 forward = new(r.M13, r.M23, r.M33);
        Vector3 eye = _vmdTarget + forward * _vmdDistance;

        // R 的三列 = right(M11,M21,M31) / up(M12,M22,M32) / forward(M13,M23,M33)；
        // view 的 col0 = R 的第 0 行 = (right.X, up.X, forward.X)，其余同构。
        o[0] = r.M11; o[1] = r.M12; o[2] = r.M13; o[3] = 0f;
        o[4] = r.M21; o[5] = r.M22; o[6] = r.M23; o[7] = 0f;
        o[8] = r.M31; o[9] = r.M32; o[10] = r.M33; o[11] = 0f;
        o[12] = -(r.M11 * eye.X + r.M21 * eye.Y + r.M31 * eye.Z);
        o[13] = -(r.M12 * eye.X + r.M22 * eye.Y + r.M32 * eye.Z);
        o[14] = -(r.M13 * eye.X + r.M23 * eye.Y + r.M33 * eye.Z);
        o[15] = 1f;
    }

    /// <summary>
    /// 左手 lookAt。forward = target - eye，right = worldUp × forward，up2 = forward × right。
    /// 列主序输出。
    /// </summary>
    private static void WriteLookAt(Span<float> o, Vector3 eye, Vector3 target, Vector3 up)
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

        // 列主序：每 4 个 float 是一列（x,y,z,w）
        o[0] = r.X;  o[1] = v.X;  o[2] = f.X;  o[3] = 0f;   // col0: right
        o[4] = r.Y;  o[5] = v.Y;  o[6] = f.Y;  o[7] = 0f;   // col1: up
        o[8] = r.Z;  o[9] = v.Z;  o[10] = f.Z; o[11] = 0f;  // col2: forward
        o[12] = -(r.X * eye.X + r.Y * eye.Y + r.Z * eye.Z);
        o[13] = -(v.X * eye.X + v.Y * eye.Y + v.Z * eye.Z);
        o[14] = -(f.X * eye.X + f.Y * eye.Y + f.Z * eye.Z);
        o[15] = 1f;                                           // col3: translation
    }

    /// <summary>
    /// 左手透视投影（GLES 3.1 默认风格）。
    /// z ∈ [near, far] → z_clip ∈ [-w, +w]（z_ndc ∈ [-1, 1]）。
    /// 列主序输出。
    /// </summary>
    public void WriteProjectionMatrix(float[] m) => WriteProjectionMatrix(m.AsSpan());

    public void WriteProjectionMatrix(Span<float> m)
    {
        UpdateFarFromRadius();
        UpdateNearFromRadius();

        float f = 1f / MathF.Tan(Fov / 2f);
        float rangeInv = 2f / (Far - Near);             // = 2/(f-n)
        float negSumDivRange = -(Far + Near) / (Far - Near);  // = -(f+n)/(f-n)

        m[0] = f / Aspect; m[1] = 0f; m[2] = 0f; m[3] = 0f;
        m[4] = 0f;         m[5] = f;  m[6] = 0f; m[7] = 0f;
        m[8] = 0f;         m[9] = 0f; m[10] = rangeInv; m[11] = 1f;
        m[12] = 0f;        m[13] = 0f; m[14] = negSumDivRange; m[15] = 0f;
    }

    /// <summary>
    /// 一步计算 View×Proj 矩阵并写入 <paramref name="outMatrix"/>（列主序）。
    /// 内部调用 WriteViewMatrix + WriteProjectionMatrix，会顺便刷新 Near/Far。
    /// </summary>
    public void ComputeViewProj(Span<float> outMatrix)
    {
        Span<float> view = stackalloc float[16];
        Span<float> proj = stackalloc float[16];
        WriteViewMatrix(view);
        WriteProjectionMatrix(proj);
        MultiplyColumnMajor(proj, view, outMatrix);  // 先应用 view，再应用 proj
    }

    /// <summary>
    /// 列主序 4×4 矩阵乘法：out = a × b。
    /// a、b、out 均为列主序 float[16]，输出也写入列主序。
    /// 列主序矩阵乘法公式与行主序相同：C_ij = Σ_k A_ik * B_kj
    /// 但索引方式不同：A 的 (i,j) 元素 = A[j*4 + i]（列 j，行 i）。
    /// </summary>
    private static void MultiplyColumnMajor(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> outMatrix)
    {
        Span<float> tmp = stackalloc float[16];
        for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++)
        {
            float sum = 0f;
            for (int k = 0; k < 4; k++)
                sum += a[k * 4 + i] * b[j * 4 + k];
            tmp[j * 4 + i] = sum;
        }
        tmp.CopyTo(outMatrix);
    }
}

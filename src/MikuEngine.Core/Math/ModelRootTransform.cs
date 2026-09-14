using System.Numerics;

namespace MikuEngine.Core.Math;

/// <summary>
/// 渲染层根变换（MMD 拡大率 + 无 全ての親 载体时的 TR 兜底）—— 纯数学，无 GL 依赖，可单测。
///
/// 与 <see cref="MikuEngine.Core.Models.SkeletalModel.ApplyModelTransform"/> 的分工：
///   * 面板的 移動/回転 在模型有 全ての親 时注入骨骼链（物理/IK 跟随，MMD 注册语义）；
///   * 拡大率（缩放）**永远**不进骨骼链 —— 由本类算出的根矩阵在<b>蒙皮之后</b>整体施加。
///     非均匀缩放（压成纸片 / 放大成巨人）作用于最终顶点，物理刚体、teleport 阈值、
///     付与、IK 全部运行在 bind 尺度，完全解耦。
///
/// 乘序（System.Numerics 行主序 row-vector，v·M 先 M 的左因子）：
///   有载体：Root = S                                   （TR 已在链内，仅剩缩放）
///   无载体：Root = R · T(move) · S                     （模拟 MMD 的「骨级 TR + 模型级 S」分层）
/// 即视觉 = 蒙皮结果 → TR → 缩放（缩放最外层、绕模型原点，与 MMD 拡大率一致）。
///
/// 法线修正：非均匀缩放下法线不能用普通矩阵变换，必须乘 (Root 线性部分)⁻ᵀ。
/// 按 GLSL 上传约定（引擎行主序矩阵原样上传 = shader 得到转置），shader 端需要的
/// uModelNormalRoot 恰等于 Root 的<b>逆矩阵</b>的线性 3x3（见 <see cref="ComputeNormalMatrix"/>）。
/// </summary>
public static class ModelRootTransform
{
    /// <summary>缩放下限：0 会让逆矩阵不存在（法线修正失败）且退化渲染，钳到一个小值。</summary>
    public const float MinScale = 0.01f;

    /// <summary>逐轴钳制缩放，防 0 / 负值（负值等价翻转，MMD 面板不允许，一并钳掉）。</summary>
    public static Vector3 ClampScale(Vector3 scale)
        => new(MathF.Max(scale.X, MinScale), MathF.Max(scale.Y, MinScale), MathF.Max(scale.Z, MinScale));

    /// <summary>
    /// 计算渲染层根矩阵。<paramref name="hasCarrier"/> = 模型存在 全ての親
    /// （<see cref="MikuEngine.Core.Models.SkeletalModel.RootTransformBoneIndex"/> ≥ 0），
    /// 此时面板 TR 已注入骨骼链，根矩阵只剩缩放。
    /// </summary>
    public static Matrix4x4 ComputeRootMatrix(bool hasCarrier, Vector3 move, Vector3 rotAnglesYxz, Vector3 scale)
    {
        Vector3 s = ClampScale(scale);
        Matrix4x4 root;

        if (hasCarrier)
        {
            root = Matrix4x4.CreateScale(s);
        }
        else
        {
            // MMD 的 YXZ 欧拉序；R·T·S = 先绕原点旋转 → 世界轴平移 → 最外层缩放
            Quaternion q = MmdMath.QuaternionFromYxzEuler(rotAnglesYxz.Y, rotAnglesYxz.X, rotAnglesYxz.Z);
            root = Matrix4x4.CreateFromQuaternion(q)
                 * Matrix4x4.CreateTranslation(move)
                 * Matrix4x4.CreateScale(s);
        }

        return root;
    }

    /// <summary>
    /// 法线修正矩阵 = Root 线性部分逆转置的 3x3。
    /// 推导：世界法线（引擎行向量形式）<c>n' = n · S · Q⁻ᵀ</c>（S = 蒙皮刚体旋转，Q = Root 线性部分）；
    /// shader 端 <c>uModelNormalRoot * sn</c> 需要的是 <c>Q⁻¹</c>，而 GLSL 上传约定（引擎矩阵原样喂 GL
    /// = shader 拿到转置）把转置抵消掉，因此引擎侧上传矩阵恰为 <c>Q⁻ᵀ</c> = 逆矩阵的转置。
    /// </summary>
    public static Matrix4x4 ComputeNormalMatrix(Matrix4x4 root)
        => Matrix4x4.Invert(root, out Matrix4x4 inv) ? Matrix4x4.Transpose(inv) : Matrix4x4.Identity;

    /// <summary>
    /// 提取线性 3x3（行主序 9 floats：M11 M12 M13 / M21 M22 M23 / M31 M32 M33）。
    /// 上传约定与 mat4 相同（transpose=false，引擎矩阵原样喂 GL），shader 端得到转置。
    /// </summary>
    public static float[] ExtractLinear3x3(in Matrix4x4 m) =>
    [
        m.M11, m.M12, m.M13,
        m.M21, m.M22, m.M23,
        m.M31, m.M32, m.M33,
    ];

    /// <summary>Matrix4x4 展平成 16 floats（行主序原样，上传约定与 FrameUniforms 一致）。</summary>
    public static float[] ToArray(in Matrix4x4 m) =>
    [
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    ];
}

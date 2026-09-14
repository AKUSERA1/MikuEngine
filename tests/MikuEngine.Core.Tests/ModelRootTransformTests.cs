using System.Numerics;
using MikuEngine.Core.Math;

namespace MikuEngine.Core.Tests;

/// <summary>
/// 渲染层根变换（拡大率）纯数学单测：
///   * 有 全ての親 载体 ⇒ 根矩阵是纯缩放（TR 已在骨骼链内）；
///   * 无载体 ⇒ R·T·S 复合，语义为「绕原点旋转 → 世界轴平移 → 最外层缩放」；
///   * 法线矩阵 = Root 线性部分的逆 ⇒ 非均匀缩放（压成纸片）下法线方向仍正确。
/// </summary>
public class ModelRootTransformTests
{
    // ------------------------------------------------------------- 缩放钳制

    [Fact]
    public void ClampScale_RejectsZeroAndNegative()
    {
        Assert.Equal(new Vector3(0.01f, 0.01f, 0.01f), ModelRootTransform.ClampScale(Vector3.Zero));
        Assert.Equal(new Vector3(2f, 0.01f, 3f), ModelRootTransform.ClampScale(new Vector3(2f, -1f, 3f)));
        Assert.Equal(new Vector3(2f, 3f, 4f), ModelRootTransform.ClampScale(new Vector3(2f, 3f, 4f)));
    }

    // ------------------------------------------------------------- 有载体：纯缩放

    [Fact]
    public void RootMatrix_WithCarrier_IsPureScale_IgnoringTr()
    {
        var root = ModelRootTransform.ComputeRootMatrix(
            hasCarrier: true,
            move: new Vector3(5f, 6f, 7f),
            rotAnglesYxz: new Vector3(0.3f, 0.4f, 0.5f),
            scale: new Vector3(2f, 3f, 4f));

        // TR 由骨骼链承担，根矩阵必须是纯对角缩放
        Assert.Equal(new Vector4(2f, 0f, 0f, 0f), root.Column1());
        Assert.Equal(new Vector4(0f, 3f, 0f, 0f), root.Column2());
        Assert.Equal(new Vector4(0f, 0f, 4f, 0f), root.Column3());
        Assert.Equal(Vector4.UnitW, root.Column4());
    }

    // ------------------------------------------------------------- 无载体：R·T·S

    [Fact]
    public void RootMatrix_WithoutCarrier_AppliesRotateThenTranslateThenScale()
    {
        Vector3 move = new(5f, 0f, 0f);
        float y = MathF.PI / 2f;
        Vector3 scale = new(2f, 2f, 2f);

        var root = ModelRootTransform.ComputeRootMatrix(
            hasCarrier: false, move, new Vector3(0f, y, 0f), scale);

        Quaternion q = MmdMath.QuaternionFromYxzEuler(y, 0f, 0f);
        Matrix4x4 r = Matrix4x4.CreateFromQuaternion(q);
        Matrix4x4 t = Matrix4x4.CreateTranslation(move);
        Matrix4x4 s = Matrix4x4.CreateScale(scale);
        Assert.Equal(r * t * s, root);

        // 点语义：v' = ((v·R) + move)·S —— 平移先于缩放被放大（MMD 分层：TR 在骨级，S 在模型级）
        Vector3 p = new(1f, 0f, 0f);
        Vector3 expected = Vector3.Transform(Vector3.Transform(Vector3.Transform(p, r), t), s);
        Vector3 actual = Vector3.Transform(p, root);
        Assert.True(Vector3.DistanceSquared(expected, actual) < 1e-10f,
            $"期望 {expected}，实际 {actual}");
    }

    // ------------------------------------------------------------- 法线矩阵

    [Fact]
    public void NormalMatrix_IsInverseOfRootLinearPart()
    {
        var root = ModelRootTransform.ComputeRootMatrix(false,
            new Vector3(1f, 2f, 3f), new Vector3(0.1f, 0.2f, 0.3f), new Vector3(2f, 4f, 0.5f));

        Matrix4x4 n = ModelRootTransform.ComputeNormalMatrix(root);

        // n = Root⁻ᵀ ⇒ Transpose(n) · Root = I（线性部分互逆；求逆有浮点误差，用容差）
        Matrix4x4 nt = Matrix4x4.Transpose(n);
        Matrix4x4 product = nt * root;
        float tol = 1e-4f;
        Assert.True(MathF.Abs(product.M11 - 1) < tol && MathF.Abs(product.M22 - 1) < tol
                 && MathF.Abs(product.M33 - 1) < tol && MathF.Abs(product.M44 - 1) < tol);
        Assert.True(MathF.Abs(product.M12) < tol && MathF.Abs(product.M13) < tol
                 && MathF.Abs(product.M21) < tol && MathF.Abs(product.M23) < tol
                 && MathF.Abs(product.M31) < tol && MathF.Abs(product.M32) < tol);
    }

    [Fact]
    public void NormalMatrix_SquashKeepsNormalDirectionCorrect()
    {
        // 压成纸片：Y 缩到 0.01。45° 斜面的法线 (1,1,0)/√2 经 (L)⁻¹ 后应被推向 Y 轴
        //（逆转置的经典行为：被压扁的方向法线分量放大），归一化后 Y 分量占绝对主导。
        var root = ModelRootTransform.ComputeRootMatrix(true,
            Vector3.Zero, Vector3.Zero, new Vector3(1f, 0.01f, 1f));

        Matrix4x4 n = ModelRootTransform.ComputeNormalMatrix(root);
        Vector3 face = Vector3.Normalize(new Vector3(1f, 1f, 0f));
        Vector3 corrected = Vector3.TransformNormal(face, n);   // = face · n（行向量约定）

        corrected = Vector3.Normalize(corrected);
        Assert.True(MathF.Abs(corrected.Y) > 0.99f,
            $"压扁后 45° 面法线应近似指向 +Y，实际 {corrected}");
    }

    [Fact]
    public void NormalMatrix_UniformScaleAndRotation_RotatesNormal()
    {
        // 纯旋转+均匀缩放：法线只被旋转（缩放分量不改变方向）
        float y = MathF.PI / 2f;
        var root = ModelRootTransform.ComputeRootMatrix(false,
            Vector3.Zero, new Vector3(0f, y, 0f), new Vector3(3f, 3f, 3f));

        Matrix4x4 n = ModelRootTransform.ComputeNormalMatrix(root);
        Vector3 corrected = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, n));

        // Z 轴法线绕 Y 转 90°（行向量约定下的方向与引擎旋转语义自洽即可，这里校验它是单位轴向量）
        float len = corrected.Length();
        Assert.True(MathF.Abs(len - 1f) < 1e-5f);
        Assert.True(MathF.Abs(MathF.Abs(corrected.X) - 1f) < 1e-4f
                 || MathF.Abs(MathF.Abs(corrected.Z) - 1f) < 1e-4f,
            $"旋转后的法线应落在 XZ 平面的轴上，实际 {corrected}");
    }

    // ------------------------------------------------------------- 上传展平

    [Fact]
    public void ExtractLinear3x3_MatchesRowMajorLayout()
    {
        var m = new Matrix4x4(
            1f, 2f, 3f, 4f,
            5f, 6f, 7f, 8f,
            9f, 10f, 11f, 12f,
            13f, 14f, 15f, 16f);

        var a = ModelRootTransform.ExtractLinear3x3(m);
        Assert.Equal(new float[] { 1, 2, 3, 5, 6, 7, 9, 10, 11 }, a);

        var f = ModelRootTransform.ToArray(m);
        Assert.Equal(16, f.Length);
        Assert.Equal(16f, f[15]);
        Assert.Equal(1f, f[0]);
    }
}

/// <summary>Matrix4x4 列访问小助手（断言可读性）。</summary>
internal static class MatrixExtensions
{
    public static Vector4 Column1(this Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14);
    public static Vector4 Column2(this Matrix4x4 m) => new(m.M21, m.M22, m.M23, m.M24);
    public static Vector4 Column3(this Matrix4x4 m) => new(m.M31, m.M32, m.M33, m.M34);
    public static Vector4 Column4(this Matrix4x4 m) => new(m.M41, m.M42, m.M43, m.M44);
}

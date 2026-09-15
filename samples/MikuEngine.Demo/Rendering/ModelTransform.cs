using System.Numerics;
using MikuEngine.Core.Models;
using MikuEngine.Render.GLES;

namespace MikuEngine.Demo.Rendering;

/// <summary>被调整的变换分量。</summary>
public enum TransformChannel
{
    /// <summary>移動 —— 注入 <c>全ての親</c>，世界轴平移。</summary>
    Translate,

    /// <summary>回転 —— 注入 <c>全ての親</c>，MMD YXZ 欧拉序。</summary>
    Rotate,

    /// <summary>拡大率 —— 只进渲染层根矩阵，与物理 / IK 解耦。</summary>
    Scale,
}

/// <summary>轴向。</summary>
public enum TransformAxis
{
    X,
    Y,
    Z,
}

/// <summary>
/// MMD「モデル操作」的**全局模式**（本 Demo 暂不做局部模式）：
/// <list type="bullet">
///   <item>移动 / 旋转写 <see cref="SkeletalModel.ModelTranslationOffset"/> /
///         <see cref="SkeletalModel.ModelRotationAngles"/> —— 由引擎注入 <c>全ての親</c>，
///         <c>操作中心</c> 留在原地、子孙绕枢轴跟随；</item>
///   <item>缩放写 <see cref="GlesModelRenderer.ModelScale"/> —— 只影响渲染根矩阵，
///         <c>WorldMatrices</c> 逐位不变，因此物理与 IK 完全看不见缩放。</item>
/// </list>
/// 步长按屏幕像素定义，方便直接接拖动交互。
/// </summary>
public static class ModelTransform
{
    /// <summary>移动：每像素多少模型单位。</summary>
    public const float UnitsPerPixel = 0.05f;

    /// <summary>旋转：每像素多少度。</summary>
    public const float DegreesPerPixel = 0.5f;

    /// <summary>缩放：每像素多少比例（0.01 = 每像素 1%）。</summary>
    public const float ScalePerPixel = 0.01f;

    public const float MinScale = 0.05f;
    public const float MaxScale = 100f;

    /// <summary>按拖动位移（像素，向上为正）调整指定分量。</summary>
    public static void Nudge(SkeletalModel skeleton, GlesModelRenderer renderer,
        TransformChannel channel, TransformAxis axis, int pixels)
    {
        if (pixels == 0) return;

        switch (channel)
        {
            case TransformChannel.Translate:
            {
                float delta = UnitsPerPixel * pixels;
                var t = skeleton.ModelTranslationOffset;
                switch (axis)
                {
                    case TransformAxis.X: t.X += delta; break;
                    case TransformAxis.Y: t.Y += delta; break;
                    default: t.Z += delta; break;
                }
                skeleton.ModelTranslationOffset = t;
                break;
            }

            case TransformChannel.Rotate:
            {
                float delta = MathF.PI / 180f * DegreesPerPixel * pixels;
                var r = skeleton.ModelRotationAngles;
                switch (axis)
                {
                    case TransformAxis.X: r.X += delta; break;
                    case TransformAxis.Y: r.Y += delta; break;
                    default: r.Z += delta; break;
                }
                skeleton.ModelRotationAngles = r;
                break;
            }

            case TransformChannel.Scale:
            {
                float factor = 1f + ScalePerPixel * pixels;
                var s = renderer.ModelScale;
                switch (axis)
                {
                    case TransformAxis.X: s.X = Clamp(s.X * factor); break;
                    case TransformAxis.Y: s.Y = Clamp(s.Y * factor); break;
                    default: s.Z = Clamp(s.Z * factor); break;
                }
                renderer.ModelScale = s;
                break;
            }
        }
    }

    /// <summary>重置为「未变换」。</summary>
    public static void Reset(SkeletalModel skeleton, GlesModelRenderer renderer)
    {
        skeleton.ModelTranslationOffset = Vector3.Zero;
        skeleton.ModelRotationAngles = Vector3.Zero;
        renderer.ModelScale = Vector3.One;
    }

    /// <summary>当前是否为恒等变换。</summary>
    public static bool IsIdentity(SkeletalModel skeleton, GlesModelRenderer renderer)
        => skeleton.ModelTranslationOffset == Vector3.Zero
        && skeleton.ModelRotationAngles == Vector3.Zero
        && renderer.ModelScale == Vector3.One;

    /// <summary>三行读数（窄面板里排得下）。</summary>
    public static string Describe(SkeletalModel skeleton, GlesModelRenderer renderer)
    {
        var t = skeleton.ModelTranslationOffset;
        var r = skeleton.ModelRotationAngles;
        var s = renderer.ModelScale;
        const float Deg = 180f / MathF.PI;
        return $"移动  {t.X,7:F2} {t.Y,7:F2} {t.Z,7:F2}"
             + Environment.NewLine + $"旋转  {r.X * Deg,7:F0} {r.Y * Deg,7:F0} {r.Z * Deg,7:F0} 度"
             + Environment.NewLine + $"缩放  {s.X,7:F2} {s.Y,7:F2} {s.Z,7:F2}";
    }

    private static float Clamp(float value) => System.Math.Clamp(value, MinScale, MaxScale);
}

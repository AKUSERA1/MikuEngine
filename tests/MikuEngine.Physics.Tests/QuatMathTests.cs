using System.Numerics;
using System.Text.Json;
using MikuEngine.Core.Math;

namespace MikuEngine.Physics.Tests;

/// <summary>
/// M-MATH-1：Slerp 行为锁定 + FromEuler 黄金值。
/// 黄金值由 <c>TestData/gen_physics_math_golden.mjs</c> 从 reze 引擎源码
/// （reference/reze-engine/src/math.ts）直接导出，容差 1e-6。
/// </summary>
public class QuatMathTests
{
    private static JsonElement Golden { get; } = LoadGolden();

    private static JsonElement LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "physics_math.golden.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private static float[] F(JsonElement e, string name)
        => e.GetProperty(name).EnumerateArray().Select(v => v.GetSingle()).ToArray();

    public static IEnumerable<object[]> SlerpCases()
    {
        foreach (var c in Golden.GetProperty("slerp").EnumerateArray())
            yield return new object[] { F(c, "a"), F(c, "b"), c.GetProperty("t").GetSingle(), F(c, "out") };
    }

    [Theory]
    [MemberData(nameof(SlerpCases))]
    public void Slerp_MatchesRezeGolden(float[] a, float[] b, float t, float[] expected)
    {
        var qa = new Quaternion(a[0], a[1], a[2], a[3]);
        var qb = new Quaternion(b[0], b[1], b[2], b[3]);
        var result = QuatMath.Slerp(qa, qb, t);
        for (int i = 0; i < 4; i++)
        {
            float actual = i switch { 0 => result.X, 1 => result.Y, 2 => result.Z, _ => result.W };
            Assert.True(MathF.Abs(actual - expected[i]) <= 1e-6f,
                $"slerp component {i}: actual={actual} expected={expected[i]}");
        }
    }

    /// <summary>
    /// BCL <see cref="Quaternion.Slerp"/> 行为锁定证据：近平行分支（reze 用归一化 lerp，
    /// BCL 用阈值 1-1e-6 且不归一化）与 reze 偏差超黄金值容差 1e-6——这正是方案 §2.3
    /// 换自写实现的依据。若未来 BCL 行为变化导致本断言失败，可重新评估改回委托 BCL。
    /// </summary>
    [Fact]
    public void Slerp_BclDiffersFromRezeNearParallel()
    {
        float maxDev = 0;
        foreach (var c in Golden.GetProperty("slerp").EnumerateArray())
        {
            var a = F(c, "a");
            var b = F(c, "b");
            float t = c.GetProperty("t").GetSingle();
            var expected = F(c, "out");
            var qa = new Quaternion(a[0], a[1], a[2], a[3]);
            var qb = new Quaternion(b[0], b[1], b[2], b[3]);

            // 只考察近平行用例（|dot| > 0.9995）
            float dot = qa.X * qb.X + qa.Y * qb.Y + qa.Z * qb.Z + qa.W * qb.W;
            if (MathF.Abs(dot) <= 0.9995f) continue;

            var r = Quaternion.Slerp(qa, qb, t);
            float dev = MathF.Max(
                MathF.Max(MathF.Abs(r.X - expected[0]), MathF.Abs(r.Y - expected[1])),
                MathF.Max(MathF.Abs(r.Z - expected[2]), MathF.Abs(r.W - expected[3])));
            if (dev > maxDev) maxDev = dev;
        }
        Assert.True(maxDev > 1e-6f,
            $"BCL Slerp 与 reze 的最大偏差仅 {maxDev}，可考虑改回委托 BCL 实现");
    }

    [Fact]
    public void FromEuler_MatchesRezeGolden()
    {
        foreach (var c in Golden.GetProperty("fromEuler").EnumerateArray())
        {
            var e = F(c, "e");
            var expected = F(c, "out");
            var r = QuatMath.FromEuler(e[0], e[1], e[2]);
            Assert.Equal(expected[0], r.X, 6e-6f);
            Assert.Equal(expected[1], r.Y, 6e-6f);
            Assert.Equal(expected[2], r.Z, 6e-6f);
            Assert.Equal(expected[3], r.W, 6e-6f);
        }
    }

    [Fact]
    public void Nlerp_TakesShortestArc()
    {
        // b 在相反半球：nlerp 必须翻折 b 后插值（reze nlerpInto 语义）
        var a = new Quaternion(0, 0, 0, 1);
        var b = new Quaternion(0, 0.9f, 0, -0.9f);
        var r = QuatMath.Nlerp(a, b, 0.5f);
        // 翻折后 b = (0, -0.9, 0, 0.9)，t=0.5 → (0, -0.45, 0, 0.95) 归一化
        float len = MathF.Sqrt(0.45f * 0.45f + 0.95f * 0.95f);
        Assert.Equal(-0.45f / len, r.Y, 1e-5f);
        Assert.Equal(0.95f / len, r.W, 1e-5f);
        // 结果必为单位长度
        Assert.Equal(1f, r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W, 1e-5f);
    }

    [Fact]
    public void RotateVec_RotatesAxisByQuarterTurn()
    {
        var q = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0); // 绕 Y 转 90°
        var v = QuatMath.RotateVec(q, new Vector3(1, 0, 0));
        Assert.Equal(0f, v.X, 1e-5f);
        Assert.Equal(0f, v.Y, 1e-5f);
        // 右手系：+X 绕 +Y 90° → -Z（MMD 列向量约定）
        Assert.Equal(-1f, v.Z, 1e-4f);
        // 逆旋转还原
        var back = QuatMath.RotateVecInv(q, v);
        Assert.Equal(1f, back.X, 1e-4f);
    }

    [Fact]
    public void FromBasis_FromUnitVectors_TwistAroundAxis_Roundtrip()
    {
        // FromUnitVectors：+X → +Y 的最短弧（绕 Z -90°，列向量约定）
        var q = QuatMath.FromUnitVectors(new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        var y = QuatMath.RotateVec(q, new Vector3(1, 0, 0));
        Assert.Equal(1f, y.Y, 1e-5f);
        Assert.Equal(0f, y.X, 1e-5f);

        // FromBasis：用 q 的旋转矩阵列重建，应还原 q（正交基）
        float[] m = new float[16];
        Mat4.FromQuatInto(q.X, q.Y, q.Z, q.W, m, 0);
        // 列主序：第 0 列 = m[0..2]，第 1 列 = m[4..6]，第 2 列 = m[8..10]
        var x = new Vector3(m[0], m[1], m[2]);
        var cy = new Vector3(m[4], m[5], m[6]);
        var cz = new Vector3(m[8], m[9], m[10]);
        var rebuilt = QuatMath.FromBasis(x, cy, cz);
        // 双覆盖容差：|dot| ≈ 1
        float dot = MathF.Abs(Quaternion.Dot(q, rebuilt));
        Assert.True(dot > 1f - 1e-5f, $"dot={dot}");

        // TwistAroundAxis：绕 +Y 的旋转对 +Y 轴分解，twist 应还原自身
        var spin = Quaternion.CreateFromYawPitchRoll(1.2f, 0, 0);
        var twist = QuatMath.TwistAroundAxis(spin, new Vector3(0, 1, 0));
        Assert.True(MathF.Abs(Quaternion.Dot(spin, twist)) > 1f - 1e-5f, $"dot={Quaternion.Dot(spin, twist)}");
        // 对垂直轴分解，twist 应为恒等（q 只绕与 a 垂直的轴时 px=py=pz=0）
        var swing = Quaternion.CreateFromAxisAngle(new Vector3(1, 0, 0), 0.7f);
        var t2 = QuatMath.TwistAroundAxis(swing, new Vector3(0, 1, 0));
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Identity, t2)) > 1f - 1e-6f);
    }
}

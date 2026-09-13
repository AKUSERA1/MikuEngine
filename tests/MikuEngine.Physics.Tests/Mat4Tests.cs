using System.Text.Json;
using MikuEngine.Core.Math;

namespace MikuEngine.Physics.Tests;

/// <summary>
/// M-MATH-2：自写列主序 Mat4 黄金值（fromQuat / multiply / fromPositionRotation /
/// localTransform / inverse），黄金值由 Node 直跑 reze src 导出，逐元素容差 1e-5。
/// </summary>
public class Mat4Tests
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

    private static void AssertArrayClose(float[] expected, float[] actual, float tol, string ctx)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(MathF.Abs(expected[i] - actual[i]) <= tol,
                $"{ctx}[{i}]: actual={actual[i]} expected={expected[i]}");
    }

    [Fact]
    public void FromQuat_MatchesRezeGolden()
    {
        var output = new float[16];
        foreach (var c in Golden.GetProperty("mat4FromQuat").EnumerateArray())
        {
            var q = F(c, "q");
            Mat4.FromQuatInto(q[0], q[1], q[2], q[3], output, 0);
            AssertArrayClose(F(c, "m"), output, 1e-5f, "fromQuat");
        }
    }

    [Fact]
    public void MultiplyArrays_MatchesRezeGolden()
    {
        var output = new float[16];
        foreach (var c in Golden.GetProperty("mat4Multiply").EnumerateArray())
        {
            float[] a = F(c, "a");
            float[] b = F(c, "b");
            Mat4.MultiplyArrays(a, 0, b, 0, output, 0);
            AssertArrayClose(F(c, "out"), output, 1e-5f, "multiply");
        }
    }

    [Fact]
    public void FromPositionRotation_MatchesRezeGolden()
    {
        var output = new float[16];
        foreach (var c in Golden.GetProperty("mat4FromPositionRotation").EnumerateArray())
        {
            var p = F(c, "p");
            var q = F(c, "q");
            Mat4.FromPositionRotationInto(p[0], p[1], p[2], q[0], q[1], q[2], q[3], output, 0);
            AssertArrayClose(F(c, "m"), output, 1e-5f, "fromPositionRotation");
        }
    }

    [Fact]
    public void LocalTransform_MatchesRezeGolden()
    {
        var output = new float[16];
        foreach (var c in Golden.GetProperty("mat4LocalTransform").EnumerateArray())
        {
            var b = F(c, "b");
            var q = F(c, "q");
            var l = F(c, "l");
            Mat4.LocalTransformInto(b[0], b[1], b[2], q[0], q[1], q[2], q[3], l[0], l[1], l[2], output, 0);
            AssertArrayClose(F(c, "m"), output, 1e-5f, "localTransform");
        }
    }

    [Fact]
    public void Inverse_MatchesRezeGolden()
    {
        var output = new float[16];
        foreach (var c in Golden.GetProperty("mat4Inverse").EnumerateArray())
        {
            float[] m = F(c, "m");
            bool ok = Mat4.InverseInto(m, output);
            Assert.Equal(c.GetProperty("ok").GetBoolean(), ok);
            AssertArrayClose(F(c, "inv"), output, 1e-5f, "inverse");
        }
    }

    /// <summary>约定自检：平移在第 12..14 槽（列主序），fromPositionRotation 与 multiply 满足 T·R 组合律。</summary>
    [Fact]
    public void ColumnMajor_ConventionSanity()
    {
        var output = new float[16];
        Mat4.FromPositionRotationInto(7, 8, 9, 0, 0, 0, 1, output, 0);
        Assert.Equal(7f, output[12]);
        Assert.Equal(8f, output[13]);
        Assert.Equal(9f, output[14]);

        // T(a)·T(b) = T(a+b)（列主序 M·v 约定的直接推论）
        float[] t1 = new float[16], t2 = new float[16], t3 = new float[16];
        Mat4.FromPositionRotationInto(1, 2, 3, 0, 0, 0, 1, t1, 0);
        Mat4.FromPositionRotationInto(10, 20, 30, 0, 0, 0, 1, t2, 0);
        Mat4.MultiplyArrays(t1, 0, t2, 0, t3, 0);
        Assert.Equal(11f, t3[12]);
        Assert.Equal(22f, t3[13]);
        Assert.Equal(33f, t3[14]);
    }
}

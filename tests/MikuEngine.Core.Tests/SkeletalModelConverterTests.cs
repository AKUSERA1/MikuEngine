using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Tests;

/// <summary>
/// SkeletalModelConverter 管线冒烟测试，用 Demo 自带的真实模型
/// samples/MikuEngine.Demo/Model/Model.pmx（Elysia：45208 顶点 / 1099 骨 / 42 材质）。
///
/// 不依赖 GL，纯数据校验 —— 目的是把"PMX → 运行时模型"这条链路锁死。
/// </summary>
public class SkeletalModelConverterTests
{
    private const string Relative = "samples/MikuEngine.Demo/Model/Model.pmx";

    private static string FindModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, Relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new IOException($"未找到 {Relative}，请确认 Demo 资源存在。");
    }

    private static SkeletalModel Load() =>
        SkeletalModelConverter.Convert(PmxParser.Parse(File.ReadAllBytes(FindModel())));

    // ------------------------------------------------------------------ 规模

    [Fact]
    public void Model_HasExpectedScale()
    {
        var m = Load();

        Assert.Equal(45208, m.VertexCount);
        Assert.Equal(155703, m.IndexData.Length);
        Assert.Equal(1099, m.BoneCount);
        Assert.Equal(42, m.Segments.Length);
    }

    [Fact]
    public void VertexData_HasStrideAlignedLength()
    {
        var m = Load();
        Assert.Equal(m.VertexCount * SkeletalModel.VertexStride, m.VertexData.Length);
        Assert.Equal(52, SkeletalModel.VertexStride);   // 1099 骨 > 255，必须 ushort，不能用 48
    }

    // ------------------------------------------------------------------ 完整性

    [Fact]
    public void Segments_CoverAllIndicesWithoutOverlap()
    {
        var m = Load();
        long sum = 0;
        foreach (var s in m.Segments)
        {
            Assert.True(s.IndexStart >= 0);
            Assert.True(s.IndexCount >= 0);
            Assert.Equal(sum, s.IndexStart);   // 紧密相邻，无空洞无重叠
            sum += s.IndexCount;
        }
        Assert.Equal(m.IndexData.Length, sum);
    }

    [Fact]
    public void AllIndices_AreWithinVertexRange()
    {
        var m = Load();
        foreach (uint i in m.IndexData)
            Assert.True(i < (uint)m.VertexCount, $"索引 {i} 越界（顶点数 {m.VertexCount}）");
    }

    [Fact]
    public void BoneIndices_AreWithinRange()
    {
        var m = Load();
        var span = m.VertexData.AsSpan();
        for (int v = 0; v < m.VertexCount; v++)
        {
            int o = v * SkeletalModel.VertexStride + 40;
            for (int k = 0; k < 4; k++)
            {
                int joint = span[o + k * 2] | (span[o + k * 2 + 1] << 8);
                Assert.True(joint < m.BoneCount, $"顶点 {v} 骨骼索引 {joint} 越界");
            }
        }
    }

    // ------------------------------------------------------------------ 权重

    [Fact]
    public void Weights_SumTo255PerVertex()
    {
        var m = Load();
        var span = m.VertexData.AsSpan();
        for (int v = 0; v < m.VertexCount; v++)
        {
            int o = v * SkeletalModel.VertexStride + 48;
            int sum = span[o] + span[o + 1] + span[o + 2] + span[o + 3];
            Assert.InRange(sum, 254, 255);
        }
    }

    // ------------------------------------------------------------------ 骨骼顺序

    [Fact]
    public void DeformOrder_GuaranteesParentBeforeChild()
    {
        var m = Load();
        var rank = new int[m.BoneCount];
        for (int k = 0; k < m.DeformOrder.Length; k++)
            rank[m.DeformOrder[k]] = k;

        for (int i = 0; i < m.BoneCount; i++)
        {
            int p = m.ParentIndices[i];
            if (p < 0) continue;
            Assert.True(rank[p] < rank[i], $"骨骼 {i}(\"{m.BoneNames[i]}\") 先于其父 {p} 被遍历");
        }
    }

    // ------------------------------------------------------------------ 绑定姿势

    [Fact]
    public void BindPose_SkinMatricesAreIdentity()
    {
        var m = Load();

        for (int i = 0; i < m.BoneCount; i++)
        {
            // World = bindWorld（不是单位阵！），Skin = bindWorld * inverse(bindWorld) = I
            Assert.True(AlmostEqual(m.SkinMatrices[i], Matrix4x4.Identity, 1e-3f),
                $"骨骼 {i}(\"{m.BoneNames[i]}\") 蒙皮矩阵不是单位阵（蒙皮结果应等于绑定姿势）");

            // World 的平移量应等于该骨在绑定姿势下的世界坐标，且与本地位置一致
            Assert.False(float.IsNaN(m.WorldMatrices[i].M41), $"骨骼 {i} 世界矩阵含 NaN");
        }
    }

    [Fact]
    public void PoseBone_ChangesSkinMatricesAndResets()
    {
        var m = Load();
        int bone = m.FindBone("上半身");
        Assert.True(bone >= 0, "模型应存在「上半身」骨骼");

        m.SetBoneLocalRotation(bone, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6f));
        m.UpdateWorldMatrices();

        int moved = 0;
        for (int i = 0; i < m.BoneCount; i++)
            if (!AlmostEqual(m.SkinMatrices[i], Matrix4x4.Identity, 1e-3f))
                moved++;

        Assert.True(moved > 0, "旋转后应有蒙皮矩阵发生变化");
        Assert.Equal(bone, m.FindBone("上半身"));

        m.ResetPose();
        for (int i = 0; i < m.BoneCount; i++)
            Assert.True(AlmostEqual(m.SkinMatrices[i], Matrix4x4.Identity, 1e-3f));
    }

    // ------------------------------------------------------------------ 材质分类

    [Fact]
    public void MaterialClassification_MatchesDiffuseAlpha()
    {
        var m = Load();

        int opaque = 0, cutout = 0, blended = 0;
        foreach (var seg in m.Segments)
        {
            var mat = m.Materials[seg.MaterialIndex];
            if (mat.Diffuse.W < 0.99f) blended++;
            else if (mat.TextureIndex >= 0) cutout++;
            else opaque++;

            Assert.Equal(seg.Type switch
            {
                MaterialRenderType.Opaque => 0,
                MaterialRenderType.Cutout => 1,
                _ => 2,
            }, (int)seg.Type);
        }

        // Elysia 实测：40 个不透明（含纹理 → Cutout）、2 个半透明（目影 0.3 / 袖透 0.8）
        Assert.Equal(40, cutout);
        Assert.Equal(2, blended);
        Assert.Equal(0, opaque);
    }

    [Fact]
    public void SegmentCenters_AreInsideBounds()
    {
        var m = Load();
        foreach (var seg in m.Segments)
        {
            Assert.True(seg.Center.X >= m.BoundsMin.X - 1f && seg.Center.X <= m.BoundsMax.X + 1f);
            Assert.True(seg.Center.Y >= m.BoundsMin.Y - 1f && seg.Center.Y <= m.BoundsMax.Y + 1f);
            Assert.True(seg.Center.Z >= m.BoundsMin.Z - 1f && seg.Center.Z <= m.BoundsMax.Z + 1f);
        }
    }

    private static bool AlmostEqual(in Matrix4x4 a, in Matrix4x4 b, float tol)
    {
        return AlmostEqual(a.M11, b.M11, tol) && AlmostEqual(a.M12, b.M12, tol) &&
               AlmostEqual(a.M13, b.M13, tol) && AlmostEqual(a.M14, b.M14, tol) &&
               AlmostEqual(a.M21, b.M21, tol) && AlmostEqual(a.M22, b.M22, tol) &&
               AlmostEqual(a.M23, b.M23, tol) && AlmostEqual(a.M24, b.M24, tol) &&
               AlmostEqual(a.M31, b.M31, tol) && AlmostEqual(a.M32, b.M32, tol) &&
               AlmostEqual(a.M33, b.M33, tol) && AlmostEqual(a.M34, b.M34, tol) &&
               AlmostEqual(a.M41, b.M41, tol) && AlmostEqual(a.M42, b.M42, tol) &&
               AlmostEqual(a.M43, b.M43, tol) && AlmostEqual(a.M44, b.M44, tol);
    }

    private static bool AlmostEqual(float a, float b, float tol) => MathF.Abs(a - b) <= tol;
}

// ------------------------------------------------------------------ 轮廓线

public class EdgeSegmentTests
{
    private static SkeletalModel Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var c = Path.Combine(dir.FullName, "samples/MikuEngine.Demo/Model/Model.pmx".Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(c)) return SkeletalModelConverter.Convert(PmxParser.Parse(File.ReadAllBytes(c)));
            dir = dir.Parent;
        }
        throw new IOException("未找到 Model.pmx");
    }

    [Fact]
    public void EdgeSegments_RequireFlagAndPositiveSize()
    {
        var m = Load();
        int withEdge = 0;
        foreach (var seg in m.Segments)
        {
            var mat = m.Materials[seg.MaterialIndex];
            bool expect = (mat.Flag & PmxMaterialFlag.EnabledToonEdge) != 0 && mat.EdgeSize > 0f;
            Assert.Equal(expect, seg.EnableEdge);
            if (seg.EnableEdge) withEdge++;
        }
        // Elysia 实测：42 个材质里 23 个带轮廓线
        Assert.Equal(23, withEdge);
    }

    [Fact]
    public void EdgeColors_ArePerMaterial_NotAllBlack()
    {
        var m = Load();
        // 脸(3) 是暗红棕 (0.37,0.05,0.05,0.60)，兔子(37) 是黄色 (1,1,0.5,0.8)
        var face = m.Materials[3];
        Assert.True(face.EdgeColor.X > 0.3f && face.EdgeColor.Y < 0.1f, "脸的轮廓色应为暗红棕");
        Assert.True(face.EdgeColor.W < 0.7f, "脸的轮廓 alpha 应为 0.60（半透明）");
        var rabbit = m.Materials[37];
        Assert.True(rabbit.EdgeColor.X > 0.9f && rabbit.EdgeColor.Y > 0.9f, "兔子的轮廓色应为亮黄");
        Assert.Equal(1.2f, rabbit.EdgeSize, 2);
    }
}

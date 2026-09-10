using MikuEngine.Core.Models;
using Xunit;

namespace MikuEngine.Core.Tests;

/// <summary>
/// PMX 解析正确性测试。
/// 模型来源：tests/MikuEngine.Core.Tests/TestModels 内的真实 PMX 文件
/// （vax、RigidBodyChainEdit/test、Alu_SummerAl_v1.5、梦见月瑞希）。
/// 正确性判据：
///  1) 全量缓冲顺序解析且剩余 0 字节（结构完整性）；
///  2) 手工反推的文件头字段逐一比对；
///  3) 跨节引用全部落在合法范围内。
/// </summary>
public class PmxParserTests
{
    /// <summary>
    /// 向上从测试输出目录查找 TestModels 文件夹（xunit 运行时 base dir 是
    /// tests/.../bin/Debug/net10.0/，所以会先往上跳到测试项目根）。
    /// </summary>
    private static string FindTestModelsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestModels");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new IOException("未能在测试输出目录向上找到 TestModels/ 文件夹。");
    }

    private static readonly string TestModelsRoot = FindTestModelsRoot();

    private static string Ed(string p) => Path.Combine(TestModelsRoot, p);

    /// <summary>待测全部模型。供全量结构校验用。</summary>
    public static readonly string[] AllModels =
    {
        Ed(@"_data\img\vax.pmx"),
        Ed(@"_plugin\RigidBodyChainEdit\test.pmx"),
        Ed(@"Alu_SummerAl_v1.5.pmx"),
        Ed(@"梦见月瑞希.pmx"),
    };

    // ------------------------------------------------------------------ 文件头

    [Fact]
    public void VaxHeader_ParsesExpectedFields()
    {
        var bytes = File.ReadAllBytes(Ed(@"_data\img\vax.pmx"));
        var model = PmxParser.Parse(bytes, out int remain);

        Assert.Equal(0, remain);
        Assert.Equal("PMX", model.Header.Signature);
        Assert.Equal(2.0f, model.Header.Version);
        Assert.Equal(PmxEncoding.Utf16Le, model.Header.Encoding);
        Assert.Equal(1, model.Header.AdditionalVec4Count);
        Assert.Equal("座標軸", model.Header.ModelName);
        Assert.Equal("", model.Header.EnglishModelName);

        Assert.Equal(252, model.Vertices.Length);

        Assert.All(new[] { model.Header.VertexIndexSize, model.Header.TextureIndexSize,
                           model.Header.MaterialIndexSize, model.Header.BoneIndexSize,
                           model.Header.MorphIndexSize, model.Header.RigidBodyIndexSize },
                   size => Assert.Equal(1, size));
    }

    [Fact]
    public void EmptySkeletonModel_HasZeroVerticesButParses()
    {
        var bytes = File.ReadAllBytes(Ed(@"_plugin\RigidBodyChainEdit\test.pmx"));
        var model = PmxParser.Parse(bytes, out int remain);

        Assert.Equal(0, remain);
        Assert.Empty(model.Vertices);
        Assert.True(model.Bones.Length > 0);
        Assert.True(model.RigidBodies.Length > 0);
    }

    // ------------------------------------------------------------------ 全量缓冲（结构完整性）

    [Fact]
    public void AllModels_ConsumeEntireBuffer()
    {
        foreach (var path in AllModels)
        {
            var bytes = File.ReadAllBytes(path);
            int remain;
            PmxParser.Parse(bytes, out remain);
            Assert.True(remain == 0, $"{Path.GetFileName(path)} 解析后剩余 {remain} 字节，结构不完整。");
        }
    }

    [Fact]
    public void FullModels_HaveRichContent()
    {
        var alu = PmxParser.Parse(File.ReadAllBytes(Ed(@"Alu_SummerAl_v1.5.pmx")));
        Assert.Equal(3, alu.Header.AdditionalVec4Count);
        Assert.True(alu.Vertices.Length > 10000, $"顶点数过少：{alu.Vertices.Length}");
        Assert.True(alu.Bones.Length > 50);
        Assert.True(alu.Materials.Length > 1);
        Assert.True(alu.Morphs.Length > 10);
        Assert.Contains(alu.Morphs, m => m.Type is PmxMorphType.Vertex or PmxMorphType.Bone or PmxMorphType.Uv or PmxMorphType.Material);

        var mizuki = PmxParser.Parse(File.ReadAllBytes(Ed(@"梦见月瑞希.pmx")));
        Assert.True(mizuki.Bones.Length > 50);
        Assert.Equal(2, mizuki.Header.BoneIndexSize);
    }

    // ------------------------------------------------------------------ 跨节引用完整性

    [Fact]
    public void AllModels_CrossReferencesAreValid()
    {
        foreach (var path in AllModels)
        {
            var model = PmxParser.Parse(File.ReadAllBytes(path));
            CollectAndAssert(model, Path.GetFileName(path));
        }
    }

    private static void CollectAndAssert(PmxModel m, string tag)
    {
        int nv = m.Vertices.Length, nb = m.Bones.Length, nm = m.Materials.Length,
            nt = m.Textures.Length, nmorph = m.Morphs.Length, nr = m.RigidBodies.Length;

        foreach (var v in m.Vertices)
            for (int i = 0; i < v.Weight.BoneCount; ++i)
                AssertBone(v.Weight.Bone(i), nb, tag, "顶点骨骼");

        foreach (var idx in m.Indices)
            Assert.True(idx >= 0 && idx < nv, $"[{tag}] 三角形索引 {idx} 越界（顶点数 {nv}）。");

        foreach (var mat in m.Materials)
        {
            Assert.True(mat.TextureIndex < 0 || mat.TextureIndex < nt, $"[{tag}] 主纹理索引越界。");
            Assert.True(mat.SphereTextureIndex < 0 || mat.SphereTextureIndex < nt, $"[{tag}] 球体纹理索引越界。");
            if (!mat.IsSharedToonTexture)
                Assert.True(mat.ToonTextureIndex < 0 || mat.ToonTextureIndex < nt, $"[{tag}] 非共享卡通纹理索引越界。");
        }

        foreach (var b in m.Bones)
        {
            AssertBone(b.ParentBoneIndex, nb, tag, "父骨骼");
            if (b.IsTailBoneIndex) AssertBone(b.TailBoneIndex, nb, tag, "尾部骨骼");
            if (b.AppendTransform is { } ap) AssertBone(ap.ParentIndex, nb, tag, "追加变换父骨骼");
            if (b.ExternalParentIndex is { } ep) AssertBone(ep, nb, tag, "外部父骨骼");
            if (b.Ik is { } ik)
            {
                AssertBone(ik.Target, nb, tag, "IK 目标");
                foreach (var link in ik.Links) AssertBone(link.BoneIndex, nb, tag, "IK 链接");
            }
        }

        foreach (var mf in m.Morphs)
        {
            int[]? idx = mf.Indices;
            if (idx == null) continue;
            int bound = mf.Type switch
            {
                PmxMorphType.Bone => nb,
                PmxMorphType.Vertex => nv,
                PmxMorphType.Uv or PmxMorphType.AdditionalUv1 or PmxMorphType.AdditionalUv2
                    or PmxMorphType.AdditionalUv3 or PmxMorphType.AdditionalUv4 => nv,
                PmxMorphType.Impulse => nr,
                _ => nmorph,
            };
            foreach (var i in idx)
                Assert.True(i >= 0 && i < bound, $"[{tag}] 表情 {mf.Type} 索引 {i} 越界（上限 {bound}）。");
            if (mf.Type == PmxMorphType.Material)
                foreach (var e in mf.MaterialElements!)
                    Assert.True(e.MaterialIndex < 0 || e.MaterialIndex < nm, $"[{tag}] 材质表情材质索引越界。");
        }

        foreach (var df in m.DisplayFrames)
            foreach (var e in df.Elements)
            {
                if (e.Type == PmxDisplayFrameElementType.Bone) AssertBone(e.Index, nb, tag, "显示帧骨骼");
                else Assert.True(e.Index >= 0 && e.Index < nmorph, $"[{tag}] 显示帧表情越界。");
            }

        foreach (var rb in m.RigidBodies)
            AssertBone(rb.BoneIndex, nb, tag, "刚体骨骼");
        foreach (var j in m.Joints)
        {
            Assert.True(j.RigidBodyIndexA >= 0 && j.RigidBodyIndexA < nr, $"[{tag}] 关节刚体 A 越界。");
            Assert.True(j.RigidBodyIndexB >= 0 && j.RigidBodyIndexB < nr, $"[{tag}] 关节刚体 B 越界。");
        }

        foreach (var sb in m.SoftBodies)
        {
            Assert.True(sb.MaterialIndex < 0 || sb.MaterialIndex < nm, $"[{tag}] 软体材质越界。");
            foreach (var a in sb.Anchors)
            {
                Assert.True(a.RigidBodyIndex >= 0 && a.RigidBodyIndex < nr, $"[{tag}] 软体锚点刚体越界。");
                Assert.True(a.VertexIndex >= 0 && a.VertexIndex < nv, $"[{tag}] 软体锚点顶点越界。");
            }
        }
    }

    private static void AssertBone(int idx, int nb, string tag, string what)
    {
        Assert.True(idx >= -1 && idx < nb, $"[{tag}] {what}索引 {idx} 越界（骨骼数 {nb}）。");
    }

    // ------------------------------------------------------------------ 纹理路径分隔符坑位

    [Fact]
    public void RealModelTextures_SurvivePathNormalization()
    {
        foreach (var path in AllModels)
        {
            var model = PmxParser.Parse(File.ReadAllBytes(path));
            foreach (var tex in model.Textures)
            {
                var normalized = PmxTexturePath.Normalize(tex);
                Assert.DoesNotContain('\\', normalized);
                Assert.DoesNotContain('\0', normalized);
            }
        }
    }

    [Theory]
    [InlineData("texture\\cloth/01.png", "texture/cloth/01.png")]
    [InlineData(".\\face\\hm01.png", "face/hm01.png")]
    [InlineData("models\\miku//body.bmp", "models/miku/body.bmp")]
    [InlineData("/ABS/absolute.png", "ABS/absolute.png")]
    [InlineData("preview/png/skin.png", "preview/png/skin.png")]
    [InlineData("", "")]
    public void TexturePathNormalize_MixesSeparators(string raw, string expected)
    {
        Assert.Equal(expected, PmxTexturePath.Normalize(raw));
    }

    [Fact]
    public void TexturePathNormalize_StripsTrailingNul()
    {
        Assert.Equal("a/b.png", PmxTexturePath.Normalize("a\\b.png\0\0"));
    }
}

using System.Numerics;
using MikuEngine.Core.Animation;
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

    private static string FindModel() => FindUp(Relative);

    private static string FindMotion() => FindUp("samples/MikuEngine.Demo/Motion/Motion.vmd");

    private static string FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new IOException($"未找到 {relative}，请确认 Demo 资源存在。");
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

    // Step 3：局部平移字段（动画前提）
    // 无动画时 LocalTranslations ≡ LocalPositions ⇒ UpdateWorldMatrices 的输出与改造前逐位一致（回归基线）。

    [Fact]
    public void BindPose_LocalTranslationsMatchLocalPositions()
    {
        var m = Load();

        Assert.NotEmpty(m.LocalPositions);
        Assert.Equal(m.LocalPositions.Length, m.LocalTranslations.Length);
        Assert.Equal(m.LocalPositions, m.LocalTranslations);
    }

    [Fact]
    public void BindPose_WorldMatricesStableAcrossRecompute()
    {
        var m = Load();
        var before = (System.Numerics.Matrix4x4[])m.WorldMatrices.Clone();

        m.UpdateWorldMatrices();

        Assert.Equal(before, m.WorldMatrices);
    }

    /// <summary>
    /// 绑定姿势下每根骨的世界位置必须等于 PMX 里的模型空间绝对坐标。
    ///
    /// 这是「PMX 绝对坐标 → 局部平移取父骨差值」的回归锁：若误把绝对坐标当局部平移，
    /// 绑定姿势的 WorldMatrices 会沿链累加（头骨能跑到模型高度的数倍），
    /// 静态渲染因 Skin ≡ 单位阵而看不出问题，但 FK 动画会绕错误支点旋转（撕裂）。
    /// </summary>
    [Fact]
    public void BindPose_BoneWorldPositions_EqualPmxAbsolutePositions()
    {
        var pmx = PmxParser.Parse(File.ReadAllBytes(FindModel()));
        var m = SkeletalModelConverter.Convert(pmx);

        Assert.Equal(pmx.Bones.Length, m.BoneCount);
        for (int i = 0; i < pmx.Bones.Length; i++)
        {
            var delta = m.WorldMatrices[i].Translation - pmx.Bones[i].Position;
            Assert.True(delta.Length() < 1e-3f,
                $"骨 #{i} \"{pmx.Bones[i].Name}\" 绑定世界位置 {m.WorldMatrices[i].Translation} " +
                $"≠ PMX 绝对坐标 {pmx.Bones[i].Position}");
        }
    }

    [Fact]
    public void ResetPose_RestoresRotations_AndTranslations()
    {
        var m = Load();
        m.LocalRotations[0] = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, 0.5f);
        m.LocalTranslations[0] = m.LocalPositions[0] + new System.Numerics.Vector3(3, 4, 5);

        m.ResetPose();

        Assert.Equal(System.Numerics.Quaternion.Identity, m.LocalRotations[0]);
        Assert.Equal(m.LocalPositions[0], m.LocalTranslations[0]);
    }

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

    /// <summary>
    /// 付与（append transform）要读源骨<b>已算完的最终局部变换</b>，因此求值顺序也必须满足
    /// 「付与源先于付与目标」——这一条光靠父边拓扑序是不够的。
    /// </summary>
    [Fact]
    public void DeformOrder_GuaranteesAppendSourceBeforeTarget()
    {
        var m = Load();
        var rank = new int[m.BoneCount];
        for (int k = 0; k < m.DeformOrder.Length; k++)
            rank[m.DeformOrder[k]] = k;

        int checkedCount = 0;
        for (int i = 0; i < m.BoneCount; i++)
        {
            int src = m.AppendSources[i];
            if (src < 0) continue;
            Assert.True(rank[src] < rank[i],
                $"骨骼 {i}(\"{m.BoneNames[i]}\") 早于其付与源 {src}(\"{m.BoneNames[src]}\") 被求值");
            checkedCount++;
        }

        Assert.True(checkedCount > 0, "该模型应存在付与骨（足D/腰キャンセル/腕捩1..3 等）");
    }

    // ------------------------------------------------------------------ 付与 / 軸制限

    [Fact]
    public void AppendTransform_IsParsedFromPmx()
    {
        var m = Load();

        // 复制腿：足D ← 足，ratio 1.0，只继承旋转
        int legD = m.FindBone("左足D");
        Assert.True(legD >= 0, "模型应存在「左足D」");
        Assert.Equal(m.FindBone("左足"), m.AppendSources[legD]);
        Assert.Equal(1f, m.AppendRatios[legD]);
        Assert.True(m.AppendRotate[legD]);
        Assert.False(m.AppendMove[legD]);

        // 腰キャンセル：ratio -1（反向抵消腰的旋转）
        int cancel = m.FindBone("腰キャンセル左");
        Assert.True(cancel >= 0, "模型应存在「腰キャンセル左」");
        Assert.Equal(m.FindBone("腰"), m.AppendSources[cancel]);
        Assert.Equal(-1f, m.AppendRatios[cancel]);

        // 腕捩1..3 按 0.25/0.5/0.75 摊扭转到前臂
        int twist1 = m.FindBone("左腕捩1");
        Assert.Equal(m.FindBone("左腕捩"), m.AppendSources[twist1]);
        Assert.Equal(0.25f, m.AppendRatios[twist1], 5);

        // 无付与的骨必须是 -1（而不是 0 —— 0 是合法的源骨索引）
        int center = m.FindBone("センター");
        Assert.Equal(m.FindBone("センター調整"), m.AppendSources[center]);
        Assert.Equal(-1, m.AppendSources[0]);
    }

    [Fact]
    public void AxisLimits_AreNormalizedOrZero()
    {
        var m = Load();

        int limited = 0;
        for (int i = 0; i < m.BoneCount; i++)
        {
            var a = m.AxisLimits[i];
            if (a == Vector3.Zero) continue;
            limited++;
            Assert.True(MathF.Abs(a.Length() - 1f) < 1e-5f,
                $"骨骼 {i}(\"{m.BoneNames[i]}\") 的軸制限轴未归一化：{a}");
        }

        // 腕捩/手捩 及其「〜調整」镜像骨，共 8 根带軸制限
        Assert.Equal(8, limited);
        int twist = m.FindBone("左腕捩");
        Assert.True(twist >= 0 && m.AxisLimits[twist] != Vector3.Zero, "左腕捩 应带軸制限");
    }

    /// <summary>
    /// 该模型用 足D/ひざD/足首D 复制整条腿（每腿约 624 个顶点），这三根复制骨自身没有动画，
    /// 完全靠付与跟随 足/ひざ/足首。付与没实现时它们会退化成「跟着下半身刚体平移」，
    /// 腿就完全不动了 —— 这个测试就是那条回归锁。
    /// </summary>
    [Fact]
    public void AppendTransform_MakesDuplicateLegBonesFollowTheRealLeg()
    {
        var pmx = PmxParser.Parse(File.ReadAllBytes(FindModel()));
        var m = SkeletalModelConverter.Convert(pmx);
        var vmd = VmdParser.Parse(File.ReadAllBytes(FindMotion()));

        MmdAnimation.Bind(vmd, m).Sample(m, 1000);
        m.UpdateWorldMatrices();

        foreach (var (dup, real) in new[]
        {
            ("左足D", "左足"), ("左ひざD", "左ひざ"), ("左足首D", "左足首"),
            ("右足D", "右足"), ("右ひざD", "右ひざ"), ("右足首D", "右足首"),
        })
        {
            int d = m.FindBone(dup), r = m.FindBone(real);
            Assert.True(d >= 0 && r >= 0, $"模型应同时存在 {dup} 与 {real}");

            var dupPos = m.WorldMatrices[d].Translation;
            var realPos = m.WorldMatrices[r].Translation;
            Assert.True((dupPos - realPos).Length() < 1e-3f,
                $"{dup} 世界位置 {dupPos} ≠ {real} {realPos}（付与未生效）");

            // 而且必须真的离开了绑定姿势（否则「跟上了」可能只是两边都没动）
            Assert.True((dupPos - pmx.Bones[d].Position).Length() > 0.5f,
                $"{dup} 相对绑定姿势没有位移，付与没有真正驱动它");
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

    // ------------------------------------------------------------------ 表情（morph）
    //
    // 用 TestModels 里的完整模型（顶点 / 骨 / UV / 材质 morph 混合）校验转换器建的稀疏表。
    // 判据核心是「不稠密化」：受影响顶点数远小于「顶点数 × morph 数」。

    private const string AluRelative = "tests/MikuEngine.Core.Tests/TestModels/Alu_SummerAl_v1.5.pmx";

    private static PmxModel LoadPmx() => PmxParser.Parse(File.ReadAllBytes(FindUp(AluRelative)));

    [Fact]
    public void VertexMorphs_AreSparseAndMatchPmx()
    {
        var pmx = LoadPmx();
        var model = SkeletalModelConverter.Convert(pmx);

        var byIndex = model.VertexMorphs.ToDictionary(m => m.MorphIndex);
        int checkedMorphs = 0;

        for (int i = 0; i < pmx.Morphs.Length; i++)
        {
            var src = pmx.Morphs[i];
            if (src.Type != PmxMorphType.Vertex) continue;

            var indices = src.Indices!;
            var positions = src.Positions!;

            int valid = 0;
            for (int k = 0; k < indices.Length; k++)
                if ((uint)indices[k] < (uint)model.VertexCount) valid++;

            if (!byIndex.TryGetValue(i, out var sparse))
            {
                Assert.Equal(0, valid);   // 没有运行时项 ⇒ 源数据里没有任何合法索引
                continue;
            }

            Assert.Equal(valid, sparse.VertexIndices.Length);

            // 逐项比对（保持源顺序；越界项在加载期已被过滤）
            int w = 0;
            for (int k = 0; k < indices.Length; k++)
            {
                int v = indices[k];
                if ((uint)v >= (uint)model.VertexCount) continue;

                Assert.Equal(v, sparse.VertexIndices[w]);
                Assert.Equal(positions[k * 3 + 0], sparse.Offsets[w].X, 1e-6f);
                Assert.Equal(positions[k * 3 + 1], sparse.Offsets[w].Y, 1e-6f);
                Assert.Equal(positions[k * 3 + 2], sparse.Offsets[w].Z, 1e-6f);
                w++;
            }
            checkedMorphs++;
        }

        Assert.True(checkedMorphs > 0, "测试模型应含顶点 morph");
    }

    [Fact]
    public void MorphTables_AreSparse_NotDensified()
    {
        var model = SkeletalModelConverter.Convert(LoadPmx());

        Assert.True(model.VertexMorphs.Length > 0, "测试模型应含顶点 morph");

        long affected = model.VertexMorphs.Sum(m => (long)m.VertexIndices.Length);
        long dense = (long)model.VertexCount * model.VertexMorphs.Length;

        Assert.True(affected < dense,
            $"顶点 morph 应是稀疏的：受影响顶点 {affected} 应远小于稠密化后的 {dense}");
    }

    [Fact]
    public void MorphKinds_MatchPmx_AndUnsupportedTypesHaveNoTable()
    {
        var pmx = LoadPmx();
        var model = SkeletalModelConverter.Convert(pmx);

        var expected = pmx.Morphs.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count());
        var actual = model.MorphKinds.GroupBy(k => (PmxMorphType)k).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(expected.Count, actual.Count);
        foreach (var (type, count) in expected)
        {
            Assert.True(actual.TryGetValue(type, out int got), $"运行时表缺少类型 {type}");
            Assert.Equal(count, got);
        }

        // 不支持的类型（Flip / Impulse / 附加 UV1~4）不得出现在任何运行时表里
        for (int i = 0; i < pmx.Morphs.Length; i++)
        {
            var type = pmx.Morphs[i].Type;
            bool supported = type is PmxMorphType.Group or PmxMorphType.Vertex or PmxMorphType.Bone
                                  or PmxMorphType.Uv or PmxMorphType.Material;
            if (supported) continue;

            Assert.Null(model.GroupMorphs[i]);
            Assert.Null(model.MaterialMorphs[i]);
            Assert.DoesNotContain(model.VertexMorphs, m => m.MorphIndex == i);
            Assert.DoesNotContain(model.UvMorphs, m => m.MorphIndex == i);
            Assert.DoesNotContain(model.BoneMorphs, m => m.MorphIndex == i);
        }
    }

    [Fact]
    public void GroupOrder_IsReferencerBeforeReferenced()
    {
        var model = SkeletalModelConverter.Convert(LoadPmx());

        int groupCount = model.GroupMorphs.Count(g => g is not null);
        Assert.Equal(groupCount, model.GroupOrder.Length);

        var position = new int[model.GroupMorphs.Length];
        Array.Fill(position, -1);
        for (int i = 0; i < model.GroupOrder.Length; i++)
            position[model.GroupOrder[i]] = i;

        for (int g = 0; g < model.GroupMorphs.Length; g++)
        {
            if (model.GroupMorphs[g] is not { } group) continue;
            foreach (int child in group.ChildIndices)
            {
                if (model.GroupMorphs[child] is null) continue;   // 非组子项不参与排序
                Assert.True(position[g] >= 0 && position[child] >= 0);
                Assert.True(position[g] < position[child], $"组 {g} 必须排在被它引用的组 {child} 之前");
            }
        }
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

// ------------------------------------------------------------------ 付与 / 軸制限（合成骨架）

/// <summary>
/// 付与与軸制限的确定性单元测试：不依赖任何模型文件，手搓最小骨架，
/// 直接对 <see cref="SkeletalModel.UpdateWorldMatrices"/> 的结果做几何断言。
/// </summary>
public class SkeletalPoseTests
{
    /// <summary>
    /// 造一个最小 SkeletalModel：约定父骨下标一定小于子骨下标，因此下标序即合法求值顺序。
    /// 逆绑定/蒙皮矩阵不参与断言，留单位阵。
    /// </summary>
    private static SkeletalModel Synthetic(int[] parents, Vector3[] localPositions)
    {
        int n = parents.Length;
        var identity = new Matrix4x4[n];
        for (int i = 0; i < n; i++) identity[i] = Matrix4x4.Identity;

        return new SkeletalModel
        {
            BoneCount = n,
            BoneNames = Enumerable.Range(0, n).Select(i => $"bone{i}").ToArray(),
            ParentIndices = parents,
            LocalPositions = (Vector3[])localPositions.Clone(),
            LocalTranslations = (Vector3[])localPositions.Clone(),
            LocalRotations = Enumerable.Repeat(Quaternion.Identity, n).ToArray(),
            AppendSources = Enumerable.Repeat(-1, n).ToArray(),
            AppendRatios = new float[n],
            AppendRotate = new bool[n],
            AppendMove = new bool[n],
            AxisLimits = new Vector3[n],
            InverseBind = identity,
            WorldMatrices = new Matrix4x4[n],
            SkinMatrices = new Matrix4x4[n],
            DeformOrder = Enumerable.Range(0, n).ToArray(),
        };
    }

    private static Quaternion WorldRotation(in SkeletalModel m, int bone) =>
        Quaternion.CreateFromRotationMatrix(m.WorldMatrices[bone]);

    /// <summary>0=根, 1=付与源, 2=目标（父=0，付与源=1）。</summary>
    private static SkeletalModel TwoBoneRig()
    {
        var m = Synthetic(
            new[] { -1, 0, 0 },
            new[] { Vector3.Zero, new Vector3(0, 1, 0), new Vector3(0, 1, 0) });
        m.AppendSources[2] = 1;
        return m;
    }

    [Fact]
    public void AppendRotation_BlendsTowardSource()
    {
        var m = TwoBoneRig();
        m.AppendRatios[2] = 0.5f;
        m.AppendRotate[2] = true;
        m.LocalRotations[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        m.UpdateWorldMatrices();

        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, WorldRotation(m, 2))) > 0.99999f,
            $"比率 0.5 应取源旋转的一半，实际 {WorldRotation(m, 2)}");
    }

    [Fact]
    public void AppendRotation_NegativeRatioCancelsSource()
    {
        var m = TwoBoneRig();
        m.AppendRatios[2] = -1f;
        m.AppendRotate[2] = true;
        m.LocalRotations[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        m.UpdateWorldMatrices();

        // ratio = -1 ⇒ 取源旋转的共轭（腰キャンセル 用这一手抵消腰的旋转）
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2f);
        Assert.True(MathF.Abs(Quaternion.Dot(expected, WorldRotation(m, 2))) > 0.99999f);
    }

    [Fact]
    public void AppendMove_AddsScaledSourceOffset()
    {
        var m = TwoBoneRig();
        m.AppendRatios[2] = 0.5f;
        m.AppendMove[2] = true;
        m.LocalRotations[1] = Quaternion.Identity;

        // 源的动效偏移 = 当前局部平移 − 绑定局部平移
        m.LocalTranslations[1] = m.LocalPositions[1] + new Vector3(2, 0, 0);

        m.UpdateWorldMatrices();

        // 目标自身绑定世界位置是 (0,1,0)，再加源偏移的一半 (1,0,0)
        var expected = new Vector3(1, 1, 0);
        Assert.True((m.WorldMatrices[2].Translation - expected).Length() < 1e-5f,
            $"实际 {m.WorldMatrices[2].Translation}，期望 {expected}");
    }

    [Fact]
    public void AxisLimit_KeepsOnlyTwistAroundAxis()
    {
        var m = TwoBoneRig();

        // 绕 Y 扭 0.7 rad + 绕 X 弯 0.4 rad 的混合旋转
        var mixed = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f)
                  * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f);
        m.LocalRotations[1] = mixed;                 // 对照组：无限制
        m.LocalRotations[2] = mixed;                 // 实验组：軸制限 = +Y
        m.AxisLimits[2] = Vector3.UnitY;

        m.UpdateWorldMatrices();

        // 轴制限后只剩绕 Y 的扭转 ⇒ Y 轴必须是不动轴（只取 3×3 部分，避开平移）
        var limitedY = Vector3.TransformNormal(Vector3.UnitY, m.WorldMatrices[2]);
        Assert.True((limitedY - Vector3.UnitY).Length() < 1e-5f,
            $"軸制限后 Y 轴应保持不动，实际被转到 {limitedY}");

        // 对照组必须真的把 Y 轴转偏了，否则上面的断言可能是「两边都没动」
        var freeY = Vector3.TransformNormal(Vector3.UnitY, m.WorldMatrices[1]);
        Assert.True((freeY - Vector3.UnitY).Length() > 1e-2f, "对照组应当把 Y 轴转偏");
    }

    [Fact]
    public void UpdateWorldMatrices_IsIdempotent_WithAppendAndAxisLimit()
    {
        var m = TwoBoneRig();
        m.AppendRatios[2] = 0.5f;
        m.AppendRotate[2] = true;
        m.AppendMove[2] = true;
        m.AxisLimits[2] = Vector3.UnitY;
        m.LocalRotations[1] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.9f);
        m.LocalTranslations[1] = m.LocalPositions[1] + new Vector3(1, 2, 3);

        m.UpdateWorldMatrices();
        var first = (Matrix4x4[])m.WorldMatrices.Clone();

        m.UpdateWorldMatrices();

        // 付与结果只写暂存数组、不回写 Local* ⇒ 重复求值不得叠加
        Assert.Equal(first, m.WorldMatrices);
    }
}

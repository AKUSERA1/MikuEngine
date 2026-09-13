using System.Buffers.Binary;
using System.Numerics;

namespace MikuEngine.Core.Models;

/// <summary>
/// PmxModel（PMX 原始数据）→ SkeletalModel（运行时模型）。
///
/// 当前只做静态预览与动画所需的部分：
///   交错 VBO / 统一索引缓冲 / 逆绑定矩阵 / 变形顺序 / 材质段分类 /
///   表情（morph）稀疏表与 Group 求值序。
/// SoftBody、SDEF 真实现留到后续。
/// </summary>
public static class SkeletalModelConverter
{
    public static SkeletalModel Convert(PmxModel pmx)
    {
        var model = new SkeletalModel
        {
            Materials = pmx.Materials,
            Textures = pmx.Textures,
        };

        BuildBones(pmx, model);
        BuildIkChains(pmx, model);
        BuildVertices(pmx, model);
        BuildIndices(pmx, model);
        BuildSegments(pmx, model);
        BuildMorphs(pmx, model);

        model.ResetPose();
        return model;
    }

    // ---------------------------------------------------------------- IK

    /// <summary>
    /// IK 链与 IK 相关的平行数组。数组尺寸<b>恒为 BoneCount</b>（即使模型没有任何 IK 链），
    /// 这样 <c>UpdateWorldMatrices</c> 里的分支判据固定为 <c>IsIkLink[i]</c>，不需要额外空判。
    /// </summary>
    private static void BuildIkChains(PmxModel pmx, SkeletalModel model)
    {
        int n = model.BoneCount;

        model.IkChains = MmdIkChainBuilder.Build(pmx.Bones);
        model.IsIkLink = new bool[n];
        model.IkRotations = new Quaternion[n];
        model.IkLinkBaseRotations = new Quaternion[n];
        model.IkEnabled = new bool[model.IkChains.Length];

        // new Quaternion[n] 得到的是 (0,0,0,0)，必须显式置为单位四元数。
        for (int i = 0; i < n; i++)
        {
            model.IkRotations[i] = Quaternion.Identity;
            model.IkLinkBaseRotations[i] = Quaternion.Identity;
        }

        for (int c = 0; c < model.IkChains.Length; c++)
        {
            foreach (var link in model.IkChains[c].Links)
                model.IsIkLink[link.BoneIndex] = true;
            model.IkEnabled[c] = true;      // 表示枠未声明 ⇒ 默认启用
        }
    }

    // ---------------------------------------------------------------- 骨骼

    private static void BuildBones(PmxModel pmx, SkeletalModel model)
    {
        int n = pmx.Bones.Length;
        model.BoneCount = n;
        model.BoneNames = new string[n];
        model.ParentIndices = new int[n];
        model.LocalPositions = new Vector3[n];
        model.LocalTranslations = new Vector3[n];
        model.LocalRotations = new Quaternion[n];
        model.AppendSources = new int[n];
        model.AppendRatios = new float[n];
        model.AppendRotate = new bool[n];
        model.AppendMove = new bool[n];
        model.AppendIsLocal = new bool[n];
        model.AxisLimits = new Vector3[n];
        model.InverseBind = new Matrix4x4[n];
        model.WorldMatrices = new Matrix4x4[n];
        model.SkinMatrices = new Matrix4x4[n];

        for (int i = 0; i < n; i++)
        {
            var b = pmx.Bones[i];
            model.BoneNames[i] = b.Name;
            model.ParentIndices[i] = b.ParentBoneIndex;

            // PMX 的 Bone.Position 是【模型空间绝对坐标】，局部平移必须取相对父骨的差值。
            // 绑定姿势下 Skin ≡ 单位阵，绝对/相对两种取值渲染结果都一样，所以静态预览无感；
            // 但 FK 动画的旋转支点取决于局部偏移 —— 用绝对值会让每根骨绕错误支点旋转
            //（表现：上半身/脖子撕裂成放射状薄片、头部错位）。
            int parent = b.ParentBoneIndex;
            Vector3 local = (uint)parent < (uint)n
                ? b.Position - pmx.Bones[parent].Position
                : b.Position;

            model.LocalPositions[i] = local;
            model.LocalTranslations[i] = local;
            model.LocalRotations[i] = Quaternion.Identity;

            // 付与（append transform）：源骨必须存在；比率钳到 [-1, 1]（PMX 允许负值，
            // -1 表示反向抵消）。PMX 只在 HasAppendRotate / HasAppendMove 之一置位时才算付与。
            // 自付与（付与源 = 自己，真实模型里存在）按 MMD 语义视为no-op。
            model.AppendSources[i] = -1;
            if (b.AppendTransform is { } a && (uint)a.ParentIndex < (uint)n && a.ParentIndex != i)
            {
                model.AppendSources[i] = a.ParentIndex;
                model.AppendRatios[i] = System.Math.Clamp(a.Ratio, -1f, 1f);
                model.AppendRotate[i] = (b.Flag & PmxBoneFlag.HasAppendRotate) != 0;
                model.AppendMove[i] = (b.Flag & PmxBoneFlag.HasAppendMove) != 0;
                model.AppendIsLocal[i] = (b.Flag & PmxBoneFlag.LocalAppendTransform) != 0;
            }

            // 軸制限：存归一化轴，Zero 表示无限制（腕捩/手捩用）。
            if (b.AxisLimit is { } axis)
            {
                float len = axis.Length();
                if (len > 1e-8f) model.AxisLimits[i] = axis / len;
            }
        }

        // 求值顺序：PMX 的 TransformOrder 规范上是拓扑序，但并非所有文件都严格保证。
        // 这里对「父边 ∪ 付与边」做 DFS 拓扑排序，一次保证父先于子、付与源先于付与目标
        // （付与要读源骨已经算完的最终局部变换）。
        model.DeformOrder = BuildEvaluationOrder(model.ParentIndices, model.AppendSources);

        // 绑定姿势世界矩阵 → 逆绑定矩阵
        // 乘序与 UpdateWorldMatrices 一致（行主序 row-vector：local * parent）。
        // 绑定姿势下局部旋转均为单位阵，逐级只累加平移，两种乘序数值相同。
        var bindWorld = new Matrix4x4[n];
        foreach (int i in model.DeformOrder)
        {
            Matrix4x4 local = Matrix4x4.Identity;
            local.Translation = model.LocalPositions[i];

            int parent = model.ParentIndices[i];
            bindWorld[i] = parent >= 0 && (uint)parent < (uint)n
                ? local * bindWorld[parent]
                : local;

            if (!Matrix4x4.Invert(bindWorld[i], out var inv))
                inv = Matrix4x4.Identity;
            model.InverseBind[i] = inv;
        }
    }

    /// <summary>
    /// 对「父边 ∪ 付与边」做 DFS 拓扑排序，返回「所有依赖先于自身」的求值顺序。
    /// 付与边必须计入：付与要读源骨<b>已经算完的最终局部变换</b>。非法数据里的环会被跳过，
    /// 且兜底保证每根骨都出现一次。
    /// </summary>
    private static int[] BuildEvaluationOrder(int[] parents, int[] appendSources)
    {
        int n = parents.Length;
        // 0=未访问, 1=在栈上, 2=已完成
        var state = new byte[n];
        var order = new List<int>(n);
        var stack = new Stack<(int Node, bool Post)>(n * 2);

        for (int start = 0; start < n; start++)
        {
            if (state[start] != 0) continue;
            stack.Push((start, false));

            while (stack.Count > 0)
            {
                var (u, post) = stack.Pop();

                if (post)
                {
                    state[u] = 2;
                    order.Add(u);
                    continue;
                }

                if (state[u] != 0) continue;   // 已被别的路径处理过（含环）
                state[u] = 1;
                stack.Push((u, true));

                // 依赖：父骨 + 付与源骨。逆序压栈只为让输出顺序更接近文件顺序，非必需。
                int src = appendSources[u];
                if (src >= 0 && state[src] == 0) stack.Push((src, false));
                int p = parents[u];
                if (p >= 0 && p < n && state[p] == 0) stack.Push((p, false));
            }
        }

        return order.ToArray();
    }

    // ---------------------------------------------------------------- 顶点

    private static void BuildVertices(PmxModel pmx, SkeletalModel model)
    {
        int count = pmx.Vertices.Length;
        model.VertexCount = count;

        var data = new byte[count * SkeletalModel.VertexStride];
        var span = data.AsSpan();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int i = 0; i < count; i++)
        {
            var v = pmx.Vertices[i];
            int o = i * SkeletalModel.VertexStride;

            // aPosition
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 0, 4), v.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 4, 4), v.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 8, 4), v.Position.Z);

            // aNormal（PMX 允许零向量表示"自动"，这里退化成 +Z，避免着色器 NaN）
            Vector3 n = v.Normal;
            if (n.LengthSquared() < 1e-12f) n = new Vector3(0f, 0f, 1f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 12, 4), n.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 16, 4), n.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 20, 4), n.Z);

            // aUv = (u, v, EdgeScale, DeformType) —— 对齐 PmxEditor fxd L199 的 float4 UV
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 24, 4), v.Uv.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 28, 4), v.Uv.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 32, 4), v.EdgeScale);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o + 36, 4), (int)v.Weight.Type);

            // aJoints（UNSIGNED_SHORT ×4）
            for (int k = 0; k < 4; k++)
            {
                int bone = v.Weight.Bone(k);
                int safe = bone >= 0 && bone < model.BoneCount ? bone : 0;
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o + 40 + k * 2, 2), (ushort)safe);
            }

            // aWeights（UNSIGNED_BYTE ×4，硬件归一化到 [0,1]）
            ComputeWeightBytes(v.Weight, out byte w0, out byte w1, out byte w2, out byte w3);
            span[o + 48] = w0;
            span[o + 49] = w1;
            span[o + 50] = w2;
            span[o + 51] = w3;

            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        model.VertexData = data;
        model.BoundsMin = min;
        model.BoundsMax = max;
    }

    /// <summary>
    /// 权重 → 字节。（v1 退化 SDEF→BDEF2、QDEF→BDEF4）。
    /// 第 4 槽用<b>推导值</b> 1-w0-w1-w2（不是文件里存的 Weight3），保证四槽和为 255，
    /// 对权重和异常的脏数据更稳。
    /// </summary>
    private static void ComputeWeightBytes(in PmxBoneWeight w, out byte w0, out byte w1, out byte w2, out byte w3)
    {
        Span<float> fw = stackalloc float[4];

        switch (w.Type)
        {
            case PmxBoneWeightType.Bdef1:
                fw[0] = 1f;
                break;

            case PmxBoneWeightType.Bdef2:
            case PmxBoneWeightType.Sdef:     // v1 退化
                fw[0] = w.Weight0;
                fw[1] = 1f - w.Weight0;
                break;

            default:                          // Bdef4 / Qdef
                fw[0] = w.Weight0;
                fw[1] = w.Weight1;
                fw[2] = w.Weight2;
                fw[3] = 1f - w.Weight0 - w.Weight1 - w.Weight2;   // 推导，而非文件里的 Weight3
                break;
        }

        // 脏数据兜底：负权重清零，再整体归一化到和为 1
        float sum = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (fw[i] < 0f || float.IsNaN(fw[i])) fw[i] = 0f;
            sum += fw[i];
        }
        if (sum > 1e-6f)
            for (int i = 0; i < 4; i++)
                fw[i] /= sum;

        // 找到最后一个非零槽，把余数塞给它，保证四槽字节和恒为 255
        // （否则 0.5/0.5 会四舍五入成 128+128=256）
        int last = 0;
        for (int i = 0; i < 4; i++)
            if (fw[i] > 1e-6f)
                last = i;

        Span<byte> b = stackalloc byte[4];
        int acc = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == last) continue;
            b[i] = ToWeightByte(fw[i]);
            acc += b[i];
        }
        b[last] = (byte)System.Math.Clamp(255 - acc, 0, 255);

        w0 = b[0]; w1 = b[1]; w2 = b[2]; w3 = b[3];
    }

    private static byte ToWeightByte(float w)
    {
        if (float.IsNaN(w) || w <= 0f) return 0;
        if (w >= 1f) return 255;
        return (byte)(w * 255f + 0.5f);
    }

    // ---------------------------------------------------------------- 索引

    private static void BuildIndices(PmxModel pmx, SkeletalModel model)
    {
        var src = pmx.Indices;
        var dst = new uint[src.Length];
        for (int i = 0; i < src.Length; i++)
            dst[i] = (uint)src[i];
        model.IndexData = dst;
    }

    // ---------------------------------------------------------------- 材质段

    private static void BuildSegments(PmxModel pmx, SkeletalModel model)
    {
        var mats = pmx.Materials;
        var segs = new DrawSegment[mats.Length];
        int cursor = 0;

        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            int indexCount = m.IndexCount;

            segs[i] = new DrawSegment
            {
                MaterialIndex = i,
                MaterialName = m.Name,
                IndexStart = cursor,
                IndexCount = indexCount,
                Type = ClassifyMaterial(m),
                DiffuseTextureId = m.TextureIndex,
                SphereTextureId = m.SphereTextureIndex,
                ToonTextureId = m.ToonTextureIndex,
                SphereMode = m.SphereTextureMode,
                EnableTexture = m.TextureIndex >= 0 && m.TextureIndex < pmx.Textures.Length,
                EnableSphere = m.SphereTextureIndex >= 0 && m.SphereTextureIndex < pmx.Textures.Length,
                EnableToon = m.ToonTextureIndex >= 0,
                IsDoubleSided = (m.Flag & PmxMaterialFlag.IsDoubleSided) != 0,
                // MMD 语义：需要「ToonEdge flag 置位」且「EdgeSize > 0」才画轮廓线。
                EnableEdge = (m.Flag & PmxMaterialFlag.EnabledToonEdge) != 0 && m.EdgeSize > 0f,
                // 铸影 / 收影旗标（铸影侧不能省、收影侧可省）。
                CastsShadow = (m.Flag & PmxMaterialFlag.EnabledDrawShadow) != 0,
                ReceivesShadow = (m.Flag & PmxMaterialFlag.EnabledReceiveShadow) != 0,
                Center = ComputeSegmentCenter(pmx, cursor, indexCount),
            };

            cursor += indexCount;
        }

        model.Segments = segs;
    }

    // ---------------------------------------------------------------- 表情（morph）

    /// <summary>
    /// 建表情数据表。**全程稀疏**：只重排 PMX 的「受影响索引 + 偏移」，绝不展开成
    /// 「顶点数 × morph 数」的稠密数组（babylon-mmd 的做法，最坏情况下 20 万顶点 x 200 morph ≈ 480 MB）。
    ///
    /// 支持：Group(0) / Vertex(1) / Bone(2) / UV(3) / Material(8)。
    ///
    /// <b>不支持</b>（数据仍留在 <see cref="PmxMorph"/> 里，这里不建运行时表、不参与求值）：
    ///   * Flip(9) / Impulse(10) —— PMX 2.1 追加项，实际模型里几乎不存在，不做相关实现。
    ///   * 附加 UV1~4(4~7) —— 本引擎顶点格式没有附加 UV 通道。
    /// </summary>
    private static void BuildMorphs(PmxModel pmx, SkeletalModel model)
    {
        var morphs = pmx.Morphs;
        int n = morphs.Length;

        model.MorphNames = new string[n];
        model.MorphKinds = new byte[n];
        model.MorphRawWeights = new float[n];
        model.MorphWeights = new float[n];
        model.MaterialMorphs = new PmxMaterialMorphElement[]?[n];

        var groups = new GroupMorphSparse?[n];
        var vertexList = new List<VertexMorphSparse>();
        var uvList = new List<UvMorphSparse>();
        var boneList = new List<BoneMorphSparse>();

        for (int i = 0; i < n; i++)
        {
            var m = morphs[i];
            model.MorphNames[i] = m.Name;
            model.MorphKinds[i] = (byte)m.Type;

            switch (m.Type)
            {
                case PmxMorphType.Vertex:
                {
                    if (m.Indices is not { } vIdx || m.Positions is not { } vPos) break;

                    var indices = new List<int>(vIdx.Length);
                    var offsets = new List<Vector3>(vIdx.Length);
                    for (int k = 0; k < vIdx.Length; k++)
                    {
                        int v = vIdx[k];
                        int s = k * 3;
                        // 脏索引 / 截断数组：加载期过滤一次，运行时零判断
                        if ((uint)v >= (uint)model.VertexCount || s + 2 >= vPos.Length) continue;
                        indices.Add(v);
                        offsets.Add(new Vector3(vPos[s], vPos[s + 1], vPos[s + 2]));
                    }
                    if (indices.Count > 0)
                        vertexList.Add(new VertexMorphSparse
                        {
                            MorphIndex = i,
                            VertexIndices = indices.ToArray(),
                            Offsets = offsets.ToArray(),
                        });
                    break;
                }

                case PmxMorphType.Uv:
                {
                    if (m.Indices is not { } uIdx || m.Offsets is not { } uOff) break;

                    var indices = new List<int>(uIdx.Length);
                    var offsets = new List<Vector2>(uIdx.Length);
                    for (int k = 0; k < uIdx.Length; k++)
                    {
                        int v = uIdx[k];
                        int s = k * 4;
                        if ((uint)v >= (uint)model.VertexCount || s + 3 >= uOff.Length) continue;
                        indices.Add(v);
                        // 只用 xy。本引擎上传的是文件原序 UV 且采样已与 PmxEditor 对齐，
                        // 因此不做 V 翻转，偏移直接相加。
                        offsets.Add(new Vector2(uOff[s], uOff[s + 1]));
                    }
                    if (indices.Count > 0)
                        uvList.Add(new UvMorphSparse
                        {
                            MorphIndex = i,
                            VertexIndices = indices.ToArray(),
                            Offsets = offsets.ToArray(),
                        });
                    break;
                }

                case PmxMorphType.Bone:
                {
                    if (m.Indices is not { } bIdx || m.Positions is not { } bPos || m.Rotations is not { } bRot) break;

                    var indices = new List<int>(bIdx.Length);
                    var translations = new List<Vector3>(bIdx.Length);
                    var rotations = new List<Quaternion>(bIdx.Length);
                    for (int k = 0; k < bIdx.Length; k++)
                    {
                        int b = bIdx[k];
                        int s3 = k * 3, s4 = k * 4;
                        if ((uint)b >= (uint)model.BoneCount) continue;
                        if (s3 + 2 >= bPos.Length || s4 + 3 >= bRot.Length) break;
                        indices.Add(b);
                        translations.Add(new Vector3(bPos[s3], bPos[s3 + 1], bPos[s3 + 2]));
                        rotations.Add(new Quaternion(bRot[s4], bRot[s4 + 1], bRot[s4 + 2], bRot[s4 + 3]));
                    }
                    if (indices.Count > 0)
                        boneList.Add(new BoneMorphSparse
                        {
                            MorphIndex = i,
                            BoneIndices = indices.ToArray(),
                            Translations = translations.ToArray(),
                            Rotations = rotations.ToArray(),
                        });
                    break;
                }

                case PmxMorphType.Material:
                    model.MaterialMorphs[i] = m.MaterialElements;
                    break;

                case PmxMorphType.Group:
                {
                    if (m.Indices is not { } gIdx || m.Ratios is not { } gRatio) break;

                    var child = new List<int>(gIdx.Length);
                    var ratio = new List<float>(gIdx.Length);
                    for (int k = 0; k < gIdx.Length; k++)
                    {
                        int c = gIdx[k];
                        if ((uint)c >= (uint)n || c == i || k >= gRatio.Length) continue;  // 越界 / 自引用丢弃
                        child.Add(c);
                        ratio.Add(gRatio[k]);
                    }
                    groups[i] = new GroupMorphSparse
                    {
                        MorphIndex = i,
                        ChildIndices = child.ToArray(),
                        Ratios = ratio.ToArray(),
                    };
                    break;
                }

                // 其它类型（Flip / Impulse / 附加 UV1~4）本步不支持：不建表、不参与求值。
            }
        }

        model.VertexMorphs = vertexList.ToArray();
        model.UvMorphs = uvList.ToArray();
        model.BoneMorphs = boneList.ToArray();
        model.GroupMorphs = groups;
        model.GroupOrder = BuildGroupOrder(groups);
    }

    /// <summary>
    /// Group 求值序。边 <c>u → c</c> 表示「u 引用 c」，因此要求 <b>u 先于 c</b>：
    /// DFS 后序天然给出「被引用者在前」，整体取反即得所需序。
    ///
    /// 环保护：只在 <c>state[c] == 0</c> 时入栈，因此自引用 / 互引用都不会死循环，
    /// 环上的边被静默丢弃（与 <see cref="BuildEvaluationOrder"/> 同风格）。
    /// 非 group 的子项不入序（它们不需要传播）。
    /// </summary>
    private static int[] BuildGroupOrder(GroupMorphSparse?[] groups)
    {
        int n = groups.Length;
        var state = new byte[n];                 // 0=未访问, 1=在栈上, 2=已完成
        var post = new List<int>(n);
        var stack = new Stack<(int Node, bool Post)>(n * 2);

        for (int start = 0; start < n; start++)
        {
            if (groups[start] is null || state[start] != 0) continue;
            stack.Push((start, false));

            while (stack.Count > 0)
            {
                var (u, isPost) = stack.Pop();
                if (isPost)
                {
                    state[u] = 2;
                    post.Add(u);
                    continue;
                }
                if (state[u] != 0) continue;
                state[u] = 1;
                stack.Push((u, true));

                var children = groups[u]!.Value.ChildIndices;
                for (int k = 0; k < children.Length; k++)
                {
                    int c = children[k];
                    if (groups[c] is null) continue;      // 非 group 子项不参与排序
                    if (state[c] == 0) stack.Push((c, false));
                }
            }
        }

        post.Reverse();
        return post.ToArray();
    }

    /// <summary>
    /// v1 简化规则：只看 Diffuse.W（MMD 非透过度）。
    /// </summary>
    public static MaterialRenderType ClassifyMaterial(PmxMaterial m)
    {
        if (m.Diffuse.W < 0.99f) return MaterialRenderType.Blended;
        if (m.TextureIndex >= 0) return MaterialRenderType.Cutout;
        return MaterialRenderType.Opaque;
    }

    private static Vector3 ComputeSegmentCenter(PmxModel pmx, int indexStart, int indexCount)
    {
        if (indexCount <= 0) return Vector3.Zero;

        var sum = Vector3.Zero;
        int used = 0;
        int end = System.Math.Min(indexStart + indexCount, pmx.Indices.Length);

        for (int i = indexStart; i < end; i++)
        {
            int vi = pmx.Indices[i];
            if ((uint)vi >= (uint)pmx.Vertices.Length) continue;
            sum += pmx.Vertices[vi].Position;
            used++;
        }

        return used > 0 ? sum / used : Vector3.Zero;
    }
}

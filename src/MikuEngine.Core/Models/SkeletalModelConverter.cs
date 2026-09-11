using System.Buffers.Binary;
using System.Numerics;

namespace MikuEngine.Core.Models;

/// <summary>
/// PmxModel（PMX 原始数据）→ SkeletalModel（运行时模型）。
///
/// 当前只做静态预览所需的部分：
///   交错 VBO / 统一索引缓冲 / 逆绑定矩阵 / 变形顺序 / 材质段分类。
/// Morph、SoftBody、SDEF 真实现留到后续。
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
        BuildVertices(pmx, model);
        BuildIndices(pmx, model);
        BuildSegments(pmx, model);

        model.ResetPose();
        return model;
    }

    // ---------------------------------------------------------------- 骨骼

    private static void BuildBones(PmxModel pmx, SkeletalModel model)
    {
        int n = pmx.Bones.Length;
        model.BoneCount = n;
        model.BoneNames = new string[n];
        model.ParentIndices = new int[n];
        model.LocalPositions = new Vector3[n];
        model.LocalRotations = new Quaternion[n];
        model.InverseBind = new Matrix4x4[n];
        model.WorldMatrices = new Matrix4x4[n];
        model.SkinMatrices = new Matrix4x4[n];

        for (int i = 0; i < n; i++)
        {
            var b = pmx.Bones[i];
            model.BoneNames[i] = b.Name;
            model.ParentIndices[i] = b.ParentBoneIndex;
            model.LocalPositions[i] = b.Position;
            model.LocalRotations[i] = Quaternion.Identity;
        }

        // 遍历顺序：PMX 的 TransformOrder 规范上是拓扑序，但并非所有文件都严格保证。
        // 这里直接从根骨做 DFS，天然保证"父先于子"，比信任 TransformOrder 更稳。
        model.DeformOrder = BuildTopologicalOrder(model.ParentIndices);

        // 绑定姿势世界矩阵 → 逆绑定矩阵
        var bindWorld = new Matrix4x4[n];
        foreach (int i in model.DeformOrder)
        {
            Matrix4x4 local = Matrix4x4.Identity;
            local.Translation = model.LocalPositions[i];

            int parent = model.ParentIndices[i];
            bindWorld[i] = parent >= 0 && (uint)parent < (uint)n
                ? bindWorld[parent] * local
                : local;

            if (!Matrix4x4.Invert(bindWorld[i], out var inv))
                inv = Matrix4x4.Identity;
            model.InverseBind[i] = inv;
        }
    }

    /// <summary>从所有根骨（parent &lt; 0）出发做迭代 DFS，返回父先于子的顺序。</summary>
    private static int[] BuildTopologicalOrder(int[] parents)
    {
        int n = parents.Length;
        var children = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            int p = parents[i];
            if (p >= 0 && p < n)
                (children[p] ??= new List<int>()).Add(i);
        }

        var order = new List<int>(n);
        var visited = new bool[n];
        var stack = new Stack<int>();

        for (int i = 0; i < n; i++)
        {
            if (parents[i] >= 0 && parents[i] < n) continue;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                if (visited[cur]) continue;
                visited[cur] = true;
                order.Add(cur);

                var kids = children[cur];
                if (kids == null) continue;
                for (int k = kids.Count - 1; k >= 0; k--)
                    if (!visited[kids[k]])
                        stack.Push(kids[k]);
            }
        }

        // 兜底：环或孤立节点（理论上不会出现），保证不丢骨骼
        for (int i = 0; i < n; i++)
            if (!visited[i])
                order.Add(i);

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

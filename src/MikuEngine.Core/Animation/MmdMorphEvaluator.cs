using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Animation;

/// <summary>
/// 表情（morph）权重的求值器：把 <see cref="SkeletalModel.MorphRawWeights"/>（动画轨道原始值）
/// 解算成 <see cref="SkeletalModel.MorphWeights"/>（有效值），并把<b>骨 morph</b> 折进局部 T/R。
///
/// 职责边界：
///   * 本类<b>无 GL 依赖</b>，纯 Core，可单元测试；
///   * 顶点 / UV / 材质 morph 只解算权重，几何与材质的落地由渲染层消费；
///   * <b>不</b>支持 Flip(9) / Impulse(10) / 附加 UV1~4(4~7)：转换器不为它们建表，
///     所以这里天然跳过（`MorphKinds` 仍保留原类型值，便于诊断与断言）。
///
/// 调用顺序（关键）：
/// <code>
/// animation.Sample(model, frame);   // ① 复位骨骼 + 写骨轨道；② 复位并写 MorphRawWeights
/// MmdMorphEvaluator.Evaluate(model); // ③ Group 传播 → MorphWeights；④ 骨 morph 写入 Local T/R
/// renderer.PrepareFrame(frame);      // ⑤ UpdateWorldMatrices（軸制限 → 付与）
/// </code>
/// 骨 morph 必须落在 ⑤ 之前、且早于軸制限与付与 —— 它是「骨自身局部变换」的一部分。
/// </summary>
public static class MmdMorphEvaluator
{
    /// <summary>
    /// 解算有效权重并应用骨 morph。<b>幂等</b>：有效权重每次从原始权重整体重算，
    /// 骨 morph 也只在 <see cref="MmdAnimation.Sample"/> 建立的绑定姿势局部 T/R 上叠加。
    /// </summary>
    public static void Evaluate(SkeletalModel model)
    {
        PropagateGroups(model);
        ApplyBoneMorphs(model);
    }

    // ---------------------------------------------------------------- Group

    /// <summary>
    /// Group（组）权重传播：<c>w[child] += ratio * w[group]</c>。
    ///
    /// 按 <see cref="SkeletalModel.GroupOrder"/>（引用者先于被引用者）遍历，因此
    /// 「组里套组」会自然级联 —— 这一点 reze-engine 只做单层、babylon-mmd 直接跳过。
    /// 多个组指向同一子项时权重<b>累加</b>。
    /// </summary>
    private static void PropagateGroups(SkeletalModel model)
    {
        var raw = model.MorphRawWeights;
        var effective = model.MorphWeights;
        var groups = model.GroupMorphs;

        int count = System.Math.Min(raw.Length, effective.Length);
        for (int i = 0; i < count; i++)
            effective[i] = raw[i];

        foreach (int gi in model.GroupOrder)
        {
            if ((uint)gi >= (uint)groups.Length) continue;
            if (groups[gi] is not { } group) continue;
            if ((uint)gi >= (uint)effective.Length) continue;

            float weight = effective[gi];
            if (weight == 0f) continue;               // 0 权重剪枝（也是常见路径）

            var children = group.ChildIndices;
            var ratios = group.Ratios;
            for (int k = 0; k < children.Length; k++)
            {
                int child = children[k];
                if ((uint)child >= (uint)effective.Length || k >= ratios.Length) continue;
                effective[child] += ratios[k] * weight;
            }
        }
    }

    // ---------------------------------------------------------------- 骨 morph

    /// <summary>
    /// 骨 morph：对目标骨叠加<b>父空间平移</b>与<b>局部旋转</b>。
    ///
    /// 旋转用 <c>own * Slerp(Identity, morph, w)</c>（右乘），与 reze-engine 的
    /// <c>applyBoneMorphs</c>、babylon-mmd 的 <c>morphRotationOffset</c> 同式。
    /// </summary>
    private static void ApplyBoneMorphs(SkeletalModel model)
    {
        var weights = model.MorphWeights;
        var translations = model.LocalTranslations;
        var rotations = model.LocalRotations;

        foreach (var morph in model.BoneMorphs)
        {
            int mi = morph.MorphIndex;
            if ((uint)mi >= (uint)weights.Length) continue;

            float weight = weights[mi];
            if (weight == 0f) continue;

            var bones = morph.BoneIndices;
            int n = System.Math.Min(System.Math.Min(bones.Length, morph.Translations.Length), morph.Rotations.Length);
            for (int k = 0; k < n; k++)
            {
                int bone = bones[k];
                if ((uint)bone >= (uint)model.BoneCount) continue;

                translations[bone] += morph.Translations[k] * weight;
                rotations[bone] = rotations[bone]
                                * Quaternion.Slerp(Quaternion.Identity, morph.Rotations[k], weight);
            }
        }
    }

    // ---------------------------------------------------------------- 材质 morph

    /// <summary>
    /// 材质 morph 混合：产出叠加了所有活跃材质 morph 之后的「有效材质」。
    ///
    /// Multiply：<c>v = v + (v * m - v) * w</c>（w = 0 时恒等）
    /// Add：     <c>v = v + m * w</c>
    /// 与 babylon-mmd 的 <c>_applyMaterialMorph</c> 同式。
    ///
    /// <paramref name="materialIndex"/> 对应的元素除 <c>MaterialIndex == -1</c>（全部材质）外都要求匹配。
    /// </summary>
    public static MaterialState ResolveMaterial(SkeletalModel model, int materialIndex)
    {
        var material = model.Materials[materialIndex];

        var state = new MaterialState
        {
            Diffuse = material.Diffuse,
            Specular = material.Specular,
            Shininess = material.Shininess,
            Ambient = material.Ambient,
            EdgeColor = material.EdgeColor,
            EdgeSize = material.EdgeSize,
            // 三个色调系数默认为「乘算恒等」
            TextureColor = Vector4.One,
            SphereColor = Vector4.One,
            ToonColor = Vector4.One,
        };

        var morphs = model.MaterialMorphs;
        var weights = model.MorphWeights;
        int count = System.Math.Min(morphs.Length, weights.Length);
        int materialCount = model.Materials.Length;

        for (int i = 0; i < count; i++)
        {
            if (morphs[i] is not { } elements) continue;

            float weight = weights[i];
            if (weight == 0f) continue;

            for (int k = 0; k < elements.Length; k++)
            {
                var e = elements[k];
                if (e.MaterialIndex < -1 || e.MaterialIndex >= materialCount) continue;
                if (e.MaterialIndex != -1 && e.MaterialIndex != materialIndex) continue;

                if (e.Type == PmxMaterialMorphType.Multiply)
                {
                    state.Diffuse = Multiply(state.Diffuse, e.Diffuse, weight);
                    state.Specular = Multiply(state.Specular, e.Specular, weight);
                    state.Shininess = Blend(state.Shininess, e.Shininess, weight);
                    state.Ambient = Multiply(state.Ambient, e.Ambient, weight);
                    state.EdgeColor = Multiply(state.EdgeColor, e.EdgeColor, weight);
                    state.EdgeSize = Blend(state.EdgeSize, e.EdgeSize, weight);
                    state.TextureColor = Multiply(state.TextureColor, e.TextureColor, weight);
                    state.SphereColor = Multiply(state.SphereColor, e.SphereTextureColor, weight);
                    state.ToonColor = Multiply(state.ToonColor, e.ToonTextureColor, weight);
                }
                else
                {
                    state.Diffuse += e.Diffuse * weight;
                    state.Specular += e.Specular * weight;
                    state.Shininess += e.Shininess * weight;
                    state.Ambient += e.Ambient * weight;
                    state.EdgeColor += e.EdgeColor * weight;
                    state.EdgeSize += e.EdgeSize * weight;
                    state.TextureColor += e.TextureColor * weight;
                    state.SphereColor += e.SphereTextureColor * weight;
                    state.ToonColor += e.ToonTextureColor * weight;
                }
            }
        }

        return state;
    }

    /// <summary>Multiply 混合：<c>v + (v * m - v) * w</c>。</summary>
    private static Vector4 Multiply(Vector4 v, Vector4 m, float w) => v + (v * m - v) * w;

    private static Vector3 Multiply(Vector3 v, Vector3 m, float w) => v + (v * m - v) * w;

    /// <summary>标量的 Multiply 混合：<c>v + (v * m - v) * w</c>。</summary>
    private static float Blend(float v, float m, float w) => v + (v * m - v) * w;
}

using System.Numerics;

namespace MikuEngine.Core.Models;

/// <summary>
/// 引擎侧的材质渲染分类。PMX 本身不区分，由 <c>SkeletalModelConverter</c> 按
/// <c>PmxMaterial.Diffuse.W</c>（MMD "非透过度"）判定。
/// </summary>
public enum MaterialRenderType : byte
{
    Opaque = 0,
    Cutout = 1,
    Blended = 2,
}

/// <summary>
/// 一个材质段 = 一次 draw call。
/// </summary>
public sealed class DrawSegment
{
    public int MaterialIndex;
    public string MaterialName = "";

    /// <summary>索引缓冲内的起始位置（以 uint 元素计）。</summary>
    public int IndexStart;

    public int IndexCount;

    public MaterialRenderType Type;

    /// <summary>纹理库 id，-1 表示无。</summary>
    public int DiffuseTextureId = -1;
    public int SphereTextureId = -1;
    public int ToonTextureId = -1;

    public PmxMaterialSphereMode SphereMode;
    public bool EnableTexture;
    public bool EnableSphere;
    public bool EnableToon;
    public bool IsDoubleSided;
    public bool EnableEdge;

    /// <summary>PMX 材质旗标 bit2（EnabledDrawShadow）—— 该材质是否写进自阴影 Z 图（铸影侧）。</summary>
    public bool CastsShadow;

    /// <summary>PMX 材质旗标 bit3（EnabledReceiveShadow）—— 该材质是否接受自阴影（收影侧）。</summary>
    public bool ReceivesShadow;

    /// <summary>本段顶点在绑定姿势下的几何中心（模型空间），用于 Blended 队列排序。</summary>
    public Vector3 Center;
}

/// <summary>
/// 运行时模型：PmxModel 经 SkeletalModelConverter 转换后的形态，引擎侧直接消费。
/// 纯数据 + 骨骼姿势计算，不含任何 GL / 平台依赖。
/// </summary>
public sealed class SkeletalModel
{
    /// <summary>
    /// 交错顶点缓冲步长（字节）。
    ///
    /// 注意：
    /// 骨骼数通常远超 UNSIGNED_BYTE 能表达的 255，因此骨骼索引必须用 UNSIGNED_SHORT（4 × 2 = 8 字节）：
    ///   aPosition vec3   12 @0
    ///   aNormal   vec3   12 @12
    ///   aUv       vec4   16 @24   (xy=UV, z=EdgeScale, w=DeformType)
    ///   aJoints   uvec4   8 @40   (UNSIGNED_SHORT ×4)
    ///   aWeights  vec4    4 @48   (UNSIGNED_BYTE ×4, normalized)
    ///   stride = 52
    /// </summary>
    public const int VertexStride = 52;

    /// <summary>
    /// 整模型可见性（VMD 表示枠的求值结果），默认 true。
    ///
    /// false ⇒ 渲染层跳过<b>主渲染 + 轮廓线 + 自阴影 caster</b> 三个 pass
    /// （与 reze-engine <c>Model.setVisible</c> 的语义一致）；拾取不受影响。
    /// 由动画层每帧写入（单动效 <c>MmdAnimation.SampleVisible</c>，
    /// 多动效为各活跃层按 AND 合并）；静态预览恒为 true。
    /// </summary>
    public bool Visible = true;

    // ── 几何 ───────────────────────────────────────────────────────────
    public byte[] VertexData = Array.Empty<byte>();
    public int VertexCount;
    public uint[] IndexData = Array.Empty<uint>();

    // ── 材质 / 分组 ─────────────────────────────────────────────────────
    public PmxMaterial[] Materials = Array.Empty<PmxMaterial>();
    public DrawSegment[] Segments = Array.Empty<DrawSegment>();

    /// <summary>PMX 纹理表（原始相对路径，未解析成磁盘路径）。</summary>
    public string[] Textures = Array.Empty<string>();

    // ── 骨骼 ───────────────────────────────────────────────────────────
    public int BoneCount;
    public string[] BoneNames = Array.Empty<string>();

    /// <summary>表情（morph）名称表，供动画轨道按名字绑定。</summary>
    public string[] MorphNames = Array.Empty<string>();

    // ── 表情（morph） ───────────────────────────────────────────────────
    //
    // 数据布局原则：**全程稀疏**。PMX 的顶点/UV/骨 morph 源数据本身就是
    // 「受影响索引 + 偏移」的稀疏形式，这里只做重排与越界过滤，绝不展开成
    // 「顶点数 × morph 数」的稠密数组（babylon-mmd 的做法，极端情况下 20 万顶点 x 200 morph ≈ 480 MB）。
    //
    // 运行时唯一每帧变化的 morph 状态是 MorphWeights；顶点/UV 偏移由渲染层按活跃
    // morph 稀疏累加，材质 morph 由 MmdMorphEvaluator.ResolveMaterial 逐段混合。

    /// <summary>每条 morph 的类型（<see cref="PmxMorphType"/> 原样），供求值分发与诊断。</summary>
    public byte[] MorphKinds = Array.Empty<byte>();

    /// <summary>
    /// 动画写入的<b>原始</b>权重（仅 VMD 轨道值），长度 = <see cref="MorphNames"/>.Length。
    /// 由 <c>MmdAnimation.Sample</c> 先整体清零再写入。
    /// </summary>
    public float[] MorphRawWeights = Array.Empty<float>();

    /// <summary>
    /// <b>有效</b>权重（Group 传播之后的解算结果），长度同 <see cref="MorphNames"/>。
    /// 由 <c>MmdMorphEvaluator.Evaluate</c> 从 <see cref="MorphRawWeights"/> 重算，
    /// 渲染层只读这一份。因为每次都整体重算，<c>Evaluate</c> 是幂等的。
    /// </summary>
    public float[] MorphWeights = Array.Empty<float>();

    /// <summary>顶点 morph 稀疏表（只含类型 1）。</summary>
    public VertexMorphSparse[] VertexMorphs = Array.Empty<VertexMorphSparse>();

    /// <summary>主 UV morph 稀疏表（只含类型 3；附加 UV1~4 不支持）。</summary>
    public UvMorphSparse[] UvMorphs = Array.Empty<UvMorphSparse>();

    /// <summary>骨 morph 稀疏表（只含类型 2）。</summary>
    public BoneMorphSparse[] BoneMorphs = Array.Empty<BoneMorphSparse>();

    /// <summary>材质 morph 元素表，按 morph 索引存放；非材质 morph 为 null。</summary>
    public PmxMaterialMorphElement[]?[] MaterialMorphs = Array.Empty<PmxMaterialMorphElement[]?>();

    /// <summary>Group morph 引用表，按 morph 索引存放；非 Group morph 为 null。</summary>
    public GroupMorphSparse?[] GroupMorphs = Array.Empty<GroupMorphSparse?>();

    /// <summary>
    /// Group 求值序：保证「引用者先于被引用者」。引用图里的环（自引用 / 互引用）
    /// 已被丢弃对应边，因此本序是良定义的拓扑序，可安全地一次遍历完成级联传播。
    /// </summary>
    public int[] GroupOrder = Array.Empty<int>();

    /// <summary>保证"父先于子"的遍历顺序，供 UpdateWorldMatrices 使用。</summary>
    public int[] DeformOrder = Array.Empty<int>();

    public int[] ParentIndices = Array.Empty<int>();

    /// <summary>
    /// 绑定姿势下的局部平移（<b>相对父骨</b>，父空间）。由 PMX 的模型空间绝对坐标差分而来
    /// （<c>Bone.Position - parent.Bone.Position</c>）。只读基准值。
    /// </summary>
    public Vector3[] LocalPositions = Array.Empty<Vector3>();

    /// <summary>
    /// 当前局部平移（父空间），初始 = <see cref="LocalPositions"/>。
    /// 动画（VMD 位移轨道）写入此数组，绑定值本身不被改动。
    /// </summary>
    public Vector3[] LocalTranslations = Array.Empty<Vector3>();

    /// <summary>当前局部旋转，初始为单位四元数（即绑定姿势）。</summary>
    public Quaternion[] LocalRotations = Array.Empty<Quaternion>();

    // ── 付与（append transform）与軸制限 ────────────────────────────────
    //
    // 付与是 MMD 的「父约束」：目标骨在自身动画之外，额外继承源骨的一部分变换。
    // 测试模型的典型用法：
    //   * 足D/ひざD/足首D（每腿 624 个顶点）付与自 足/ひざ/足首  —— 复制腿，让它不随腰弯曲
    //   * 腰キャンセル左/右 付与自 腰（ratio = -1）—— 抵消腰的旋转
    //   * 左目/左目先 付与自 両目/左目（ratio 0.8 / -0.7）—— 眼球跟随
    //   * 腕捩1..3 付与自 腕捩（ratio 0.25/0.5/0.75）—— 把扭转按比例摊到前臂
    //   * 大量「〜調整」骨：源是编辑器用的静默骨架，ratio 1.0，实际等价于无操作

    /// <summary>付与源骨索引，-1 = 无付与。</summary>
    public int[] AppendSources = Array.Empty<int>();

    /// <summary>付与比率（PMX 允许负值，-1 = 反向抵消）。已钳制到 [-1, 1]。</summary>
    public float[] AppendRatios = Array.Empty<float>();

    /// <summary>是否继承旋转（PMX 骨标志 bit8 / 0x0100）。</summary>
    public bool[] AppendRotate = Array.Empty<bool>();

    /// <summary>是否继承平移（PMX 骨标志 bit9 / 0x0200）。</summary>
    public bool[] AppendMove = Array.Empty<bool>();

    /// <summary>付与变换是否取「付与亲的世界变换」而非默认的「局部变换」（PMX 骨标志 bit7 / 0x0080）。</summary>
    public bool[] AppendIsLocal = Array.Empty<bool>();

    /// <summary>軸制限轴（<b>已归一化</b>；<see cref="Vector3.Zero"/> = 无限制）。带限制的骨只能绕该轴旋转。</summary>
    public Vector3[] AxisLimits = Array.Empty<Vector3>();

    // ── IK（反向动力学）─────────────────────────────────────────────────
    //
    // 设计要点（详见 docs/2026-09-12-mmd-ik-design.md）：
    //   * IK 结果写进【独立】的 IkRotations[]，**绝不回写 LocalRotations**。付与是在
    //     UpdateWorldMatrices 内、且刻意不回写 LocalRotations 以保证幂等；一旦 IK 回写 LocalRotations，
    //     下一帧付与会把上次的 IK 同时继承一次（双重付与），且本方法不再幂等。
    //   * 位置：叠加在【付与之后】的旋转上，与 PmxEditor UpdateLocalMatrix 的
    //     `rotation *= IKRotation`（IsIKLink 分支在 IsRotateAdd 之后）同序。

    /// <summary>运行时 IK 链（转换期由 <see cref="MmdIkChainBuilder.Build"/> 构建一次）。</summary>
    public MmdIkChain[] IkChains = Array.Empty<MmdIkChain>();

    /// <summary>本骨是否为某条 IK 链的链骨。只有 true 的骨才读 <see cref="IkRotations"/>。</summary>
    public bool[] IsIkLink = Array.Empty<bool>();

    /// <summary>
    /// IK 求解结果（相对"付与后旋转"的增量）。每帧由 IK 求解器整体复位为 Identity 后再写，
    /// **不跨帧**。长度恒等于 <see cref="BoneCount"/>。
    /// </summary>
    public Quaternion[] IkRotations = Array.Empty<Quaternion>();

    /// <summary>
    /// 链骨的「付与之后、IK 之前」旋转（对应 PmxEditor 的 <c>LocalRotationForIKLink</c>）。
    /// IK 的角度限制要在 <c>base * IkRotations[i]</c> 上做 Euler 分解与回弹，故由
    /// <see cref="UpdateWorldMatrices"/> 每帧导出（IK 求值过程中也会被增量重算覆盖）。
    /// </summary>
    public Quaternion[] IkLinkBaseRotations = Array.Empty<Quaternion>();

    /// <summary>
    /// 每条链的使能位，索引与 <see cref="IkChains"/> 对齐。VMD 表示枠的 IK 开关写入此处；
    /// 未声明的默认全开（表示枠是阶梯量，帧号早于首键时视为启用）。
    /// </summary>
    public bool[] IkEnabled = Array.Empty<bool>();

    /// <summary>IK 求解总开关（A/B 对照用；关闭时 <see cref="IkRotations"/> 恒为单位阵）。</summary>
    public bool IkSolverEnabled = true;

    // 付与求值需要「源骨的最终局部变换」，而已完成骨的最终值不能再覆盖 Local*（否则重复调用
    // UpdateWorldMatrices 会二次付与）。因此用独立暂存数组，保证本方法幂等。
    private Quaternion[] _finalRotations = Array.Empty<Quaternion>();
    private Vector3[] _finalTranslations = Array.Empty<Vector3>();

    /// <summary><see cref="UpdateWorldMatricesSubtree"/> 的子树标记，复用避免每帧分配。</summary>
    private bool[] _subtreeMask = Array.Empty<bool>();

    /// <summary>
    /// 最近一次 <see cref="UpdateWorldMatrices"/> 算出的「付与之后」的最终局部旋转/平移（只读）。
    /// <see cref="IkSolverEnabled"/> 为 false 时跑一次，得到的就是「付与之后、IK 之前」的有效局部变换 ——
    /// IK 诊断导出靠它让外部（Node 侧的 reze oracle）能原样重放同一套骨架，不必复刻付与/軸制限。
    /// </summary>
    public ReadOnlySpan<Quaternion> FinalRotations => _finalRotations;

    /// <inheritdoc cref="FinalRotations"/>
    public ReadOnlySpan<Vector3> FinalTranslations => _finalTranslations;

    public Matrix4x4[] InverseBind = Array.Empty<Matrix4x4>();
    public Matrix4x4[] WorldMatrices = Array.Empty<Matrix4x4>();

    /// <summary>SkinMatrices[i] = WorldMatrices[i] * InverseBind[i]（行主序，可直接喂 GLSL mat4）。</summary>
    public Matrix4x4[] SkinMatrices = Array.Empty<Matrix4x4>();

    // ── 包围盒（绑定姿势，模型空间） ─────────────────────────────────────
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;

    public Vector3 BoundsCenter => (BoundsMin + BoundsMax) * 0.5f;
    public Vector3 BoundsSize => BoundsMax - BoundsMin;

    /// <summary>按名字找骨骼，找不到返回 -1。</summary>
    public int FindBone(string name)
    {
        for (int i = 0; i < BoneNames.Length; i++)
            if (string.Equals(BoneNames[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    public void SetBoneLocalRotation(int boneIndex, Quaternion rotation)
    {
        if ((uint)boneIndex >= (uint)BoneCount) return;
        LocalRotations[boneIndex] = rotation;
    }

    public void ResetPose()
    {
        for (int i = 0; i < LocalRotations.Length; i++)
            LocalRotations[i] = Quaternion.Identity;
        for (int i = 0; i < LocalTranslations.Length; i++)
            LocalTranslations[i] = LocalPositions[i];
        UpdateWorldMatrices();
    }

    /// <summary>把表情权重归零（关闭表情驱动时用，保证不再残留上一层表情）。</summary>
    public void ResetMorphWeights()
    {
        for (int i = 0; i < MorphRawWeights.Length; i++)
            MorphRawWeights[i] = 0f;
        for (int i = 0; i < MorphWeights.Length; i++)
            MorphWeights[i] = 0f;
    }

    /// <summary>
    /// 由当前局部 T/R 重算世界矩阵与蒙皮矩阵。按 <see cref="DeformOrder"/> 遍历保证
    /// 「父先于子」且「付与源先于付与目标」。本方法<b>幂等</b>：付与/軸制限的中间结果只写暂存数组，
    /// 不回写 <see cref="LocalRotations"/>/<see cref="LocalTranslations"/>。
    ///
    /// 乘序：System.Numerics 是<b>行主序 row-vector</b> 约定（<c>Vector3.Transform(v, m)</c> 等价
    /// <c>v * m</c>），因此「先局部、后父级」必须写成 <c>local * parentWorld</c>；
    /// 蒙皮矩阵同理为 <c>InverseBind * World</c>（bind 空间 → 骨空间 → 动画世界）。
    /// 绑定姿势下所有局部旋转为单位阵，两种乘序数值相同（Skin ≡ I），所以静态预览看不出差别；
    /// 一旦有旋转，<c>parentWorld * local</c> 会把父骨原点拿子骨旋转去转 —— 误差随「骨到原点的距离」
    /// 放大（下半身离原点近尚可，上半身/手臂/手指链条远且长 → 撕裂成放射状薄片）。
    /// </summary>
    public void UpdateWorldMatrices()
    {
        EnsureScratch();

        for (int k = 0; k < DeformOrder.Length; k++)
            RecomputeBone(DeformOrder[k]);
    }

    /// <summary>
    /// 只重算 <paramref name="rootBone"/> 及其<b>全部后代</b>的世界/蒙皮矩阵，其余骨保留上一轮的值。
    ///
    /// 供 IK 迭代内的增量刷新使用（复刻 PmxEditor <c>IKTransform.CalcBonePosition_Link</c> 的
    /// "只推进链内"语义 —— 这里取"子树"作为<b>超集</b>：链骨与末端之间可能夹着非链骨，
    /// 只刷链骨会让中间骨的陈旧世界矩阵污染链骨自身的 world，而重算超集的值与之完全一致）。
    ///
    /// 遍历仍走 <see cref="DeformOrder"/>，因此"父先于子"与"付与源先于付与目标"两个不变量不受影响。
    /// </summary>
    public void UpdateWorldMatricesSubtree(int rootBone)
    {
        if ((uint)rootBone >= (uint)BoneCount) return;
        EnsureScratch();

        // 标记子树：DeformOrder 保证父先于子，一趟即可传播。
        System.Array.Clear(_subtreeMask, 0, BoneCount);
        _subtreeMask[rootBone] = true;
        for (int k = 0; k < DeformOrder.Length; k++)
        {
            int i = DeformOrder[k];
            if (_subtreeMask[i]) continue;
            int p = ParentIndices[i];
            if (p >= 0 && _subtreeMask[p]) _subtreeMask[i] = true;
        }

        for (int k = 0; k < DeformOrder.Length; k++)
        {
            int i = DeformOrder[k];
            if (_subtreeMask[i]) RecomputeBone(i);
        }
    }

    private void EnsureScratch()
    {
        if (_finalRotations.Length != BoneCount)
        {
            _finalRotations = new Quaternion[BoneCount];
            _finalTranslations = new Vector3[BoneCount];
        }
        if (_subtreeMask.Length != BoneCount)
            _subtreeMask = new bool[BoneCount];
    }

    /// <summary>
    /// 单根骨：軸制限 → 付与 → IK → 世界矩阵 / 蒙皮矩阵。幂等（只写暂存数组与输出数组）。
    /// </summary>
    private void RecomputeBone(int i)
    {
        Quaternion rotation = LocalRotations[i];
        Vector3 translation = LocalTranslations[i];

        // ── 軸制限：先约束「自身动画旋转」，再谈付与 ──────────────────────
        // MMD 在动效加载时就把带軸制限骨的旋转烘焙到该轴上，因此约束发生在付与之前。
        Vector3 axis = AxisLimits[i];
        if (axis != Vector3.Zero)
            rotation = ProjectOntoAxis(rotation, axis);

        // ── 付与（append transform）─────────────────────────────────────
        // 继承的是源骨的「最终」变换（源骨自己的付与已算完）。
        //   * 默认（局部模式）：旋转取源骨最终局部旋转 _finalRotations[src]，
        //     平移取「当前局部平移 − 绑定局部平移」（即动效偏移）。
        //   * 世界模式（PMX 骨标志 bit7 LocalAppendTransform）：旋转取源骨的最终【世界】旋转，
        //     平移取源骨原点相对绑定姿势的【世界】位移（World · InverseBind 的平移）。
        //     与 babylon-mmd AppendTransformSolver 的 isLocal 分支逐式等价。
        int src = AppendSources[i];
        if (src >= 0)
        {
            float ratio = AppendRatios[i];

            if (AppendRotate[i])
            {
                Quaternion srcRot = AppendIsLocal[i]
                    ? WorldRotationOf(src)
                    : _finalRotations[src];
                rotation = rotation * AppendRotation(srcRot, ratio);
            }

            if (AppendMove[i])
            {
                Vector3 srcOffset = AppendIsLocal[i]
                    ? WorldDisplacementOf(src)
                    : _finalTranslations[src] - LocalPositions[src];
                translation += srcOffset * ratio;
            }
        }

        // ── IK：叠加在「付与之后」的旋转上 ────────────────────────────────
        // 对应 PmxEditor UpdateLocalMatrix 的 `if (IsIKLink) { LocalRotationForIKLink = rotation;
        // rotation *= IKRotation; }`（付与的 IsRotateAdd 在前，IK 在后，顺序一致）。
        // 只有 IK 链骨需要读 IkRotations，非链骨完全不碰这一段。
        if (IsIkLink.Length == BoneCount && IsIkLink[i])
        {
            IkLinkBaseRotations[i] = rotation;
            rotation = rotation * IkRotations[i];
        }

        // 源骨以「最终」旋转被继承 —— PE 的付与局部模式在源骨是 IK link 时会额外乘其 IKRotation
        //（UpdateLocalMatrix 的 `if (AddParent.IsIKLink && !IsAddLocal) result *= AddParent.IKRotation`），
        // 因此这里必须把含 IK 的 rotation 作为最终值写回暂存。
        _finalRotations[i] = rotation;
        _finalTranslations[i] = translation;

        // local = R · T（先绕骨原点旋转，再平移到父空间中的骨位置）
        Matrix4x4 local = Matrix4x4.CreateFromQuaternion(rotation);
        local.Translation = translation;

        int parent = ParentIndices[i];
        WorldMatrices[i] = parent >= 0 ? local * WorldMatrices[parent] : local;
        SkinMatrices[i] = InverseBind[i] * WorldMatrices[i];
    }

    /// <summary>
    /// 把四元数的向量部分投影到 <paramref name="axis"/> 上再重新归一化 —— 只保留绕该轴的扭转，
    /// 丢掉其余分量（reze-engine <c>applyFixedAxes</c> 同法，也是 MMD 対して 腕捩/手捩 的行为）。
    /// </summary>
    private static Quaternion ProjectOntoAxis(Quaternion q, Vector3 axis)
    {
        float dot = q.X * axis.X + q.Y * axis.Y + q.Z * axis.Z;
        float x = axis.X * dot, y = axis.Y * dot, z = axis.Z * dot;
        float len = MathF.Sqrt(x * x + y * y + z * z + q.W * q.W);
        if (len <= 1e-8f) return Quaternion.Identity;
        float inv = 1f / len;
        return new Quaternion(x * inv, y * inv, z * inv, q.W * inv);
    }

    /// <summary>
    /// 返「单位四元数与 <paramref name="q"/> 之间、进度为 |ratio|」的旋转。
    /// 比率为负时先取共轭（= 反向旋转），这与 MMD 付与的 ratio &lt; 0 语义一致
    /// （腰キャンセル 用 -1.0 来抵消腰的旋转）。
    /// </summary>
    private static Quaternion AppendRotation(Quaternion q, float ratio)
    {
        if (ratio < 0f)
        {
            q = new Quaternion(-q.X, -q.Y, -q.Z, q.W);
            ratio = -ratio;
        }
        if (ratio >= 1f) return q;
        return Quaternion.Slerp(Quaternion.Identity, q, ratio);
    }

    /// <summary>
    /// 源骨的最终世界旋转（rigid 世界矩阵直接取旋转分量）。仅世界模式付与（bit7）使用，
    /// <see cref="WorldMatrices"/> 此时已含源骨自身的軸制限与付与，故等价于 babylon-mmd 的
    /// <c>Quaternion.FromRotationMatrix(targetBoneWorldMatrix)</c>。
    /// </summary>
    private Quaternion WorldRotationOf(int src)
        => Quaternion.CreateFromRotationMatrix(WorldMatrices[src]);

    /// <summary>
    /// 源骨原点相对绑定姿势的世界位移 = <c>World · InverseBind</c> 的平移分量。仅世界模式付与（bit7）使用，
    /// 逐式等价于 babylon-mmd 的 <c>worldMatrix · absoluteInverseBindMatrix</c> 平移
    /// （行主序约定下 translation = World.Translation + World.Linear · InverseBind.Translation）。
    /// </summary>
    private Vector3 WorldDisplacementOf(int src)
    {
        Matrix4x4 disp = WorldMatrices[src] * InverseBind[src];
        return disp.Translation;
    }
}

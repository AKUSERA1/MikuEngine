using MikuEngine.Core.Math;

namespace MikuEngine.Physics;

/// <summary>
/// 
/// 物理引擎，移植自 reze-engine 的 TypeScript 实现。类名 MMDPhysics 与 reze 原实现区分
/// 
/// Skeleton sync layer：kinematic 目标、snap、teleport carry、写回、reset、
/// 地面开关、NaN gate、alpha 插值。
///
/// Static / kinematic bodies follow their bone via boneWorld × bodyOffset;
/// dynamic bodies integrate under gravity + constraints and write their pose
/// back via bodyWorld × bodyOffsetInverse.
///
/// reze 把这些塞在一个 step(dt, bones, binds) 里；C# 侧拆成与渲染帧序对应的
/// 三个相位，由 <see cref="Update"/>串起：
/// FK/IK/付与 → <see cref="Update"/>（= SetKinematicTargets → Step(ticks) → WriteBack）
/// 
/// 时钟：tickTarget = floor(动画帧号 × k)，k=<see cref="TickRateMultiplier"/>，
/// 每 tick 固定 <see cref="TickDuration"/> 秒；物理状态是帧号的纯函数，与渲染率/时钟模式解耦。
/// 
/// dt 语义：**动画秒**（帧号差 × 1/PlaybackFps），不是 wall dt——teleport 阈值
/// "250 units/s 按帧时间缩放"在 FrameLocked/RealTime 两种时钟下都必须以帧号域换算后的 dt 计，否则阈值失真。
/// </summary>
public sealed class MMDPhysics
{
    // Module-level scratch
    private static readonly float[] BodyMat = new float[16];
    private static readonly float[] BoneMat = new float[16];
    private static readonly float[] ScratchQuat = new float[4];

    private readonly JointDef[] _joints;
    private readonly RigidBodyStore _store;
    private readonly World _world;
    private readonly SixDofSpringConstraint[] _constraints;
    private readonly SolverCache _solverCache;
    private readonly ContactPool _contacts;
    private bool _firstFrame = true;

    /// <summary>
    /// 动画帧率（MMD 标准 30）。tick 时长 = 1/(PlaybackFps × <see cref="TickRateMultiplier"/>)；
    /// 默认 30×2 → 60Hz 物理步长。变更时失效 World 的阻尼因子缓存（按 dt 键控）。
    /// </summary>
    public float PlaybackFps
    {
        get => _playbackFps;
        set
        {
            if (_playbackFps == value) return;
            _playbackFps = value;
            _world.InvalidateDampingCache();
        }
    }
    private float _playbackFps = 30f;

    /// <summary>
    /// tick 时钟倍率 k（每动画帧 k 个物理 tick，固定 2 = Standard 档，
    /// 无 UI 档位）。tickTarget = floor(动画帧号 × k)。
    /// </summary>
    public const int TickRateMultiplier = 2;

    /// <summary>单个物理 tick 的时长（动画域秒）。</summary>
    public float TickDuration => 1f / (_playbackFps * TickRateMultiplier);

    /// <summary>
    /// reze 的 maxSubSteps=6 之外还有一道 wall-time 预算（performance.now EMA
    /// 卸载）。MikuEngine 改为 tick 时钟（k=2，MaxCatchUp=4×k=8）硬上限
    /// 取代之,且掉帧追赶的上限语义由 tickTarget 差值天然表达。
    /// </summary>
    private const int MaxCatchUp = 8;

    // tick 时钟基准：上一渲染帧已消费到的 tickTarget。advance = 本帧 tickTarget − 它。
    private int _lastTickTarget;
    // 物理模拟开关的上一帧状态
    private bool _wasEnabled;

    // Fixed-timestep render interpolation ("Fix Your Timestep"): the dynamic body pose is
    // rendered as lerp(prev, curr, alpha) between the last two completed substeps, where
    // alpha = leftover accumulator / fixedTimeStep. Removes the temporal aliasing that shows
    // as hair/cloth judder when the render rate doesn't line up with the 60Hz physics step.
    private readonly float[] _prevPositions;
    private readonly float[] _prevOrientations;

    // Per-frame kinematic body targets (boneWorld × bodyOffset). Kinematic
    // bodies advance toward these incrementally per substep instead of jumping
    // to the final pose before substep 1. Velocities come from the target
    // trajectory (frame-to-frame target delta over render dt) — deriving them
    // from the per-substep advancement instead ripples with the accumulator
    // phase at render rates above 60 Hz and visibly excites cloth chains.
    private readonly float[] _kinTargetPos;
    private readonly float[] _kinTargetOri;
    private readonly float[] _kinTargetVel;
    private readonly float[] _kinTargetAngVel;

    // For each dynamic body, the kinematic body its constraint chain roots at
    // (-1 if unreachable). On a teleport, dynamic bodies are carried rigidly by
    // their root's transform delta so cloth keeps its pose relative to the
    // character instead of being dragged across the jump.
    private readonly int[] _kinRoot;
    // Kinematic bodies whose target jumped discontinuously this frame.
    private readonly byte[] _teleportFlags;
    // PMX mode-2 bodies jointed *directly* to a bone-follow body (breasts,
    // chain roots). Only these get MMD's bone-position alignment: their bone's
    // animated matrix is trustworthy, whereas deeper chain bones' animated
    // matrices don't include the physics deflection of their parents, so
    // pinning those would freeze the chain.
    private readonly byte[] _alignPinned;
    // Debug counter: frames on which a teleport (scrub/jump discontinuity)
    // was detected and settled.
    public int TeleportCount { get; private set; }

    // tick 域 target 采样：上一渲染帧的骨骼世界矩阵快照（帧末缓存）。
    // kinematic 目标表达到「本帧末 tick 时刻」的骨骼姿态 = lerp(prevBone, curBone, s)，
    // s 由帧号域解析——这样 target 是动画帧号的纯函数，物理状态与渲染率解耦
    // （单测结果：144Hz RealTime 与 FrameLocked 在同帧号处逐位一致）。
    private float[] _prevBoneWorld = Array.Empty<float>();
    private float[] _boneSample = Array.Empty<float>();
    private bool _hasPrevBone;
    private static readonly float[] SampleQa = new float[4];
    private static readonly float[] SampleQb = new float[4];

    /// <summary>Where the floor body actually lives. <see cref="RigidBodyStore.GroundIndex"/>
    /// is the SWITCH — see <see cref="SetFloor"/> — and forgets it.</summary>
    private int _groundBody = -1;

    public RigidBodyStore Store => _store;
    public World World => _world;

    // Authored damping, kept so SetJiggleDamping sets rather than compounds.
    private float[]? _authoredLinDamp;
    private float[]? _authoredAngDamp;

    public MMDPhysics(IReadOnlyList<RigidBodyDef> rigidbodies, IReadOnlyList<JointDef>? joints = null)
    {
        _joints = joints is { Count: > 0 } ? joints.ToArray() : Array.Empty<JointDef>();
        // The floor: a huge static box whose top face is model-space y = 0, so hair
        // and hems rest on the ground instead of clipping through when a pose
        // reaches it. Boneless (every bone-sync loop skips boneIndex < 0); collides
        // with EVERY dynamic body regardless of group masks via findContacts'
        // dedicated plane pass (spheres, capsules and boxes — box hems included).
        //
        // It is MODEL SPACE, which is the whole reason setFloor exists: y = 0 is
        // wherever this figure's own origin is, not where the scene's ground is. A
        // character standing on a stage, hanging in the air, or carried up by root
        // motion takes her floor with her, and hair that should fall past her feet
        // piles on nothing instead.
        List<RigidBodyDef> all = new List<RigidBodyDef>(rigidbodies) { RigidBodyDef.CreateGround() };
        _store = new RigidBodyStore(all);
        int gi = _store.Count - 1;
        // Mask 0 keeps the floor OUT of the generic pair list — findContacts runs a
        // dedicated plane pass against every dynamic body (all shapes, boxes too).
        _store.CollisionGroup[gi] = 0;
        _store.WillCollideMask[gi] = 0;
        _groundBody = gi;
        _store.GroundIndex = gi;
        _world = new World(new System.Numerics.Vector3(0, -98, 0));
        _constraints = ConstraintBuilder.BuildConstraints(rigidbodies, _joints);
        _solverCache = new SolverCache(_constraints);
        _contacts = new ContactPool();
        int n = _store.Count;
        _prevPositions = new float[n * 3];
        _prevOrientations = new float[n * 4];
        _kinTargetPos = new float[n * 3];
        _kinTargetOri = new float[n * 4];
        _kinTargetVel = new float[n * 3];
        _kinTargetAngVel = new float[n * 3];
        _kinRoot = BuildKinematicRoots();
        _teleportFlags = new byte[n];

        _alignPinned = new byte[n];
        byte[] types = _store.Type;
        foreach (SixDofSpringConstraint c in _constraints)
        {
            byte tA = types[c.BodyA];
            byte tB = types[c.BodyB];
            bool aFollows = tA == (byte)RigidbodyType.Static || tA == (byte)RigidbodyType.Kinematic;
            bool bFollows = tB == (byte)RigidbodyType.Static || tB == (byte)RigidbodyType.Kinematic;
            if (aFollows && _store.Aligned[c.BodyB] != 0) _alignPinned[c.BodyB] = 1;
            if (bFollows && _store.Aligned[c.BodyA] != 0) _alignPinned[c.BodyA] = 1;
        }
    }

    // BFS over the joint graph from every kinematic body, assigning each
    // reachable dynamic body the kinematic body it (transitively) hangs off.
    private int[] BuildKinematicRoots()
    {
        int n = _store.Count;
        int[] root = new int[n];
        Array.Fill(root, -1);
        byte[] types = _store.Type;
        List<int>[] adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (SixDofSpringConstraint c in _constraints)
        {
            adj[c.BodyA].Add(c.BodyB);
            adj[c.BodyB].Add(c.BodyA);
        }
        List<int> queue = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (types[i] == (byte)RigidbodyType.Static || types[i] == (byte)RigidbodyType.Kinematic)
            {
                root[i] = i;
                queue.Add(i);
            }
        }
        for (int h = 0; h < queue.Count; h++)
        {
            int i = queue[h];
            foreach (int j in adj[i])
            {
                if (root[j] != -1) continue;
                root[j] = root[i];
                queue.Add(j);
            }
        }
        return root;
    }

    // Snapshot the current body pose as the interpolation "previous" state.
    private void SavePrevState()
    {
        Array.Copy(_store.Positions, _prevPositions, _prevPositions.Length);
        Array.Copy(_store.Orientations, _prevOrientations, _prevOrientations.Length);
    }

    /// <summary>
    /// Whether the built-in floor collides at all.
    ///
    /// The body stays in the store either way: it is static, boneless and outside
    /// the pair list, so an idle one costs nothing, and keeping it means turning
    /// the floor back on cannot disturb any index the solver caches. What moves is
    /// <see cref="RigidBodyStore.GroundIndex"/>, which is what findContacts reads
    /// to decide whether to run the plane pass.
    /// </summary>
    public void SetFloor(bool on)
    {
        _store.GroundIndex = on ? _groundBody : -1;
    }

    /// <summary>
    /// Let the bodies carrying these bones swing longer, by damping them less.
    ///
    /// DAMPING, and not solver iterations, and the difference is the whole point.
    /// Under-converging a joint does make it swing further — it also stops it ever
    /// reaching equilibrium, so the body hangs visibly low at rest. Sag and swing
    /// come as a pair there and no amount of tuning separates them. Damping does
    /// separate them: for m·x″ + c·x′ + k·x = mg the rest position is mg/k, which
    /// c does not appear in. Less damping is a longer, larger oscillation about
    /// exactly the same resting height.
    ///
    /// Scoped to the bones asked for, because it is a look and not a correction —
    /// rigs whose visible bones inherit from a simulated one (付与親) are authored
    /// against an MMD that lets them move more than a faithfully damped
    /// simulation does. Hair and skirt keep their authored damping.
    ///
    /// <paramref name="scale"/> multiplies the AUTHORED damping: 1 restores it,
    /// 0.5 halves it, 0 leaves the body undamped and ringing. Idempotent — the
    /// authored values are snapshotted on first use, so repeated calls set rather
    /// than compound.
    /// </summary>
    public void SetJiggleDamping(IReadOnlyList<int> boneIndices, float scale)
    {
        if (boneIndices.Count == 0) return;
        if (_authoredLinDamp == null || _authoredAngDamp == null)
        {
            _authoredLinDamp = new float[_store.Count];
            _authoredAngDamp = new float[_store.Count];
            Array.Copy(_store.LinearDamping, _authoredLinDamp, _store.Count);
            Array.Copy(_store.AngularDamping, _authoredAngDamp, _store.Count);
        }
        float s = Math.Clamp(scale, 0f, 1f);
        int[] boneOf = _store.BoneIndex;
        // 小集合线性扫（MMD 刚体量级 ~ 数百，Set 构造成本反而不划算；调用频率
        // 假定为场景配置级的偶尔调用）。
        for (int i = 0; i < _store.Count; i++)
        {
            int b = boneOf[i];
            if (b < 0) continue;
            bool wanted = false;
            for (int k = 0; k < boneIndices.Count; k++)
            {
                if (boneIndices[k] == b) { wanted = true; break; }
            }
            if (!wanted) continue;
            _store.LinearDamping[i] = _authoredLinDamp[i] * s;
            _store.AngularDamping[i] = _authoredAngDamp[i] * s;
        }
        // The factors are cached against dt, which has not changed.
        _world.InvalidateDampingCache();
    }

    /// <summary>
    /// Bones whose world matrix this simulation OVERWRITES each step.
    ///
    /// The same test WriteBack runs — a Dynamic body bound to a real bone —
    /// exposed because the pose pipeline has to know. PMX lets a bone
    /// inherit rotation from an 付与親 (append parent), and when that parent is
    /// simulated the inheritance has to consume the SIMULATED result, not the
    /// animated pose the frame started with. Nothing else can answer which bones
    /// those are: the mapping lives in this store.
    /// </summary>
    public List<int> GetPhysicsDrivenBones()
    {
        List<int> boneList = new List<int>();
        for (int i = 0; i < _store.Count; i++)
        {
            if (_store.Type[i] != (byte)RigidbodyType.Dynamic) continue;
            int b = _store.BoneIndex[i];
            if (b >= 0) boneList.Add(b);
        }
        return boneList;
    }

    // Snap dynamic bodies back to their bone-driven pose, zero velocities.
    // Used when the simulation diverged or the user scrubbed the timeline.
    // （reze 注释说的 dynamic 实为所有 bone-bound 体——循环本身不过滤 type。）
    private void SnapBodiesToBones(float[] boneWorldMatrices)
    {
        int n = _store.Count;
        float[] offsets = _store.BodyOffsetMatrix;
        float[] positions = _store.Positions;
        float[] orientations = _store.Orientations;
        float[] lv = _store.LinearVelocities;
        float[] av = _store.AngularVelocities;
        int[] boneIdx = _store.BoneIndex;
        int boneCount = boneWorldMatrices.Length / 16;

        for (int i = 0; i < n; i++)
        {
            int b = boneIdx[i];
            if (b < 0 || b >= boneCount) continue;

            Mat4.MultiplyArrays(boneWorldMatrices, b * 16, offsets, i * 16, BodyMat, 0);

            int i3 = i * 3;
            int i4 = i * 4;
            positions[i3 + 0] = BodyMat[12];
            positions[i3 + 1] = BodyMat[13];
            positions[i3 + 2] = BodyMat[14];
            Mat4.ToQuatInto(BodyMat, 0, ScratchQuat, 0);
            orientations[i4 + 0] = ScratchQuat[0];
            orientations[i4 + 1] = ScratchQuat[1];
            orientations[i4 + 2] = ScratchQuat[2];
            orientations[i4 + 3] = ScratchQuat[3];

            lv[i3 + 0] = 0;
            lv[i3 + 1] = 0;
            lv[i3 + 2] = 0;
            av[i3 + 0] = 0;
            av[i3 + 1] = 0;
            av[i3 + 2] = 0;
        }
    }

    /// <summary>
    /// Reset 三件事（snap / 清零 / reseed）。
    /// 乱序模拟后（时间轴拖动、发散）调用；首帧前调用是无操作（reze 同）。
    /// </summary>
    public void Reset(float[] boneWorldMatrices)
    {
        if (_firstFrame) return;
        SnapBodiesToBones(boneWorldMatrices);
        SavePrevState(); // prev == curr after a snap, so no interpolation jump
        // Reseed kinematic targets from the snapped pose — otherwise the next
        // step derives the target-trajectory velocity against the pre-reset
        // targets and feeds one frame of enormous anchor velocity to the solver.
        Array.Copy(_store.Positions, _kinTargetPos, _kinTargetPos.Length);
        Array.Copy(_store.Orientations, _kinTargetOri, _kinTargetOri.Length);
        Array.Clear(_kinTargetVel, 0, _kinTargetVel.Length);
        Array.Clear(_kinTargetAngVel, 0, _kinTargetAngVel.Length);
    }

    /// <summary>
    /// 引擎主入口：物理开关 gate + tick 时钟 + 三相位，一次调用完成一渲染帧的物理段。
    /// 调用方在 FK/IK/付与（<c>SkeletalModel.UpdateWorldMatrices</c>）之后调用。
    ///
    /// 物理语义：OFF = 跳过整个物理段，骨骼世界矩阵保持动画结果
    /// （本方法不碰 boneWorldMatrices）；OFF→ON 边沿强制 <see cref="Reset"/>
    /// （snap 当前骨骼姿态 + 速度清零 + tick 基准同步，首帧无跳变）；ON→OFF
    /// 无操作（物理体留在原地，骨骼自然回动画姿态）。开关状态属场景配置，
    ///
    /// tick 时钟：<c>tickTarget = floor(frame × k)</c>（k=<see cref="TickRateMultiplier"/>），
    /// 本帧前进 advance = tickTarget − 上帧 tickTarget 个固定 tick；alpha =
    /// frame × k 的小数部分（Fix Your Timestep 的渲染插值相位）。advance ≤ 0
    /// （暂停/回卷）不推进模拟——回卷的骨骼突变由 teleport 检测兜底（carry/snap）。
    /// </summary>
    /// <param name="enabled">物理总开关（场景配置）。</param>
    /// <param name="boneWorldMatrices">骨骼世界矩阵（列主序 float[16×骨数]，FK/IK/付与结果）。</param>
    /// <param name="boneInverseBindMatrices">骨骼逆绑定矩阵（同布局；仅首帧初始化用）。</param>
    /// <param name="frame">当前连续动画帧号（<c>MmdAnimationPlayer.CurrentFrame</c>）。</param>
    /// <param name="prevFrame">上一渲染帧的动画帧号。</param>
    public void Update(bool enabled, float[] boneWorldMatrices, float[] boneInverseBindMatrices, double frame, double prevFrame)
    {
        if (!enabled)
        {
            _wasEnabled = false;
            return;
        }

        double frameK = frame * TickRateMultiplier;
        int tickTarget = (int)System.Math.Floor(frameK + 1e-4);
        // 1e-4 吸收 RealTime 连续游标的 double 累积误差（帧号域真差异 ≥ 0.5 帧），
        // 差值可能微负 → clamp 到 [0,1)。
        float alpha = (float)(frameK - System.Math.Floor(frameK + 1e-4));
        if (alpha < 0) alpha = 0;
        else if (alpha >= 1f) alpha = 0.999999f;

        if (!_wasEnabled)
        {
            // OFF→ON：snap 到当前骨骼姿态并同步 tick 基准，使 ON 首帧
            // advance=0（只 snap 不模拟）——写回 == snap 姿态 == 动画，无跳变。
            Reset(boneWorldMatrices);
            _lastTickTarget = tickTarget;
            _wasEnabled = true;
        }

        int advance = tickTarget - _lastTickTarget;
        _lastTickTarget = tickTarget;

        // teleport 阈值按渲染帧动画时长（：250 units/s 按帧时间缩放）。
        float dtAnim = (float)((frame - prevFrame) / PlaybackFps);
        if (dtAnim < 0) dtAnim = 0;

        // 目标采样到本帧末 tick 时刻（帧号 tickTarget/k）：target 成为帧号的
        // 纯函数，渲染率只影响采样密度、不影响 tick 时刻的采样值。
        float boneSampleT = 1f;
        if (advance > 0 && _hasPrevBone && frame > prevFrame && _prevBoneWorld.Length == boneWorldMatrices.Length)
        {
            double tickEndFrame = tickTarget / (double)TickRateMultiplier;
            double s = (tickEndFrame - prevFrame) / (frame - prevFrame);
            boneSampleT = s < 0 ? 0 : (s > 1 ? 1 : (float)s);
        }

        RefreshKinematicTargets(boneWorldMatrices, boneInverseBindMatrices,
            dtAnim, advance * TickDuration, boneSampleT, advance > 0);
        Step(advance);
        WriteBack(boneWorldMatrices, alpha);

        // 帧末快照骨骼矩阵，供下一渲染帧的 tick 域采样。
        if (_prevBoneWorld.Length != boneWorldMatrices.Length)
        {
            _prevBoneWorld = new float[boneWorldMatrices.Length];
            _boneSample = new float[boneWorldMatrices.Length];
        }
        Array.Copy(boneWorldMatrices, _prevBoneWorld, boneWorldMatrices.Length);
        _hasPrevBone = true;
    }

    /// <summary>
    /// 宿主矩阵转换：SkeletalModel 的 <c>WorldMatrices</c>/<c>InverseBind</c> 是
    /// System.Numerics 行主序（v·M，平移在 M41..43），物理内核是列主序（M·v，
    /// 平移在 m[12..14]）。两者是**同一个变换的转置表示**：行主序存 Tᵀ 的线性
    /// 序列 == 列主序存 T 的线性序列，因此正确转换就是 16 float 线性直拷。
    /// 切不可再按下标重排——那会对已转置的存储再转一次，
    /// 内核收到 Mᵀ：平移丢失进 w、旋转反向，蒙皮炸成薄片。
    /// </summary>
    public static void CopyMatricesToColumnMajor(System.Numerics.Matrix4x4[] source, float[] destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            ref System.Numerics.Matrix4x4 m = ref source[i];
            int o = i * 16;
            destination[o + 0] = m.M11; destination[o + 1] = m.M12; destination[o + 2] = m.M13; destination[o + 3] = m.M14;
            destination[o + 4] = m.M21; destination[o + 5] = m.M22; destination[o + 6] = m.M23; destination[o + 7] = m.M24;
            destination[o + 8] = m.M31; destination[o + 9] = m.M32; destination[o + 10] = m.M33; destination[o + 11] = m.M34;
            destination[o + 12] = m.M41; destination[o + 13] = m.M42; destination[o + 14] = m.M43; destination[o + 15] = m.M44;
        }
    }

    /// <summary>
    /// <see cref="CopyMatricesToColumnMajor"/> 的逆：内核写回的列主序骨骼矩阵 →
    /// 宿主行主序 <see cref="System.Numerics.Matrix4x4"/>（渲染层用它替换
    /// <c>WorldMatrices</c> 后重算蒙皮）。同为线性直拷，见上函数注释。
    /// </summary>
    public static void CopyColumnMajorToMatrices(float[] source, System.Numerics.Matrix4x4[] destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            int o = i * 16;
            destination[i] = new System.Numerics.Matrix4x4(
                source[o + 0], source[o + 1], source[o + 2], source[o + 3],
                source[o + 4], source[o + 5], source[o + 6], source[o + 7],
                source[o + 8], source[o + 9], source[o + 10], source[o + 11],
                source[o + 12], source[o + 13], source[o + 14], source[o + 15]);
        }
    }

    /// <summary>
    /// 相位 1：从当前骨骼姿态计算本帧 kinematic 目标，检测 teleport 并执行
    /// carry/snap。返回是否发生 teleport（teleport 计数见 <see cref="TeleportCount"/>）。
    /// 首帧自动完成 reze firstFrame 初始化（computeBoneOffsets + snap + seed）。
    /// dt 为动画域帧时间（teleport 阈值与目标轨迹速度都用它，
    /// 引擎主路径走 <see cref="Update"/>（tick 域采样）。
    /// </summary>
    public bool SetKinematicTargets(float[] boneWorldMatrices, float[] boneInverseBindMatrices, float dt)
    {
        return RefreshKinematicTargets(boneWorldMatrices, boneInverseBindMatrices, dt, dt, 1f, true);
    }

    /// <summary>
    /// kinematic 目标核心。<paramref name="velocityDt"/>：目标差分的分母（动画域秒）——
    /// tick 时钟下 = advance × TickDuration，与渲染率无关；
    /// <paramref name="dtThreshold"/>：teleport 阈值的帧时间（渲染帧动画时长）；
    /// <paramref name="boneSampleT"/>：骨骼采样因子——<1 时把目标表达到
    /// 「本帧末 tick 时刻」（lerp 上一渲染帧与当前渲染帧骨骼矩阵），使 target
    /// 成为帧号的纯函数；<paramref name="updateTargets"/>=false（tick 无进展的
    /// 渲染帧）只保证首帧初始化，不重算 target/velocity、不判 teleport。
    /// </summary>
    private bool RefreshKinematicTargets(
        float[] boneWorldMatrices, float[] boneInverseBindMatrices,
        float dtThreshold, float velocityDt, float boneSampleT, bool updateTargets)
    {
        if (_firstFrame)
        {
            _store.ComputeBoneOffsets(boneInverseBindMatrices);
            // Start at current bone pose, not the PMX bind pose, so animations
            // that skip frame 0 don't pop bodies on first step.
            SnapBodiesToBones(boneWorldMatrices);
            SavePrevState();
            // Seed kinematic targets so the first frame's target-delta velocity
            // is zero instead of a jump from the origin.
            Array.Copy(_store.Positions, _kinTargetPos, _kinTargetPos.Length);
            Array.Copy(_store.Orientations, _kinTargetOri, _kinTargetOri.Length);
            _firstFrame = false;
        }

        if (!updateTargets) return false;

        // Compute this frame's kinematic targets from the current bone pose. A
        // target jump beyond what continuous motion can produce (timeline scrub)
        // is a teleport, handled per kinematic root: rigidly carry that root's
        // dynamic chains across the jump with zeroed momentum, snap the root,
        // and keep simulating — dragging cloth through a discontinuity at the
        // raw derived velocity is what used to explode the solver. Chains under
        // unaffected roots keep their momentum untouched.
        float[] source = boneWorldMatrices;
        if (boneSampleT < 1f && _hasPrevBone && _prevBoneWorld.Length == boneWorldMatrices.Length)
        {
            SampleBonesAtTick(boneWorldMatrices, boneSampleT, boneWorldMatrices.Length / 16);
            source = _boneSample;
        }
        bool teleport = ComputeKinematicTargets(source, dtThreshold, velocityDt);
        if (teleport)
        {
            TeleportCount++;
            CarryDynamicThroughTeleport();
            SnapKinematicToTargets(true);
            SavePrevState(); // prev == curr so interpolation doesn't streak
        }
        return teleport;
    }

    // 把骨骼世界矩阵插值到帧号域的 tick 时刻（prevBone→cur 的 s 处），写入
    // _boneSample。位置线性 lerp、旋转最短弧 nlerp——帧内骨骼运动小，nlerp 与
    // 精确 slerp 的差远小于渲染一帧的骨骼运动，而两跑（双模式）走同一条近似
    // 路径，确定性不受影响。
    private void SampleBonesAtTick(float[] cur, float s, int boneCount)
    {
        for (int b = 0; b < boneCount; b++)
        {
            int o = b * 16;
            Mat4.ToQuatInto(_prevBoneWorld, o, SampleQa, 0);
            Mat4.ToQuatInto(cur, o, SampleQb, 0);
            float dot =
                SampleQa[0] * SampleQb[0] + SampleQa[1] * SampleQb[1] +
                SampleQa[2] * SampleQb[2] + SampleQa[3] * SampleQb[3];
            if (dot < 0)
            {
                SampleQb[0] = -SampleQb[0]; SampleQb[1] = -SampleQb[1];
                SampleQb[2] = -SampleQb[2]; SampleQb[3] = -SampleQb[3];
            }
            float qx = SampleQa[0] + (SampleQb[0] - SampleQa[0]) * s;
            float qy = SampleQa[1] + (SampleQb[1] - SampleQa[1]) * s;
            float qz = SampleQa[2] + (SampleQb[2] - SampleQa[2]) * s;
            float qw = SampleQa[3] + (SampleQb[3] - SampleQa[3]) * s;
            float len2 = qx * qx + qy * qy + qz * qz + qw * qw;
            if (len2 > 1e-12f)
            {
                float inv = 1f / MathF.Sqrt(len2);
                qx *= inv; qy *= inv; qz *= inv; qw *= inv;
            }
            float tx = _prevBoneWorld[o + 12] + (cur[o + 12] - _prevBoneWorld[o + 12]) * s;
            float ty = _prevBoneWorld[o + 13] + (cur[o + 13] - _prevBoneWorld[o + 13]) * s;
            float tz = _prevBoneWorld[o + 14] + (cur[o + 14] - _prevBoneWorld[o + 14]) * s;
            Mat4.FromPositionRotationInto(tx, ty, tz, qx, qy, qz, qw, _boneSample, o);
        }
    }

    // Fill kinTargetPos/kinTargetOri = boneWorld × bodyOffset for every bone-
    // bound Static/Kinematic body. Returns true if any target is discontinuous
    // with the current body pose — farther than continuous motion can carry it
    // in one render frame, or rotated more than 90°.
    private bool ComputeKinematicTargets(float[] boneWorldMatrices, float dt, float velocityDt)
    {
        int n = _store.Count;
        float[] offsets = _store.BodyOffsetMatrix;
        float[] positions = _store.Positions;
        float[] orientations = _store.Orientations;
        byte[] types = _store.Type;
        int[] boneIdx = _store.BoneIndex;
        float[] tp = _kinTargetPos;
        float[] to = _kinTargetOri;
        int boneCount = boneWorldMatrices.Length / 16;

        // 250 units/s scaled by frame time, floored at 4 units: far above the
        // fastest limb motion (a false positive freezes that chain's momentum
        // for a frame, which reads as stutter), far below any real scrub jump.
        // dt 是动画域帧时间（帧号差 × 1/PlaybackFps），见类头注释。
        float maxJump = MathF.Max(4f, 250f * dt);
        float maxJumpSq = maxJump * maxJump;
        byte[] flags = _teleportFlags;
        bool teleport = false;

        float[] tv = _kinTargetVel;
        float[] tav = _kinTargetAngVel;
        float invDt = velocityDt > 0 ? 1f / velocityDt : 0;

        for (int i = 0; i < n; i++)
        {
            flags[i] = 0;
            byte t = types[i];
            if (t != (byte)RigidbodyType.Static && t != (byte)RigidbodyType.Kinematic) continue;
            int b = boneIdx[i];
            if (b < 0 || b >= boneCount) continue;

            Mat4.MultiplyArrays(boneWorldMatrices, b * 16, offsets, i * 16, BodyMat, 0);

            int i3 = i * 3;
            int i4 = i * 4;
            // Previous frame's target — the reference for the trajectory velocity.
            float oldTx = tp[i3 + 0], oldTy = tp[i3 + 1], oldTz = tp[i3 + 2];
            float oldOx = to[i4 + 0], oldOy = to[i4 + 1], oldOz = to[i4 + 2], oldOw = to[i4 + 3];

            tp[i3 + 0] = BodyMat[12];
            tp[i3 + 1] = BodyMat[13];
            tp[i3 + 2] = BodyMat[14];
            Mat4.ToQuatInto(BodyMat, 0, ScratchQuat, 0);
            float nOx = ScratchQuat[0], nOy = ScratchQuat[1], nOz = ScratchQuat[2], nOw = ScratchQuat[3];
            to[i4 + 0] = nOx;
            to[i4 + 1] = nOy;
            to[i4 + 2] = nOz;
            to[i4 + 3] = nOw;

            float dx = tp[i3 + 0] - positions[i3 + 0];
            float dy = tp[i3 + 1] - positions[i3 + 1];
            float dz = tp[i3 + 2] - positions[i3 + 2];
            // |q·q'| = cos(θ/2); below cos(45°) the body turned more than 90°.
            float dot =
                to[i4 + 0] * orientations[i4 + 0] +
                to[i4 + 1] * orientations[i4 + 1] +
                to[i4 + 2] * orientations[i4 + 2] +
                to[i4 + 3] * orientations[i4 + 3];
            if (dx * dx + dy * dy + dz * dz > maxJumpSq || MathF.Abs(dot) < 0.7071f)
            {
                flags[i] = 1;
                teleport = true;
                tv[i3 + 0] = 0; tv[i3 + 1] = 0; tv[i3 + 2] = 0;
                tav[i3 + 0] = 0; tav[i3 + 1] = 0; tav[i3 + 2] = 0;
                continue;
            }

            tv[i3 + 0] = (tp[i3 + 0] - oldTx) * invDt;
            tv[i3 + 1] = (tp[i3 + 1] - oldTy) * invDt;
            tv[i3 + 2] = (tp[i3 + 2] - oldTz) * invDt;
            // ω ≈ 2 · qDiff.xyz / dt with qDiff = qNew · conj(qOld). Shortest-arc
            // sign keeps qDiff and −qDiff (same rotation) from doubling ω.
            float cox = -oldOx, coy = -oldOy, coz = -oldOz, cow = oldOw;
            float qdx = nOw * cox + nOx * cow + nOy * coz - nOz * coy;
            float qdy = nOw * coy - nOx * coz + nOy * cow + nOz * cox;
            float qdz = nOw * coz + nOx * coy - nOy * cox + nOz * cow;
            float qdw = nOw * cow - nOx * cox - nOy * coy - nOz * coz;
            float sign = qdw < 0 ? -1f : 1f;
            tav[i3 + 0] = 2 * sign * qdx * invDt;
            tav[i3 + 1] = 2 * sign * qdy * invDt;
            tav[i3 + 2] = 2 * sign * qdz * invDt;
        }
        return teleport;
    }

    /// <summary>
    /// 相位 2：tick 子步循环（B6 tick 时钟）。<paramref name="tickAdvance"/> =
    /// 本渲染帧 tickTarget 差值；每个 tick 固定 <see cref="TickDuration"/> 秒，
    /// 上限 <see cref="MaxCatchUp"/>，超限丢余量并 snap kinematic 到目标
    /// （reze 的 backlog 分支）。tick 数由帧号唯一决定 ⇒ 同帧号双跑逐位一致。
    /// </summary>
    public void Step(int tickAdvance)
    {
        if (tickAdvance <= 0) return;
        // Fixed-timestep substeps. The maxSubSteps cap prevents runaway after
        // a long stall (tab backgrounded, etc.). Snapshot the pose before each step so
        // after the loop prevState is one substep behind the live (current) state.
        // Kinematic bodies split the remaining gap evenly across this frame's
        // substeps (f = 1/substeps-left) and land exactly on the frame's bone
        // pose by the last one: at 60 Hz render that reproduces the classic
        // sync-once-per-frame behavior exactly (no fractional lag trembling
        // against the rendered mesh), while at lower rates the per-substep
        // constraint error stays bounded to one fixed step of bone motion.
        int nSub = tickAdvance > MaxCatchUp ? MaxCatchUp : tickAdvance;
        float tickDt = TickDuration;
        for (int k = 0; k < nSub; k++)
        {
            SavePrevState();
            AdvanceKinematicToTargets(1f / (nSub - k));
            _world.Step(_store, tickDt, _contacts, _constraints, _solverCache);
            RestoreNonFiniteBodies();
        }
        if (tickAdvance > MaxCatchUp)
        {
            // Substep budget exhausted mid-catchup: drop the remaining time and
            // snap kinematic bodies the rest of the way so they don't start next
            // frame lagging behind their bones. They keep the velocity their
            // trajectory implies — the character did move, and cloth in contact
            // needs to know that or it stops being dragged and starts juddering.
            SnapKinematicToTargets(false, true);
        }
    }

    // Move kinematic bodies fraction f of the way to the frame target with the
    // target-trajectory velocity, so joints see continuous anchor motion (and a
    // smooth velocity signal) instead of a frame-sized jump on substep 1.
    private void AdvanceKinematicToTargets(float f)
    {
        int n = _store.Count;
        float[] positions = _store.Positions;
        float[] orientations = _store.Orientations;
        float[] lv = _store.LinearVelocities;
        float[] av = _store.AngularVelocities;
        byte[] types = _store.Type;
        int[] boneIdx = _store.BoneIndex;
        float[] tp = _kinTargetPos;
        float[] to = _kinTargetOri;
        float[] tv = _kinTargetVel;
        float[] tav = _kinTargetAngVel;

        for (int i = 0; i < n; i++)
        {
            byte t = types[i];
            if (t != (byte)RigidbodyType.Static && t != (byte)RigidbodyType.Kinematic) continue;
            if (boneIdx[i] < 0) continue;

            int i3 = i * 3;
            int i4 = i * 4;
            positions[i3 + 0] += (tp[i3 + 0] - positions[i3 + 0]) * f;
            positions[i3 + 1] += (tp[i3 + 1] - positions[i3 + 1]) * f;
            positions[i3 + 2] += (tp[i3 + 2] - positions[i3 + 2]) * f;
            lv[i3 + 0] = tv[i3 + 0];
            lv[i3 + 1] = tv[i3 + 1];
            lv[i3 + 2] = tv[i3 + 2];
            av[i3 + 0] = tav[i3 + 0];
            av[i3 + 1] = tav[i3 + 1];
            av[i3 + 2] = tav[i3 + 2];

            // Shortest-arc nlerp toward the target orientation.
            float oldOx = orientations[i4 + 0], oldOy = orientations[i4 + 1];
            float oldOz = orientations[i4 + 2], oldOw = orientations[i4 + 3];
            float tx = to[i4 + 0], ty = to[i4 + 1], tz = to[i4 + 2], tw = to[i4 + 3];
            if (oldOx * tx + oldOy * ty + oldOz * tz + oldOw * tw < 0)
            {
                tx = -tx; ty = -ty; tz = -tz; tw = -tw;
            }
            float newOx = oldOx + (tx - oldOx) * f;
            float newOy = oldOy + (ty - oldOy) * f;
            float newOz = oldOz + (tz - oldOz) * f;
            float newOw = oldOw + (tw - oldOw) * f;
            float len2 = newOx * newOx + newOy * newOy + newOz * newOz + newOw * newOw;
            if (len2 > 1e-12f)
            {
                float inv = 1f / MathF.Sqrt(len2);
                newOx *= inv; newOy *= inv; newOz *= inv; newOw *= inv;
            }
            else
            {
                newOx = tx; newOy = ty; newOz = tz; newOw = tw;
            }
            orientations[i4 + 0] = newOx;
            orientations[i4 + 1] = newOy;
            orientations[i4 + 2] = newOz;
            orientations[i4 + 3] = newOw;
        }
    }

    // Snap kinematic bodies straight to the frame target with zero velocity.
    // Used on teleports (onlyFlagged) and when the substep budget runs out
    // mid-catchup (all).
    //
    // keepTrajectoryVelocity decides what the cloth is then told about how
    // those bodies got there, and the two callers want opposite answers.
    //
    // After a teleport the honest answer is "nothing" — a scrub is not motion
    // and handing the solver the implied velocity is what used to fling cloth
    // across the discontinuity.
    //
    // After the substep budget is exhausted it is the opposite. The body really
    // did travel along its animated trajectory this frame; the simulation just
    // could not afford to walk it there in steps. Zeroing there tells every
    // dynamic body in contact that the character stopped dead, so cloth loses
    // its drag and is teleported by the body instead of dragged by it — it stops
    // flowing and starts juddering. That branch only fires on a device already
    // running under its refresh rate, which is precisely where the simulation
    // can least afford to also look broken. The trajectory velocity is already
    // computed every frame in kinTargetVel; it just needs to survive.
    private void SnapKinematicToTargets(bool onlyFlagged, bool keepTrajectoryVelocity = false)
    {
        int n = _store.Count;
        float[] positions = _store.Positions;
        float[] orientations = _store.Orientations;
        float[] lv = _store.LinearVelocities;
        float[] av = _store.AngularVelocities;
        byte[] types = _store.Type;
        int[] boneIdx = _store.BoneIndex;
        float[] tp = _kinTargetPos;
        float[] to = _kinTargetOri;
        float[] tv = _kinTargetVel;
        float[] tav = _kinTargetAngVel;
        byte[] flags = _teleportFlags;

        for (int i = 0; i < n; i++)
        {
            byte t = types[i];
            if (t != (byte)RigidbodyType.Static && t != (byte)RigidbodyType.Kinematic) continue;
            if (boneIdx[i] < 0) continue;
            if (onlyFlagged && flags[i] == 0) continue;
            int i3 = i * 3;
            int i4 = i * 4;
            positions[i3 + 0] = tp[i3 + 0];
            positions[i3 + 1] = tp[i3 + 1];
            positions[i3 + 2] = tp[i3 + 2];
            orientations[i4 + 0] = to[i4 + 0];
            orientations[i4 + 1] = to[i4 + 1];
            orientations[i4 + 2] = to[i4 + 2];
            orientations[i4 + 3] = to[i4 + 3];
            if (keepTrajectoryVelocity)
            {
                lv[i3 + 0] = tv[i3 + 0]; lv[i3 + 1] = tv[i3 + 1]; lv[i3 + 2] = tv[i3 + 2];
                av[i3 + 0] = tav[i3 + 0]; av[i3 + 1] = tav[i3 + 1]; av[i3 + 2] = tav[i3 + 2];
            }
            else
            {
                lv[i3 + 0] = 0; lv[i3 + 1] = 0; lv[i3 + 2] = 0;
                av[i3 + 0] = 0; av[i3 + 1] = 0; av[i3 + 2] = 0;
            }
        }
    }

    // Rigidly carry each dynamic body whose kinematic root teleported along
    // with that root's current→target transform delta (velocity zeroed),
    // preserving the cloth pose relative to the character across the jump.
    // Must run before snapKinematicToTargets (it reads the pre-snap pose).
    private void CarryDynamicThroughTeleport()
    {
        int n = _store.Count;
        float[] positions = _store.Positions;
        float[] orientations = _store.Orientations;
        float[] lv = _store.LinearVelocities;
        float[] av = _store.AngularVelocities;
        byte[] types = _store.Type;
        float[] tp = _kinTargetPos;
        float[] to = _kinTargetOri;
        int[] root = _kinRoot;
        byte[] flags = _teleportFlags;

        for (int i = 0; i < n; i++)
        {
            if (types[i] != (byte)RigidbodyType.Dynamic) continue;
            int k = root[i];
            if (k < 0 || flags[k] == 0 || _store.BoneIndex[k] < 0) continue;
            int k3 = k * 3;
            int k4 = k * 4;
            // Root delta rotation R = qTarget · conj(qCurrent).
            float cx = -orientations[k4 + 0], cy = -orientations[k4 + 1],
                  cz = -orientations[k4 + 2], cw = orientations[k4 + 3];
            float txq = to[k4 + 0], tyq = to[k4 + 1], tzq = to[k4 + 2], twq = to[k4 + 3];
            float rx = twq * cx + txq * cw + tyq * cz - tzq * cy;
            float ry = twq * cy - txq * cz + tyq * cw + tzq * cx;
            float rz = twq * cz + txq * cy - tyq * cx + tzq * cw;
            float rw = twq * cw - txq * cx - tyq * cy - tzq * cz;

            int i3 = i * 3;
            int i4 = i * 4;
            // Position: rotate the offset from the root by R, re-anchor at target.
            float ox = positions[i3 + 0] - positions[k3 + 0];
            float oy = positions[i3 + 1] - positions[k3 + 1];
            float oz = positions[i3 + 2] - positions[k3 + 2];
            // v' = v + 2·rw·(r × v) + 2·(r × (r × v))
            float c1x = ry * oz - rz * oy;
            float c1y = rz * ox - rx * oz;
            float c1z = rx * oy - ry * ox;
            float c2x = ry * c1z - rz * c1y;
            float c2y = rz * c1x - rx * c1z;
            float c2z = rx * c1y - ry * c1x;
            positions[i3 + 0] = tp[k3 + 0] + ox + 2 * (rw * c1x + c2x);
            positions[i3 + 1] = tp[k3 + 1] + oy + 2 * (rw * c1y + c2y);
            positions[i3 + 2] = tp[k3 + 2] + oz + 2 * (rw * c1z + c2z);

            // Orientation: q' = R · q, renormalized.
            float qx = orientations[i4 + 0], qy = orientations[i4 + 1],
                  qz = orientations[i4 + 2], qw = orientations[i4 + 3];
            float nx = rw * qx + rx * qw + ry * qz - rz * qy;
            float ny = rw * qy - rx * qz + ry * qw + rz * qx;
            float nz = rw * qz + rx * qy - ry * qx + rz * qw;
            float nw = rw * qw - rx * qx - ry * qy - rz * qz;
            float len2 = nx * nx + ny * ny + nz * nz + nw * nw;
            if (len2 > 1e-12f)
            {
                float inv = 1f / MathF.Sqrt(len2);
                nx *= inv; ny *= inv; nz *= inv; nw *= inv;
                orientations[i4 + 0] = nx;
                orientations[i4 + 1] = ny;
                orientations[i4 + 2] = nz;
                orientations[i4 + 3] = nw;
            }

            // Momentum doesn't carry across a discontinuity.
            lv[i3 + 0] = 0; lv[i3 + 1] = 0; lv[i3 + 2] = 0;
            av[i3 + 0] = 0; av[i3 + 1] = 0; av[i3 + 2] = 0;
        }
    }

    // Overwrite pinned mode-2 bodies' positions with boneWorld × bodyOffset
    // from the animated bone pose, keeping simulated orientation and
    // velocities. prevPositions follows so interpolation doesn't streak.
    private void AlignPinnedBodiesToBones(float[] boneWorldMatrices)
    {
        int n = _store.Count;
        byte[] pinned = _alignPinned;
        int[] boneIdx = _store.BoneIndex;
        float[] offsets = _store.BodyOffsetMatrix;
        float[] positions = _store.Positions;
        float[] prevPos = _prevPositions;
        int boneCount = boneWorldMatrices.Length / 16;

        for (int i = 0; i < n; i++)
        {
            if (pinned[i] == 0) continue;
            int b = boneIdx[i];
            if (b < 0 || b >= boneCount) continue;
            Mat4.MultiplyArrays(boneWorldMatrices, b * 16, offsets, i * 16, BodyMat, 0);
            int i3 = i * 3;
            positions[i3 + 0] = BodyMat[12];
            positions[i3 + 1] = BodyMat[13];
            positions[i3 + 2] = BodyMat[14];
            prevPos[i3 + 0] = BodyMat[12];
            prevPos[i3 + 1] = BodyMat[13];
            prevPos[i3 + 2] = BodyMat[14];
        }
    }

    // Backstop: if a dynamic body's state went non-finite despite the velocity
    // caps, restore its previous-substep pose with zero velocity instead of
    // letting NaNs spread through constraints and contacts. Runs after every
    // substep so prevState is always a finite pose.
    private void RestoreNonFiniteBodies()
    {
        int n = _store.Count;
        float[] positions = _store.Positions;
        float[] orientations = _store.Orientations;
        float[] lv = _store.LinearVelocities;
        float[] av = _store.AngularVelocities;
        byte[] types = _store.Type;
        float[] prevPos = _prevPositions;
        float[] prevOri = _prevOrientations;

        for (int i = 0; i < n; i++)
        {
            if (types[i] != (byte)RigidbodyType.Dynamic) continue;
            int i3 = i * 3;
            int i4 = i * 4;
            // NaN/Inf propagates through the sum, so one check covers all 13 slots.
            float s =
                positions[i3 + 0] + positions[i3 + 1] + positions[i3 + 2] +
                orientations[i4 + 0] + orientations[i4 + 1] + orientations[i4 + 2] + orientations[i4 + 3] +
                lv[i3 + 0] + lv[i3 + 1] + lv[i3 + 2] +
                av[i3 + 0] + av[i3 + 1] + av[i3 + 2];
            if (float.IsFinite(s)) continue;
            positions[i3 + 0] = prevPos[i3 + 0];
            positions[i3 + 1] = prevPos[i3 + 1];
            positions[i3 + 2] = prevPos[i3 + 2];
            orientations[i4 + 0] = prevOri[i4 + 0];
            orientations[i4 + 1] = prevOri[i4 + 1];
            orientations[i4 + 2] = prevOri[i4 + 2];
            orientations[i4 + 3] = prevOri[i4 + 3];
            lv[i3 + 0] = 0; lv[i3 + 1] = 0; lv[i3 + 2] = 0;
            av[i3 + 0] = 0; av[i3 + 1] = 0; av[i3 + 2] = 0;
        }
    }

    /// <summary>
    /// 相位 3：pinned 对齐 + 以渲染插值位姿把动态体写回骨骼矩阵
    /// （boneWorld = bodyWorld × bodyOffsetInverse）。
    /// alpha ∈ [0,1)（Fix Your Timestep）：tick 时钟下 = 帧号 × k 的小数部分
    /// （<see cref="Update"/> 计算），显示 lerp(上一 tick, 本帧末 tick, alpha)。
    /// </summary>
    public void WriteBack(float[] boneWorldMatrices, float alpha)
    {
        // MMD mode-2 bone alignment for pinned (depth-1) bodies: position
        // re-pins to the animated bone, rotation stays simulated.
        // （reze 在子步循环后、alpha 计算前执行；本方法只在子步循环后被调用，
        // 先 align 后写回与 reze 顺序等价。）
        AlignPinnedBodiesToBones(boneWorldMatrices);

        // Fraction into the next (not-yet-taken) step; always in [0, 1).
        ApplyDynamicsToBones(boneWorldMatrices, alpha);
    }

    private void ApplyDynamicsToBones(float[] boneWorldMatrices, float alpha)
    {
        int n = _store.Count;
        float[] inv = _store.BodyOffsetInverse;
        float[] positions = _store.Positions;
        float[] orientations = _store.Orientations;
        float[] prevPos = _prevPositions;
        float[] prevOri = _prevOrientations;
        byte[] types = _store.Type;
        int[] boneIdx = _store.BoneIndex;
        float oneMinus = 1 - alpha;
        int boneCount = boneWorldMatrices.Length / 16;

        for (int i = 0; i < n; i++)
        {
            if (types[i] != (byte)RigidbodyType.Dynamic) continue;
            int b = boneIdx[i];
            if (b < 0 || b >= boneCount) continue;

            int i3 = i * 3;
            int i4 = i * 4;

            // Position: straight lerp.
            float px = prevPos[i3 + 0] * oneMinus + positions[i3 + 0] * alpha;
            float py = prevPos[i3 + 1] * oneMinus + positions[i3 + 1] * alpha;
            float pz = prevPos[i3 + 2] * oneMinus + positions[i3 + 2] * alpha;

            // Orientation: shortest-arc nlerp (bodies rotate little per fixed step, so nlerp
            // tracks slerp closely and avoids the trig).
            float ax = prevOri[i4 + 0], ay = prevOri[i4 + 1], az = prevOri[i4 + 2], aw = prevOri[i4 + 3];
            float bx = orientations[i4 + 0], by = orientations[i4 + 1], bz = orientations[i4 + 2], bw = orientations[i4 + 3];
            if (ax * bx + ay * by + az * bz + aw * bw < 0)
            {
                bx = -bx; by = -by; bz = -bz; bw = -bw;
            }
            float qx = ax * oneMinus + bx * alpha;
            float qy = ay * oneMinus + by * alpha;
            float qz = az * oneMinus + bz * alpha;
            float qw = aw * oneMinus + bw * alpha;
            float ql = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (ql > 0)
            {
                float invL = 1 / ql;
                qx *= invL; qy *= invL; qz *= invL; qw *= invL;
            }
            else
            {
                qx = 0; qy = 0; qz = 0; qw = 1;
            }

            Mat4.FromPositionRotationInto(px, py, pz, qx, qy, qz, qw, BodyMat, 0);
            Mat4.MultiplyArrays(BodyMat, 0, inv, i * 16, BoneMat, 0);

            // Sanity gate against NaN / extreme values — silently drop the update.
            if (float.IsFinite(BoneMat[0]) && MathF.Abs(BoneMat[0]) < 1e6f)
            {
                int dst = b * 16;
                if (_alignPinned[i] != 0)
                {
                    // Pinned mode-2: the bone keeps its animated position, physics
                    // drives rotation only.
                    boneWorldMatrices[dst + 0] = BoneMat[0]; boneWorldMatrices[dst + 1] = BoneMat[1]; boneWorldMatrices[dst + 2] = BoneMat[2];
                    boneWorldMatrices[dst + 4] = BoneMat[4]; boneWorldMatrices[dst + 5] = BoneMat[5]; boneWorldMatrices[dst + 6] = BoneMat[6];
                    boneWorldMatrices[dst + 8] = BoneMat[8]; boneWorldMatrices[dst + 9] = BoneMat[9]; boneWorldMatrices[dst + 10] = BoneMat[10];
                }
                else
                {
                    Array.Copy(BoneMat, 0, boneWorldMatrices, dst, 16);
                }
            }
        }
    }
}

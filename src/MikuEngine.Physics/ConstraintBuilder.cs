using System.Numerics;
using MikuEngine.Core.Math;

namespace MikuEngine.Physics;

/// <summary>
/// 6DOF spring constraint trimmed to what MMD uses. Connects bodyA and bodyB
/// via local-space anchor frames; at simulate time the world frames are
/// TA = worldA · frameA, TB = worldB · frameB. The 6 DOFs are the linear
/// diff in TA's basis (axes 0..2) and the Euler-XYZ angular diff between
/// TA's and TB's basis (axes 3..5).
/// （对照 reze physics/constraint.ts SixDofSpringConstraint 逐字段移植）
///
/// Springs (when enabled) drive each DOF toward equilibriumPoint[i] with
/// stiffness[i]. Per-axis stop ERP is <see cref="ConstraintBuilder.StopErp"/>
/// — PMX joint limits are tuned against this softness.
/// </summary>
public sealed class SixDofSpringConstraint
{
    public int BodyA;
    public int BodyB;

    /// <summary>Local 4x4 (column-major) anchor frames on each body.</summary>
    public readonly float[] FrameA = new float[16];
    public readonly float[] FrameB = new float[16];

    /// <summary>
    /// Per-axis limits. For each i: when min[i] &gt; max[i] the axis is free
    /// (Bullet's "free" convention); when min[i] == max[i] the axis is locked.
    /// </summary>
    public readonly float[] LinearMin = new float[3];
    public readonly float[] LinearMax = new float[3];
    /// <summary>弧度。</summary>
    public readonly float[] AngularMin = new float[3];
    public readonly float[] AngularMax = new float[3];

    /// <summary>Springs. length 6（0..2 线性、3..5 角度）。</summary>
    public readonly byte[] SpringEnabled = new byte[6];
    public readonly float[] SpringStiffness = new float[6];
    /// <summary>Baked at setup time（build 时全零：两 frame 在 bind pose 重合）。</summary>
    public readonly float[] EquilibriumPoint = new float[6];

    /// <summary>
    /// True for joints that close a cycle in the joint graph (e.g. the
    /// horizontal ring welds of cross-linked skirt lattices). Loop edges get
    /// reduced limit-correction rates in the solver — a cycle over-determines
    /// positions and full-rate corrections chatter (see LOOP_ERP_SCALE).
    /// </summary>
    public bool IsLoop;
}

/// <summary>
/// Build per-joint constraints from PMX data:
///   frameA = (bodyA_worldBind)^-1 · jointWorldBind
///   frameB = (bodyB_worldBind)^-1 · jointWorldBind
/// Equilibrium is zero on every axis (both frames coincide at bind pose).
/// （对照 reze physics/constraint.ts buildConstraints 逐行移植）
/// </summary>
public static class ConstraintBuilder
{
    /// <summary>
    /// Stop-limit ERP. PMX rigs are tuned against MMD's stiff limit response;
    /// lowering this makes cloth resting against its limits sink visibly deeper
    /// (equilibrium penetration scales with 1/ERP). Rest chatter at this
    /// stiffness was fixed at the source (spring double-drive, unilateral
    /// stops) — don't lower ERP to paper over jitter.
    /// </summary>
    public const float StopErp = 0.45f;

    public static SixDofSpringConstraint[] BuildConstraints(
        IReadOnlyList<RigidBodyDef> rigidbodies,
        IReadOnlyList<JointDef> joints)
    {
        List<SixDofSpringConstraint> output = new();
        float[] jointWorld = new float[16];
        float[] bodyWorld = new float[16];
        float[] bodyInv = new float[16];

        for (int j = 0; j < joints.Count; j++)
        {
            JointDef joint = joints[j];
            int a = joint.RigidbodyIndexA;
            int b = joint.RigidbodyIndexB;
            if (a < 0 || b < 0 || a >= rigidbodies.Count || b >= rigidbodies.Count) continue;
            if (a == b) continue;

            // jointWorldBind from PMX (Euler XYZ as written by saba reference).
            Quaternion jq = QuatMath.FromEuler(joint.Rotation.X, joint.Rotation.Y, joint.Rotation.Z);
            Mat4.FromPositionRotationInto(
                joint.Position.X, joint.Position.Y, joint.Position.Z,
                jq.X, jq.Y, jq.Z, jq.W,
                jointWorld, 0);

            SixDofSpringConstraint con = new();
            if (!BuildLocalFrame(rigidbodies[a], jointWorld, bodyWorld, bodyInv, con.FrameA)) continue;
            if (!BuildLocalFrame(rigidbodies[b], jointWorld, bodyWorld, bodyInv, con.FrameB)) continue;
            con.BodyA = a;
            con.BodyB = b;

            con.LinearMin[0] = joint.PositionMin.X;
            con.LinearMin[1] = joint.PositionMin.Y;
            con.LinearMin[2] = joint.PositionMin.Z;
            con.LinearMax[0] = joint.PositionMax.X;
            con.LinearMax[1] = joint.PositionMax.Y;
            con.LinearMax[2] = joint.PositionMax.Z;
            // Some PMX rigs encode "free" angular axes as ±π·N which wraps badly
            // in limit comparisons — normalize to [-π, π] up front.
            con.AngularMin[0] = NormalizeAngle(joint.RotationMin.X);
            con.AngularMin[1] = NormalizeAngle(joint.RotationMin.Y);
            con.AngularMin[2] = NormalizeAngle(joint.RotationMin.Z);
            con.AngularMax[0] = NormalizeAngle(joint.RotationMax.X);
            con.AngularMax[1] = NormalizeAngle(joint.RotationMax.Y);
            con.AngularMax[2] = NormalizeAngle(joint.RotationMax.Z);

            con.SpringStiffness[0] = joint.SpringPosition.X;
            con.SpringStiffness[1] = joint.SpringPosition.Y;
            con.SpringStiffness[2] = joint.SpringPosition.Z;
            con.SpringStiffness[3] = joint.SpringRotation.X;
            con.SpringStiffness[4] = joint.SpringRotation.Y;
            con.SpringStiffness[5] = joint.SpringRotation.Z;
            for (int i = 0; i < 6; i++) con.SpringEnabled[i] = con.SpringStiffness[i] != 0 ? (byte)1 : (byte)0;

            output.Add(con);
        }

        // Mark loop-closing joints via union-find over the joint graph. All
        // bone-follow bodies count as one "world" component (they're each anchored
        // to the skeleton), so a joint bridging two separately anchored subtrees
        // also closes a kinematic cycle. One edge per cycle gets softened, which
        // releases the over-determination regardless of which edge it is.
        int n = rigidbodies.Count;
        int[] parent = new int[n + 1];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int worldNode = n;
        for (int i = 0; i < n; i++)
        {
            if (rigidbodies[i].Type != RigidbodyType.Dynamic) parent[Find(parent, i)] = Find(parent, worldNode);
        }
        foreach (SixDofSpringConstraint con in output)
        {
            int ra = Find(parent, con.BodyA);
            int rb = Find(parent, con.BodyB);
            if (ra == rb) con.IsLoop = true;
            else parent[ra] = rb;
        }

        return output.ToArray();
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    /// <summary>frame = bodyWorldBind^-1 · jointWorld. False if bodyWorldBind is singular.</summary>
    private static bool BuildLocalFrame(
        RigidBodyDef rb,
        float[] jointWorld,
        float[] bodyWorld,
        float[] bodyInv,
        float[] output)
    {
        Quaternion q = QuatMath.FromEuler(rb.ShapeRotation.X, rb.ShapeRotation.Y, rb.ShapeRotation.Z);
        Mat4.FromPositionRotationInto(
            rb.ShapePosition.X, rb.ShapePosition.Y, rb.ShapePosition.Z,
            q.X, q.Y, q.Z, q.W,
            bodyWorld, 0);
        if (!Mat4.InverseInto(bodyWorld, bodyInv)) return false;
        Mat4.MultiplyArrays(bodyInv, 0, jointWorld, 0, output, 0);
        return true;
    }

    private static float NormalizeAngle(float a)
    {
        float twoPi = MathF.PI * 2;
        a %= twoPi;
        if (a < -MathF.PI) a += twoPi;
        else if (a > MathF.PI) a -= twoPi;
        return a;
    }
}

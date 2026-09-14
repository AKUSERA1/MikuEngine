using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Physics;

/// <summary>刚体形状。对照 reze physics/types.ts RigidbodyShape。</summary>
public enum RigidbodyShape : byte
{
    Sphere = 0,
    Box = 1,
    Capsule = 2,
}

/// <summary>
/// 刚体类型。对照 reze physics/types.ts RigidbodyType。
/// 注意：PMX mode 2 是 DYNAMIC（加载层映射为 Dynamic + Aligned=true），不要把
/// PMX 原始字节 1:1 映射到本枚举——那会冻住 mode-2 刚体。
/// </summary>
public enum RigidbodyType : byte
{
    /// <summary>跟随骨骼（锚点，PMX mode 0）。</summary>
    Static = 0,
    Dynamic = 1,
    /// <summary>
    /// reze 加载器已不再产出该值（mode 2 映射为 Dynamic）；保留成员是因为
    /// 物理步进仍以它询问"该刚体是否跟随骨骼"，宿主也可能直接设置。
    /// </summary>
    Kinematic = 2,
}

/// <summary>
/// 内核刚体定义，对照 reze physics/types.ts Rigidbody。
/// PMX 数据经 <see cref="FromPmx"/> 转换；shapePosition/shapeRotation 是 PMX bind 姿态
/// 的模型空间值（rotation 为弧度欧拉角）。
/// </summary>
public sealed class RigidBodyDef
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }
    public int BoneIndex { get; init; }
    /// <summary>PMX 碰撞组 0..15（store 内转单比特掩码）。</summary>
    public int Group { get; init; }
    /// <summary>16 位碰撞组掩码：本刚体与哪些组碰撞。</summary>
    public ushort CollisionMask { get; init; }
    public RigidbodyShape Shape { get; init; }
    /// <summary>形状尺寸（语义依形状：球半径 / 盒半长 / 胶囊半径+圆柱半长）。</summary>
    public Vector3 Size { get; init; }
    /// <summary>Bind pose 模型空间位置（PMX 原值）。</summary>
    public Vector3 ShapePosition { get; init; }
    /// <summary>Bind pose 模型空间旋转（弧度欧拉，PMX 原值）。</summary>
    public Vector3 ShapeRotation { get; init; }
    public float Mass { get; init; }
    public float LinearDamping { get; init; }
    public float AngularDamping { get; init; }
    public float Restitution { get; init; }
    public float Friction { get; init; }
    public RigidbodyType Type { get; init; }

    /// <summary>
    /// PMX mode 2：动态刚体，但骨骼只取旋转，刚体位置每帧重新钉回动画骨骼。
    /// </summary>
    public bool Aligned { get; init; }

    //TODO: 共享物理世界，模型间可以碰撞
    /// <summary>
    /// 预留字段位：多模型共享 world 时的模型组 id。当前单 world 单模型，不启用。
    /// </summary>
    public int ModelGroupId { get; init; }

    /// <summary>
    /// PMX 刚体 → 内核定义。模式映射沿用 reze pmx-loader 的决策：
    /// 0 = 跟随骨骼（Static），1 = 物理（Dynamic），2 = 物理 + 骨骼位置对齐
    /// （Dynamic + Aligned）。
    /// </summary>
    public static RigidBodyDef FromPmx(PmxRigidBody rb)
    {
        var type = rb.PhysicsMode switch
        {
            PmxRigidBodyMode.FollowBone => RigidbodyType.Static,
            PmxRigidBodyMode.Physics => RigidbodyType.Dynamic,
            PmxRigidBodyMode.PhysicsWithBone => RigidbodyType.Dynamic,
            _ => RigidbodyType.Static,
        };
        return new RigidBodyDef
        {
            Name = rb.Name,
            EnglishName = rb.EnglishName,
            BoneIndex = rb.BoneIndex,
            Group = rb.CollisionGroup,
            CollisionMask = rb.CollisionMask,
            Shape = (RigidbodyShape)rb.ShapeType,
            Size = rb.ShapeSize,
            ShapePosition = rb.ShapePosition,
            ShapeRotation = rb.ShapeRotation,
            Mass = rb.Mass,
            LinearDamping = rb.LinearDamping,
            AngularDamping = rb.AngularDamping,
            Restitution = rb.Repulsion,
            Friction = rb.Friction,
            Type = type,
            Aligned = rb.PhysicsMode == PmxRigidBodyMode.PhysicsWithBone,
        };
    }

    /// <summary>
    /// 内置地面刚体（对照 reze RezePhysics 构造函数中的 ground 定义）。
    /// 一个巨大的静态盒，顶面为模型空间 y = 0，让头发与裙摆停在地面而不是穿进去。
    /// 无骨（boneIndex=-1，所有骨骼同步循环都跳过它）；通过 findContacts 的专用
    /// 平面 pass 与每一个动态刚体碰撞（无视组掩码——球、胶囊、盒全覆盖）。
    /// 它是模型空间的，这正是 setFloor 开关存在的理由：y=0 在该模型自身原点处，
    /// 不是场景地面。角色站在舞台上、悬在空中、或被 root motion 抬起时，
    /// 她的地面跟着她走。
    /// </summary>
    /// <remarks>
    /// 内核构造负责把该 def 追加到列表末尾、置零其组掩码并把 store.GroundIndex 指向它。
    /// </remarks>
    public static RigidBodyDef CreateGround()
    {
        return new RigidBodyDef
        {
            Name = "__ground__",
            EnglishName = "__ground__",
            BoneIndex = -1,
            Group = 0,
            CollisionMask = 0xffff,
            Shape = RigidbodyShape.Box,
            Size = new Vector3(500, 1, 500),
            ShapePosition = new Vector3(0, -1, 0),
            ShapeRotation = new Vector3(0, 0, 0),
            Mass = 0,
            LinearDamping = 0,
            AngularDamping = 0,
            Restitution = 0,
            Friction = 0.6f,
            Type = RigidbodyType.Static,
        };
    }
}

/// <summary>内核关节定义，对照 reze physics/types.ts Joint。
/// </summary>
public sealed class JointDef
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }

    /// <summary>PMX 关节类型原始字节（= PmxJointType）。内核不 switch 该值，约束统一按 6DOF spring 处理。</summary>
    public int Type { get; init; }

    public int RigidbodyIndexA { get; init; }
    public int RigidbodyIndexB { get; init; }
    public Vector3 Position { get; init; }
    /// <summary>弧度欧拉角。</summary>
    public Vector3 Rotation { get; init; }
    public Vector3 PositionMin { get; init; }
    public Vector3 PositionMax { get; init; }
    /// <summary>弧度欧拉角。</summary>
    public Vector3 RotationMin { get; init; }
    /// <summary>弧度欧拉角。</summary>
    public Vector3 RotationMax { get; init; }
    /// <summary>弹簧刚度。</summary>
    public Vector3 SpringPosition { get; init; }
    /// <summary>弹簧刚度。</summary>
    public Vector3 SpringRotation { get; init; }

    public static JointDef FromPmx(PmxJoint j)
    {
        return new JointDef
        {
            Name = j.Name,
            EnglishName = j.EnglishName,
            Type = (int)j.Type,
            RigidbodyIndexA = j.RigidBodyIndexA,
            RigidbodyIndexB = j.RigidBodyIndexB,
            Position = j.Position,
            Rotation = j.Rotation,
            PositionMin = j.PositionMin,
            PositionMax = j.PositionMax,
            RotationMin = j.RotationMin,
            RotationMax = j.RotationMax,
            SpringPosition = j.SpringPosition,
            SpringRotation = j.SpringRotation,
        };
    }
}

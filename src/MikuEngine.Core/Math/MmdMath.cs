using System.Numerics;

namespace MikuEngine.Core.Math;

/// <summary>
/// MMD 专用数学工具。矩阵约定：MMD/PMX 是列主序存储 + 列向量乘法（M·v，平移在 m[12..14]），
/// 与 System.Numerics.Matrix4x4 的行主序存储 + 行向量乘法（v·M，XNA 血统）互为转置——
/// 两者并不一致（此前头注释的说法有误）。跨约定传递矩阵必须显式转置并注释理由。
/// 物理与骨骼同步路径一律使用列主序的 MikuEngine.Core.Math.Mat4；
/// System.Numerics 的 Vector3/Quaternion 不涉及存储约定，可放心使用。
/// </summary>
public static class MmdMath
{
    /// <summary>
    /// MMD 的 YXZ 欧拉角顺序。MMD VMD 中角度限制按此顺序应用。
    /// </summary>
    public const string EulerOrderYxz = "YXZ";

    /// <summary>
    /// 把 Euler 角（YXZ 顺序）转换为 Quaternion。
    /// </summary>
    public static Quaternion QuaternionFromYxzEuler(float y, float x, float z)
    {
        // 先 Y 再 X 再 Z —— 注意 System.Numerics 的 Quaternion.CreateFromYawPitchRoll 是 YXZ
        return Quaternion.CreateFromYawPitchRoll(y, x, z);
    }

    /// <summary>
    /// 四元数 → 欧拉角（YXZ 顺序）。MMD IK 求解器需要这个来做角度限制。
    /// </summary>
    public static Vector3 YxzEulerFromQuaternion(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        Vector3 euler;

        // X（pitch）= asin(-m32) = asin(2(wy - xz))
        float sinX = 2f * (q.W * q.Y - q.X * q.Z);
        if (MathF.Abs(sinX) >= 1f)
            euler.X = MathF.CopySign(MathF.PI / 2f, sinX); // 万向锁
        else
            euler.X = MathF.Asin(sinX);

        // Y（yaw）= atan2(m31, m33) = atan2(2(wx + yz), 1 - 2(y² + z²))
        float sinYcosX = 2f * (q.W * q.X + q.Y * q.Z);
        float cosYcosX = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        euler.Y = MathF.Atan2(sinYcosX, cosYcosX);

        // Z（roll）= atan2(m12, m22) = atan2(2(wx + yz), 1 - 2(x² + y²)) 不对
        // 正确公式：atan2(2(wz + xy), 1 - 2(x² + z²))
        float sinZcosX = 2f * (q.W * q.Z + q.X * q.Y);
        float cosZcosX = 1f - 2f * (q.X * q.X + q.Z * q.Z);
        euler.Z = MathF.Atan2(sinZcosX, cosZcosX);

        return euler;
    }
}

using System.Numerics;

namespace MikuEngine.Core.Math;

/// <summary>
/// reze <c>math.ts</c> Quat 子集在 <see cref="System.Numerics.Quaternion"/> 上的补齐。
/// 移植原则：逐行对照翻译，reze 注释里的行为决策原样保留为 C# 注释。
/// System.Numerics.Quaternion/Vector3 为 struct，栈上传递即零分配，天然替代 reze 的
/// <c>*Into</c> 输出缓冲 + scratch 槽设计（返回值按寄存器语义写回，无堆分配）。
/// </summary>
/// <remarks>
/// 约定陷阱：System.Numerics 的 <see cref="Matrix4x4"/> 是行主序 + 行向量（v·M），与
/// MMD/babylon/reze 的列主序（M·v）互为转置——本类只做向量/四元数代数，不涉及矩阵；
/// 矩阵一律用 <see cref="Mat4"/>（列主序 float[16]）。
/// </remarks>
public static class QuatMath
{
    /// <summary>
    /// 球面线性插值。M-MATH-1 行为锁定结论：BCL <see cref="Quaternion.Slerp"/> 在
    /// 近平行分支（cos &gt; 1-1e-6）做纯 lerp 不归一化，且阈值（1-1e-6）与 reze
    /// （0.9995）不同，与 reze slerpInto 偏差 ~1e-4，超出黄金值容差 1e-6，
    /// 故按方案 §2.3 换为对照 reze Quat.slerpInto 的自写实现。
    /// </summary>
    /// <param name="a">单位四元数起点。</param>
    /// <param name="b">单位四元数终点（dot&lt;0 时内部取负走最短弧）。</param>
    /// <param name="t">插值参数 [0,1]。</param>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        float cos = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        float bx = b.X, by = b.Y, bz = b.Z, bw = b.W;

        // If dot product is negative, negate one quaternion to take shorter path
        if (cos < 0f)
        {
            cos = -cos;
            bx = -bx;
            by = -by;
            bz = -bz;
            bw = -bw;
        }

        // If quaternions are very close, use linear interpolation
        if (cos > 0.9995f)
        {
            float x = a.X + t * (bx - a.X);
            float y = a.Y + t * (by - a.Y);
            float z = a.Z + t * (bz - a.Z);
            float w = a.W + t * (bw - a.W);
            // reze 用 Math.hypot（JS double）；float32 值域内 Sqrt(x²+y²+z²+w²) 等价
            float invLen = 1f / MathF.Sqrt(x * x + y * y + z * z + w * w);
            return new Quaternion(x * invLen, y * invLen, z * invLen, w * invLen);
        }

        // Standard SLERP
        float theta0 = MathF.Acos(cos);
        float sinTheta0 = MathF.Sin(theta0);
        float theta = theta0 * t;
        float s0 = MathF.Sin(theta0 - theta) / sinTheta0;
        float s1 = MathF.Sin(theta) / sinTheta0;
        return new Quaternion(
            s0 * a.X + s1 * bx,
            s0 * a.Y + s1 * by,
            s0 * a.Z + s1 * bz,
            s0 * a.W + s1 * bw);
    }

    /// <summary>
    /// out = normalized lerp from a to b, taking the shorter path (negates b when dot &lt; 0).
    /// Cheaper than slerp; non-constant angular velocity.（对照 reze Quat.nlerpInto）
    /// </summary>
    public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
    {
        float d = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        float s = d < 0 ? -1f : 1f;
        float x = a.X + (b.X * s - a.X) * t;
        float y = a.Y + (b.Y * s - a.Y) * t;
        float z = a.Z + (b.Z * s - a.Z) * t;
        float w = a.W + (b.W * s - a.W) * t;
        float invLen = 1f / MathF.Sqrt(x * x + y * y + z * z + w * w);
        return new Quaternion(x * invLen, y * invLen, z * invLen, w * invLen);
    }

    /// <summary>
    /// out = v rotated by unit quaternion q (no matrix, no allocation).
    /// v' = v + 2*qw*(qv × v) + 2*(qv × (qv × v))（对照 reze Quat.rotateVecInto）。
    /// </summary>
    public static Vector3 RotateVec(Quaternion q, Vector3 v)
    {
        float qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;
        float vx = v.X, vy = v.Y, vz = v.Z;
        // t = 2 * (qv × v)
        float tx = 2f * (qy * vz - qz * vy);
        float ty = 2f * (qz * vx - qx * vz);
        float tz = 2f * (qx * vy - qy * vx);
        return new Vector3(
            vx + qw * tx + qy * tz - qz * ty,
            vy + qw * ty + qz * tx - qx * tz,
            vz + qw * tz + qx * ty - qy * tx);
    }

    /// <summary>out = v rotated by the inverse (conjugate) of unit quaternion q.（reze Quat.rotateVecInvInto）</summary>
    public static Vector3 RotateVecInv(Quaternion q, Vector3 v)
    {
        float qx = -q.X, qy = -q.Y, qz = -q.Z, qw = q.W;
        float vx = v.X, vy = v.Y, vz = v.Z;
        float tx = 2f * (qy * vz - qz * vy);
        float ty = 2f * (qz * vx - qx * vz);
        float tz = 2f * (qx * vy - qy * vx);
        return new Vector3(
            vx + qw * tx + qy * tz - qz * ty,
            vy + qw * ty + qz * tx - qx * tz,
            vz + qw * tz + qx * ty - qy * tx);
    }

    /// <summary>
    /// out = quaternion from axis (unnormalized) and angle。reze 版内部归一化轴
    /// （len&gt;0 ? 1/len : 0），与 BCL CreateFromAxisAngle（假定轴已单位化）不同，故自写。
    /// </summary>
    public static Quaternion FromAxisAngle(float ax, float ay, float az, float angle)
    {
        float len = MathF.Sqrt(ax * ax + ay * ay + az * az);
        float invLen = len > 0 ? 1f / len : 0f;
        float nx = ax * invLen, ny = ay * invLen, nz = az * invLen;
        float half = angle * 0.5f;
        float s = MathF.Sin(half), c = MathF.Cos(half);
        return new Quaternion(nx * s, ny * s, nz * s, c);
    }

    /// <summary>
    /// out = rotation taking the standard basis onto the orthonormal axes x, y, z
    /// (the columns of the column-major rotation matrix): rotateVec(out, (1,0,0)) = x, etc.
    /// Matches Babylon's FromUnitVectorsToRef exactly…（对照 reze Quat.fromBasisInto，
    /// Shepperd's method；正交基输入下输出即单位四元数，无需再归一化）
    /// </summary>
    public static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        // Shepperd's method on the 3x3 with columns x, y, z.
        float m00 = x.X, m01 = x.Y, m02 = x.Z;
        float m10 = y.X, m11 = y.Y, m12 = y.Z;
        float m20 = z.X, m21 = z.Y, m22 = z.Z;
        float trace = m00 + m11 + m22;
        float qx, qy, qz, qw;
        if (trace > 0)
        {
            float s = 0.5f / MathF.Sqrt(trace + 1f);
            qx = (m12 - m21) * s;
            qy = (m20 - m02) * s;
            qz = (m01 - m10) * s;
            qw = 0.25f / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = 2f * MathF.Sqrt(1f + m00 - m11 - m22);
            qx = 0.25f * s;
            qy = (m10 + m01) / s;
            qz = (m20 + m02) / s;
            qw = (m12 - m21) / s;
        }
        else if (m11 > m22)
        {
            float s = 2f * MathF.Sqrt(1f + m11 - m00 - m22);
            qx = (m10 + m01) / s;
            qy = 0.25f * s;
            qz = (m21 + m12) / s;
            qw = (m20 - m02) / s;
        }
        else
        {
            float s = 2f * MathF.Sqrt(1f + m22 - m00 - m11);
            qx = (m20 + m02) / s;
            qy = (m21 + m12) / s;
            qz = 0.25f * s;
            qw = (m01 - m10) / s;
        }
        return new Quaternion(qx, qy, qz, qw);
    }

    /// <summary>
    /// out = shortest-arc rotation taking unit vector from to unit vector to.
    /// Matches Babylon's FromUnitVectorsToRef exactly, including the near-antiparallel
    /// branch (w = 1 + dot &lt; 0.001 → 180° about a perpendicular picked the same way).
    /// （对照 reze Quat.fromUnitVectorsInto）
    /// </summary>
    public static Quaternion FromUnitVectors(Vector3 from, Vector3 to)
    {
        float r = from.X * to.X + from.Y * to.Y + from.Z * to.Z + 1;
        float x, y, z, w;
        if (r < 0.001f)
        {
            if (MathF.Abs(from.X) > MathF.Abs(from.Z))
            {
                x = -from.Y; y = from.X; z = 0; w = 0;
            }
            else
            {
                x = 0; y = -from.Z; z = from.Y; w = 0;
            }
        }
        else
        {
            // q = (from × to, 1 + from·to)
            x = from.Y * to.Z - from.Z * to.Y;
            y = from.Z * to.X - from.X * to.Z;
            z = from.X * to.Y - from.Y * to.X;
            w = r;
        }
        float invLen = 1f / MathF.Sqrt(x * x + y * y + z * z + w * w);
        return new Quaternion(x * invLen, y * invLen, z * invLen, w * invLen);
    }

    /// <summary>
    /// out = twist component of q around unit axis a, so that q = swing · twist
    /// (swing = Quat.multiply(q, conjugate(twist))). Singular when q is ~180° about an
    /// axis perpendicular to a — returns identity there.（对照 reze Quat.twistAroundAxisInto）
    /// </summary>
    public static Quaternion TwistAroundAxis(Quaternion q, Vector3 a)
    {
        float d = q.X * a.X + q.Y * a.Y + q.Z * a.Z;
        float px = a.X * d;
        float py = a.Y * d;
        float pz = a.Z * d;
        float len = MathF.Sqrt(px * px + py * py + pz * pz + q.W * q.W);
        if (len < 1e-8f)
        {
            return Quaternion.Identity;
        }
        return new Quaternion(px / len, py / len, pz / len, q.W / len);
    }

    /// <summary>
    /// Convert Euler angles to quaternion (ZXY order, left-handed, PMX format)。
    /// PMX 刚体 bind 姿态欧拉 → 四元数用这个（RigidBodyStore 构造路径），不做
    /// 万向锁特殊处理，直接公式 + 末尾归一化（对照 reze Quat.fromEuler）。
    /// </summary>
    public static Quaternion FromEuler(float rotX, float rotY, float rotZ)
    {
        float cx = MathF.Cos(rotX * 0.5f);
        float sx = MathF.Sin(rotX * 0.5f);
        float cy = MathF.Cos(rotY * 0.5f);
        float sy = MathF.Sin(rotY * 0.5f);
        float cz = MathF.Cos(rotZ * 0.5f);
        float sz = MathF.Sin(rotZ * 0.5f);

        float w = cy * cx * cz + sy * sx * sz;
        float x = cy * sx * cz + sy * cx * sz;
        float y = sy * cx * cz - cy * sx * sz;
        float z = cy * cx * sz - sy * sx * cz;

        float invLen = 1f / MathF.Sqrt(x * x + y * y + z * z + w * w);
        return new Quaternion(x * invLen, y * invLen, z * invLen, w * invLen);
    }
}

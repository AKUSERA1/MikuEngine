namespace MikuEngine.Core.Math;

/// <summary>
/// 列主序 float[16] 4x4 矩阵静态工具。
/// 存储约定：列主序（m[0..3] 为第 0 列），平移在 m[12..14]，列向量乘法 M·v —— 与
/// GLSL、MMD 一致，蒙皮矩阵缓冲可与 GLES <c>uniformMatrix4fv</c> 直传，无需转置。
/// </summary>
/// <remarks>
/// 注意：<see cref="System.Numerics.Matrix4x4"/> 是行主序存储 + 行向量乘法（v·M，XNA 血统，
/// 平移在 M41..M43），与列主序互为转置。两者禁止混用；跨约定必须显式转置并注释理由。
/// 物理与骨骼同步路径一律使用本类型。
/// </remarks>
public static class Mat4
{
    /// <summary>Reset matrix to identity in place。</summary>
    public static void SetIdentity(float[] m, int offset)
    {
        m[offset + 0] = 1;
        m[offset + 1] = 0;
        m[offset + 2] = 0;
        m[offset + 3] = 0;
        m[offset + 4] = 0;
        m[offset + 5] = 1;
        m[offset + 6] = 0;
        m[offset + 7] = 0;
        m[offset + 8] = 0;
        m[offset + 9] = 0;
        m[offset + 10] = 1;
        m[offset + 11] = 0;
        m[offset + 12] = 0;
        m[offset + 13] = 0;
        m[offset + 14] = 0;
        m[offset + 15] = 1;
    }

    /// <summary>
    /// Static method to multiply two matrix array segments directly into output array (no object creation)
    /// Column-major multiplication: result = a * b。
    /// </summary>
    public static void MultiplyArrays(float[] a, int aOffset, float[] b, int bOffset, float[] output, int outputOffset)
    {
        for (int c = 0; c < 4; c++)
        {
            float b0 = b[bOffset + c * 4 + 0];
            float b1 = b[bOffset + c * 4 + 1];
            float b2 = b[bOffset + c * 4 + 2];
            float b3 = b[bOffset + c * 4 + 3];
            output[outputOffset + c * 4 + 0] =
                a[aOffset + 0] * b0 + a[aOffset + 4] * b1 + a[aOffset + 8] * b2 + a[aOffset + 12] * b3;
            output[outputOffset + c * 4 + 1] =
                a[aOffset + 1] * b0 + a[aOffset + 5] * b1 + a[aOffset + 9] * b2 + a[aOffset + 13] * b3;
            output[outputOffset + c * 4 + 2] =
                a[aOffset + 2] * b0 + a[aOffset + 6] * b1 + a[aOffset + 10] * b2 + a[aOffset + 14] * b3;
            output[outputOffset + c * 4 + 3] =
                a[aOffset + 3] * b0 + a[aOffset + 7] * b1 + a[aOffset + 11] * b2 + a[aOffset + 15] * b3;
        }
    }

    /// <summary>Write rotation matrix from quaternion into existing array (column-major)。</summary>
    public static void FromQuatInto(float x, float y, float z, float w, float[] output, int offset)
    {
        float x2 = x + x, y2 = y + y, z2 = z + z;
        float xx = x * x2, xy = x * y2, xz = x * z2;
        float yy = y * y2, yz = y * z2, zz = z * z2;
        float wx = w * x2, wy = w * y2, wz = w * z2;
        output[offset + 0] = 1 - (yy + zz);
        output[offset + 1] = xy + wz;
        output[offset + 2] = xz - wy;
        output[offset + 3] = 0;
        output[offset + 4] = xy - wz;
        output[offset + 5] = 1 - (xx + zz);
        output[offset + 6] = yz + wx;
        output[offset + 7] = 0;
        output[offset + 8] = xz + wy;
        output[offset + 9] = yz - wx;
        output[offset + 10] = 1 - (xx + yy);
        output[offset + 11] = 0;
        output[offset + 12] = 0;
        output[offset + 13] = 0;
        output[offset + 14] = 0;
        output[offset + 15] = 1;
    }

    /// <summary>
    /// Fused local transform: out = T(bindT) · R(quat) · T(localT).
    /// Result translation = bindT + R * localT; rotation column block = R.
    /// Column-major. Zero allocations.
    /// </summary>
    public static void LocalTransformInto(
        float bx, float by, float bz,
        float qx, float qy, float qz, float qw,
        float lx, float ly, float lz,
        float[] output, int offset)
    {
        float x2 = qx + qx, y2 = qy + qy, z2 = qz + qz;
        float xx = qx * x2, xy = qx * y2, xz = qx * z2;
        float yy = qy * y2, yz = qy * z2, zz = qz * z2;
        float wx = qw * x2, wy = qw * y2, wz = qw * z2;
        float m00 = 1 - (yy + zz), m01 = xy + wz, m02 = xz - wy;
        float m10 = xy - wz, m11 = 1 - (xx + zz), m12 = yz + wx;
        float m20 = xz + wy, m21 = yz - wx, m22 = 1 - (xx + yy);
        output[offset + 0] = m00; output[offset + 1] = m01; output[offset + 2] = m02; output[offset + 3] = 0;
        output[offset + 4] = m10; output[offset + 5] = m11; output[offset + 6] = m12; output[offset + 7] = 0;
        output[offset + 8] = m20; output[offset + 9] = m21; output[offset + 10] = m22; output[offset + 11] = 0;
        output[offset + 12] = bx + m00 * lx + m10 * ly + m20 * lz;
        output[offset + 13] = by + m01 * lx + m11 * ly + m21 * lz;
        output[offset + 14] = bz + m02 * lx + m12 * ly + m22 * lz;
        output[offset + 15] = 1;
    }

    /// <summary>Write position+rotation transform into existing array.</summary>
    public static void FromPositionRotationInto(
        float px, float py, float pz,
        float qx, float qy, float qz, float qw,
        float[] output, int offset)
    {
        FromQuatInto(qx, qy, qz, qw, output, offset);
        output[offset + 12] = px;
        output[offset + 13] = py;
        output[offset + 14] = pz;
    }

    /// <summary>
    /// Full 4x4 matrix inverse using adjugate method. Works for any invertible matrix, not just
    /// orthonormal transforms（骨骼层级变换后的矩阵含缩放/非正交成分，不能用正交假设的逆转置）。
    /// Returns true on success, false if singular (out untouched).
    /// </summary>
    public static bool InverseInto(float[] m, float[] output)
    {
        float a00 = m[0], a01 = m[1], a02 = m[2], a03 = m[3];
        float a10 = m[4], a11 = m[5], a12 = m[6], a13 = m[7];
        float a20 = m[8], a21 = m[9], a22 = m[10], a23 = m[11];
        float a30 = m[12], a31 = m[13], a32 = m[14], a33 = m[15];
        float b00 = a00 * a11 - a01 * a10;
        float b01 = a00 * a12 - a02 * a10;
        float b02 = a00 * a13 - a03 * a10;
        float b03 = a01 * a12 - a02 * a11;
        float b04 = a01 * a13 - a03 * a11;
        float b05 = a02 * a13 - a03 * a12;
        float b06 = a20 * a31 - a21 * a30;
        float b07 = a20 * a32 - a22 * a30;
        float b08 = a20 * a33 - a23 * a30;
        float b09 = a21 * a32 - a22 * a31;
        float b10 = a21 * a33 - a23 * a31;
        float b11 = a22 * a33 - a23 * a32;
        float det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
        if (MathF.Abs(det) < 1e-10f) return false;
        det = 1.0f / det;
        output[0] = (a11 * b11 - a12 * b10 + a13 * b09) * det;
        output[1] = (a02 * b10 - a01 * b11 - a03 * b09) * det;
        output[2] = (a31 * b05 - a32 * b04 + a33 * b03) * det;
        output[3] = (a22 * b04 - a21 * b05 - a23 * b03) * det;
        output[4] = (a12 * b08 - a10 * b11 - a13 * b07) * det;
        output[5] = (a00 * b11 - a02 * b08 + a03 * b07) * det;
        output[6] = (a32 * b02 - a30 * b05 - a33 * b01) * det;
        output[7] = (a20 * b05 - a22 * b02 + a23 * b01) * det;
        output[8] = (a10 * b10 - a11 * b08 + a13 * b06) * det;
        output[9] = (a01 * b08 - a00 * b10 - a03 * b06) * det;
        output[10] = (a30 * b04 - a31 * b02 + a33 * b00) * det;
        output[11] = (a21 * b02 - a20 * b04 - a23 * b00) * det;
        output[12] = (a11 * b07 - a10 * b09 - a12 * b06) * det;
        output[13] = (a00 * b09 - a01 * b07 + a02 * b06) * det;
        output[14] = (a31 * b01 - a30 * b03 - a32 * b00) * det;
        output[15] = (a20 * b03 - a21 * b01 + a22 * b00) * det;
        return true;
    }

    /// <summary>
    /// Extract the rotation block as a unit quaternion (xyzw) into q[qOffset..].
    /// Branch-by-trace on the rotation diagonal; the intermediate arithmetic runs
    /// in double because reze's JS numbers are float64 and the near-degenerate
    /// branches lose unit length quickly in float32.
    /// </summary>
    public static void ToQuatInto(float[] m, int offset, float[] q, int qOffset)
    {
        // Column-major: m[col*4 + row].
        double m00 = m[offset + 0], m01 = m[offset + 4], m02 = m[offset + 8];
        double m10 = m[offset + 1], m11 = m[offset + 5], m12 = m[offset + 9];
        double m20 = m[offset + 2], m21 = m[offset + 6], m22 = m[offset + 10];
        double trace = m00 + m11 + m22;
        double x = 0, y = 0, z = 0, w = 1;
        if (trace > 0)
        {
            double s = System.Math.Sqrt(trace + 1.0) * 2;
            w = 0.25 * s;
            x = (m21 - m12) / s;
            y = (m02 - m20) / s;
            z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = System.Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
            w = (m21 - m12) / s;
            x = 0.25 * s;
            y = (m01 + m10) / s;
            z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = System.Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
            w = (m02 - m20) / s;
            x = (m01 + m10) / s;
            y = 0.25 * s;
            z = (m12 + m21) / s;
        }
        else
        {
            double s = System.Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
            w = (m10 - m01) / s;
            x = (m02 + m20) / s;
            y = (m12 + m21) / s;
            z = 0.25 * s;
        }
        double invLen = 1 / System.Math.Sqrt(x * x + y * y + z * z + w * w);
        q[qOffset + 0] = (float)(x * invLen);
        q[qOffset + 1] = (float)(y * invLen);
        q[qOffset + 2] = (float)(z * invLen);
        q[qOffset + 3] = (float)(w * invLen);
    }
}

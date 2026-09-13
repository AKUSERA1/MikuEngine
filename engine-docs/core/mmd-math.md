# 数学工具 — MmdMath / QuatMath / Mat4

MMD 专用数学工具。注意：MMD 使用列主序矩阵，与 `System.Numerics.Matrix4x4` 约定一致。

## 源文件

[MmdMath.cs](../../../src/MikuEngine.Core/Math/MmdMath.cs) ·
[QuatMath.cs](../../../src/MikuEngine.Core/Math/QuatMath.cs) ·
[Mat4.cs](../../../src/MikuEngine.Core/Math/Mat4.cs)

## 公开常量

```csharp
MmdMath.EulerOrderYxz   // = "YXZ" — MMD VMD 中角度限制按此顺序应用
```

## 方法

### QuaternionFromYxzEuler

```csharp
public static Quaternion QuaternionFromYxzEuler(float y, float x, float z);
```

按 YXZ 顺序把欧拉角转换为四元数。`System.Numerics.Quaternion.CreateFromYawPitchRoll` 本身就是 YXZ 顺序的实现，本方法直接转发。

| 参数 | 含义 |
|---|---|
| `y` | 绕 +Y 轴旋转（yaw），弧度 |
| `x` | 绕 +X 轴旋转（pitch），弧度 |
| `z` | 绕 +Z 轴旋转（roll），弧度 |

### YxzEulerFromQuaternion

```csharp
public static Vector3 YxzEulerFromQuaternion(Quaternion q);
```

四元数 → YXZ 欧拉角。MMD IK 求解器需要这个来做角度限制。

内部处理了万向锁（`|sinX| >= 1` 时 X 取 ±π/2，Y 和 Z 归零）。

返回的 `Vector3`：
- `X` = pitch（绕 +X）
- `Y` = yaw（绕 +Y）
- `Z` = roll（绕 +Z）

## 使用示例

```csharp
using MikuEngine.Core.Math;

// YXZ 欧拉角 → 四元数
Quaternion q = MmdMath.QuaternionFromYxzEuler(
    y: MathF.PI / 2f,   // 90° 绕 Y
    x: 0f,
    z: 0f);

// 四元数 → YXZ 欧拉角
Vector3 euler = MmdMath.YxzEulerFromQuaternion(q);
// euler.Y ≈ π/2, euler.X ≈ 0, euler.Z ≈ 0
```

## MMD 欧拉角顺序说明

MMD 的 VMD 中，关节角度限制用的就是 YXZ 顺序：先绕 Y（yaw），再绕 X（pitch），最后绕 Z（roll）。这是 MikuMikuDance 约定的固定顺序，不能与 Unity（XYZ）或 Unreal（ZYX）混淆。

## QuatMath（四元数补集）

`System.Numerics.Quaternion` 缺失的运算补充，主要服务物理内核（刚体积分 / 接触求解），
行为注释与 reze 同源。全为静态方法：

| 方法 | 用途 |
|---|---|
| `Slerp(a, b, t)` | 球面插值（dot 为负自动取短弧） |
| `Nlerp(a, b, t)` | 半球对齐归一化线性插值（快，混合器用它） |
| `RotateVec(q, v)` / `RotateVecInv(q, v)` | 向量旋转 q·v·q⁻¹ / q⁻¹·v·q |
| `FromAxisAngle(ax, ay, az, angle)` | 轴角 → 四元数（轴自动归一化） |
| `FromBasis(x, y, z)` | 3×3 基（列 x/y/z）→ 四元数（Shepperd 法） |
| `FromUnitVectors(from, to)` | 单位向量到单位向量的最短旋转 |
| `TwistAroundAxis(q, a)` | 提取绕轴 a 的扭转分量（swing-twist 分解） |
| `FromEuler(rotX, rotY, rotZ)` | 欧拉角（弧度）→ 四元数（物理刚体 bind 朝向用） |

## Mat4 补充接口

`Mat4`（float[] 列主序工具）新增 / 相关：

| 方法 | 用途 |
|---|---|
| `Mat4.ToQuatInto(m, mOff, q, qOff)` | 从 4×4 矩阵提取旋转块为单位四元数（double 中间运算保证数值稳定）；物理写回 / kinematic 目标导出用 |
| `Mat4.FromQuatInto(...)` | 四元数 → 旋转矩阵 |
| `Mat4.LocalTransformInto(...)` / `FromPositionRotationInto(...)` | 局部 T×R 变换构建（骨骼 / 刚体偏移矩阵） |
| `Mat4.MultiplyArrays(...)` | 平铺数组乘法（物理热路径，零分配） |

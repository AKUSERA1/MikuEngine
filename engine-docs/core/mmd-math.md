# MmdMath API

MMD 专用数学工具。注意：MMD 使用列主序矩阵，与 `System.Numerics.Matrix4x4` 约定一致。

## 源文件

[MmdMath.cs](../../../src/MikuEngine.Core/Math/MmdMath.cs)

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

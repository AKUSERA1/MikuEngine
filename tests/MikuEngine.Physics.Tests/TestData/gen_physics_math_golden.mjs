// M-MATH-1 / M-MATH-2 黄金值生成器。
// 直接 import reze 引擎源码（Node 24+ 原生 TS type-stripping，可擦除语法无需编译），
// 保证黄金值就是 reze 行为本身。运行一次即可，勿在 CI 维护 TS 运行时（方案 §7）。
//
//   node gen_physics_math_golden.mjs
//
import { Quat, Mat4 } from '../../../reference/reze-engine/src/math.ts'
import { writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const here = dirname(fileURLToPath(import.meta.url))

const q = (x, y, z, w) => new Quat(x, y, z, w)
const v3 = (x, y, z) => ({ x, y, z })
const arr = (qq) => [qq.x, qq.y, qq.z, qq.w]

// ---------------------------------------------------------------- Slerp
// 覆盖：一般角、dot<0（最短弧翻转）、近平行（cos>0.9995 走归一化 lerp 分支）、
// 端点 t=0/1、近对跖（cos 略小于 1 不触发 nlerp 分支但接近）。
const slerpCases = []
const pushSlerp = (a, b, ts) => {
  for (const t of ts) {
    const out = Quat.slerp(a, b, t)
    slerpCases.push({ a: arr(a), b: arr(b), t, out: arr(out) })
  }
}
const halfSqrt2 = Math.SQRT1_2
pushSlerp(q(0, 0, 0, 1), q(0, halfSqrt2, 0, halfSqrt2), [0, 0.25, 0.5, 0.75, 1])
// dot<0：b 与 a 半球相反
pushSlerp(q(0, 0, 0, 1), q(0, 0.5, 0.5, -0.5), [0.3, 0.5, 0.9])
// 近平行（cos>0.9995 走归一化 lerp 分支）：必须含 t≠0.5 的用例——t=0.5 时
// nlerp 与 slerp 几乎重合，无法区分两种实现（M-MATH-1 区分度要求）
pushSlerp(q(0, 0, 0, 1), q(0, 0.0070714, 0, 0.999975), [0.25, 0.5, 0.75])
// cos ≈ 0.9996（nlerp 分支上沿，区分度最大处）
pushSlerp(q(0, 0, 0, 1), q(0, Math.sin(0.0283 / 2), 0, Math.cos(0.0283 / 2)), [0.25, 0.75])
// 一般 slerp 分支
pushSlerp(q(0.5, 0.5, 0.5, 0.5), q(-0.2, 0.4, -0.5, 0.7), [0.3, 0.65])
pushSlerp(q(0, halfSqrt2, 0, halfSqrt2), q(halfSqrt2, 0, 0, halfSqrt2), [0.4])
// 非归一化输入（reze slerp 不做输入归一化——黄金值锁定该行为）
pushSlerp(q(0, 0, 0, 2), q(0, 1, 1, 1), [0.25, 0.5])

// ------------------------------------------------------------ FromEuler
// PMX 刚体 bind 姿态欧拉（ZXY）→ 四元数（RigidBodyStore 构造路径）。
const fromEulerCases = []
for (const [rx, ry, rz] of [
  [0, 0, 0], [Math.PI / 2, 0, 0], [0, Math.PI / 2, 0], [0, 0, Math.PI / 2],
  [0.1, -0.2, 0.3], [-1.2, 2.4, -3.1], [1.0, 1.0, 1.0],
]) {
  fromEulerCases.push({ e: [rx, ry, rz], out: arr(Quat.fromEuler(rx, ry, rz)) })
}

// ------------------------------------------------------- Mat4（M-MATH-2）
const fromQuatCases = []
for (const [x, y, z, w] of [
  [0, 0, 0, 1], [0, 1, 0, 0], [halfSqrt2, 0, 0, halfSqrt2],
  [0.1, 0.2, 0.3, 0.9], [-0.4, 0.5, -0.6, 0.4],
]) {
  const m = new Float32Array(16)
  Mat4.fromQuatInto(x, y, z, w, m, 0)
  fromQuatCases.push({ q: [x, y, z, w], m: [...m] })
}

// fromQuat × multiply 组合（列主序 a·b）
const multiplyCases = []
const matOf = (x, y, z, w) => { const m = new Float32Array(16); Mat4.fromQuatInto(x, y, z, w, m, 0); return m }
{
  const a = matOf(0.1, 0.2, 0.3, 0.9)
  const b = matOf(-0.4, 0.5, -0.6, 0.4)
  const c = matOf(0, halfSqrt2, 0, halfSqrt2)
  const pairs = [[a, b], [b, a], [a, c], [c, a]]
  for (const [ma, mb] of pairs) {
    const out = new Float32Array(16)
    Mat4.multiplyArrays(ma, 0, mb, 0, out, 0)
    multiplyCases.push({ a: [...ma], b: [...mb], out: [...out] })
  }
}

// fromPositionRotation / localTransform / inverse（B1 checklist 五件套的黄金值）
const fromPositionRotationCases = []
const localTransformCases = []
const inverseCases = []
{
  const combos = [
    [1, 2, 3, 0.1, 0.2, 0.3, 0.9],
    [-5, 0.5, 12, 0, 1, 0, 0],
    [0, 0, 0, -0.4, 0.5, -0.6, 0.4],
  ]
  for (const [px, py, pz, qx, qy, qz, qw] of combos) {
    const m = new Float32Array(16)
    Mat4.fromPositionRotationInto(px, py, pz, qx, qy, qz, qw, m)
    fromPositionRotationCases.push({ p: [px, py, pz], q: [qx, qy, qz, qw], m: [...m] })

    const lt = new Float32Array(16)
    Mat4.localTransformInto(px, py, pz, qx, qy, qz, qw, 0.5, -1.5, 2.5, lt)
    localTransformCases.push({ b: [px, py, pz], q: [qx, qy, qz, qw], l: [0.5, -1.5, 2.5], m: [...lt] })

    const inv = new Float32Array(16)
    const ok = Mat4.inverseInto(m, inv)
    inverseCases.push({ m: [...m], ok, inv: [...inv] })
  }
  // 平移主导的矩阵（含 scale 的非正交矩阵也走 adjugate 逆）
  const scaled = new Float32Array(16)
  Mat4.fromPositionRotationInto(3, -2, 7, 0, 0, 0, 1, scaled)
  for (let i = 0; i < 12; i++) scaled[i] *= 1.5
  const inv = new Float32Array(16)
  const ok = Mat4.inverseInto(scaled, inv)
  inverseCases.push({ m: [...scaled], ok, inv: [...inv] })
}

const golden = {
  generator: 'gen_physics_math_golden.mjs（Node 直跑 reference/reze-engine/src/math.ts，勿手改）',
  slerp: slerpCases,
  fromEuler: fromEulerCases,
  mat4FromQuat: fromQuatCases,
  mat4Multiply: multiplyCases,
  mat4FromPositionRotation: fromPositionRotationCases,
  mat4LocalTransform: localTransformCases,
  mat4Inverse: inverseCases,
}

const outPath = join(here, 'physics_math.golden.json')
writeFileSync(outPath, JSON.stringify(golden, null, 1) + '\n')
console.log(`written: ${outPath}`)
console.log(`slerp=${slerpCases.length} fromEuler=${fromEulerCases.length} fromQuat=${fromQuatCases.length} multiply=${multiplyCases.length} fpr=${fromPositionRotationCases.length} localT=${localTransformCases.length} inverse=${inverseCases.length}`)

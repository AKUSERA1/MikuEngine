// 用 reze-engine 的 IK 求解器（dist 编译产物，未做任何修改）对拍 MikuEngine 的 IK 结果。
//
// 输入：MikuEngine.Demo --ik-smoke --ik-dump=<json> 导出的对拍数据。
//   其中 `effRot`/`effPos` 是「付与/軸制限之后、IK 之前」的有效局部变换 —— 直接把它当作
//   reze 的 bindTranslation + 单位偏移，可以让 reze 的世界矩阵组合与 MikuEngine 逐位等价，
//   从而把「FK/付与」的差异从比较中隔离掉，只比较 IK 求解本身。
//
// 用法：node --import ./register.mjs run_reze.mjs <dump.json>
import { readFileSync } from "node:fs"
import { Quat, Vec3, Mat4 } from "../../reference/reze-engine/dist/math.js"
import { IKSolverSystem } from "../../reference/reze-engine/dist/ik-solver.js"

const dumpPath = process.argv[2] ?? "C:/tmp/ikdump.json"
const dump = JSON.parse(readFileSync(dumpPath, "utf8"))
const B = dump.bones
const n = B.length

// ── 拓扑序（父先于子；不复用引擎的 DeformOrder，独立推一遍）──────────────
const order = []
const seen = new Uint8Array(n)
const visit = (i) => {
    if (seen[i]) return
    seen[i] = 1
    const p = B[i].parent
    if (p >= 0 && p < n) visit(p)
    order.push(i)
}
for (let i = 0; i < n; i++) visit(i)

// ── rig ────────────────────────────────────────────────────────────────
const bones = B.map((b) => ({
    index: b.i,
    parentIndex: b.parent,
    bindTranslation: b.effPos,   // 见文件头注释：把"付与后的有效位置"当 bind
    children: [],
}))
for (const b of bones) if (b.parentIndex >= 0) bones[b.parentIndex].children.push(b.index)

const localRotations = B.map((b) => new Quat(b.effRot[0], b.effRot[1], b.effRot[2], b.effRot[3]))
const localTranslations = B.map(() => new Vec3(0, 0, 0))
const worldMatrices = B.map(() => Mat4.identity())
const ikChainInfo = B.map(() => ({ ikRotation: Quat.identity(), localRotation: Quat.identity() }))

const _tmpRot = Quat.identity()
const _localMat = new Float32Array(16)
const _mask = new Uint8Array(n)

function computeWorld(i, applyIK) {
    const bone = bones[i]
    const lr = localRotations[i]
    let qx = lr.x, qy = lr.y, qz = lr.z, qw = lr.w
    if (applyIK) {
        const ik = ikChainInfo[i].ikRotation
        Quat.multiplyInto(ik, lr, _tmpRot)          // reze 语义：combined = ik ⊗ local
        qx = _tmpRot.x; qy = _tmpRot.y; qz = _tmpRot.z; qw = _tmpRot.w
    }
    const t = bone.bindTranslation
    Mat4.localTransformInto(t[0], t[1], t[2], qx, qy, qz, qw, 0, 0, 0, _localMat)
    const wm = worldMatrices[i]
    if (bone.parentIndex >= 0) {
        Mat4.multiplyArrays(worldMatrices[bone.parentIndex].values, 0, _localMat, 0, wm.values, 0)
    } else {
        wm.values.set(_localMat)
    }
}

function updateSubtree(i, applyIK) {
    // 用"标记 + 按拓扑序重算"而不是递归：父图理论上无环（PMX 保证），但递归深度不可控，
    // 且该写法与引擎 UpdateWorldMatricesSubtree 的语义完全一致。
    _mask.fill(0)
    _mask[i] = 1
    for (const k of order) {
        const p = bones[k].parentIndex
        if (!_mask[k] && p >= 0 && _mask[p]) _mask[k] = 1
    }
    for (const k of order) if (_mask[k]) computeWorld(k, applyIK)
}

for (const i of order) computeWorld(i, false)

const pos = (i) => [worldMatrices[i].values[12], worldMatrices[i].values[13], worldMatrices[i].values[14]]
const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2])

// ── 校验 1：重放出的 FK 世界坐标必须与引擎一致（否则 rig/hierarchy 重放就是错的）──
let fkMax = 0
let fkMaxBone = -1
for (let i = 0; i < n; i++) {
    const d = dist(pos(i), B[i].fkWorld)
    if (d > fkMax) { fkMax = d; fkMaxBone = i }
}
console.log(`[reze-oracle] dump=${dumpPath} 帧=${dump.meta.frame} 骨=${n} 链=${dump.ikChains.length}`)
console.log(`[reze-oracle] FK 重放校验：最大偏差 ${fkMax.toExponential(3)} (骨骼 ${fkMaxBone} ${B[fkMaxBone]?.name})`)
if (fkMax > 1e-3) {
    console.log("[reze-oracle] FK 重放偏差过大 ⇒ 下面的 IK 比较不可信，先修 rig 重放。")
}

// ── 构造 solver 并求解 ──────────────────────────────────────────────────
const solvers = dump.ikChains.map((ch, idx) => ({
    index: idx,
    ikBoneIndex: ch.goal,          // reze 命名：Effector（= 带 IK 标志的骨，本引擎的 Goal）
    targetBoneIndex: ch.driven,    // reze 命名：Target（= PMX Ik.Target，本引擎的 Driven）
    iterationCount: ch.iteration,
    limitAngle: ch.limitAngle,
    links: ch.links.map((l) => ({
        boneIndex: l.bone,
        // IK_NO_LIMIT=1：把角度限制整体摘掉，用于二分"轴/转角公式"与"Euler 限位路径"的差异。
        hasLimit: process.env.IK_NO_LIMIT === "1" ? false : l.hasLimit,
        minAngle: new Vec3(l.min[0], l.min[1], l.min[2]),
        maxAngle: new Vec3(l.max[0], l.max[1], l.max[2]),
    })),
}))

IKSolverSystem.solve(
    solvers, bones, localRotations, localTranslations, worldMatrices, ikChainInfo,
    (boneIndex, applyIK) => updateSubtree(boneIndex, applyIK),
)

// 最终全量刷新 —— 与引擎在 Solve 之后再跑一次 UpdateWorldMatrices() 完全对应。
// 注意必须用 applyIK=false：reze 在每个 solver 结束时已把 ikRotation 折进 localRotations
//（`localRot = ik ⊗ local`），这里再叠一次就是双重应用。此步的意义是刷新"付与骨"
//（如 足首D，它不是任何链骨的后代）—— 引擎侧由最终的全量 UpdateWorldMatrices 覆盖。
for (const i of order) computeWorld(i, false)

// ── 逐链比较 ────────────────────────────────────────────────────────────
console.log("")
console.log("链                          FK误差   引擎误差   reze误差   |Δ被驱动端|  |Δ目标|   链骨最大|Δ|")
let worstDriven = 0, worstAny = 0, worstRow = ""
const name = (i) => B[i].name
for (let c = 0; c < solvers.length; c++) {
    const s = solvers[c]
    const ch = dump.ikChains[c]
    const fkErr = dist(B[s.ikBoneIndex].fkWorld, B[s.targetBoneIndex].fkWorld)
    const ourErr = dist(B[s.ikBoneIndex].ikWorld, B[s.targetBoneIndex].ikWorld)
    const rezeDriven = pos(s.targetBoneIndex)
    const ourDriven = B[s.targetBoneIndex].ikWorld
    const rezeErr = dist(pos(s.ikBoneIndex), rezeDriven)
    const dDriven = dist(rezeDriven, ourDriven)
    const dGoal = dist(pos(s.ikBoneIndex), B[s.ikBoneIndex].ikWorld)
    let dChainMax = 0, dChainBone = -1
    for (const l of s.links) {
        const d = dist(pos(l.boneIndex), B[l.boneIndex].ikWorld)
        if (d > dChainMax) { dChainMax = d; dChainBone = l.boneIndex }
    }
    if (dDriven > worstDriven) { worstDriven = dDriven; worstRow = name(s.ikBoneIndex) }
    worstAny = Math.max(worstAny, dChainMax)

    // 两边实际施加在链骨上的最大旋转角（度）—— 定位"轴方向/符号/限位"层面的分歧。
    let ourDeg = 0, rezeDeg = 0
    for (const l of s.links) {
        const q = B[l.boneIndex].ikRot
        ourDeg = Math.max(ourDeg, 2 * Math.acos(Math.min(1, Math.abs(q[3]))) * 180 / Math.PI)
        const rq = ikChainInfo[l.boneIndex].ikRotation
        rezeDeg = Math.max(rezeDeg, 2 * Math.acos(Math.min(1, Math.abs(rq.w))) * 180 / Math.PI)
    }

    const label = `${name(s.ikBoneIndex)}→${name(s.targetBoneIndex)}`
    console.log(
        `${label.padEnd(26)} ${fkErr.toFixed(4).padStart(7)} ${ourErr.toFixed(4).padStart(9)} ` +
        `${rezeErr.toFixed(4).padStart(9)} ${dDriven.toFixed(4).padStart(11)} ` +
        `rot ${ourDeg.toFixed(1).padStart(5)}°/${rezeDeg.toFixed(1).padStart(5)}°`,
    )
}

console.log("")
console.log(`[reze-oracle] 被驱动端最大分歧 ${worstDriven.toFixed(4)}（${worstRow}）｜链骨位置最大分歧 ${worstAny.toFixed(4)}`)
const worstErr = Math.max(...solvers.map((s) => dist(pos(s.ikBoneIndex), pos(s.targetBoneIndex))))
console.log(`[reze-oracle] reze 侧最大末端误差 ${worstErr.toFixed(4)}（模型身高量级约 20）`)
console.log(worstDriven < 1.0 && worstErr < 1.0
    ? "[reze-oracle] 判定：与 reze 结果一致（分歧 < 1 模型单位）—— 无「求解不了/严重偏差」"
    : "[reze-oracle] 判定：存在可见偏差，需检查（见上表）")

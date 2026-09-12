#!/usr/bin/env node
// VMD golden 数据生成器 —— 解析逻辑逐行移植自 babylon-mmd：
//   esm/Loader/Parser/vmdObject.js       (VmdData.CheckedCreate / BoneKeyFrame / MorphKeyFrame)
//   esm/Loader/Parser/mmdDataDeserializer.js (getDecoderString / getUint32 / getFloat32Tuple)
// 产出：counts + 分区原始字节 FNV-1a64（bone / morph / property）+
//       全量键规范流 FNV-1a64 + 轨道聚合 + property（表示枠）键 + 确定性抽样全字段。
// C# 侧 VmdParser 必须与这里的每一个数字 / 字节 / 比特位一致。
//
// 用法: node gen_vmd_golden.mjs <vmdPath> <outJsonPath>

import { readFileSync, writeFileSync } from "node:fs";

const SIGNATURE = "Vocaloid Motion Data 0002";
const SIGNATURE_BYTES = 30;
const MODEL_NAME_BYTES = 20;
const BONE_KF_BYTES = 15 + 4 + 12 + 16 + 64; // 111
const MORPH_KF_BYTES = 15 + 4 + 4;           // 23
const CAMERA_KF_BYTES = 4 + 4 + 12 + 12 + 24 + 4 + 1; // 61
const LIGHT_KF_BYTES = 4 + 12 + 12;          // 28
const SELFSHADOW_KF_BYTES = 4 + 1 + 4;       // 9
const PROPERTY_BASE_BYTES = 4 + 1;           // + 4 (ikStateCount)
const IK_STATE_BYTES = 20 + 1;

// ── FNV-1a 64 ────────────────────────────────────────────────────────
const FNV_OFFSET = 0xcbf29ce484222325n;
const FNV_PRIME = 0x100000001b3n;
const U64_MASK = 0xffffffffffffffffn;

function fnvUpdate(h, bytes) {
  for (let i = 0; i < bytes.length; i++) {
    h ^= BigInt(bytes[i]);
    h = (h * FNV_PRIME) & U64_MASK;
  }
  return h;
}
function fnvHex(h) { return h.toString(16).padStart(16, "0"); }

// ── float32 → uint32 比特位 ─────────────────────────────────────────
const _f32 = new Float32Array(1);
const _u32 = new Uint32Array(_f32.buffer);
function f32bits(v) { _f32[0] = v; return _u32[0]; }

// ── 反序列化器（移植自 mmdDataDeserializer.js，仅保留 VMD 用到的部分）──
class Deserializer {
  constructor(buffer) {
    this.buf = buffer;
    this.view = new DataView(buffer);
    this.u8 = new Uint8Array(buffer);
    this.offset = 0;
    this.decoder = new TextDecoder("shift-jis");
  }
  get bytesAvailable() { return this.buf.byteLength - this.offset; }
  getUint32() { const v = this.view.getUint32(this.offset, true); this.offset += 4; return v; }
  getUint8() { return this.u8[this.offset++]; }
  getFloat32Tuple(n) {
    const out = [];
    for (let i = 0; i < n; i++) { out.push(this.view.getFloat32(this.offset, true)); this.offset += 4; }
    return out;
  }
  // trim=true：在第一个 0x00 字节处截断（babylon-mmd getDecoderString 语义）
  getDecoderString(len, trim) {
    let bytes = this.u8.subarray(this.offset, this.offset + len);
    this.offset += len;
    if (trim) {
      for (let i = 0; i < bytes.length; ++i) {
        if (bytes[i] === 0) { bytes = bytes.subarray(0, i); break; }
      }
    }
    return this.decoder.decode(bytes);
  }
}

// ── 主流程 ───────────────────────────────────────────────────────────
const [vmdPath, outPath] = process.argv.slice(2);
if (!vmdPath || !outPath) {
  console.error("用法: node gen_vmd_golden.mjs <vmdPath> <outJsonPath>");
  process.exit(1);
}

const fileBytes = readFileSync(vmdPath);
const ab = fileBytes.buffer.slice(fileBytes.byteOffset, fileBytes.byteOffset + fileBytes.byteLength);
const ds = new Deserializer(ab);

// —— VmdData.CheckedCreate 的结构校验（逐行对应）——
if (ds.bytesAvailable < SIGNATURE_BYTES + MODEL_NAME_BYTES) throw new Error("文件过短");
const signature = new TextDecoder("utf-8").decode(ab.slice(0, SIGNATURE_BYTES));
if (!signature.startsWith(SIGNATURE)) throw new Error("签名不符: " + JSON.stringify(signature));
// 模型名区 = 30..50（babylon-mmd CheckedCreate 是跳过；这里读出来留作 golden 基准）
ds.offset = SIGNATURE_BYTES;
const modelName = ds.getDecoderString(MODEL_NAME_BYTES, true);

if (ds.bytesAvailable < 4) throw new Error("缺 boneKeyFrameCount");
const boneKeyFrameCount = ds.getUint32();
if (ds.bytesAvailable < boneKeyFrameCount * BONE_KF_BYTES) throw new Error("bone 区越界");
const boneOffset = ds.offset;
ds.offset += boneKeyFrameCount * BONE_KF_BYTES;

if (ds.bytesAvailable < 4) throw new Error("缺 morphKeyFrameCount");
const morphKeyFrameCount = ds.getUint32();
if (ds.bytesAvailable < morphKeyFrameCount * MORPH_KF_BYTES) throw new Error("morph 区越界");
const morphOffset = ds.offset;
ds.offset += morphKeyFrameCount * MORPH_KF_BYTES;

let cameraKeyFrameCount = 0, lightKeyFrameCount = 0;
if (ds.bytesAvailable !== 0) {
  if (ds.bytesAvailable < 4) throw new Error("缺 cameraKeyFrameCount");
  cameraKeyFrameCount = ds.getUint32();
  if (ds.bytesAvailable < cameraKeyFrameCount * CAMERA_KF_BYTES) throw new Error("camera 区越界");
  ds.offset += cameraKeyFrameCount * CAMERA_KF_BYTES;
  if (ds.bytesAvailable < 4) throw new Error("缺 lightKeyFrameCount");
  lightKeyFrameCount = ds.getUint32();
  if (ds.bytesAvailable < lightKeyFrameCount * LIGHT_KF_BYTES) throw new Error("light 区越界");
  ds.offset += lightKeyFrameCount * LIGHT_KF_BYTES;
}

let selfShadowKeyFrameCount = 0;
if (ds.bytesAvailable !== 0) {
  if (ds.bytesAvailable < 4) throw new Error("缺 selfShadowKeyFrameCount");
  selfShadowKeyFrameCount = ds.getUint32();
  if (ds.bytesAvailable < selfShadowKeyFrameCount * SELFSHADOW_KF_BYTES) throw new Error("selfShadow 区越界");
  ds.offset += selfShadowKeyFrameCount * SELFSHADOW_KF_BYTES;
}

let propertyKeyFrameCount = 0;
let propertyOffset = 0, propertyBytes = 0;
const propertyKeys = [];
if (ds.bytesAvailable !== 0) {
  if (ds.bytesAvailable < 4) throw new Error("缺 propertyKeyFrameCount");
  propertyKeyFrameCount = ds.getUint32();
  propertyOffset = ds.offset;
  for (let i = 0; i < propertyKeyFrameCount; ++i) {
    if (ds.bytesAvailable < PROPERTY_BASE_BYTES) throw new Error("property 帧越界");
    const frameNumber = ds.getUint32();
    const visible = ds.getUint8() !== 0;          // babylon-mmd：byte != 0 ⇒ 可见
    if (ds.bytesAvailable < 4) throw new Error("缺 ikStateCount");
    const ikStateCount = ds.getUint32();
    if (ds.bytesAvailable < ikStateCount * IK_STATE_BYTES) throw new Error("ikState 区越界");
    const ikStates = [];
    for (let j = 0; j < ikStateCount; ++j) {
      const rawBytes = ds.u8.subarray(ds.offset, ds.offset + 20);
      const ikName = ds.getDecoderString(20, true);
      const ikEnabled = ds.getUint8() !== 0;
      ikStates.push({ name: ikName, nameRaw: hex(trimName(rawBytes)), enabled: ikEnabled });
    }
    propertyKeys.push({ frame: frameNumber, visible, ikStates });
  }
  propertyBytes = ds.offset - propertyOffset;
}
const leftoverBytes = ds.bytesAvailable;
if (leftoverBytes > 0) console.warn(`警告: 解析后剩余 ${leftoverBytes} 字节`);

// —— 分区原始字节哈希（验证 C# 侧分区定位）——
const u8all = new Uint8Array(ab);
const boneFnv = fnvUpdate(FNV_OFFSET, u8all.subarray(boneOffset, boneOffset + boneKeyFrameCount * BONE_KF_BYTES));
const morphFnv = fnvUpdate(FNV_OFFSET, u8all.subarray(morphOffset, morphOffset + morphKeyFrameCount * MORPH_KF_BYTES));
const propertyFnv = fnvUpdate(FNV_OFFSET, u8all.subarray(propertyOffset, propertyOffset + propertyBytes));

// —— 全量键解析（文件顺序）+ 轨道聚合 + 规范流哈希 ——
// 名字处理：原始字节是权威（WHATWG 与 .NET 932 对非标准 Shift-JIS 字节映射不同，
// 解码字符串是解码器风味）；规范流哈希与轨道聚合键都使用原始字节。
const SEP = new Uint8Array([0x1f]);
let streamHash = FNV_OFFSET;

function trimName(raw) {
  const z = raw.indexOf(0);
  return z < 0 ? raw : raw.subarray(0, z);
}
function hex(b) { return [...b].map(x => x.toString(16).padStart(2, "0")).join(""); }

function hashU32(h, v) {
  const b = new Uint8Array(4);
  new DataView(b.buffer).setUint32(0, v, true);
  return fnvUpdate(h, b);
}
function hashF32(h, v) { return hashU32(h, f32bits(v)); }

const boneKeys = [];
ds.offset = boneOffset; // 校验阶段已顺序扫过全文件，读取阶段回到分区起点（babylon-mmd 用显式 _startOffset，等价）
for (let i = 0; i < boneKeyFrameCount; i++) {
  const raw = ds.u8.subarray(ds.offset, ds.offset + 15);
  const nameRaw = trimName(raw);
  const name = ds.getDecoderString(15, true);
  const frame = ds.getUint32();
  const pos = ds.getFloat32Tuple(3);
  const rot = ds.getFloat32Tuple(4);
  const interp = new Uint8Array(64);
  for (let k = 0; k < 64; k++) interp[k] = ds.getUint8();
  boneKeys.push({ name, nameRaw: hex(nameRaw), frame, pos, rot, interp });
  streamHash = fnvUpdate(streamHash, nameRaw);
  streamHash = fnvUpdate(streamHash, SEP);
  streamHash = hashU32(streamHash, frame);
  streamHash = fnvUpdate(streamHash, SEP);
  for (const v of pos) streamHash = hashF32(streamHash, v);
  for (const v of rot) streamHash = hashF32(streamHash, v);
  streamHash = fnvUpdate(streamHash, interp);
}

const morphKeys = [];
ds.offset = morphOffset;
for (let i = 0; i < morphKeyFrameCount; i++) {
  const raw = ds.u8.subarray(ds.offset, ds.offset + 15);
  const nameRaw = trimName(raw);
  const name = ds.getDecoderString(15, true);
  const frame = ds.getUint32();
  const weight = ds.view.getFloat32(ds.offset, true); ds.offset += 4;
  morphKeys.push({ name, nameRaw: hex(nameRaw), frame, weight });
  streamHash = fnvUpdate(streamHash, nameRaw);
  streamHash = fnvUpdate(streamHash, SEP);
  streamHash = hashU32(streamHash, frame);
  streamHash = fnvUpdate(streamHash, SEP);
  streamHash = hashF32(streamHash, weight);
}

// 轨道聚合（按原始字节键控；首次出现顺序；first/last 按"文件顺序首/末"记录）
function aggregate(keys) {
  const map = new Map();
  for (const k of keys) {
    let t = map.get(k.nameRaw);
    if (!t) { t = { nameRaw: k.nameRaw, name: k.name, count: 0, firstFrame: k.frame, lastFrame: k.frame }; map.set(k.nameRaw, t); }
    t.count++;
    t.lastFrame = k.frame;
  }
  return [...map.values()];
}

// 确定性抽样：步长抽样覆盖全文件 + 恒含首末
function sampleIndices(count) {
  if (count === 0) return [];
  const stride = Math.max(1, Math.floor(count / 300));
  const idx = [];
  for (let i = 0; i < count; i += stride) idx.push(i);
  if (idx[idx.length - 1] !== count - 1) idx.push(count - 1);
  return idx;
}

const sampledBoneKeys = sampleIndices(boneKeys.length).map(i => {
  const k = boneKeys[i];
  return {
    index: i, name: k.name, nameRaw: k.nameRaw, frame: k.frame,
    pos: k.pos.map(v => f32bits(v).toString()),
    rot: k.rot.map(v => f32bits(v).toString()),
    interp: [...k.interp],
  };
});
const sampledMorphKeys = sampleIndices(morphKeys.length).map(i => {
  const k = morphKeys[i];
  return { index: i, name: k.name, nameRaw: k.nameRaw, frame: k.frame, weight: f32bits(k.weight).toString() };
});

const golden = {
  source: vmdPath.replace(/\\/g, "/"),
  fileSize: fileBytes.length,
  modelName,
  modelNameRaw: hex(trimName(fileBytes.subarray(30, 50))),
  counts: {
    bone: boneKeyFrameCount, morph: morphKeyFrameCount,
    camera: cameraKeyFrameCount, light: lightKeyFrameCount,
    selfShadow: selfShadowKeyFrameCount, property: propertyKeyFrameCount,
    leftoverBytes,
  },
  sections: {
    boneOffset, boneBytes: boneKeyFrameCount * BONE_KF_BYTES, boneFnv: fnvHex(boneFnv),
    morphOffset, morphBytes: morphKeyFrameCount * MORPH_KF_BYTES, morphFnv: fnvHex(morphFnv),
    propertyOffset, propertyBytes, propertyFnv: fnvHex(propertyFnv),
  },
  streamFnv: fnvHex(streamHash),
  boneTracks: aggregate(boneKeys),
  morphTracks: aggregate(morphKeys),
  propertyKeys,
  sampledBoneKeys,
  sampledMorphKeys,
};

writeFileSync(outPath, JSON.stringify(golden));
const mb = (fileBytes.length / 1048576).toFixed(1);
console.log(`OK ${mb}MB  bone=${boneKeyFrameCount} morph=${morphKeyFrameCount} ` +
  `camera=${cameraKeyFrameCount} light=${lightKeyFrameCount} selfShadow=${selfShadowKeyFrameCount} property=${propertyKeyFrameCount} ` +
  `boneTracks=${golden.boneTracks.length} morphTracks=${golden.morphTracks.length} ` +
  `sampled=${sampledBoneKeys.length}+${sampledMorphKeys.length} leftover=${leftoverBytes}`);

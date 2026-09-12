# -*- coding: utf-8 -*-
"""cmp_baked_world.py — 世界位置层面的「足底贴地」对拍。

MMD 侧：把 baked.vmd（IK 后、付与前）按 PMX rig 重放出世界位置。
引擎侧：--ik-bake-dump 导出的每骨世界坐标。
比较非付与祖先链上的骨（足首/つま先/足先EX/趾骨等）。
"""
import struct, sys, json, math, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pmx_bones import parse as parse_pmx

def load_baked(path):
    d = open(path, 'rb').read()
    off = 30 + 20
    n = struct.unpack_from('<I', d, off)[0]; off += 4
    rot, pos = {}, {}
    for _ in range(n):
        raw = d[off:off+15]; off += 15
        fr = struct.unpack_from('<I', d, off)[0]; off += 4
        p = struct.unpack_from('<3f', d, off); q = struct.unpack_from('<4f', d, off+12); off += 12+16+64
        try:
            nm = raw.split(b'\x00')[0].decode('cp932')
        except Exception:
            continue
        rot.setdefault(nm, {})[fr] = q
        pos.setdefault(nm, {})[fr] = p
    return rot, pos

def qx(q, v):
    x,y,z,w = q; vx,vy,vz = v
    # 行向量 v' = v * M(q)，与引擎 Matrix4x4.CreateFromQuaternion 逐元素一致
    # （x' = vx*M11 + vy*M21 + vz*M31 ...）
    return (
        vx*(1-2*(y*y+z*z)) + vy*(2*(x*y-z*w))   + vz*(2*(x*z+y*w)),
        vx*(2*(x*y+z*w))   + vy*(1-2*(x*x+z*z)) + vz*(2*(y*z-x*w)),
        vx*(2*(x*z-y*w))   + vy*(2*(y*z+x*w))   + vz*(1-2*(x*x+y*y)),
    )

def qmul(a, b):
    ax,ay,az,aw = a; bx,by,bz,bw = b
    # 行向量约定：世界 = 局部 * 父世界 ⇒ 累积 q_world = q_local 后乘 q_parent_world
    return (
        ax*bw + ay*bz - az*by + aw*bx,
        -ax*bz + ay*bw + az*bx + aw*by,
        ax*by - ay*bx + az*bw + aw*bz,
        -ax*bx - ay*by - az*bz + aw*bw,
    )

def main():
    root = os.path.dirname(os.path.abspath(__file__))
    baked_path = sys.argv[1]
    eng = json.load(open(sys.argv[2], encoding='utf-8'))
    names = eng['bones']
    frames = {f['f']: f for f in eng['frames']}

    _, pbones = parse_pmx(os.path.join(root, '../../samples/MikuEngine.Demo/Model/1/1.pmx'))
    B = {b['name']: b for b in pbones}
    parent_of = {b['name']: (B[[x['name'] for x in pbones if x['i']==b['parent']][0]]['name']
                             if b['parent'] >= 0 else None) for b in pbones}

    brot, bpos = load_baked(baked_path)
    frs = sorted(frames)
    print('== 世界位置对拍（MMD bake 重放 vs 引擎，%d 帧）==' % len(frs))
    rows = []
    for bi, nm in enumerate(names):
        if nm in brot and frs[0] in brot[nm]:
            pass
        else:
            continue
        # 跳过付与驱动骨及付与祖先链上的骨（baked 不含付与，重放世界无意义）
        anc, p = [], parent_of.get(nm)
        while p:
            anc.append(p); p = parent_of.get(p)
        if any(B[a]['append'] and B[a]['append'][0] != B[a]['i'] for a in anc + [nm]):
            continue
        errs = []
        for f in frs:
            # 重放整条祖先链的世界位置（行向量）：
            #   world_pos[bn] = parent_world_pos + (bindPos + animPos) 旋转于「父的累积世界旋转」
            #   world_rot[bn] = world_rot[parent] * q_local（后乘累积）
            # 骨自身旋转不影响自身原点位置。
            chain = anc[::-1] + [nm]        # 根 → … → 该骨（此前误把叶子放链首，重放完全错序）
            qacc = (0.0, 0.0, 0.0, 1.0)   # 根的累积世界旋转
            pw = (0.0, 0.0, 0.0)
            for bn in chain:
                b = B[bn]
                # 本模型 PMX 的骨坐标是【模型空间绝对值】（非标准，见转换器注释），
                # 局部平移必须取相对父骨的差值 —— 与引擎 LocalPositions 同规则。
                par = parent_of.get(bn)
                rel = tuple(a-c for a, c in zip(b['pos'], B[par]['pos'])) if par else b['pos']
                t = tuple(a+c for a, c in zip(rel, bpos.get(bn, {}).get(f, (0,0,0))))
                origin = tuple(a+b for a, b in zip(pw, qx(qacc, t)))
                q = brot.get(bn, {}).get(f, (0,0,0,1))
                qacc = qmul(q, qacc)   # 骨旋转在外层：R(acc_new)=R(bone)·R(parent_chain)
                pw = origin
            m = pw
            e = frames[f]['p'][bi]
            errs.append((math.dist(m, e), f))
        errs.sort()
        vals = [x for x, _ in errs]
        rows.append((max(vals), nm, 'mean=%.4f max=%.4f @帧%d' %
                     (sum(vals)/len(vals), max(vals), errs[-1][1])))
    rows.sort(key=lambda r: -r[0])
    for mx, nm, desc in rows:
        print('  %-14s %s' % (nm, desc))

if __name__ == '__main__':
    main()

# -*- coding: utf-8 -*-
"""最小 PMX 解析器：只为导出 IK 定义与骨层级（供 IK 实现与 reze oracle 使用）。
不做渲染、不做权重，只走到 bone / morph 段之前需要的字段。
"""
import struct, sys, json, os

class R:
    def __init__(self, b): self.b = b; self.o = 0
    def u8(self):
        v = self.b[self.o]; self.o += 1; return v
    def i32(self):
        v = struct.unpack_from('<i', self.b, self.o)[0]; self.o += 4; return v
    def u32(self):
        v = struct.unpack_from('<I', self.b, self.o)[0]; self.o += 4; return v
    def f32(self):
        v = struct.unpack_from('<f', self.b, self.o)[0]; self.o += 4; return v
    def vec(self, n):
        v = struct.unpack_from('<%df' % n, self.b, self.o); self.o += 4 * n; return list(v)
    def txt(self, enc):
        n = self.i32()
        s = self.b[self.o:self.o + n]; self.o += n
        if n == 0: return ''
        return s.decode(enc, 'replace')

def main(path):
    d = open(path, 'rb').read()
    r = R(d)
    assert d[:4] == b'PMX ', 'not pmx'
    r.o = 4                                            # 跳过 magic
    ver = r.f32()
    nglob = r.u8()
    g = [r.u8() for _ in range(nglob)]
    enc = 'utf-16-le' if g[0] == 0 else 'utf-8'
    uvAdd = g[1]
    vidx, tidx, midx, bidx, moidx, ridx = g[2], g[3], g[4], g[5], g[6], g[7]
    def rix(sz):
        if sz == 1: return struct.unpack_from('<b', r.b, r.o)[0] if (setattr(r, 'o', r.o + 1) or True) else 0
        v = int.from_bytes(r.b[r.o:r.o + sz], 'little', signed=True); r.o += sz; return v
    for _ in range(4): r.txt(enc)                      # name eng comment engComment
    nv = r.i32()
    for _ in range(nv):                                 # vertices
        r.o += 12 + 12 + 8 + 16 * uvAdd
        dt = r.u8()
        if dt == 0: r.o += bidx
        elif dt == 1: r.o += 2 * bidx + 4
        elif dt == 2: r.o += 4 * bidx + 16
        elif dt == 3: r.o += 2 * bidx + 4 + 36
        elif dt == 4: r.o += 4 * bidx + 16
        else: raise ValueError('deform %d' % dt)
    nf = r.i32(); r.o += (nf // 3) * 3 * vidx * 0  # PMX stores vertex count not face count
    r.o -= 0
    # 修正：上面读的是 i32 顶点数；索引数 = nf * vidx
    r.o += nf * vidx
    ntex = r.i32()
    for _ in range(ntex): r.txt(enc)
    nmat = r.i32()
    for _ in range(nmat):
        r.txt(enc); r.txt(enc)
        r.o += 16 + 12 + 4 + 12 + 1 + 16 + 4
        r.o += tidx + tidx + 1
        toonFlag = r.u8()
        r.o += 1 if toonFlag == 1 else tidx
        r.txt(enc); r.o += 4
    nb = r.i32()
    bones, iks = [], []
    for i in range(nb):
        name = r.txt(enc); eng = r.txt(enc)
        pos = r.vec(3); parent = rix(bidx); order = r.i32()
        flag = struct.unpack_from('<H', r.b, r.o)[0]; r.o += 2
        tail = r.vec(3) if (flag & 1) else None
        r.o += 4 if (flag & 1) else 12
        if flag & 0x100: r.o += bidx + 4
        if flag & 0x200: r.o += 12
        if flag & 0x400: r.o += 12
        if flag & 0x800: r.o += 12
        if flag & 0x1000: r.o += 12
        if flag & 0x2000: r.o += 4
        bones.append({'i': i, 'name': name, 'eng': eng, 'pos': pos, 'parent': parent, 'order': order, 'flag': flag})
        if flag & 0x20:
            target = rix(bidx); it = r.i32(); angle = r.f32(); nl = r.i32()
            links = []
            for _ in range(nl):
                li = rix(bidx); lim = r.u8()
                lo = r.vec(3) if lim else [0.0, 0.0, 0.0]
                hi = r.vec(3) if lim else [0.0, 0.0, 0.0]
                links.append({'bone': li, 'limited': bool(lim), 'min': lo, 'max': hi})
            iks.append({'bone': i, 'target': target, 'iteration': it, 'angle': angle, 'links': links})
    out = {'path': os.path.basename(path), 'version': ver, 'encoding': enc,
           'vertexCount': nv, 'boneCount': nb, 'bones': bones, 'iks': iks}
    return out

if __name__ == '__main__':
    res = main(sys.argv[1])
    dest = sys.argv[2]
    with open(dest, 'w', encoding='utf-8') as f:
        json.dump(res, f, ensure_ascii=False, indent=1)
    print('bones=%d iks=%d -> %s' % (res['boneCount'], len(res['iks']), dest))
    for ik in res['iks']:
        nm = res['bones'][ik['bone']]['name']
        tn = res['bones'][ik['target']]['name'] if 0 <= ik['target'] < len(res['bones']) else '?'
        chain = ' <- '.join(res['bones'][l['bone']]['name'] + ('(L)' if l['limited'] else '') for l in ik['links'])
        print('IK %-12s target=%-10s iter=%d angle=%.3f  links: %s' % (nm, tn, ik['iteration'], ik['angle'], chain))

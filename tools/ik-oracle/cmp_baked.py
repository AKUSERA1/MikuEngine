# -*- coding: utf-8 -*-
"""cmp_baked.py — 引擎 IK 输出 vs mmdbridge 烘焙真值（baked.vmd）逐帧对拍。

baked.vmd 由 mmdbridge fork 在 MMD 本体（关物理）里烘焙，语义（f0 实测判定）=
MMD 帧管线「IK 之后、付与之前」的骨架状态：含 FK+IK，**不含付与**（付与驱动的骨
如 足D/足首D 在 baked 里恒 identity，本脚本会将其剔除、只作参考不计入判定）。
PMX 骨的绑定局部旋转恒为单位阵，所以 VMD 四元数即最终局部旋转，可与引擎的
FinalRotations（已含 IkRotations 叠加）直接比（符号不敏感）。

用法: python cmp_baked.py <baked.vmd> <engine_dump.json> [--top N]
"""
import struct, sys, json, math, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pmx_bones import parse as parse_pmx

def load_baked(path, want):
    """单遍扫描 baked.vmd，返回 {bone: {frame: (x,y,z,w)}}（仅 want 里的骨）。"""
    d = open(path, 'rb').read()
    off = 30 + 20
    n = struct.unpack_from('<I', d, off)[0]; off += 4
    out = {nm: {} for nm in want}
    for _ in range(n):
        raw = d[off:off+15]; off += 15
        fr = struct.unpack_from('<I', d, off)[0]; off += 4
        off += 12
        rot = struct.unpack_from('<4f', d, off); off += 16
        off += 64
        try:
            nm = raw.split(b'\x00')[0].decode('cp932')
        except Exception:
            continue
        if nm in out and fr not in out[nm]:
            out[nm][fr] = rot
    return out

def load_motion_keys(path, want):
    """原始 动作+IK.vmd 的位置键（帧对齐校验用）。返回 {bone: {frame: (x,y,z)}}。"""
    d = open(path, 'rb').read()
    off = 30 + 20
    n = struct.unpack_from('<I', d, off)[0]; off += 4
    out = {nm: {} for nm in want}
    for _ in range(n):
        raw = d[off:off+15]; off += 15
        fr = struct.unpack_from('<I', d, off)[0]; off += 4
        pos = struct.unpack_from('<3f', d, off); off += 12
        off += 16 + 64
        try:
            nm = raw.split(b'\x00')[0].decode('cp932')
        except Exception:
            continue
        if nm in out and fr not in out[nm]:
            out[nm][fr] = pos
    return out

def qang(a, b):
    """两四元数的夹角（度），符号不敏感。"""
    dot = abs(a[0]*b[0] + a[1]*b[1] + a[2]*b[2] + a[3]*b[3])
    dot = min(1.0, max(-1.0, dot))
    return 2.0 * math.acos(dot) * 180.0 / math.pi

def main():
    baked_path, eng_path = sys.argv[1], sys.argv[2]
    top = int(sys.argv[sys.argv.index('--top')+1]) if '--top' in sys.argv else 8
    eng = json.load(open(eng_path, encoding='utf-8'))
    names = eng['bones']

    # baked.vmd = MMD 帧管线「IK 之后、付与之前」的骨架状态（帧 0 键值 vs 原始动效实测判定）。
    # 付与驱动的骨在 baked 里恒 identity（付与会在播放时重新叠加），这类骨的比较无意义，只作参考。
    pmx_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
        '../../samples/MikuEngine.Demo/Model/1/1.pmx')
    _, pbones = parse_pmx(os.path.abspath(pmx_path))
    append_driven = {b['name'] for b in pbones if b['append'] and b['append'][0] != b['i']}
    frames = {f['f']: f['r'] for f in eng['frames']}
    fnums = sorted(frames)

    baked = load_baked(baked_path, set(names))

    # 帧对齐校验：baked 的 足首 位置键 vs 原始动作+IK.vmd 的位置键（两边都是纯动画数据）
    motion = load_motion_keys(
        baked_path.replace('baked.vmd', '动作+IK.vmd'),
        {'左足首', '右足首', '左ひざ', '右ひざ'})
    baked_pos = load_baked_pos = None
    # 位置需重扫一遍 baked —— 直接复用 load_baked 不行（它只存 rot），单独扫：
    d = open(baked_path, 'rb').read()
    off = 30 + 20
    n = struct.unpack_from('<I', d, off)[0]; off += 4
    bpos = {nm: {} for nm in motion}
    for _ in range(n):
        raw = d[off:off+15]; off += 15
        fr = struct.unpack_from('<I', d, off)[0]; off += 4
        pos = struct.unpack_from('<3f', d, off); off += 12
        off += 16 + 64
        try:
            nm = raw.split(b'\x00')[0].decode('cp932')
        except Exception:
            continue
        if nm in bpos and fr not in bpos[nm]:
            bpos[nm][fr] = pos
    print('== 帧对齐校验（baked vs 动作+IK.vmd 的同名位置键，仅在原始键帧处比较）==')
    for nm in sorted(motion):
        common = sorted(set(motion[nm]) & set(bpos[nm]))
        if not common:
            print('  %-8s 无公共键帧' % nm); continue
        errs = [math.dist(motion[nm][f], bpos[nm][f]) for f in common]
        print('  %-8s 公共键帧 %4d  位置差 max=%.5f mean=%.5f' %
              (nm, len(common), max(errs), sum(errs)/len(errs)))

    print('\n== 逐骨旋转角误差（引擎 vs baked，%d 帧）==' % len(fnums))
    rows = []
    for bi, nm in enumerate(names):
        rb = baked.get(nm)
        if not rb:
            rows.append((None, nm, 'baked 中无此骨（跳过）'))
            continue
        errs = []
        for f in fnums:
            if f in rb:
                errs.append((qang(frames[f][bi], rb[f]), f))
        if not errs:
            rows.append((None, nm, '无公共帧'))
            continue
        errs.sort()
        vals = [e for e, _ in errs]
        vals_sorted = sorted(vals)
        p95 = vals_sorted[int(len(vals_sorted)*0.95)-1]
        mean = sum(vals)/len(vals)
        rows.append((max(vals), nm, 'mean=%.2f° p95=%.2f° max=%.2f° @帧%s' %
                     (mean, p95, max(vals), errs[-1][1])))
    ok = [r for r in rows if r[0] is not None]
    miss = [r for r in rows if r[0] is None]
    ok.sort(reverse=True)
    ok_ref = [r for r in ok if r[1] in append_driven]
    ok = [r for r in ok if r[1] not in append_driven]
    print('  —— 按最大误差降序（前 %d）——' % top)
    for mx, nm, desc in ok[:top]:
        print('  %-14s %s' % (nm, desc))
    print('  —— 全部 ——')
    for mx, nm, desc in sorted(ok, key=lambda r: r[1]):
        print('   %-14s %s' % (nm, desc))
    for _, nm, desc in miss:
        print('   %-14s %s' % (nm, desc))
    print('\n汇总：%d 骨可比 / %d 骨缺数据；全体最大角误差 = %.2f°（%s）' %
          (len(ok), len(miss), ok[0][0], ok[0][1]))

if __name__ == '__main__':
    main()

# -*- coding: utf-8 -*-
"""pmx_bones.py — 最小 PMX 骨段解析器（镜像引擎 PmxParser 的字段布局）。
输出全部骨的 name/parent/order/flag/append(src,ratio)/axisLimit/IK 定义。
"""
import struct, sys, json

class R:
    __slots__=('b','o')
    def __init__(self,b): self.b=b; self.o=0
    def i8(self):
        v=struct.unpack_from('<b',self.b,self.o)[0]; self.o+=1; return v
    def u8(self):
        v=self.b[self.o]; self.o+=1; return v
    def u16(self):
        v=struct.unpack_from('<H',self.b,self.o)[0]; self.o+=2; return v
    def i32(self):
        v=struct.unpack_from('<i',self.b,self.o)[0]; self.o+=4; return v
    def f32(self):
        v=struct.unpack_from('<f',self.b,self.o)[0]; self.o+=4; return v
    def v3(self):
        v=struct.unpack_from('<3f',self.b,self.o); self.o+=12; return v
    def v4(self):
        v=struct.unpack_from('<4f',self.b,self.o); self.o+=16; return v
    def txt(self,enc):
        n=self.i32(); s=self.b[self.o:self.o+n]; self.o+=n
        return s.decode(enc,'replace') if n else ''
    def idx(self,sz,signed=True):
        v=int.from_bytes(self.b[self.o:self.o+sz],'little',signed=signed); self.o+=sz; return v

def parse(path):
    d=open(path,'rb').read(); r=R(d); r.o=4
    ver=r.f32(); ng=r.u8(); g=[r.u8() for _ in range(ng)]
    enc='utf-16-le' if g[0]==0 else 'utf-8'
    uvAdd,vidx,tidx,midx,bidx,moidx,ridx=g[1],g[2],g[3],g[4],g[5],g[6],g[7]
    r.txt(enc); r.txt(enc); r.txt(enc); r.txt(enc)   # model name/comment x2
    nv=r.i32()
    for _ in range(nv):
        r.o+=12+12+8+16*uvAdd
        dt=r.u8()
        if dt==0: r.o+=bidx
        elif dt==1: r.o+=2*bidx+4
        elif dt==2 or dt==4: r.o+=4*bidx+16
        elif dt==3: r.o+=2*bidx+4+36
        else: raise ValueError('deform %d @%d'%(dt,r.o))
        r.o+=4   # edge scale
    nidx=r.i32(); r.o+=nidx*vidx
    ntex=r.i32()
    for _ in range(ntex): r.txt(enc)
    nmat=r.i32()
    for _ in range(nmat):
        r.txt(enc); r.txt(enc)
        r.v4(); r.v3(); r.f32(); r.v3()
        r.u8(); r.v4(); r.f32()
        r.idx(tidx); r.idx(tidx); r.u8()
        shared=r.u8()
        if shared==1: r.u8()
        else: r.idx(tidx)
        r.txt(enc)
        r.i32()
    nb=r.i32()
    bones=[]
    for i in range(nb):
        nm=r.txt(enc); eng=r.txt(enc)
        pos=r.v3(); parent=r.idx(bidx); order=r.i32(); flag=r.u16()
        tailIsBone=(flag&1)!=0
        if tailIsBone: r.idx(bidx)
        else: r.v3()
        ap=None
        if flag&0x100 or flag&0x200:
            ap=(r.idx(bidx), r.f32())
        axis=r.v3() if (flag&0x400) else None
        if flag&0x800: r.v3(); r.v3()
        if flag&0x2000: r.i32()
        ik=None
        if flag&0x20:
            target=r.idx(bidx); it=r.i32(); ang=r.f32(); nl=r.i32()
            links=[]
            for _ in range(nl):
                lb=r.idx(bidx); lim=r.u8()
                lo=r.v3() if lim else None
                hi=r.v3() if lim else None
                links.append({'bone':lb,'limited':bool(lim),'min':lo,'max':hi})
            ik={'target':target,'iter':it,'angle':ang,'links':links}
        bones.append({'i':i,'name':nm,'pos':pos,'parent':parent,'order':order,'flag':flag,
                      'append':ap,'axis':axis,'ik':ik})
    return ver,bones

if __name__=='__main__':
    ver,bones=parse(sys.argv[1])
    byi={b['i']:b for b in bones}
    want=sys.argv[2:] if len(sys.argv)>2 else None
    for b in bones:
        if want and b['name'] not in want: continue
        ap=b['append']
        aps='%s(ratio=%.2f)'%(byi[ap[0]]['name'],ap[1]) if ap else '-'
        aks='%s -> %s iter=%d ang=%.3f links:%s'%(
            b['name'], byi[b['ik']['target']]['name'] if b['ik'] else '-',
            b['ik']['iter'] if b['ik'] else 0, b['ik']['angle'] if b['ik'] else 0,
            [(byi[l['bone']]['name'],l['limited'],
              [round(x,3) for x in l['min']] if l['min'] else '',
              [round(x,3) for x in l['max']] if l['max'] else '') for l in b['ik']['links']]
        ) if b['ik'] else ''
        fl=[]
        if b['flag']&0x100: fl.append('付与R')
        if b['flag']&0x200: fl.append('付与M')
        if b['flag']&0x80: fl.append('local')
        if b['flag']&0x400: fl.append('軸固定')
        print('%-14s i=%3d order=%4d parent=%-12s [%s] append=%s %s'%(
            b['name'],b['i'],b['order'],byi[b['parent']]['name'] if b['parent']>=0 else '-',
            ','.join(fl) or '-',aps, aks))

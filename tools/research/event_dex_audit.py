import zipfile, struct, re, json, sys
from pathlib import Path

def instructions(data, code):
    if not code:return
    count=struct.unpack_from('<I',data,code+12)[0]
    units=struct.unpack_from('<'+'H'*count,data,code+16)
    p=0
    while p<len(units):
        w=units[p]; op=w&255; size=1
        if op==0 and w!=0:
            if w==0x100:size=4+units[p+1]*2
            elif w==0x200:size=2+units[p+1]*4
            elif w==0x300:size=4+(units[p+1]*(units[p+2]+(units[p+3]<<16))+1)//2
        elif op in (2,5,8,0x13,0x15,0x16,0x19,0x1a,0x1c,0x1f,0x20,0x22,0x23,0x29,0xfe,0xff):size=2
        elif op in (3,6,9,0x14,0x17,0x1b,0x24,0x25,0x26,0x2a,0x2b,0x2c,0xfc,0xfd):size=3
        elif op==0x18:size=5
        elif 0x2d<=op<=0x3d or 0x44<=op<=0x6d or 0x90<=op<=0xaf or 0xd0<=op<=0xe2:size=2
        elif 0x6e<=op<=0x72 or 0x74<=op<=0x78:size=3
        elif op in (0xfa,0xfb):size=4
        if p+size>len(units):raise ValueError((hex(code),p,hex(op)))
        yield code+16+p*2,op,units[p:p+size]
        p+=size

def leb(data, pos):
    v = shift = 0
    while True:
        b = data[pos]; pos += 1; v |= (b & 127) << shift
        if b < 128: return v, pos
        shift += 7

def read_dex(data):
    u32=lambda p:struct.unpack_from('<I',data,p)[0]
    sc,so=struct.unpack_from('<II',data,56)
    ss=[]
    for i in range(sc):
        p=u32(so+i*4); _,p=leb(data,p); q=data.index(b'\0',p)
        ss.append(data[p:q].decode('utf-8','replace'))
    tc,to=struct.unpack_from('<II',data,64)
    ts=[ss[u32(to+i*4)] for i in range(tc)]
    pc,po=struct.unpack_from('<II',data,72)
    ps=[]
    for i in range(pc):
        _,rt,off=struct.unpack_from('<III',data,po+i*12)
        params=[] if off==0 else [ts[struct.unpack_from('<H',data,off+4+j*2)[0]] for j in range(u32(off))]
        ps.append('('+''.join(params)+')'+ts[rt])
    mc,mo=struct.unpack_from('<II',data,88)
    ms=[]
    for i in range(mc):
        c,p,n=struct.unpack_from('<HHI',data,mo+i*8)
        ms.append((ts[c],ss[n],ps[p]))
    cc,co=struct.unpack_from('<II',data,96)
    methods=[]
    for i in range(cc):
        c,_,_,_,_,_,cd,_=struct.unpack_from('<IIIIIIII',data,co+i*32)
        if cd==0:continue
        sf,p=leb(data,cd); inf,p=leb(data,p); dm,p=leb(data,p); vm,p=leb(data,p)
        for _ in range(sf+inf): _,p=leb(data,p); _,p=leb(data,p)
        for count in (dm,vm):
            idx=0
            for _ in range(count):
                inc,p=leb(data,p); flags,p=leb(data,p); code,p=leb(data,p); idx+=inc
                methods.append((*ms[idx],code))
    return ss,ts,ms,methods

if __name__=='__main__':
    pattern=re.compile(sys.argv[1],re.I)
    for apk in Path('research/apps').glob('*.apk'):
        print('\nAPK',apk.name)
        with zipfile.ZipFile(apk) as z:
            for dn in z.namelist():
                if not dn.endswith('.dex'):continue
                d=z.read(dn); ss,ts,ms,methods=read_dex(d)
                for c,n,p,o in methods:
                    hits=[]
                    for off,op,words in instructions(d,o):
                        if 0x6e<=op<=0x72 or 0x74<=op<=0x78:
                            target=' '.join(ms[words[1]])
                            if pattern.search(target):hits.append((hex(off),'invoke',target))
                        elif op in (0x1a,0x1b):
                            idx=words[1] if op==0x1a else words[1]+(words[2]<<16)
                            if pattern.search(ss[idx]):hits.append((hex(off),'const-string',ss[idx]))
                    if pattern.search(c+' '+n) or hits:
                        print(dn,hex(o),c,n,p)
                        for hit in hits:print('   ',*hit)

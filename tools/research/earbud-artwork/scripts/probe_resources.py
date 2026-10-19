#!/usr/bin/env python3
"""Offline APK contract inspection and explicitly enabled artwork metadata probes.

Run with Python 3.11+, preferably python -B. APK code is never executed.
--offline performs no network activity; --probe performs one named metadata POST
and at most one verified vendor-CDN GET. --known-defaults queries one documented
App default per earphone, serially at no more than one network request per second.
Authentication material is derived only
in memory and is never included in evidence or command output. Dependencies are
not installed automatically; cryptography is needed for the authenticated whitelist
and network modes.
"""
import sys
sys.dont_write_bytecode = True
import argparse
import base64
import hashlib
import hmac
import io
import json
import gzip
import os
from pathlib import Path, PurePosixPath
import re
import struct
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parents[4] / "research/earbud-artwork"
RESEARCH = HERE.parent
API_BASE = "https://iot-earbuds-cn.heytapmobi.com/"
MAX_BYTES = 32 * 1024 * 1024
_LAST_NETWORK_REQUEST = None


def network_pace():
    global _LAST_NETWORK_REQUEST
    if _LAST_NETWORK_REQUEST is not None:
        time.sleep(max(0.0, 1.0 - (time.monotonic() - _LAST_NETWORK_REQUEST)))
    _LAST_NETWORK_REQUEST = time.monotonic()
APK_SPEC = {
    "com.heytap.headset": {
        "filename": "欢律.apk", "version_name": "17.6.5",
        "version_code": 117006005, "channel": "2",
        "sha256": "eacb314e092cfb6c2f075eee834498412255cadf184978f3dc16400f6e1dd5d4",
        "dex": "classes3.dex", "interface": "Lcom/oplus/melody/model/net/H;",
    },
}
class Dex:
 def __init__(self,b):
  self.b=b
  self.u=lambda p:struct.unpack_from("<I",b,p)[0]
  self.us=lambda p:struct.unpack_from("<H",b,p)[0]
  n,p=self.u(56),self.u(60)
  self.strs=[]
  for i in range(n):
   q=self.u(p+4*i);_,q=self.leb(q);z=b.index(0,q);self.strs.append(b[q:z].decode("utf8","replace"))
  n,p=self.u(64),self.u(68);self.types=[self.strs[self.u(p+4*i)] for i in range(n)]
  n,p=self.u(72),self.u(76);self.protos=[]
  for i in range(n):
   short,ret,args=struct.unpack_from("<III",b,p+12*i)
   a=[] if not args else [self.types[self.us(args+4+2*j)] for j in range(self.u(args))]
   self.protos.append("("+''.join(a)+")"+self.types[ret])
  n,p=self.u(80),self.u(84);self.fields=[]
  for i in range(n):
   c,t,s=struct.unpack_from("<HHI",b,p+8*i);self.fields.append(self.types[c]+"->"+self.strs[s]+":"+self.types[t])
  n,p=self.u(88),self.u(92);self.methods=[]
  for i in range(n):
   c,t,s=struct.unpack_from("<HHI",b,p+8*i);self.methods.append(self.types[c]+"->"+self.strs[s]+self.protos[t])
  n,p=self.u(96),self.u(100);self.classes={}
  for i in range(n):
   c,ac,s,ifs,src,an,cd,sv=struct.unpack_from("<8I",b,p+32*i)
   f,m=[],[]
   if cd:
    ns,cd=self.leb(cd);ni,cd=self.leb(cd);nd,cd=self.leb(cd);nv,cd=self.leb(cd)
    for nn in [ns,ni]:
     idx=0
     for j in range(nn):
      d,cd=self.leb(cd);a,cd=self.leb(cd);idx+=d;f.append((idx,a))
    for nn in [nd,nv]:
     idx=0
     for j in range(nn):
      d,cd=self.leb(cd);a,cd=self.leb(cd);co,cd=self.leb(cd);idx+=d;m.append((idx,a,co))
   self.classes[self.types[c]]={"fields":f,"methods":m,"ann":an,"super":self.types[s] if s!=0xffffffff else None}
 def leb(self,p):
  v=0;sh=0
  while True:
   x=self.b[p];p+=1;v|=(x&127)<<sh;sh+=7
   if x<128:return v,p
 def value(self,p):
  h=self.b[p];p+=1;t=h&31;n=(h>>5)+1
  if t==0x1c:
   z,p=self.leb(p);a=[]
   for i in range(z):v,p=self.value(p);a.append(v)
   return a,p
  if t==0x1d:return self.annotation(p)
  if t==0x1e:return None,p
  if t==0x1f:return bool(h>>5),p
  v=int.from_bytes(self.b[p:p+n],"little");p+=n
  if t==0x17:v=self.strs[v]
  elif t==0x18:v=self.types[v]
  elif t==0x19 or t==0x1b:v=self.fields[v]
  elif t==0x1a:v=self.methods[v]
  return v,p
 def annotation(self,p):
  t,p=self.leb(p);n,p=self.leb(p);out={"type":self.types[t]}
  for i in range(n):
   s,p=self.leb(p);v,p=self.value(p);out[self.strs[s]]=v
  return out,p
 def annset(self,p):
  if not p:return []
  out=[]
  for i in range(self.u(p)):
   q=self.u(p+4+4*i);an,_=self.annotation(q+1);out.append(an)
  return out
 def annotations(self,c):
  p=self.classes[c]["ann"]
  if not p:return {}
  ca,nf,nm,np=struct.unpack_from("<4I",self.b,p);p+=16;out={"class":self.annset(ca),"fields":{},"methods":{},"params":{}}
  for n,k,a in [(nf,"fields",self.fields),(nm,"methods",self.methods),(np,"params",self.methods)]:
   for i in range(n):
    idx,off=struct.unpack_from("<II",self.b,p);p+=8
    if k=="params":out[k][a[idx]]=[self.annset(self.u(off+4+4*j)) for j in range(self.u(off))]
    else:out[k][a[idx]]=self.annset(off)
  return out
 def insns(self,co):
  if not co:return []
  b=self.b;u=self.u;us=self.us;n=u(co+12);start=co+16;pc=0;out=[]
  widths={0:1,1:1,2:2,3:3,4:1,5:2,6:3,7:1,8:2,9:3,10:1,11:1,12:1,13:1,14:1,15:1,16:1,17:1,18:1,19:2,20:3,21:2,22:2,23:3,24:5,25:2,26:2,27:3,28:2,29:1,30:1,31:2,32:2,33:1,34:2,35:2,36:3,37:3,38:3,39:1,40:1,41:2,42:3,43:3,44:3,45:2,46:2,47:2,48:2,49:2,50:2,51:2,52:2,53:2,54:2,55:2,56:2,57:2,58:2,59:2,60:2,61:2}
  for a,z,w in [(0x44,0x6d,2),(0x6e,0x72,3),(0x74,0x78,3),(0x7b,0x8f,1),(0x90,0xaf,2),(0xb0,0xcf,1),(0xd0,0xe2,2)]:widths.update({o:w for o in range(a,z+1)})
  while pc<n:
   q=start+2*pc;v=us(q);op=v&255;w=widths.get(op,1)
   if op==0 and v:
    if v==0x100:w=4+us(q+2)*2
    elif v==0x200:w=2+us(q+2)*4
    elif v==0x300:w=4+(us(q+2)*u(q+4)+1)//2
   raw=[us(q+2*j) for j in range(min(w,n-pc))]
   info=""
   if op==0x1a:info='STR '+repr(self.strs[raw[1]])
   elif op==0x1b:info='STR '+repr(self.strs[raw[1]|(raw[2]<<16)])
   elif op in [0x1c,0x1f,0x20,0x22,0x23,0x24,0x25]:info='TYPE '+self.types[raw[1]]
   elif 0x52<=op<=0x6d:info='FIELD '+self.fields[raw[1]]
   elif op in list(range(0x6e,0x73))+list(range(0x74,0x79)):info='CALL '+self.methods[raw[1]]
   out.append((pc,op,raw,info));pc+=w
  return out


def sha256(data):
    return hashlib.sha256(data).hexdigest()




def verified_apk(package):
    spec = APK_SPEC[package]
    path = RESEARCH / "apps" / spec["filename"]
    data = path.read_bytes()
    if sha256(data) != spec["sha256"]:
        raise ValueError("APK hash mismatch: " + path.name)
    return zipfile.ZipFile(io.BytesIO(data))


def safe_message(value):
    if not isinstance(value, str):
        return value
    value = re.sub(r"https?://\S+", "[URL omitted]", value)
    value = re.sub(r"[0-9a-fA-F]{32,}", "[hex omitted]", value)
    if re.search(r"(?i)(token|secret|sign)\s*[=:]", value):
        return "[sensitive-bearing message omitted]"
    return value[:500]


def sanitize(value):
    if isinstance(value, dict):
        return {
            k: sanitize(v) for k, v in value.items()
            if k.lower() not in {"sign", "nonce", "ts", "token", "accesstoken",
                                 "access_token", "authorization", "secret", "key"}
        }
    if isinstance(value, list):
        return [sanitize(v) for v in value]
    if isinstance(value, str) and value.startswith(("http://", "https://")):
        u = urllib.parse.urlsplit(value)
        # Full URLs are unnecessary to reproduce a fresh metadata request.
        return {"url_redacted": True, "scheme": u.scheme, "host": u.hostname,
                "path_sha256": sha256(u.path.encode()), "query_present": bool(u.query)}
    return value




def crypto_modules():
    try:
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        from cryptography.hazmat.primitives.serialization import pkcs7, Encoding
        from cryptography.exceptions import InvalidTag
        return AESGCM, pkcs7, Encoding, InvalidTag
    except ImportError:
        # Reuse already present packages; never install dependencies automatically.
        roots = []
        if os.environ.get("LOCALAPPDATA"):
            roots.append(Path(os.environ["LOCALAPPDATA"]) / "uv/cache/archive-v0")
        roots.append(Path.home() / ".cache/uv/archive-v0")
        for root in roots:
            if not root.is_dir():
                continue
            directories = list(root.iterdir())
            sys.path.extend(str(p) for p in directories
                            if (p / "cffi").is_dir() or any(p.glob("_cffi_backend*")))
            candidates = [p for p in directories if (p / "cryptography").is_dir()]
            for candidate in sorted(candidates):
                sys.path.insert(0, str(candidate))
                try:
                    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
                    from cryptography.hazmat.primitives.serialization import pkcs7, Encoding
                    from cryptography.exceptions import InvalidTag
                    return AESGCM, pkcs7, Encoding, InvalidTag
                except ImportError:
                    for module in list(sys.modules):
                        if module == "cryptography" or module.startswith("cryptography."):
                            del sys.modules[module]
                    sys.path.remove(str(candidate))
        raise RuntimeError("cryptography unavailable; provide an existing installation on PYTHONPATH")




class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise urllib.error.HTTPError(req.full_url, code, "Redirect blocked", headers, fp)


def vendor_cdn(url):
    parsed = urllib.parse.urlsplit(url)
    return bool(parsed.scheme == "https" and parsed.hostname and
                (parsed.hostname == "heytapimage.com" or parsed.hostname.endswith(".heytapimage.com")) and
                not parsed.username and not parsed.password and parsed.port in (None, 443))


def bounded_read(response, limit):
    declared = response.headers.get("Content-Length")
    if declared is not None and int(declared) > limit:
        raise ValueError("Response exceeds size limit")
    value = response.read(limit + 1)
    if len(value) > limit:
        raise ValueError("Response exceeds size limit")
    return value


def response_records(value):
    records = []
    if isinstance(value, dict):
        if "resourceUrl" in value or "cartoonUrl" in value:
            records.append(value)
        for child in value.values():
            records.extend(response_records(child))
    elif isinstance(value, list):
        for child in value:
            records.extend(response_records(child))
    return records





























if __name__ == "__main__":
    raise SystemExit("Use tools/research/online-artwork/scripts/fetch_artwork.py for HeyMelody acquisition; legacy probes are archived.")

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
    "com.oplus.melody": {
        "filename": "无线耳机_17.6.3.apk", "version_name": "17.6.3",
        "version_code": 17006003, "channel": "1",
        "sha256": "c1149df9e24ed85bf84f7c81c712cee794299d2b95057c6e221fb547f2717059",
        "dex": "classes2.dex", "interface": "Lcom/oplus/melody/model/net/F;",
    },
}
CASES = {
    "x4-model": {"productId": "06EC10", "color": "0", "moduleId": "14",
                 "endpoint": "v1/earphone/resource/fetch",
                 "color_evidence": "Explicit probe value; no configured OPPO default confirmed."},
    "x3-model": {"productId": "067410", "color": "0", "moduleId": "14",
                 "endpoint": "v1/earphone/resource/fetch",
                 "color_evidence": "Explicit probe value; support3DModel=1 in both APK configurations."},
    "enco-free-control": {"productId": "060410", "color": "0",
                         "endpoint": "v1/earphone/resource/control",
                         "color_evidence": "defaultColor=0 in both recovered APK whitelists."},
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


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


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


def static_contract():
    sources = []
    for package, spec in APK_SPEC.items():
        with verified_apk(package) as archive:
            dex_bytes = archive.read(spec["dex"])
            dex = Dex(dex_bytes)
            interface = dex.annotations(spec["interface"])
            methods = []
            for method, annotations in interface.get("methods", {}).items():
                routes = [a.get("value") for a in annotations
                          if a.get("value") in ("v1/earphone/resource/fetch",
                                                "v1/earphone/resource/{type}")]
                if routes:
                    methods.append({"method": method, "http_method": "POST",
                                    "route": routes[0], "parameters": "HeaderMap, Body",
                                    "annotations": annotations,
                                    "parameter_annotations": interface["params"].get(method)})
            if len(methods) != 2:
                raise ValueError("Expected resource interface annotations not found")
            sources.append({
                "source": spec["filename"], "package": package,
                "version_name": spec["version_name"], "version_code": spec["version_code"],
                "apk_sha256": spec["sha256"], "dex_entry": spec["dex"],
                "dex_sha256": sha256(dex_bytes), "class": spec["interface"],
                "resource_methods": methods,
            })
    return {
        "schema_version": 1, "sources": sources, "api_base": API_BASE,
        "authentication": {
            "headers": ["Content-Type", "appid", "ts", "nonce", "sv", "sign"],
            "public_appid": "earphone", "sv": "v1",
            "request_body_signature": "HMAC-SHA1, lowercase hexadecimal, exact UTF-8 body bytes",
            "material_source": "APK-local encrypted application material authenticated using APK certificate digest; memory only",
            "requires_account_or_phone_identifier_in_traced_chain": False,
            "secret_material_persisted": False,
            "code_evidence": [
                {"dex": "classes2.dex", "class": "LC7/a;", "method": "c()Lkotlin/Pair;",
                 "offset_bytes": "0x0008", "behavior": "Read current APK signing certificate digest"},
                {"dex": "classes.dex", "class": "LY6/b;", "method": "F(String,byte[])String",
                 "offset_bytes": "0x0028", "behavior": "AES/GCM/NoPadding with 12-byte nonce and 128-bit tag"},
                {"dex": "classes2.dex", "class": "Lcom/oplus/melody/model/net/D;",
                 "method": "a(String,SecretKeySpec)Map", "offset_bytes": "0x0082",
                 "behavior": "HmacSha1 over the serialized request body"},
            ],
        },
        "body_defaults": {"platform": "android",
                          "channel_by_package": {"com.heytap.headset": "2", "com.oplus.melody": "1"},
                          "versionCode": "String containing package versionCode"},
        "model_module": {
            "endpoint": "v1/earphone/resource/fetch",
            "additional_body_fields": ["productId", "color", "moduleId"],
            "appearance_module_id": 14,
            "response_fields": ["fileName", "resourceUrl", "resourceName", "resourceDescription", "sha256"],
            "config_type": "Lf9/d;", "config_fields": ["model3D", "modelWebp"],
            "code_evidence": {"class": "LF8/h;", "method": "b(int,String)CompletableFuture",
                              "dex": "classes2.dex", "offset_bytes": "0x001E",
                              "behavior": "Constant moduleId 14, passed to LJ8/a.e at 0x0022"},
        },
        "control_resource": {
            "endpoint": "v1/earphone/resource/control",
            "additional_body_fields": ["productId", "color"],
            "omitted_fields": ["moduleId", "language"],
            "response_fields": ["url", "imgUrl", "cartoonUrl", "productId", "sha256"],
            "selected_download_field": "cartoonUrl",
            "config_type": "Lf9/b;",
            "config_fields": ["boxImageRes", "capsuleImageRes", "capsuleVideoRes",
                              "detailImageRes", "leftImageRes", "rightImageRes",
                              "lottieResList", "leftLottieResList", "rightLottieResList",
                              "discoveryStates", "hearingEnhancementRes", "hearingEnhancementAnimRes"],
            "code_evidence": [
                {"class": "LJ8/a;", "method": "f(int,String)CompletableFuture", "offset_bytes": "0x0000",
                 "behavior": "Resolve color, create control_<productId>_<color> cache name, invoke abstract k at 0x0042"},
                {"class": "LJ8/o;", "method": "k(int,String,String,String,boolean)CompletableFuture",
                 "offset_bytes": "0x000E", "behavior": "Concrete implementation creates LJ8/n cache/network supplier"},
                {"class": "LJ8/n;", "method": "get()Object", "offset_bytes": "0x00EA",
                 "behavior": "Non-fetch/non-popup branch creates LJ8/d"},
                {"class": "LJ8/d;", "method": "apply(Object)Object", "offset_bytes": "0x0022",
                 "behavior": "Call net/n.l(color,type,productId), implemented by net/D.l"},
                {"class": "Lcom/oplus/melody/model/net/D;", "method": "l(int,String,String)CompletableFuture",
                 "offset_bytes": "0x0008", "behavior": "Body productId; color added at 0x0012; path type is control"},
                {"class": "LJ8/j;", "method": "apply(Object)Object", "offset_bytes": "0x00FC",
                 "behavior": "control selects getAnimUrl() serialized as cartoonUrl; sha256 at 0x0104"},
            ],
        },
        "color_resolution": {
            "class": "Lcom/oplus/melody/model/repository/earphone/L;",
            "method": "E(int,String)int", "dex": "classes2.dex",
            "explicit_color": "Any input other than -1 is returned unchanged; first branch at byte offset 0x0002",
            "unknown_color": "Input -1 uses configured defaultColor >=0; otherwise realme+supportSpp returns 0, realme without SPP returns 1; other brands retain -1",
            "configured_defaults": {"060410": 0, "060810": 0},
            "x4_default": "No defaultColor field in either recovered whitelist",
            "server_color_enumeration_discovered": False,
            "color_number_is_marketing_name": False,
        },
        "cache": {
            "class": "LJ8/a;", "method": "l(String)File", "offset_bytes": "0x0008",
            "base": "Context.getFilesDir()/melody-model-download",
            "oem_candidate": "/data/user_de/0/com.oplus.melody/files/melody-model-download",
            "heymelody_candidate": "/data/user/0/com.heytap.headset/files/melody-model-download",
            "control_name": "control_<productId>_<resolvedColor>",
            "model_name": "fetch14_<productId>_<resolvedColor>",
            "config": "config.json", "legacy_model_member": "res/raw/detail_model.vfxms",
            "preferences_name": "melody-model-download",
            "shared_public_cache_proven": False,
        },
        "cases": CASES,
    }


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


def app_material(archive):
    AESGCM, pkcs7, Encoding, InvalidTag = crypto_modules()
    dex = Dex(archive.read("classes2.dex"))
    blobs = []
    for index, access, code in dex.classes["LC7/a;"]["methods"]:
        if "->c()" not in dex.methods[index]:
            continue
        for pc, op, raw, info in dex.insns(code):
            if op == 0x1a:
                literal = dex.strs[raw[1]]
                if len(literal) < 60:
                    continue
                try:
                    blob = base64.b64decode(literal, validate=True)
                except ValueError:
                    continue
                if len(blob) >= 28:
                    blobs.append(blob)
    for entry in archive.namelist():
        if not (entry.startswith("META-INF/") and entry.endswith((".RSA", ".DSA"))):
            continue
        for certificate in pkcs7.load_der_pkcs7_certificates(archive.read(entry)):
            certificate_digest = hashlib.sha256(certificate.public_bytes(Encoding.DER)).digest()
            for blob in blobs:
                try:
                    material = AESGCM(certificate_digest).decrypt(blob[:12], blob[12:], None)
                    material.decode("utf-8")
                    return material
                except (InvalidTag, UnicodeDecodeError):
                    continue
    raise RuntimeError("APK application material could not be authenticated")


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


def download(opener, record, case, endpoint):
    field = "cartoonUrl" if endpoint.endswith("/control") else "resourceUrl"
    url = record.get(field)
    expected = record.get("sha256")
    result = {"selected_url_field": field, "attempted": False}
    if not isinstance(url, str) or not vendor_cdn(url):
        result["skipped_reason"] = "Missing or unapproved vendor HTTPS CDN URL"
        return result
    if not isinstance(expected, str) or not re.fullmatch("[0-9a-fA-F]{64}", expected):
        result["skipped_reason"] = "Missing SHA-256"
        return result
    result["source_url"] = sanitize(url)
    result["attempted"] = True
    try:
        network_pace()
        with opener.open(urllib.request.Request(url, method="GET"), timeout=30) as response:
            result["http_status"] = response.status
            data = bounded_read(response, MAX_BYTES)
        result["bytes"] = len(data)
        result["sha256"] = sha256(data)
        result["sha256_matches"] = hmac.compare_digest(result["sha256"], expected.lower())
        if not result["sha256_matches"]:
            raise ValueError("SHA-256 mismatch")
        if not zipfile.is_zipfile(io.BytesIO(data)):
            raise ValueError("Expected a ZIP resource package")
        stem = ("control" if endpoint.endswith("/control") else "fetch14")
        stem += "_" + case["productId"] + "_" + case["color"] + "_" + result["sha256"][:12]
        destination = HERE / "remote" / (stem + ".zip")
        destination.parent.mkdir(parents=True, exist_ok=True)
        if destination.exists() and sha256(destination.read_bytes()) != result["sha256"]:
            raise ValueError("Existing immutable archive hash mismatch")
        destination.write_bytes(data)
        result["saved_package"] = destination.relative_to(HERE).as_posix()
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            result["zip_member_count"] = len(archive.infolist())
            result["zip_members"] = [{"path": member.filename, "bytes": member.file_size}
                                     for member in archive.infolist()]
    except Exception as error:
        # Exception messages can contain token-bearing URLs; persist only the type.
        result["error_type"] = type(error).__name__
        if isinstance(error, urllib.error.HTTPError):
            result["http_status"] = error.code
    return result


def probe(case_id, case=None):
    case = CASES[case_id] if case is None else case
    package = "com.oplus.melody"
    spec = APK_SPEC[package]
    body_object = {"platform": "android", "channel": spec["channel"],
                   "versionCode": str(spec["version_code"]),
                   "productId": case["productId"], "color": case["color"]}
    if "moduleId" in case:
        body_object["moduleId"] = case["moduleId"]
    body = json.dumps(body_object, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    result = {"case_id": case_id, "observed_at": datetime.now(timezone.utc).isoformat(),
              "source": spec["filename"], "package": package,
              "version_name": spec["version_name"], "apk_sha256": spec["sha256"],
              "http_method": "POST", "endpoint": case["endpoint"],
              "request_body": body_object, "color_evidence": case["color_evidence"],
              "authentication_headers_persisted": False, "request_sent": False}
    with verified_apk(package) as archive:
        material = app_material(archive)
    headers = {"Content-Type": "application/json", "appid": "earphone",
               "ts": str(int(time.time() * 1000)), "nonce": str(uuid.uuid4()), "sv": "v1",
               "sign": hmac.new(material, body, hashlib.sha1).hexdigest()}
    material = None
    opener = urllib.request.build_opener(NoRedirect())
    request = urllib.request.Request(API_BASE + case["endpoint"], data=body,
                                     headers=headers, method="POST")
    try:
        result["request_sent"] = True
        try:
            network_pace()
            response = opener.open(request, timeout=25)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            result["http_status"] = response.status
            raw_response = bounded_read(response, 1024 * 1024)
        result["response_bytes"] = len(raw_response)
        parsed = json.loads(raw_response)
        result["response_sanitized"] = sanitize(parsed)
        if isinstance(parsed, dict):
            result["code"] = parsed.get("code")
            result["msg"] = safe_message(parsed.get("msg", parsed.get("message")))
            records = response_records(parsed)
            result["resource_records_found"] = len(records)
            result["response_product_ids"] = sorted({str(item.get("productId", "")) for item in records})
            result["resource_url_present"] = any(item.get("resourceUrl") or item.get("cartoonUrl")
                                                  for item in records)
            result["sha256_present"] = any(bool(item.get("sha256")) for item in records)
            matching = [item for item in records
                        if str(item.get("productId", "")).upper() == case["productId"]]
            # ResourceFetchDO has no productId. One record then has request-only attribution.
            if case["endpoint"].endswith("/fetch") and not matching and len(records) == 1 and "productId" not in records[0]:
                matching = records
            if result["http_status"] == 200 and result["code"] in (0, 200) and len(matching) == 1:
                result["model_attribution"] = (
                    "request_and_response_productId" if "productId" in matching[0] else "request_only")
                result["resource_get"] = download(opener, matching[0], case, case["endpoint"])
            elif records:
                result["resource_get"] = {"attempted": False,
                                          "skipped_reason": "Response model association ambiguous or business request unsuccessful"}
    except Exception as error:
        result["error_type"] = type(error).__name__
    finally:
        headers.clear()
        request = None
    return sanitize(result)


def png_info(data):
    if not data.startswith(b"\x89PNG\r\n\x1a\n") or len(data) < 33:
        return None, None, None
    width, height = struct.unpack_from(">II", data, 16)
    color_type = data[25]
    offset = 8
    alpha = color_type in (4, 6)
    while offset + 12 <= len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        chunk = data[offset + 4:offset + 8]
        if chunk == b"tRNS":
            alpha = True
        if chunk == b"IEND":
            break
        offset += length + 12
    return width, height, alpha


def export_saved_artwork(records):
    assets = []
    model_file = HERE / "models.json"
    model_names = ({m["product_id"]: m["name"] for m in json.loads(model_file.read_text(encoding="utf-8"))["models"]}
                   if model_file.exists() else {})
    seen_packages = set()
    seen_archives = set()
    for record in records:
        get = record.get("resource_get", {})
        relative = get.get("saved_package")
        if not relative:
            continue
        candidate = Path(relative)
        if candidate.is_absolute():
            raise ValueError("Evidence archive path must be relative")
        # Accept the first feasibility probe's repo-relative path as an import.
        for prefix in ("research/earbud-artwork/", "docs/research/earbud-artwork/"):
            candidate = Path(str(candidate).replace("\\", "/").removeprefix(prefix))
        path = (HERE / candidate).resolve()
        if not path.is_relative_to(HERE / "remote"):
            raise ValueError("Archive outside research remote directory")
        if path in seen_archives:
            continue
        seen_archives.add(path)
        raw_archive = path.read_bytes()
        archive_sha = sha256(raw_archive)
        package_key = (record.get("productId", record.get("request_body", {}).get("productId")),
                       record.get("color", record.get("request_body", {}).get("color")), archive_sha)
        if package_key in seen_packages:
            continue
        seen_packages.add(package_key)
        if archive_sha != get.get("sha256") or not get.get("sha256_matches"):
            raise ValueError("Saved package verification evidence missing or inconsistent")
        body = record.get("request_body", {})
        product = body.get("productId", record.get("productId"))
        color = int(body.get("color", record.get("color", 0)))
        endpoint = record.get("endpoint", "v1/earphone/resource/control")
        destination = path.with_suffix("")
        with zipfile.ZipFile(io.BytesIO(raw_archive)) as archive:
            members = archive.infolist()
            if len({member.filename for member in members}) != len(members):
                raise ValueError("Duplicate ZIP members")
            if sum(member.file_size for member in members) > MAX_BYTES:
                raise ValueError("Uncompressed ZIP exceeds size limit")
            for member in members:
                if member.is_dir():
                    continue
                name = PurePosixPath(member.filename)
                if "\\" in member.filename or name.is_absolute() or ".." in name.parts:
                    raise ValueError("Unsafe ZIP member")
                if ((member.external_attr >> 16) & 0o170000) == 0o120000:
                    raise ValueError("ZIP symlink rejected")
                if name.suffix.lower() not in {".json", ".png", ".webp", ".jpg", ".jpeg",
                                              ".vfxms", ".gltf", ".glb", ".obj", ".fbx",
                                              ".ktx", ".ktx2", ".bin", ".mp4", ".webm"}:
                    continue
                value = archive.read(member)
                derived = False
                if name.suffix.lower() == ".json":
                    # Configs can contain URLs; omit query-bearing metadata from derived JSON.
                    parsed = json.loads(value)
                    cleaned = sanitize(parsed)
                    if cleaned != parsed:
                        value = (json.dumps(cleaned, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
                        derived = True
                output = destination.joinpath(*(("derived",) + name.parts if derived else name.parts))
                if not output.resolve().is_relative_to(destination.resolve()):
                    raise ValueError("Unsafe resolved member destination")
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_bytes(value)
                width, height, alpha = png_info(value)
                lower = name.name.lower()
                category = ("config" if name.suffix.lower() == ".json" else
                            "case" if "box" in lower else
                            "earpiece" if "left" in lower or "right" in lower else
                            "capsule" if "capsule" in lower else "device_appearance")
                kind = ("config" if name.suffix.lower() == ".json" else
                        "bitmap" if name.suffix.lower() in {".png", ".webp", ".jpg", ".jpeg"} else
                        "video" if name.suffix.lower() in {".mp4", ".webm"} else "model")
                digest = sha256(value)
                original_path = output.relative_to(HERE).as_posix()
                assets.append({
                    "id": digest, "sha256": digest, "kind": kind, "category": category,
                    "title": model_names.get(product, product) + " " + name.stem,
                    "model_status": "confirmed" if record.get("model_attribution", "request_and_response_productId") ==
                                    "request_and_response_productId" else "candidate",
                    "model_ids": [product], "color": color, "original_path": original_path,
                    "preview_path": original_path if kind == "bitmap" else None,
                    "width": width, "height": height, "alpha": alpha,
                    "derived": derived,
                    "origins": [{
                        "package": "com.oplus.melody", "version_name": "17.6.3",
                        "apk_sha256": APK_SPEC["com.oplus.melody"]["sha256"],
                        "apk_entry": "remote control ZIP member",
                        "zip_member": member.filename,
                        "remote_archive": path.relative_to(HERE).as_posix(),
                        "endpoint": endpoint, "product_id": product, "color": color,
                        "package_sha256": archive_sha,
                        "member_sha256": sha256(archive.read(member)),
                        "source_url": get.get("source_url"),
                    }],
                    "notes": ["Model bound by request and server productId; color is a numeric identifier, not a marketing name."] +
                             (["Derived/redacted JSON; original member bytes remain in the verified ZIP."] if derived else []),
                })
    manifest = {"schema_version": 1, "generated_at": datetime.now(timezone.utc).isoformat(),
                "asset_count": len(assets), "assets": assets}
    write_json(HERE / "remote/remote-assets.json", manifest)
    return manifest


def authenticated_whitelist(package="com.oplus.melody"):
    """Read the certificate-authenticated model list; keep cryptographic inputs in memory."""
    member, list_key = (("res/AQ.json", "whiteList") if package == "com.oplus.melody"
                        else ("res/30.json", "compatWhiteList"))
    AESGCM, pkcs7, Encoding, InvalidTag = crypto_modules()
    with verified_apk(package) as archive:
        encrypted = archive.read(member)
        for entry in archive.namelist():
            if not entry.startswith("META-INF/") or not entry.endswith((".RSA", ".DSA")):
                continue
            for cert in pkcs7.load_der_pkcs7_certificates(archive.read(entry)):
                digest = hashlib.sha256(cert.public_bytes(Encoding.DER)).digest()
                try:
                    plaintext = AESGCM(digest).decrypt(encrypted[:12], encrypted[12:], None)
                except InvalidTag:
                    continue
                with gzip.GzipFile(fileobj=io.BytesIO(plaintext)) as stream:
                    decoded = stream.read(8 * 1024 * 1024 + 1)
                if len(decoded) > 8 * 1024 * 1024:
                    raise ValueError("Whitelist exceeds bounded size")
                result = json.loads(decoded)
                return result[list_key], {
                    "package": package, "apk_sha256": APK_SPEC[package]["sha256"],
                    "apk_entry": member, "plaintext_sha256": sha256(decoded),
                    "authenticated": True, "model_count": len(result[list_key]),
                }
    raise ValueError("Whitelist authentication failed")


def known_default_selection():
    """Select one App-resolved default per earphone, never enumerate colors."""
    models, source = authenticated_whitelist()
    candidates, skipped = [], []
    for model in models:
        product = model["id"].upper()
        base = {"product_id": product, "name": model.get("name"),
                "brand": model.get("brand"), "type": model.get("type")}
        if not re.fullmatch("[0-9A-F]{6}", product):
            raise ValueError("Unexpected product identifier")
        if model.get("type") == "S":
            skipped.append({**base, "reason": "Speaker; outside earphone artwork scope"})
            continue
        default = model.get("defaultColor", -1)
        if type(default) is not int:
            raise ValueError("Unexpected defaultColor type")
        if default >= 0:
            color, rule = default, "whitelist_explicit_defaultColor"
        elif str(model.get("brand", "")).lower() == "realme":
            support_spp = model.get("supportSpp", False)
            if type(support_spp) is not bool:
                raise ValueError("Unexpected supportSpp type")
            color = 0 if support_spp else 1
            rule = "L.E_realme_supportSpp_true_to_0" if support_spp else "L.E_realme_supportSpp_false_or_missing_to_1"
        else:
            skipped.append({**base, "reason": "L.E keeps unknown color -1: no configured default and not realme"})
            continue
        if not 0 <= color <= 16:
            raise ValueError("Default outside traced App color range")
        candidates.append({**base, "resolved_color": color, "evidence_rule": rule,
                           "supportSpp_declared": model.get("supportSpp"),
                           "supportSpp_missing": "supportSpp" not in model,
                           "configured_defaultColor": model.get("defaultColor")})
    return {
        "schema_version": 1, "source": source,
        "endpoint": "v1/earphone/resource/control", "http_method": "POST",
        "selection_rule": "One documented App default color per earphone; no color or module enumeration",
        "candidate_count": len(candidates), "skip_count": len(skipped),
        "candidates": candidates, "skipped": skipped,
        "color_code_is_marketing_name": False,
        "code_evidence": [
            {"dex": "classes2.dex", "class": "Lcom/oplus/melody/model/repository/earphone/L;",
             "method": "E(int,String)int", "offset_bytes": "0x0002",
             "behavior": "Input other than -1 returned unchanged"},
            {"dex": "classes2.dex", "class": "Lcom/oplus/melody/model/repository/earphone/L;",
             "method": "E(int,String)int", "offset_bytes": "0x0020",
             "behavior": "getDefaultColor >=0 takes precedence"},
            {"dex": "classes2.dex", "class": "Lcom/oplus/melody/model/repository/earphone/L;",
             "method": "E(int,String)int", "offset_bytes": "0x0052",
             "behavior": "WhitelistConfigDTO.getBrand equalsIgnoreCase(realme); getSupportSpp at 0x0066; true returns0 at0x0084, false returns1 at0x009C"},
            {"dex": "classes.dex", "class": "Lcom/oplus/melody/common/data/WhitelistConfigDTO;",
             "method": "getSupportSpp()boolean", "offset_bytes": "0x0000",
             "behavior": "Direct iget-boolean of primitive supportSpp field; no runtime phone state"},
            {"dex": "classes.dex", "class": "Lcom/oplus/melody/common/data/WhitelistConfigDTO;",
             "method": "<init>(...,int,kotlin.jvm.internal.e)", "offset_bytes": "0x00D8",
             "behavior": "Kotlin default mask8192 branch sets supportSpp=false at0x00E0; no-arg constructor uses default mask"},
            {"dex": "classes2.dex", "class": "Lcom/oplus/melody/model/repository/whitelist/a;",
             "method": "p(InputStream,byte[])Object", "offset_bytes": "0x0056",
             "behavior": "AES-GCM then gzip then JsonUtils.e to WhitelistContentDO"},
            {"dex": "classes.dex", "class": "Lcom/oplus/melody/common/util/JsonUtils$a;",
             "method": "<clinit>()void", "offset_bytes": "0x0148",
             "behavior": "Gson normal DTO reader; only Bundle-specific adapter registered; omitted supportSpp remainsfalse"},
            {"dex": "classes.dex", "class": "Lcom/oplus/melody/common/util/T;",
             "method": "a(Collection,String,String)WhitelistConfigDTO", "offset_bytes": "0x0076",
             "behavior": "Whitelist product ID lookup compares getId() ignoring case; returns original DTO"},
        ],
    }


def historical_observations():
    """Earlier in-memory observations; not new requests or persisted original responses."""
    base = {
        "source": APK_SPEC["com.oplus.melody"]["filename"], "package": "com.oplus.melody",
        "version_name": "17.6.3", "apk_sha256": APK_SPEC["com.oplus.melody"]["sha256"],
        "http_method": "POST", "endpoint": "v1/earphone/resource/fetch",
        "observed_at": None, "original_response_persisted": False,
        "exact_observation_time_persisted": False, "replayed_for_evidence": False,
        "evidence_note": "Earlier Plan-mode in-memory observation, reconstructed from recorded agent reports; original response and exact time were not saved.",
        "authentication_headers_persisted": False,
    }
    records = []
    for case_id, product, status, code, msg, authenticated in (
        ("historical-unsigned-x4-model", "06EC10", 500, None,
         "The signature must not be blank! Please make sure the correctly[sign] parameter exists in request’s uri or header.", False),
        ("historical-signed-x4-model", "06EC10", 200, 0, "成功", True),
        ("historical-signed-x3-model", "067410", 200, 0, "成功", True),
    ):
        records.append({**base, "case_id": case_id, "http_status": status, "code": code, "msg": msg,
                        "signed_request": authenticated, "request_sent": True,
                        "request_body": {"platform": "android", "channel": "1", "versionCode": "17006003",
                                         "productId": product, "color": "0", "moduleId": "14"},
                        "data_present": False if authenticated else None,
                        "resource_url_present": False, "sha256_present": False,
                        "response_keys_observed": ["code", "msg"] if authenticated else None,
                        "resource_records_found": 0})
    return records


def add_history(records):
    existing_ids = {r.get("case_id") for r in records}
    return records + [r for r in historical_observations() if r["case_id"] not in existing_ids]


def persist_probes(records, batch=None):
    evidence = {
        "schema_version": 1, "max_metadata_requests_per_probe_invocation": 1,
        "max_cdn_gets_per_probe_invocation": 1,
        "network_mode": "Explicit --probe or --known-defaults only", "probes": records,
    }
    if batch is not None:
        evidence["known_defaults_batch"] = batch
    elif (HERE / "evidence/server-probes.json").exists():
        previous = json.loads((HERE / "evidence/server-probes.json").read_text(encoding="utf-8"))
        if "known_defaults_batch" in previous:
            evidence["known_defaults_batch"] = previous["known_defaults_batch"]
    write_json(HERE / "evidence/server-probes.json", evidence)


def has_control_observation(records, product, color):
    return any(r.get("endpoint") == "v1/earphone/resource/control" and
               r.get("request_sent", True) and
               str(r.get("request_body", {}).get("productId", r.get("productId", ""))).upper() == product and
               str(r.get("request_body", {}).get("color", r.get("color", ""))) == str(color)
               for r in records)


def run_known_defaults(records, selection):
    batch = {"started_at": datetime.now(timezone.utc).isoformat(), "qps_limit": 1,
             "concurrency": 1, "candidate_count": selection["candidate_count"],
             "new_metadata_requests": 0, "reused_existing_observations": 0,
             "max_one_query_per_product_id": True, "stop_http_statuses": [403, 429],
             "max_cdn_bytes": MAX_BYTES, "stop_reason": None, "completed": False}
    last_start = None
    for item in selection["candidates"]:
        product, color = item["product_id"], item["resolved_color"]
        if has_control_observation(records, product, color):
            batch["reused_existing_observations"] += 1
            continue
        if last_start is not None:
            time.sleep(max(0, 1.0 - (time.monotonic() - last_start)))
        case_id = "default-control-" + product + "-" + str(color)
        case = {"productId": product, "color": str(color),
                "endpoint": selection["endpoint"], "color_evidence": item["evidence_rule"]}
        last_start = time.monotonic()
        result = probe(case_id, case)
        result["selection_source"] = selection["source"]
        records.append(result)
        batch["new_metadata_requests"] += int(result.get("request_sent", False))
        persist_probes(records, batch)
        print(json.dumps({"progress": batch["new_metadata_requests"], "product_id": product, "color": color,
                          "http_status": result.get("http_status"), "code": result.get("code"),
                          "msg": result.get("msg"), "verified_package_saved": bool(result.get("resource_get", {}).get("saved_package")),
                          "error_type": result.get("error_type")}, ensure_ascii=False), flush=True)
        get_status = result.get("resource_get", {}).get("http_status")
        if result.get("http_status") in (403, 429) or get_status in (403, 429):
            batch["stop_reason"] = "HTTP403/429 stop rule"
            break
        if not result.get("request_sent") or result.get("error_type"):
            batch["stop_reason"] = "Authentication/network/response error; no automatic retries"
            break
    else:
        batch["completed"] = True
    batch["finished_at"] = datetime.now(timezone.utc).isoformat()
    batch["verified_packages_total"] = sum(bool(r.get("resource_get", {}).get("saved_package")) for r in records)
    batch["selected_products_with_observation"] = sum(has_control_observation(records, i["product_id"], i["resolved_color"])
                                                      for i in selection["candidates"])
    persist_probes(records, batch)
    return batch


def load_probes():
    path = HERE / "evidence/server-probes.json"
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))["probes"]
    initial = HERE / "evidence/control-probe.json"
    if initial.exists():
        value = json.loads(initial.read_text(encoding="utf-8"))
        value.update({
            "case_id": "enco-free-control", "package": "com.oplus.melody",
            "source": APK_SPEC["com.oplus.melody"]["filename"],
            "version_name": "17.6.3", "apk_sha256": APK_SPEC["com.oplus.melody"]["sha256"],
            "http_method": "POST", "endpoint": CASES["enco-free-control"]["endpoint"],
            "request_body": {"platform": "android", "channel": "1", "versionCode": "17006003",
                             "productId": "060410", "color": "0"},
            "model_attribution": "request_and_response_productId",
            "authentication_headers_persisted": False,
            "color_evidence": CASES["enco-free-control"]["color_evidence"],
            "evidence_note": "Imported immediately saved representative probe; no exact observation timestamp was persisted.",
        })
        value["resource_get"]["source_url"] = value["response_sanitized"]["data"]["cartoonUrl"]
        return [value]
    return []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--offline", action="store_true", help="Inspect APKs and existing verified archives without network")
    mode.add_argument("--probe", action="store_true", help="Explicitly enable one named metadata POST and optional one CDN GET")
    mode.add_argument("--known-defaults", action="store_true", help="Explicitly enable serial control requests for documented defaults, reusing existing observations")
    parser.add_argument("--case", choices=sorted(CASES), default="enco-free-control")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    contract = static_contract()
    write_json(HERE / "evidence/resource-contract.json", contract)
    selection = known_default_selection()
    write_json(HERE / "evidence/default-color-selection.json", selection)
    contract["color_resolution"]["configured_defaults"] = {
        item["product_id"]: item["resolved_color"] for item in selection["candidates"]
        if item["evidence_rule"] == "whitelist_explicit_defaultColor"
    }
    contract["color_resolution"]["default_selection_evidence"] = "evidence/default-color-selection.json"
    write_json(HERE / "evidence/resource-contract.json", contract)
    probes = add_history(load_probes())
    if args.probe:
        result = probe(args.case)
        probes.append(result)
    batch = run_known_defaults(probes, selection) if args.known_defaults else None
    persist_probes(probes, batch)
    manifest = export_saved_artwork(probes)
    if args.probe:
        print(json.dumps(result, ensure_ascii=False))
    elif args.known_defaults:
        print(json.dumps({"batch": batch, "remote_asset_count": manifest["asset_count"]}, ensure_ascii=False))
    else:
        print(json.dumps({"mode": "offline", "verified_apk_count": len(contract["sources"]),
                          "saved_probe_count": len(probes), "remote_asset_count": manifest["asset_count"]},
                         ensure_ascii=False))


if __name__ == "__main__":
    main()

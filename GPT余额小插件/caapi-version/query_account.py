"""Query the CAAPI account balance with a locally protected website session.

The website Cookie header is accepted only on stdin. It is never printed or
passed as a process argument. Windows DPAPI protects the saved session.
"""

import ctypes
import json
import os
import re
import sys
import urllib.error
import urllib.request
from ctypes import wintypes
from http.cookies import SimpleCookie
from pathlib import Path

BASE = "https://caapi.top"
COOKIE_NAME = re.compile(r"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")


class DataBlob(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]


crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
crypt32.CryptProtectData.argtypes = [
    ctypes.POINTER(DataBlob), ctypes.c_wchar_p, ctypes.POINTER(DataBlob),
    ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(DataBlob),
]
crypt32.CryptProtectData.restype = wintypes.BOOL
crypt32.CryptUnprotectData.argtypes = [
    ctypes.POINTER(DataBlob), ctypes.POINTER(ctypes.c_wchar_p),
    ctypes.POINTER(DataBlob), ctypes.c_void_p, ctypes.c_void_p,
    wintypes.DWORD, ctypes.POINTER(DataBlob),
]
crypt32.CryptUnprotectData.restype = wintypes.BOOL
kernel32.LocalFree.argtypes = [ctypes.c_void_p]
kernel32.LocalFree.restype = ctypes.c_void_p


def protect(data: bytes, decrypt: bool = False, machine: bool = False) -> bytes:
    buffer = ctypes.create_string_buffer(data)
    incoming = DataBlob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    outgoing = DataBlob()
    function = crypt32.CryptUnprotectData if decrypt else crypt32.CryptProtectData
    flags = 1 if decrypt else (5 if machine else 1)
    args = [ctypes.byref(incoming), None, None, None, None, flags, ctypes.byref(outgoing)]
    if not function(*args):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return ctypes.string_at(outgoing.data, outgoing.size)
    finally:
        kernel32.LocalFree(ctypes.cast(outgoing.data, ctypes.c_void_p))


def session_path() -> Path:
    override = os.environ.get("CAAPI_OVERLAY_SESSION_PATH", "")
    if override and Path(override).is_absolute():
        return Path(override)
    appdata = os.environ.get("LOCALAPPDATA")
    if not appdata:
        raise RuntimeError("无法定位 Windows 本机数据目录")
    return Path(appdata) / "CaapiUsageOverlay" / "account-session.bin"


def parse_cookie_header(value: str) -> dict[str, str]:
    value = value.strip()
    if value.lower().startswith("cookie:"):
        value = value[7:].strip()
    if not value or len(value) > 16_384 or "\r" in value or "\n" in value:
        raise ValueError("请粘贴请求标头中的 Cookie 值")
    cookies = {}
    for pair in value.split(";"):
        if "=" not in pair:
            continue
        name, content = pair.strip().split("=", 1)
        if COOKIE_NAME.fullmatch(name) and content:
            cookies[name] = content
    if not cookies:
        raise ValueError("未识别到有效的 Cookie")
    return cookies


def read_session() -> dict[str, str] | None:
    path = session_path()
    if not path.exists():
        return None
    try:
        saved = path.read_bytes()
        if saved[:2] not in (b"U1", b"M1"):
            raise ValueError("unknown session format")
        data = json.loads(protect(saved[2:], decrypt=True))
        if not isinstance(data, dict) or not all(
            isinstance(key, str) and isinstance(value, str) for key, value in data.items()
        ):
            raise ValueError("invalid session")
        return data
    except (OSError, ValueError, json.JSONDecodeError):
        raise PermissionError("本机授权信息无法解密，请重新授权") from None


def save_session(cookies: dict[str, str]) -> None:
    path = session_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(cookies).encode("utf-8")
    try:
        encrypted = b"U1" + protect(payload)
    except OSError:
        encrypted = b"M1" + protect(payload, machine=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_bytes(encrypted)
    os.replace(temporary, path)


def request_json(path: str, cookies: dict[str, str] | None = None,
                 token: str | None = None, method: str = "GET") -> tuple[int, dict]:
    headers = {"Accept": "application/json", "User-Agent": "Codex CAAPI Usage Overlay"}
    if cookies:
        headers["Cookie"] = "; ".join(f"{key}={value}" for key, value in cookies.items())
    if token:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(BASE + path, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=12)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        for header in response.headers.get_all("Set-Cookie", []) or []:
            if cookies is None:
                continue
            parsed = SimpleCookie()
            try:
                parsed.load(header)
            except Exception:
                continue
            for name, morsel in parsed.items():
                if morsel["max-age"] == "0" or not morsel.value:
                    cookies.pop(name, None)
                else:
                    cookies[name] = morsel.value
        try:
            body = json.load(response)
        except (ValueError, UnicodeDecodeError):
            raise RuntimeError("CAAPI 返回了无效数据") from None
        return response.status, body


def account_balance(cookies: dict[str, str]) -> float:
    status, refresh = request_json("/api/user/auth/refresh", cookies, method="POST")
    if status == 401 or refresh.get("code") in {"AUTH_UNAUTHORIZED", "AUTH_SESSION_EXPIRED"}:
        raise PermissionError("网站登录已过期，请重新授权")
    if status != 200 or not refresh.get("success"):
        raise RuntimeError("CAAPI 网站授权失败")
    access_token = refresh.get("data", {}).get("access_token")
    if not isinstance(access_token, str) or not access_token:
        raise RuntimeError("CAAPI 未返回网站访问令牌")
    status, self_data = request_json("/api/user/self", cookies, token=access_token)
    if status == 401:
        raise PermissionError("网站登录已过期，请重新授权")
    if status != 200 or not self_data.get("success"):
        raise RuntimeError("CAAPI 账号余额接口失败")
    try:
        quota = float(self_data["data"]["quota"])
    except (KeyError, TypeError, ValueError):
        raise RuntimeError("CAAPI 未返回账号剩余额度") from None
    try:
        _, config = request_json("/api/status")
        per_unit = float(config["data"]["quota_per_unit"])
        if per_unit <= 0:
            per_unit = 500_000
    except (KeyError, TypeError, ValueError, RuntimeError, urllib.error.URLError):
        per_unit = 500_000
    return round(max(0, quota) / per_unit, 6)


def main() -> int:
    command = sys.argv[1] if len(sys.argv) > 1 else "--query"
    if command == "--self-test":
        sample = b"codex-cookie-test"
        results = {}
        for scope in ("user", "machine"):
            try:
                encrypted = protect(sample, machine=scope == "machine")
                results[scope] = protect(encrypted, decrypt=True) == sample
            except OSError as error:
                results[scope] = str(error)
        if parse_cookie_header("Cookie: a=b; c=d") != {"a": "b", "c": "d"}:
            raise RuntimeError("Cookie 解析测试失败")
        print(json.dumps({"cookie_parse": True, "dpapi": results}))
        return 0
    if command == "--clear":
        session_path().unlink(missing_ok=True)
        print(json.dumps({"cleared": True}))
        return 0
    if command == "--authorize":
        cookies = parse_cookie_header(sys.stdin.read())
        balance = account_balance(cookies)
        save_session(cookies)
        print(json.dumps({"available_usd": balance}))
        return 0
    if command == "--query":
        cookies = read_session()
        if cookies is None:
            print(json.dumps({"auth_required": True}))
            return 0
        balance = account_balance(cookies)
        save_session(cookies)
        print(json.dumps({"available_usd": balance}))
        return 0
    raise ValueError("未知查询命令")


if __name__ == "__main__":
    try:
        sys.exit(main())
    except PermissionError as error:
        print(json.dumps({"auth_required": True, "error": str(error)}, ensure_ascii=False))
        sys.exit(0)
    except urllib.error.URLError:
        print(json.dumps({"error": "CAAPI 连接失败，请检查网络"}, ensure_ascii=False))
        sys.exit(1)
    except Exception as error:
        print(json.dumps({"error": str(error)}, ensure_ascii=False))
        sys.exit(1)

"""Read the active Codex CAAPI key and return safe usage totals as JSON."""

import json
import os
import sys
import tomllib
import urllib.error
import urllib.request
from pathlib import Path


def get_json(url: str, token: str | None = None) -> dict:
    headers = {"User-Agent": "Mozilla/5.0 (Codex CAAPI Usage Overlay)", "Accept": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=10) as response:
        return json.load(response)


def main() -> int:
    config_path = Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex") / "config.toml"
    with config_path.open("rb") as file:
        config = tomllib.load(file)
    provider = config["model_providers"][config["model_provider"]]
    base = provider["base_url"].rstrip("/").removesuffix("/v1")
    if base != "https://caapi.top":
        raise ValueError("当前 Codex 模型供应商不是 caapi.top")
    token = provider.get("experimental_bearer_token") or os.environ.get(provider.get("env_key", ""))
    if not token:
        raise ValueError("当前 CAAPI 供应商缺少 API Key")
    usage = get_json(base + "/api/usage/token", token)["data"]
    try:
        per_unit = float(get_json(base + "/api/status")["data"]["quota_per_unit"])
        if per_unit <= 0:
            per_unit = 500000
    except (KeyError, ValueError, urllib.error.URLError):
        per_unit = 500000
    used = float(usage["total_used"]) / per_unit
    available = float(usage["total_available"]) / per_unit
    unlimited = bool(usage.get("unlimited_quota"))
    print(json.dumps({
        "spent_usd": round(used, 6),
        "available_usd": None if unlimited else round(max(0, available), 6),
        "unlimited": unlimited,
        "quota_per_usd": per_unit,
        "source": base + "/api/usage/token",
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except urllib.error.HTTPError as error:
        print(json.dumps({"error": "CAAPI 授权或接口失败", "status": error.code}, ensure_ascii=False))
        sys.exit(1)
    except Exception as error:
        print(json.dumps({"error": str(error)}, ensure_ascii=False))
        sys.exit(1)

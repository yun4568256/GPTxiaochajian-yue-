---
name: caapi-usage
description: Explain or query CAAPI key spend and account balance with the Windows Codex usage overlay.
---

# CAAPI 用量小助手

The plugin root is two levels above this file. The clickable overlay is a separate Windows application installed from the release ZIP. It appears when Codex is in the foreground, and each click performs one query. The right-click menu controls bubble duration, mascot size, sound, and local website authorization.

Never print or collect a Codex API Key or CAAPI website Cookie in chat. The user enters the Cookie only in the overlay's local window. It is encrypted with Windows DPAPI in local app data.

When asked about key spend, run `python <plugin-root>/query_usage.py` only if Python 3.11+ and a CAAPI Codex provider are configured. When asked about website account balance, run `python <plugin-root>/query_account.py --query` only after the user has authorized locally. If it returns `auth_required`, direct the user to the overlay's authorization window. The account balance, key spend, and local Codex session tokens are separate measures.

The overlay must be launched by the user from the Windows desktop. A Codex tool shell may run on an isolated desktop where its GUI is invisible. If the user needs the program now, ask them to run its installed EXE from Explorer or the Startup shortcut. Do not imply installing this skill alone starts the overlay.

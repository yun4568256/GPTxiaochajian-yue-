---
name: openai-usage
description: Explain the Windows overlay for OpenAI organization costs, completion tokens, and a self-set monthly budget.
---

# OpenAI 官方用量小助手

The clickable overlay is a separate Windows application installed from the release ZIP. It appears when Codex is in the foreground, and each click queries OpenAI once. The user configures an OpenAI Admin API Key and monthly budget from the overlay's right-click menu. Never ask for the Admin Key in chat or print it. The key is encrypted in local app data, unless supplied through the `OPENAI_ADMIN_KEY` environment variable.

Explain the measures precisely: organization costs come from `/v1/organization/costs`; organization text tokens come from `/v1/organization/usage/completions`; the remaining budget is a user-defined number minus current month costs, not a prepaid account balance. The displayed model and recent session tokens come from local Codex logs. A regular API Key cannot query organization costs.

Do not claim the overlay starts when this skill alone is installed. The user must install or run the Windows EXE. A Codex tool shell may be on an isolated desktop where the GUI cannot be seen. The online Admin API query has not been verified with a real organization key; simulated pagination and token parsing have been tested.

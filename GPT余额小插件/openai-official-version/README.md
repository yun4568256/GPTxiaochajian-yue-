# OpenAI 官方 API 用量小助手

Windows 上伴随 Codex 显示的点击查询悬浮窗。每次点击人物，程序向 OpenAI 官方组织接口查询本月费用和文本生成 token，并显示自设预算剩余。

## 下载和安装

1. 下载并解压 `OpenAI-Usage-Overlay-win.zip`。
2. 安装 [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)。
3. 在解压目录打开 PowerShell，运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1`。脚本把程序复制到当前用户的 `%LOCALAPPDATA%\Programs\OpenAiUsageOverlay`，创建 Windows 登录自启快捷方式，并启动程序。不需要管理员权限。
4. 在 Codex 窗口中右键人物，打开“配置 Admin Key 与月预算…”，输入 OpenAI **Admin API Key** 和每月预算（美元）。点击人物查询。

也可直接双击解压后的 `app/OpenAiUsageOverlay.exe`，这时不创建自启快捷方式。只建议同时运行一个用量助手版本。

## 显示口径

- **本月组织费用**：OpenAI `/v1/organization/costs` 返回的当月累计美元费用，按 UTC 月份统计。
- **自设月预算剩余**：本地设置的预算减去本月组织费用。它不等于 OpenAI 账户预付余额，也不是 OpenAI 官方限额。
- **组织文本 token**：OpenAI `/v1/organization/usage/completions` 返回的输入与输出 token 之和；其他类型的用量未计入此数。
- **模型和最近会话 token**：来自本机 Codex 会话日志，可能与上面的 OpenAI 组织统计不是同一口径。

普通 OpenAI API Key 不能读取组织费用；需使用有权限的 Admin API Key。点击时才发起请求，不在后台轮询。气泡默认在查询完成后显示 8 秒；右键可设为 5～10 秒、调整人物宽度、关闭音效。点击人物会向下弹一下，气泡会随人物移动。

Admin API Key 使用 Windows DPAPI 在本机加密保存。用户加密不可用时回退到同机加密；文件仍位于当前用户的本机数据目录。也可用 `OPENAI_ADMIN_KEY` 环境变量提供密钥，它优先于本机保存的密钥。不要把密钥写进源码、截图或 GitHub 仓库。

## 源码构建

在 Windows 上安装 .NET 9 SDK，运行 `dotnet publish OpenAiUsageOverlay.csproj -c Release -o publish`。根目录的 `build-release.ps1` 会构建两个可下载 ZIP。本目录包含 Codex 插件清单和技能说明；悬浮窗需要单独安装 Windows 程序。

公开构建已经通过模拟数据的费用分页、token 汇总和本机加密回环测试。由于没有实际 OpenAI Admin API Key，在线组织接口查询尚未实测。如果遇到 HTTP 403，请检查 Admin Key 的组织权限。

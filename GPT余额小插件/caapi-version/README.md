# CAAPI 用量小助手

Windows 上伴随 Codex 显示的点击查询悬浮窗。人物、气泡、音效和查询方式沿用已验证的 CAAPI 版本。

## 下载和安装

1. 下载并解压 `CAAPI-Usage-Overlay-win.zip`。
2. 安装 [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) 和 Python 3.11 或更新版本。Python 需在 `PATH` 中；也可以把 `python.exe` 的完整路径写入 `app/python-path.txt`。
3. 在解压目录打开 PowerShell，运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1`。脚本把程序复制到当前用户的 `%LOCALAPPDATA%\Programs\CaapiUsageOverlay`，创建 Windows 登录自启快捷方式，并启动程序。不需要管理员权限。
4. 切到 Codex 窗口，点击人物查询。首次查询账号余额时，按下方步骤授权。

也可不安装，直接双击解压后的 `app/CaapiUsageOverlay.exe`。这时不会创建开机自启快捷方式。只建议同时运行一个用量助手版本。

## 查询内容

- 点击人物时向下弹一下、播放短音效、弹出气泡，并向 CAAPI 查询一次；不在后台轮询。
- 气泡显示当前 Codex CAAPI 密钥的累计消耗与密钥剩余额度，以及 CAAPI 网站账号的总剩余额度。
- 模型名和最近会话的 token 数来自本机 Codex 会话日志；它们与 CAAPI 密钥累计消耗是不同口径。
- 气泡跟随人物并避开屏幕边缘。右键可设置显示 5～10 秒、人物宽度 80～260 像素、音效，以及网站授权。
- 仅 Codex 在前台时显示人物。程序在 Windows 登录后启动并等待 Codex 窗口出现。

程序从 Codex 的 `config.toml` 读取当前模型供应商。其 `base_url` 必须是 `https://caapi.top/v1`；API Key 可来自该供应商的 `experimental_bearer_token`，或由 `env_key` 指向的环境变量。密钥仅发送到 `caapi.top` 的用量接口。

## 首次查询账号总余额

CAAPI 密钥可能被标为“无限额度”，无法据此推算网站账号余额。账号余额需要本机网站登录授权：

1. 在浏览器登录 [CAAPI 用量页面](https://caapi.top/dashboard/overview)。
2. 按 F12 打开“网络”面板，刷新页面，选择 `/api/user/auth/refresh` 请求。
3. 从“请求标头”复制 `Cookie` 的值，粘贴到悬浮窗弹出的授权窗口，点击“保存并查询”。

只在本机窗口输入 Cookie，不要发到聊天或上传到 GitHub。Cookie 以 Windows DPAPI 加密保存在当前用户的本机数据目录；用户加密不可用时，程序回退到同机加密。右键可清除授权；网站会话过期时需重新授权。

## 源码构建

在 Windows 上安装 .NET 9 SDK，运行 `dotnet publish CaapiUsageOverlay.csproj -c Release -o publish`。程序运行时仍需要 Python 3.11+。根目录的 `build-release.ps1` 会构建两个可下载 ZIP。

本目录也是 Codex 插件目录，包含 `.codex-plugin/plugin.json` 和技能说明。Codex 插件只提供查询指引；可点击的悬浮窗由上面的 Windows 程序提供。交互参考 [dsh-whale-widget](https://github.com/NieRMHY/dsh-whale-widget)，代码和点击音效均为本项目实现。

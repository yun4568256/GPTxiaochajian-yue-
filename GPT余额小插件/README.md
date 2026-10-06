# GPT 余额小插件

这是两个适用于 Windows 的 Codex 伴随悬浮窗。点击人物后查询一次，气泡显示 5～10 秒；人物会原地弹动，气泡随位置移动，并可调整人物大小和关闭音效。

| 版本 | 查询来源 | 显示的“剩余” | 运行要求 |
| --- | --- | --- | --- |
| [CAAPI 版](caapi-version/README.md) | CAAPI 密钥用量与网站账号余额 | 网站账号实际剩余额度；密钥余额另列 | .NET 9 Desktop Runtime、Python 3.11+、CAAPI Codex 配置 |
| [OpenAI 官方 API 版](openai-official-version/README.md) | OpenAI 组织费用和文本生成 token | **自设月预算减去本月费用**，不是账户预付余额 | .NET 9 Desktop Runtime、OpenAI Admin API Key |

## 直接下载

从 [downloads](downloads) 下载对应 ZIP，解压后运行其中的 `Install.ps1`。两个 ZIP 都包含可运行程序、安装脚本和说明；无需先编译。安装脚本为当前 Windows 用户创建登录自启快捷方式。请只安装需要的版本，以免在 Codex 上看到两个人物。

安装命令：`powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1`。安装后切回 Codex，人物出现时点击即可查询。第一次运行官方版需在人物右键菜单配置 Admin API Key 和月预算；CAAPI 版查询网站账号余额需在本机授权窗口输入网站 Cookie。不要把凭据提交到仓库。

## 源码与发布

两个版本的源码、Codex 插件清单和技能文件分别在 `caapi-version` 与 `openai-official-version`。悬浮窗是单独的 Windows 程序；仅安装 Codex 插件清单不会自动安装桌面程序。

在 Windows 上安装 .NET 9 SDK 后，运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\build-release.ps1` 重新构建 ZIP。推送 `v*` 格式的 Git 标签时，GitHub Actions 会构建 ZIP 并附到 GitHub Release；手动触发工作流也会生成可下载的构建产物。

代码采用 [MIT 许可证](LICENSE)。人物图片的授权范围见 [ASSETS.md](ASSETS.md)；公开发布前请确认其再分发权利。官方版已通过模拟接口数据和本机加密测试，但未使用真实 OpenAI Admin API Key 实测在线组织查询。

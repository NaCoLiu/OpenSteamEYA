# SteamEYA

SteamEYA 是一个 WinUI 3 桌面工具，用于通过 EYA 令牌登录 Steam、清理创意工坊订阅、解析上游卡密，并查询账号的 Steam/JWT/CS2 状态。

## 运行环境

当前 Release 为精简版 Windows x64 包，不是自包含包。这样可以把发布成品控制在 8 MB 以内，但用户电脑需要先安装运行依赖。

必需依赖：

- Windows 10 1809（Build 17763）或更高版本，推荐 Windows 11。
- [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)。
- [Windows App Runtime / Windows App SDK Runtime](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)。
- Steam 客户端。

解压依赖：

- [7-Zip](https://www.7-zip.org/download.html) 或 NanaZip，用于解压 Release 里的 `.7z` 文件。
- 下载 7-Zip 时请认准官方域名 `7-zip.org`。

如果双击 `SteamEyaWinUI.exe` 没有反应，优先检查 .NET 8 Desktop Runtime x64 和 Windows App Runtime 是否已经安装。

## 下载和运行

1. 打开 GitHub Releases。
2. 下载最新的 `SteamEYA-<version>-win-x64.7z`。
3. 解压到一个普通目录，不要直接在压缩包内运行。
4. 双击 `SteamEyaWinUI.exe`。

发布包不做自解压，避免弹出 7-Zip SFX 解压框。

## 发版规则

每次向 `main` 分支推送都会自动创建一个 Release。

版本号规则：

- Tag 格式：`v0.1.<run_number>`
- Release 标题：`SteamEYA v0.1.<run_number>`
- 产物名称：`SteamEYA-0.1.<run_number>-win-x64.7z`

Release 会包含：

- Windows x64 精简版 `.7z` 成品包。
- `latest.json`，包含版本号、tag、平台、commit、文件大小、SHA256 和更新日志。
- Release Notes，包含版本号和从上一个 `v*` 版本以来的提交更新日志。

## 检查更新

项目仓库为公开仓库，客户端可以直接读取 GitHub Latest Release，不需要 GitHub Token。

应用内“关于”页面会：

- 显示当前版本。
- 连接 GitHub Releases。
- 读取最新 Release 附件里的 `latest.json`。
- 比较本地版本和最新版本。
- 提供 GitHub 仓库、发布页和下载更新入口。

后续自动更新可以继续复用 `latest.json` 里的 `artifactName`、`artifactSize`、`artifactSha256` 和 `changelog` 字段。

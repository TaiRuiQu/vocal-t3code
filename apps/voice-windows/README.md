# T3 Voice for Windows

独立的 Windows 11 语音编排助手。云电脑运行 T3 Code 和执行 agent；本机只运行托盘程序和按需启动的 Codex / Claude Code，不需要安装或运行 T3 Code 桌面或服务器。

## 启动

需要 Windows 11 x64，以及已登录的 `codex.exe` / `claude.exe`。设置、任务进度和结果窗口使用 **WinUI 3**。开发构建需要 .NET 10 SDK，首次恢复微软 Windows App SDK 依赖时需要网络，不依赖仓库的 Node 或 pnpm 环境。独立发布目录包含 .NET 和 Windows App SDK 运行文件，请完整保留目录内容。

```powershell
dotnet run --project apps/voice-windows/T3.Voice --configuration Release
```

日常运行固定使用仓库内 `release\T3-Voice\T3.Voice.exe`。构建或更新时先从
托盘退出程序，然后执行下面的命令；它直接覆盖同一目录，不生成版本目录、
增量包或 ZIP，配置与历史记录保留。

```powershell
powershell -NoProfile -File apps/voice-windows/Publish.ps1
```

直接运行 `dotnet publish apps/voice-windows/T3.Voice -c Release` 也使用这个固定目录。

## GitHub 自动打包

将 T3 Voice 源码和 `.github/workflows/release-voice.yml` 提交并推送后，每次推送新 tag
都会在 Windows runner 上运行测试、独立打包 T3 Voice，并上传到该 tag 的 GitHub Release。
此流程不构建 T3 Code 本体，也不安装仓库的 Node 或 pnpm 依赖。

```powershell
git tag v0.1.0
git push origin v0.1.0
```

在仓库的 Releases 中下载 `T3-Voice-win-x64.zip`，完整解压后运行
`T3-Voice\T3.Voice.exe`。同时提供 `.sha256` 校验文件，Actions 页面也保留打包产物。
该流程使用 GitHub 自动提供的 `GITHUB_TOKEN`，无需额外配置发布密钥。

## 首次设置

首次打开设置：

1. 选择本机 Codex 或 Claude Code；找不到命令时填写 `.exe` 完整路径。模型留空会使用对应 CLI 的默认值。切换提供商后沿用本地对话和记忆，各提供商分别保留自己的会话。
2. 填写阿里云百炼 DashScope API Key、与其地域一致的 WebSocket 地址和实时 ASR 模型。默认是北京地域的 `fun-asr-realtime`。选择麦克风。
3. 在每台 Windows / Ubuntu 云电脑上启动 T3 Code，登录它自己的 agent，并启用 T3 Connect。在助手中添加机器的 HTTPS 地址，在浏览器完成授权并选择完整权限。也支持已有的其他 HTTPS T3 访问地址。
4. 保存设置。主题可选跟随系统、浅色或深色，默认跟随系统；提示位置可选左上或右上。默认开机自启，可关闭。托盘双击打开任务和对话，右键打开设置或退出。

## 语音与任务

- 连续轻敲两次**右 Shift** 开始录音，看到“正在听”后说话；再次双击结束并自动发送。按 **Esc** 丢弃录音，包括最后转写尚未返回时。左 Shift、长按、自动重复和 Shift 组合键不会触发；右 Shift 原按键仍传给游戏。
- 录音中实时显示识别文字；只在录音时采集并向阿里云发送音频。不会在本机加载 ASR 模型，也不保存录音文件。
- 自动提示不激活窗口、不截取鼠标。追问保留在角落，下一段语音默认回答它；说“新任务”可以另起任务。完整消息和待答问题可在任务窗口回看，也可输入文字。
- 编排 agent 按已有项目位置、任务上下文和远端可用模型选择机器。已有工作继续原对话，独立工作创建新对话。每台机器已跟踪任务繁忙时，新启动的任务持久排队。
- 代码、仓库、提交和 PR 都由远端 agent 操作，支持沿用项目的 Git 服务；新仓库默认私有。它报告完成后总管汇报；明显未完成的任务可以继续安排。
- 本机退出或断网不取消已经启动的云端任务；重新打开后恢复跟踪并补报结果。未成功处理的语音保留，可通过托盘“重试待处理任务”重试。远端授权失效时在设置中重新授权。

默认面向无边框游戏窗口；独占全屏与以管理员权限运行的游戏需要在对应游戏中验证。不会注入游戏进程。双击手势间隔为 450ms，每次轻敲不超过 350ms。

## 本地数据和资源

数据位于 `%LOCALAPPDATA%\T3Voice\voice.sqlite3`，与 T3 Code 的数据库分开：保存对话、记忆、任务、排队操作及设置。API Key 和机器凭据使用当前 Windows 用户的 DPAPI 加密；不会沿用或修改 T3 Code 的安装数据。可用 `--data-dir <directory>` 指定独立目录。语音任务会交给本机已登录的 CLI，CLI 保留它自己的会话记录。

助手使用 WinUI 3、事件驱动键盘 hook 和 WinMM 录音，不启动 Electron、浏览器渲染器或本地 T3 server。CLI 在每轮结束后退出，远端状态由有超时的等待请求跟踪。任务窗口显示助手及其 CLI 子进程的当前总工作集；500 MiB 是整组进程的资源目标，模型 CLI 自身的用量也计入，需要在真实账号和任务上验证。不会为了强制满足指标而中断已下发的云端工作。

本次 Windows 测量中，关闭设置、只保留托盘与浮窗时，Claude Code 两个
编排场景的合计工作集峰值约为 455 MiB 和 437 MiB；设置窗口同时打开时
曾测到约 557 MiB。它们是具体场景的采样结果，不是任意模型、长期会话下
始终低于 500 MiB 的保证。打游戏时建议关闭设置窗口。

## 验证

```powershell
dotnet run --project apps/voice-windows/T3.Voice.Tests --configuration Release
dotnet build apps/voice-windows/T3.Voice --configuration Release
```

已安装 Codex 的初始化、插件隔离与退出验证（不启动模型回合）：

```powershell
dotnet run --project apps/voice-windows/T3.Voice.Tests -c Release -- --check-codex
```

测试使用本机 OAuth 回调、假的 MCP HTTP/WebSocket/CLI 协议、SQLite 和 Windows DPAPI，不消耗模型或 ASR 额度。覆盖 PKCE、流式识别与取消、键盘手势、CLI 协议、恢复、重复派发保护和排队竞争。真实云端、模型账号和阿里 ASR 需要在设置完成后集成验证。

UI 预览（不调用模型、不录音、不设置开机自启）：

```powershell
dotnet run --project apps/voice-windows/T3.Voice -c Release -- --demo --data-dir "$env:TEMP\t3-voice-preview"
```

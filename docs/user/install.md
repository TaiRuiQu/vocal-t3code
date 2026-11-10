# 安装并连接 T3 Voice

在执行 agent 任务的电脑上运行 T3 Code，在你的电脑上运行 T3 Voice。

## 安装终端主机

下载本分支发布的 `t3-<版本>-win32-x64.zip`，完整解压后双击其中的 `t3.exe`。保留压缩包内的全部文件；无需另行安装 Node、Electron 或 cloudflared。

源码运行和打包方法见[仓库说明](../../README.md)。T3 Voice 的安装见[使用说明](../../apps/voice-windows/README.md)。

## 准备 provider

在主机上安装以下 CLI 中的至少一个，并完成它自己的登录。CLI 应能从启动 T3 Code 的终端中直接运行。

| Provider    | 登录命令              |
| ----------- | --------------------- |
| OpenCode    | `opencode auth login` |
| Codex       | `codex login`         |
| Claude Code | `claude auth login`   |

T3 Connect 登录用于连接主机，provider 登录用于执行任务，两者需要分别完成。

## 启动并复制链接

1. 运行 `t3.exe`，或在终端执行 `t3`。
2. 首次启动时，打开终端显示的 T3 Connect 登录网址，按提示输入验证码并完成授权。已有有效登录时会自动复用。
3. 等待终端显示 `T3 Voice connection:`，复制下一行的 HTTPS 地址。
4. 在 T3 Voice 设置中添加该地址，使用主机关联的同一个 T3 Connect 账号完成首次连接授权。
5. 保持主机终端运行。按 Ctrl+C 停止后端。

链接会在隧道健康检查通过后显示。登录网址、验证码、配对二维码链接和 relay 地址都不是应填入 T3 Voice 的连接地址。

## 常用命令

| 操作                       | 命令                   |
| -------------------------- | ---------------------- |
| 自动登录并启动             | `t3`                   |
| 查看连接状态               | `t3 connect status`    |
| 退出 T3 Connect 登录       | `t3 connect logout`    |
| 使用已保存的连接无交互启动 | `t3 serve`             |
| 使用独立数据目录           | `t3 --base-dir <目录>` |

没有显示连接链接时，先查看终端错误，再运行 `t3 connect status`。更多主机侧检查见 [T3 Connect 故障排查](./t3-connect-troubleshooting.md)。

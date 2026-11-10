# T3 Code · T3 Voice Host

这是供 T3 Voice 连接的终端后端。仅保留 OpenCode、Codex 和 Claude Code；不包含 Web、Electron、移动端或营销站点界面。

## 使用

1. 在运行后端的电脑上安装并登录至少一个 provider 的 CLI。
2. 下载并完整解压 Windows 终端版压缩包，双击其中的 `t3.exe`。
3. 首次启动会在终端显示 T3 Connect 登录网址和验证码。在任意设备上完成登录；后续启动自动复用保存的授权。
4. 等待终端出现 `T3 Voice connection:`，复制下一行的 HTTPS 地址，在 T3 Voice 设置中添加机器并授权。
5. 保持终端运行。按 Ctrl+C 停止后端。

T3 Connect 的登录负责远程连接；OpenCode、Codex 和 Claude Code 使用这台电脑上各自 CLI 的登录。T3 Voice 首次添加机器时还需要使用同一个 T3 Connect 账号完成连接授权。

HTTPS 地址在隧道健康检查通过后才会显示。不要把登录网址、验证码、配对二维码链接或 relay 地址填入 T3 Voice。

## 源码运行

使用仓库要求的 Node 版本和 Vite+，安装依赖后启用仓库提供的 T3 Connect 公共配置：

```powershell
Copy-Item .env.example .env
vp i
vp run dev
```

如果已有 `.env`，保留它并补充 `.env.example` 中的公共配置。开发数据使用当前 checkout 的隔离目录；不要把开发服务器指向正在使用的安装数据。

## 构建

Windows x64 构建还需要 Rust 工具链和 Visual Studio C++ Build Tools。完成上面的依赖和公共配置设置后运行：

```powershell
vp run build
vp run build:exe
node scripts/stage-windows-relay-client.ts
cargo build --locked --release --manifest-path native/resource-monitor/Cargo.toml --target x86_64-pc-windows-msvc
New-Item -ItemType Directory -Force apps/server/dist/resource-monitor | Out-Null
Copy-Item native/resource-monitor/target/x86_64-pc-windows-msvc/release/t3-resource-monitor.exe apps/server/dist/resource-monitor/
vp run dist:cli --platform win --arch x64 --version 0.2.0 --output-dir release/t3-code --relay-client-dir apps/server/resources/relay-client
```

产物为 `release/t3-code/t3-0.2.0-win32-x64.zip`，完整解压后运行 `t3.exe`。Windows 发布包包含原生终端程序、所需依赖和经过校验的 T3 Connect relay client；无需安装 Node 或 Electron。发布流程同时构建独立的 T3 Voice 应用。

常用命令：`t3` 启动并自动登录，`t3 connect logout` 退出登录，`t3 serve` 使用已保存连接无交互启动。`--base-dir <目录>` 可指定独立的数据目录。

T3 Voice 的说明见 [apps/voice-windows/README.md](apps/voice-windows/README.md)。

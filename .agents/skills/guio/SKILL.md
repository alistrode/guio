# guio

## 何时使用

在本仓库（GuIO 通用自动化工具）上做构建、运行 CLI、起常驻服务、录制回放、接 MCP、或排查「点不到/输入不进去/截屏错位」时加载本 skill。

## 命令索引（命令 → 等价源码）

| 目的 | 命令 | 等价源码 / 脚本 |
|---|---|---|
| 探活 | `guio ping` | `src/Frontends/GuIO.Cli/Program.cs` → `src/Service/RpcService.cs` |
| 列出设备 | `guio devices` | `src/Service/DeviceManager.cs` |
| 接入窗口 | `guio connect --device win://app/notepad` | `src/Drivers/GuIO.Driver.Windows/WindowManager.cs` |
| 枚举窗口 | `guio windows --filter <关键字>` | 同上（`windows.list` 能力） |
| 观测语义树/帧 | `guio observe --device win://window/<hwnd>` | `src/Drivers/GuIO.Driver.Windows/UiaSemanticReader.cs`；安卓 `src/Drivers/GuIO.Driver.Android/GuioAccessibilityService.cs` |
| 按文字/图像/坐标点击 | `guio click --text "确定"` / `--image ok.png` / `--x 300 --y 420` | `src/Vision/SelectorResolver.cs`（四级降级链）+ `src/Kernel/ActionDispatcher.cs` |
| 定位不点击 | `guio find --text "文本编辑器"` | `src/Vision/SelectorResolver.cs` |
| 输入中文/emoji | `guio type --text "你好" --unicode true` | `src/Drivers/GuIO.Driver.Windows/InputInjector.cs` |
| 请求前置前台 | （自动） | `InputInjector.cs` 内建 `EnsureForeground` |
| 截屏 | `guio screenshot --out shot.png` | `src/Drivers/GuIO.Driver.Windows/ScreenCapture.cs`、`WgcCapture.cs` |
| 本地识图 | `guio vision --image shot.png --prompt "..."` | `src/Vision/MarkupOverlay.cs`（SoM 编号）+ Ollama `qwen3-vl:4b`，仅回环地址 |
| 音量/静音 | `guio volume --volume 0.5` | `src/Drivers/GuIO.Driver.Windows/WindowsMedia.cs` |
| 回环录音 | `guio record --out rec.wav --seconds 3` | 同上（NAudio WASAPI loopback） |
| 起常驻服务 | `guio-agent serve --port 0` | `src/Agents/GuIO.Agent.Windows/Program.cs` |
| 录制 | `guio rec-start` / `guio rec-stop --out demo.json` | `src/Kernel/Recorder.cs` |
| 回放 | `guio replay --file demo.json --rescale true` | `src/Kernel/Recorder.cs` + `src/Kernel/FlowEngine.cs` |
| 等界面收敛 | `guio wait --text "文本编辑器" --timeoutMs 3000` | `src/Kernel/ControlLoops.cs`、`IntentLoop.cs` |
| 给 AI 当工具 | `guio-mcp`（stdio，`gui_*` 工具） | `src/Frontends/GuIO.Mcp/Program.cs` |
| 图形界面 | `dist/win-app/guio-app.exe` | `src/Frontends/GuIO.App/MainWindow.cs` |
| 能力协商 | （内核） | `src/Kernel/CapabilityRegistry.cs` |
| 重试与防振荡 | （内核） | `src/Kernel/RetryPolicy.cs` |
| 遥测与证据帧 | （内核） | `src/Kernel/Telemetry.cs` |
| 多设备调度 | （内核） | `src/Kernel/DeviceScheduler.cs` |
| 语音转写 | （`GuIO.Speech`） | `src/Speech/WhisperTranscriber.cs` |
| 本机 IPC / 跨机传输 | （`GuIO.Transport`） | `src/Transport/TcpConnection.cs` |
| 新增平台 | 实现 `IDeviceDriver` 后注册一行 | `src/Contracts/Driver.cs`、`DriverExtensions.cs` |

## 构建与发布（命令 → 工程文件）

| 目的 | 命令 | 工程 |
|---|---|---|
| 全量编译 | `dotnet build GuIO.slnx` | `GuIO.slnx`、`Directory.Build.props` |
| 单测 | `dotnet test tests/GuIO.Tests` | `tests/GuIO.Tests/CoreTests.cs` |
| 发布 CLI | `dotnet publish src/Frontends/GuIO.Cli -c Release -o dist/win` | `GuIO.Cli.csproj`（net8.0-windows10.0.19041.0） |
| 发布 Windows 宿主 | `dotnet publish src/Agents/GuIO.Agent.Windows -c Release -o dist/win` | `GuIO.Agent.Windows.csproj` |
| 发布 MCP | `dotnet publish src/Frontends/GuIO.Mcp -c Release -o dist/win` | `GuIO.Mcp.csproj` |
| 发布 APK | `dotnet publish src/Agents/GuIO.Agent.Android -c Release -o dist/android` | `GuIO.Agent.Android.csproj`（net10.0-android，`ApplicationId=com.guio.agent`） |

## 运行前提（缺一不可）

- Windows 宿主需 **net8.0-windows10.0.19041.0** 目标；`guio.exe` / `guio-agent.exe` 是**单文件自包含**，无需另装 .NET 运行时。
- 安卓宿主需 **net10.0-android** 与 Android SDK；APK 为 `apk` 格式、`ApplicationId=com.guio.agent`、`SupportedOSPlatformVersion=26`。
- 本地识图需本机 Ollama + 已下载 `qwen3-vl:4b`（或 `GUIO_VLM_MODEL` 指定的已下载模型）；**只接受 127.0.0.1**，非本机地址被拒绝。
- 安卓截屏/录屏需 MediaProjection 授权；shell 级动作需可选 Shizuku 提权，未接入时返回 `Unsupported`。

## 已知边界（不要当成缺陷排查）

- 不支持 iOS 与浏览器自动化。
- 安卓回环录音受系统限制（需 MediaProjection + 被捕获应用 opt-in），以能力声明暴露，不伪装可用。
- `dist/android/com.guio.agent-Signed.apk` 使用**默认调试签名**，非生产签名。

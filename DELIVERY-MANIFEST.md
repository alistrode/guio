# GuIO 交付清单 · Delivery Manifest

版本 `0.1.0`（`Directory.Build.props` 中 `<Version>`），交付日期 2026-10-08，本仓库为首次上传。

## 一、本仓库包含

| 部分 | 内容 |
|---|---|
| 源码 | `src/` 全部 66 个源文件与工程文件（Contracts / Kernel / Vision / Transport / Service / Drivers / Agents / Frontends / Speech） |
| 测试 | `tests/GuIO.Tests/`（`CoreTests.cs` + 工程文件） |
| 自包含分发包 | `dist/win/` 47 文件 178.95 MB；`dist/win-app/` 89 文件 135.94 MB；`dist/android/` 3 文件 18.25 MB |
| 资源 | `assets/` 18 文件 |
| 文档 | `README.md`、`DELIVERY.md`、`.agents/skills/guio/SKILL.md` |

合计 **229 文件 / 333.7 MB**。

### 分发包关键产物

| 产物 | 路径 | 大小 |
|---|---|---|
| Windows CLI（单文件自包含） | `dist/win/guio.exe` | 75.07 MB |
| Windows 常驻服务（单文件自包含） | `dist/win/guio-agent.exe` | 75.07 MB |
| MCP Server | `dist/win/guio-mcp.exe` | 0.14 MB |
| Windows GUI | `dist/win-app/guio-app.exe` + 运行时 | 135.94 MB |
| Android APK | `dist/android/com.guio.agent-Signed.apk` | 9.13 MB |

## 二、按规则排除的内容

| 排除项 | 规模 | 理由 |
|---|---|---|
| `bin/` 与 `obj/` 编译中间产物 | 3,492 文件 / 约 1,181 MB | 构建缓存，可由 `dotnet build` 完全重建；与 `dist/` 内容重复 |

排除后不影响任何源码与分发包的完整性。重建命令见 `README.md` 第七节与本目录 `.agents/skills/guio/SKILL.md`。

## 三、外部依赖清单（运行与重建所需）

### 运行时

| 组件 | 版本 | 来源 | 是否随包 |
|---|---|---|---|
| .NET 运行时 | net8.0 / net8.0-windows10.0.19041.0 | https://dotnet.microsoft.com/download | **不需要**，`guio.exe`/`guio-agent.exe` 为单文件自包含 |
| .NET SDK | 10.x（发布安卓需 net10.0-android 支持） | https://dotnet.microsoft.com/download | 否，仅重建需要 |
| Android SDK + JDK | 与 net10.0-android 匹配 | https://developer.android.com/studio | 否，仅重建 APK 需要 |
| Ollama | 任意近期版本 | https://ollama.com/download | 否，仅 `guio vision` / `gui_vision` 需要 |
| Qwen3-VL 模型 | `qwen3-vl:4b`（6 GB 显存可用 `qwen3-vl:2b`） | `ollama pull qwen3-vl:4b` | 否，可选 |

### NuGet 依赖（编译期自动还原）

FlaUI（Core / UIA3）、NAudio（Core / Wasapi / WinMM / Asio / Midi / WinForms）、Vortice（Direct3D11 / DirectX / DXGI / Mathematics）、SharpGen.Runtime、SharpGen.Runtime.COM、WinRT.Runtime、Microsoft.Windows.SDK.NET、System.Drawing.Common、System.IO.Pipelines、System.Management、System.Text.Json、Interop.UIAutomationClient、Avalonia（GUI 前端）。

精确版本以各 `*.csproj` 的 `PackageReference` 为准，还原由 `dotnet restore` 完成。

### 签名

`com.guio.agent-Signed.apk` 使用**默认调试签名**，仓库内不含 keystore；生产签名需自行配置。

## 四、已知文档口径差异（上传前未修改原文）

- 单测数量：`README.md` 第五节写「15 项单测」，`DELIVERY.md` 第二节实测记录写「19 passed」。
- MCP 工具数：`README.md` 第三节写「15 个 `gui_*` 工具」，`DELIVERY.md` 第一节写「14 个」。

两处均为原文原有表述，未做改动，供后续核对。

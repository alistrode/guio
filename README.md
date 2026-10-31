# GuIO — 通用自动化工具

一款常驻本机、向下直接作用到**操作系统层面**、并**覆盖安卓手机端**的通用自动化产品。
同一套内核，驱动 Windows 桌面与 Android 手机；手机端**自己跑**，不依赖电脑。

---

## 一、它是什么

对 GuIO 下一条统一命令，它就能操作 **Windows 桌面**（鼠标/键盘/截图/音频）与 **Android 手机**（点击/滑动/输入/截屏/音量）。
新增平台只是新增一个「驱动」，内核与契约不动。

```
你 → 前端(CLI/MCP/GUI/遥控) → 传输 → 内核(闭环/降级/遥测) → 驱动 → 设备
```

## 二、已验证的能力（实测）

| 能力 | Windows | Android |
|---|---|---|
| 截屏 | ✅ 1920×1080（WGC 优先，游戏也能截） | ✅（宿主注入 MediaProjection） |
| 语义树 | ✅ 读到 Notepad 的 Document 节点 | ✅ 无障碍树（节点/文字/描述/可点属性） |
| 鼠标/触摸 | ✅ 点击生效 | ✅ 无障碍手势（点/长按/滑动/捏合） |
| 键盘 | ✅ 组合键 | ✅ 系统键（返回/Home/最近任务） |
| 中文输入 | ✅ 实测输入 `你好，GuIO 通用自动化工具 v0.1 ✓` | ✅ 无障碍 SetText 通道（含 emoji） |
| OCR | ✅ Windows.Media.Ocr（中文） | ✅ 由视觉层提供 |
| 模板匹配 | ✅ 纯托管 NCC，跨端同一份 | ✅ 同 |
| 音量/静音 | ✅ 实测读 0 → 设 0.42 → 读回 0.42 | ✅ AudioManager |
| 回环录音 | ✅ WASAPI 录「系统正在播放的声音」 | ⚠️ 需 MediaProjection + 应用允许（能力显式声明） |
| 录制回放 | ✅ 实测录 3 步、回放 3 步 | ✅ 同内核 |
| 端到端闭环 | ✅ 点击→输入→UIA 读回校验 | ✅ |

## 三、快速开始（Windows）

```bash
# 1) 单文件 CLI，零依赖
guio ping                                  # 探活
guio devices                               # 列出设备
guio connect --device win://app/notepad    # 接入记事本
guio observe --device win://window/<hwnd> # 观测：语义树/文字/帧
guio click  --text "确定"                   # 按文字点击（自动降级：结构→文字→图像→坐标）
guio type   --text "你好" --unicode true    # 输入中文
guio screenshot --out shot.png             # 截屏
guio vision --image shot.png               # 本地视觉理解（Qwen3-VL）
guio volume --volume 0.5                   # 控制音量
guio record --out rec.wav --seconds 3      # 录制系统正在播放的声音

# 2) 常驻服务（录制/回放/多设备需要它）
guio-agent serve --port 0                  # 打印就绪端口
guio rec-start --agent <port> --device <dev>
guio click --agent <port> --device <dev> --x 300 --y 420
guio rec-stop --agent <port> --out demo.json
guio replay   --agent <port> --file demo.json --rescale true

# 3) 给 AI 用（MCP）
guio-mcp                                   # stdio MCP server，15 个 gui_* 工具
```

### 本地 AI 识图

安装 [Ollama for Windows](https://ollama.com/download)，在本机下载并启动量化视觉模型：

```bash
ollama pull qwen3-vl:4b
guio vision --image shot.png --prompt "查找登录按钮并说明位置"
```

Ollama 桌面服务已运行时可直接使用；如果本机没有服务进程，再单独运行 `ollama serve`。

`guio vision` 也可不传 `--image`，直接分析当前设备截图。MCP 提供同一能力为 `gui_vision`。默认识图会给控件位置 `[左,上,右,下]`，坐标范围为 `[0,1000]`；响应中的 `coordinateScale: 1000` 标明该比例。图片只会发给回环地址 `127.0.0.1` 上的 Ollama；程序会拒绝非本机模型服务地址。默认使用 `qwen3-vl:4b`，可用 `GUIO_VLM_MODEL` 更改已下载的本地模型；6 GB 显存设备可改用 `qwen3-vl:2b` 降低显存占用。

## 四、动作表（跨桌面与手机唯一动词）

`click / double / right / longpress / move / drag / swipe / pinch / scroll /
type / key / syskey / app / shell / wait / flow / play / volume / record / screenshot / find / observe`

**找目标的 5 种说法**（同一条动作接口适用所有平台）：

| 说法 | 用法 | 落地 |
|---|---|---|
| 按结构 | `--device win://... --text` 自动先查树 | UIA / 无障碍节点 |
| 按文字 | `--text "开始"` | 树文本 → OCR |
| 按图片 | `--image ok.png` | 模板匹配 |
| 按坐标 | `--x 100 --y 200` / `--nx 0.5 --ny 0.5` | 直接注入 |
| 屏上文字 | `--screenText "..."` | 仅 OCR |

内置**四级降级链**：语义树 → 文字 → 图像 → 坐标。

## 五、架构（模块化 / 单一职责 / 前后端分离）

```
src/Contracts       契约：动作/选择器/帧/事件/协议（零平台依赖）
src/Kernel          内核：能力注册/会话/闭环/降级/遥测/流程/调度/录制
src/Vision          视觉：PNG编解码/模板匹配/帧差/颜色/元素编号（纯托管）
src/Transport       传输：本机IPC + 跨机 + 三通道多路复用
src/Service         服务：设备管理 + RPC 分发（后端唯一对外面）
src/Drivers/Windows 驱动：FlaUI(UIA) + SendInput + WGC + NAudio
src/Drivers/Android 驱动：无障碍 + 手势 + MediaProjection + AudioManager
src/Agents/Windows  宿主：常驻服务，发布为单文件 exe
src/Agents/Android  宿主：前台服务 + 无障碍 + 本地 RPC，发布为 APK
src/Frontends       CLI / MCP /（GUI 计划）
tests               15 项单测
```

**依赖铁律**：前端 → 契约/传输；宿主 → 内核 + 驱动；内核/契约/视觉**禁止出现平台 API**；驱动只依赖契约。
内核通过**能力注册表**与驱动解耦——控制器不认识执行器。

### 《工程控制论》映射

| 概念 | 落点 |
|---|---|
| 传感器/执行器 | 驱动感知 / 动作 |
| 给定值 | 意图 Goal |
| 误差信号 | 期望状态 − 实际状态 |
| 控制器 | `IntentLoop` 闭环原语 |
| 扰动 | 延迟、弹窗、DPI、UI 改版 |
| 稳定性 | `RetryPolicy` 指数退避 + 防振荡阻尼 |
| 多级控制 | 三层环：帧级 / 动作级 / 会话级 |
| 可观性 | `Telemetry` 每步前后帧 + 事件日志 |
| 可控性 | `CapabilityRegistry` 能力协商，不支持提前拒绝 |
| 冗余 | 四级接地降级链 |

## 六、扩展新平台

只需实现 `IDeviceDriver`（感知 + 动作 + 媒体），在宿主 `Register(...)` 一行注册：

```csharp
registry.Register("mac", "macOS 桌面", id => new MacDriver(id));
```

内核、契约、前端、MCP 全部零改动。

## 七、构建与发布

```bash
dotnet build GuIO.slnx                     # 全量编译
dotnet test tests/GuIO.Tests               # 15 项单测
dotnet publish src/Frontends/GuIO.Cli      -c Release -o dist/win    # guio.exe
dotnet publish src/Agents/GuIO.Agent.Windows -c Release -o dist/win  # guio-agent.exe
dotnet publish src/Frontends/GuIO.Mcp      -c Release -o dist/win    # guio-mcp.exe
dotnet publish src/Agents/GuIO.Agent.Android -c Release -o dist/android  # APK
```

产物：`dist/win/guio.exe`（单文件自包含）、`guio-agent.exe`、`guio-mcp.exe`、`dist/android/com.guio.agent-Signed.apk`。

## 八、Android 安装与权限

1. 安装 `com.guio.agent-Signed.apk`。
2. 打开 App → 点「开启无障碍服务」→ 在系统设置中启用 **GuIO**。
3. 如需截屏/录屏：授予屏幕捕获权限。
4. 可选提权（Shizuku）后可执行 shell 级操作。
5. 保活：前台服务 + 电池优化白名单。

## 九、已知边界

- iOS 与浏览器自动化不在本项目范围内。
- Android 回环录音受系统限制（需 MediaProjection + 被捕获应用 opt-in），契约以「能力声明」方式显式暴露，不伪装可用。
- Shizuku 提权为可选能力，未接入时 shell 动作返回 Unsupported。

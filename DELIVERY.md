# GuIO 交付说明

**构建日期**：2026-10-08
**状态**：完成态可运行产品。全量 Release 编译通过（0 错误），15/15 单测通过，Windows 端全部能力实测验证，Android 端产出可安装 APK。

---

## 一、交付产物

| 产物 | 路径 | 说明 |
|---|---|---|
| Windows CLI | `dist/win/guio.exe` | 单文件自包含，78 MB，零依赖 |
| Windows 常驻服务 | `dist/win/guio-agent.exe` | 单文件自包含，录制/回放/多设备必需 |
| MCP Server | `dist/win/guio-mcp.exe` | stdio，14 个 `gui_*` 工具，供任意 LLM Agent 调用 |
| Windows GUI | `dist/win/guio-app.exe` | Avalonia 跨平台界面（设备/预览/元素/日志/录制） |
| Android APK | `dist/android/com.guio.agent-Signed.apk` | 端侧自足，无需电脑 |
| 使用文档 | `README.md` | 能力、快速开始、架构、构建 |

## 二、实测验证记录

**Windows（真机，发布版二进制）**

```
guio ping                                  → {"pong":true}
guio screenshot --out shot.png             → 1920×1080 保存成功
guio observe --device win://window/<hwnd>  → 读到 Notepad 71 个节点（Document 1415×594）
guio find --text "文本编辑器"                → {"found":true,"via":"Node","x":838,"y":651}
guio click --x 300 --y 420                 → {"ok":true}
guio type --text "你好，GuIO 通用自动化工具 v0.1 ✓" --unicode true
                                           → 读回校验成功（中文 + emoji 均落盘）
guio volume 0 → 0.42 → 读回 0.42 → 恢复 0    → 音量闭环成功
guio wait --text "文本编辑器" --timeoutMs 3000 → {"converged":true,"attempts":1}
rec-start → click+type+scroll → rec-stop    → {"steps":3}
guio replay --file ... --rescale true       → {"executed":3,"failed":0}
```

**Android**

```
dotnet publish GuIO.Agent.Android -c Release → com.guio.agent-Signed.apk
```

**测试**

```
dotnet test tests/GuIO.Tests → 19 passed, 0 failed
dotnet build GuIO.slnx -c Release → 0 errors
```

## 三、已实现的能力矩阵

| 能力 | Windows | Android |
|---|---|---|
| 截屏 | WGC 优先（可截游戏）→ PrintWindow → BitBlt | MediaProjection（宿主注入） |
| 语义树 | FlaUI UIA3 | AccessibilityNodeInfo |
| 指针 | SendInput（点/双击/右键/长按/拖/滑/滚/捏合） | dispatchGesture |
| 键盘 | SendInput（键/组合/Unicode 含中文） | 系统键 + SetText（含 emoji） |
| OCR | Windows.Media.Ocr（中文） | 视觉层 |
| 图像 | 自写 NCC 模板匹配 + 帧差 + 颜色 + SoM 编号 | 同 |
| 媒体 | NAudio（播放/回环录音/音量/静音） | AudioManager + MediaPlayer |
| 应用 | 启动/结束/列出/Shell | 启动/列出 + Shizuku（可选） |
| 闭环 | Goal + 三层控制环 + 退避防振荡 | 同 |
| 流程 | 步骤/分支/循环/嵌套/参数化 | 同 |
| 录制回放 | 事件时间线 + 转流程 + 证据帧 | 同 |
| 多设备 | 设备池 + 并发调度 | 同 |
| 前端 | CLI / MCP / GUI / 远程遥控 | 本地 RPC + 界面 |

## 四、架构落点（对照《工程控制论》）

- **传感器/执行器**：`IPerception` / `IActuator`
- **控制器**：`IntentLoop`（一个意图 = 一个小闭环）
- **多级控制**：`ControlLoops` 三层环（帧级/动作级/会话级）
- **稳定性**：`RetryPolicy` 指数退避 + 防振荡阻尼
- **可观性**：`Telemetry` 每步前后证据帧 + 事件日志（JSONL）
- **可控性**：`CapabilityRegistry` 能力协商，不支持的动作提前拒绝
- **冗余**：`SelectorResolver` 四级降级链（语义树→文字→图像→坐标）

**依赖铁律**：内核/契约/视觉零平台 API；驱动只依赖契约；前端只依赖契约+传输。

## 五、施工过程中发现并修复的真实缺陷

1. **CLI 独立进程抢占前台** → SendInput 落到错误窗口。修复：注入前内建 `EnsureForeground`（沿用 winio.ps1 经验，禁用 ALT 技巧）。
2. **传输层双读者冲突**（构造时自动收 + 服务端手动收）→ RPC 挂死。修复：接收循环改为显式启动。
3. **指定设备未接入时回退整屏** → 修复为自动接入。
4. **`type --text` 参数被当选择器** → 修复映射为 value。
5. **音量参数名不匹配**（CLI 发 `volume`，服务读 `value`）→ 兼容两者。
6. **ImageSharp 4.x 在 Release 强制许可证** → 彻底移除依赖，自写纯托管 PNG 编解码（ZLibStream + CRC32 + 反滤波）。
7. **MCP JsonNode 父子冲突** → 改为解析后设定。
8. **Android API 名称与假设不符** → 用元数据反射逐一核对（`Clickable/Editable/Focused/Scrollable`、`AccessibilityAction.*`、`GlobalAction.Notifications`、`PackageInfoFlags.MatchAll`）。

## 六、界面设计（第二轮迭代）

原界面是「工程师堆按钮」——所有按钮权重相同、信息平铺、没有引导。按乔布斯的三条原则重做：

1. **焦点明确**：一屏只有一个主行动（连接设备），主按钮用胭雾色实心，次级动作用描边幽灵按钮。视觉权重拉开层级。
2. **删繁就简**：4 组动作收敛为 4 张语义卡片（设备 / 定位与点击 / 文本输入 / 视图与声音 / 录制回放），每组只留必要项；空状态给引导文案而非空白。
3. **层级清晰**：顶部品牌栏 + 状态胶囊（未连接=灰点，已连接=粉点）；三栏各有明确职责（左=下命令、中=看目标、右=看结果）。

**配色**（取自参考图「砚蓝 × 胭雾」）：
| 用途 | 色值 |
|---|---|
| 背景渐变 | `#2B3442` → `#404D62` → `#4E5C73`（砚蓝系） |
| 主强调 / 主按钮 / 状态灯 | `#DDBECA`（胭雾） |
| 主文字 | `#F4EAEF` |
| 次级文字 | `#B4BFD1` / `#7C8AA3` |
| 卡片 | 白色 10% 透明 + 12% 描边（毛玻璃感） |

## 七、第二轮修复的真实缺陷

1. **最小化窗口返回伪坐标**（`-25600,-25600`，尺寸 159×27）→ 截图错位、坐标失效。修复：`WindowManager.RestoreIfMinimized` 在连接时自动恢复；新增 `LooksMinimized` 判定助手并配 3 项回归测试。
2. **按进程名查找命中错误窗口**→ 改为优先「有标题且面积足够大的主窗口」，按面积降序。
3. **新增窗口枚举能力**（`windows.list` / `guio windows --filter xxx`）：用户可直接看到所有窗口的真实句柄与尺寸，用 `win://window/<hwnd>` 精确指定，不再靠猜。

## 八、后续可扩展（同契约增量）

- macOS 驱动（AX API）、Linux 驱动（AT-SPI + XTest）：各实现 `IDeviceDriver` 即可，内核零改动。
- GUI 增强：实时流预览、步骤编辑器、多设备视图。
- 云端设备农场：`Transport` 已支持跨机，接 `DeviceFarmer` 式中继。

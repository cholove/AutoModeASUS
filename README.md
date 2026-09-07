# AutoModeASUS — 华硕笔记本性能模式自动切换（C# 版）

适配机型：**华硕灵耀X双屏 (Zenbook Duo UX8406, 2024)**，已在实机验证。

> C# 移植版（2026-08-10 交付）。单 exe、零依赖、Win11 原生运行，直接替换原 Python 版。
> 2026-09-07 新增**文件对话框快速跳转（Ctrl+G）**，Python 旧版及迁移期诊断脚本已清理。

## 功能

| 场景 | 动作 |
|---|---|
| 无人操作 ≥ 5 分钟（无鼠标/键盘输入） | 自动切换到**标准模式**（默认档位，风扇降速） |
| 检测到人工操作（鼠标/键盘） | 自动切回**活动偏好模式**（默认性能，托盘可改） |
| 托盘菜单手动切换 | 三模式一键切换：**性能 / 标准 / 安静**，选择立即生效并设为活动偏好 |
| 托盘「自动切换」开关 | 可暂停/恢复自动规则（勾选=自动开启，取消=纯手动） |
| 手动按 Fn+F 切换 | 程序以实际模式为新偏好，继续运行 |
| **打开/保存对话框中按 Ctrl+G** | **一键跳转到当前资源管理器打开的文件夹**（Listary 风格，详见下节） |
| 托盘「文件对话框快速跳转」开关 | 启用/停用 Ctrl+G，状态写入 config.json |
| 托盘图标 | **五叶风扇形状**，整体颜色实时反映当前模式（绿=安静 橙=标准 红=性能） |

## 文件对话框快速跳转（Ctrl+G）

在任意"打开/另存为"对话框里按 **Ctrl+G**，对话框会直接导航到你**当前资源管理器打开的文件夹**，省去逐级点选。

### 使用步骤

1. 用资源管理器打开目标文件夹（如 `D:\Temp`）。
2. 在任意程序中按 `Ctrl+O` 或"另存为"呼出文件对话框。
3. 按 `Ctrl+G` → 文件名框自动填入 `D:\Temp\` 并回车，对话框跳转到该目录。

### 支持范围

| 对话框类型 | 支持 | 识别方式 |
|---|---|---|
| Windows 经典/通用打开保存对话框 | ✅ 开箱即用 | 窗口类 `#32770` |
| WPS 文字 / 表格 / 演示 / PDF 的自绘浏览框 | ✅ 开箱即用 | Qt 窗口 + UIA 元素 `KcfdFileDialog` |
| 其他程序的自定义对话框 | ⚙️ 需添加规则 | config.json 的 `QuickJumpRules`，见下文 |

- 路径来源：仅 **Windows 资源管理器**（含 Win11 多标签）。存在多个窗口时取 Z 序最靠上（最近活动）的那个。
- 非文件对话框状态下按 Ctrl+G **完全无副作用**，不影响其他程序使用该快捷键。

### 为其他程序添加跳转规则（QuickJumpRules）

对未内置支持的对话框（如 Notepad++、各类 Electron 程序），可在 `config.json` 的 `QuickJumpRules` 数组中添加规则，**重启程序生效**：

```json
"QuickJumpRules": [
  { "Name": "Notepad++", "Exe": "notepad++.exe", "WindowClass": "", "Mode": "win32" },
  { "Name": "某Electron软件", "Exe": "foo.exe", "WindowClass": "Chrome_WidgetWin_", "Mode": "uia" }
]
```

> 键名不区分大小写（`exe` / `Exe` 均可）；托盘开关写回配置时会统一为上述 PascalCase。

| 字段 | 必填 | 说明 |
|---|---|---|
| Name | 否 | 备注名，仅日志显示 |
| WindowClass | 二选一 | 窗口类名**子串**匹配（不区分大小写） |
| Exe | 二选一 | 进程名**精确**匹配，需含扩展名（如 `chrome.exe`），不区分大小写 |
| Mode | 否 | `win32`（默认）=WM_SETTEXT 写编辑框+回车；`uia`=UIA ValuePattern 写入+回车，适合自绘控件 |
| AutomationId | 否 | Mode=uia 时指定目标编辑框的 AutomationId（默认取第一个 Edit） |

**如何获取窗口类名/进程名**：在不支持的对话框中按一次 Ctrl+G，日志（`log\auto-mode.log`）会输出：

```
快速跳转: 窗口未匹配 (class=Chrome_WidgetWin_1 exe=foo.exe), 如需支持请在 config.json 的 QuickJumpRules 中添加规则
```

照抄这两个值写规则即可。

**规则调试建议**：先试 `mode=win32`；若日志提示"未找到可见编辑框"或写入后不跳转，改 `mode=uia`；若 uia 提示"未找到 Edit 元素/可填 automationId"，用 [Accessibility Insights](https://accessibilityinsights.io/) 查看目标输入框的 AutomationId 填入。

**注意**：规则匹配的是前台顶层窗口。回车行为由目标程序解释——若其"文件名框+回车"语义是"打开文件"而非"进入目录"，该程序不适合此方案（日志会看到写入成功但对话框报错）。

### 开关与配置

- 托盘菜单 → **「文件对话框快速跳转 (Ctrl+G)」** 勾选/取消，即时生效并持久化。
- `config.json` 中 `QuickJumpEnabled`：`1`=开启（默认），`0`=关闭。

### 已知限制

| 情况 | 表现与处理 |
|---|---|
| 目标程序以**管理员权限**运行 | UIPI 拦截注入，跳转失败并在日志提示。需本程序同样以管理员运行 |
| Win11 资源管理器**多标签** | 各标签共享同一窗口句柄，无法精确区分"活动标签"，取该窗口的第一个有效路径 |
| Ctrl+G 已被其他程序占用 | 启动时注册失败，日志提示并自动禁用该功能（如同时运行 Listary / XiaoYao 需先退出其一） |
| 后台无资源管理器窗口 | 日志提示"未获取到资源管理器当前文件夹"，不做任何操作 |

> 实现思路移植自 [XiaoYao_QuickJump](https://github.com/lch319/XiaoYao_QuickJump)（AutoHotkey），本仓库为纯 C# 重写，不依赖 AHK 运行时。

## 快速开始

```bat
:: 后台运行（托盘 + 日志），双击 exe 亦可
D:\Software\AutoModeASUS\AutoModeASUS.exe

:: 安装开机自启（HKCU Run，指向 exe 自身）
AutoModeASUS.exe --install

:: 卸载开机自启
AutoModeASUS.exe --uninstall
```

开机自启已默认安装完毕，重启后自动生效，无需其他操作。

## 命令行

```
AutoModeASUS.exe                后台运行(托盘+日志, 单实例, 无控制台窗口)
AutoModeASUS.exe get            读取当前性能模式
AutoModeASUS.exe set balanced   切标准模式   (同 0)
AutoModeASUS.exe set silent     切安静模式   (同 1)
AutoModeASUS.exe set turbo      切性能模式   (同 2)
AutoModeASUS.exe diag           诊断: 枚举 WMI 实例/参数名/返回值（排障用）
AutoModeASUS.exe --install      安装开机自启
AutoModeASUS.exe --uninstall    移除开机自启
```

停止后台进程：托盘菜单「退出」，或双击 `stop.bat`（写 stop.flag，下个轮询周期退出）。

## 配置 (config.json, 首次运行自动生成)

| 键 | 默认 | 说明 |
|---|---|---|
| IdleSeconds | 300 | 无人操作多少秒后自动切标准模式（300=5分钟） |
| PollSeconds | 10 | 轮询间隔（秒） |
| ActivitySeconds | 15 | 空闲低于该秒数视为"有人在操作" |
| IdleModeValue | 0 | 空闲时自动切换到的模式（0=标准/平衡） |
| DefaultActiveValue | 2 | 活动偏好模式（用户操作时切回；2=性能） |
| QuickJumpEnabled | 1 | 文件对话框 Ctrl+G 快速跳转：1=开启，0=关闭（托盘开关会自动写回） |
| QuickJumpRules | [] | 自定义对话框跳转规则，见「为其他程序添加跳转规则」，修改后重启生效 |
| LogMaxKB | 1024 | 日志轮转大小 |

修改后重启进程生效。

## 技术背景（为什么这样实现）

**UX8406 是 2024 新架构，性能模式通道与普通华硕机型完全不同：**

1. **ATKACPI 设备通道不可用** — `\\.\ATKACPI`（G-Helper 常用）在本机无句柄持有者。
2. **普通 WMI 通道不可用** — `DeviceID 0x00120075`（普通机型性能档）在本机返回 `-2 (ENODEV)`。
3. **真实通道 = "lite thermal policy" WMI 通道** — `DeviceID 0x00110019`。
   - 依据：Linux 内核补丁 `asus-wmi: Add lite thermal policy support`（作者 Joshua Leivenzon，2024-07-31，UX8406 用户本人提交），说明 UX8406 使用不同的 WMI 设备 ID，且模式值映射与普通机型**颠倒**。
4. **模式值映射（lite，与普通机型相反！）**：

   | 值 | UX8406 (lite) | 普通机型 |
   |---|---|---|
   | 0 | 平衡/标准 (Balanced) | 平衡/标准 |
   | 1 | **安静 (Silent)** | Turbo |
   | 2 | **性能 (Turbo)** | 安静 |

   已实机确认：切值 1 后风扇明显安静（用户验证）。

5. **WMI 调用必须带实例路径** — `AsusAtkWmi_WMNB.InstanceName="ACPI\\PNP0C14\\ATK_0"`。用类路径调用会报「无效的方法参数」。
6. **DSTS 输出参数名是 `device_status`（全小写）** — 不是 PowerShell 里常见的 `Status`。读取时若按 `Status` 取值会抛「找不到属性」，返回值 `196610 = 0x00030002`（低字节=模式值）。C# 版 `AsusWmi.cs` 已按此实现，DEVS 返回值兼容 `ReturnValue / Result / device_status` 三种属性名。

## 编译（源码变更后）

源码：`AsusWmi.cs`（WMI 通道层）+ `Program.cs`（主程序/托盘）+ `QuickJump.cs`（Ctrl+G 快速跳转）。需要已装 .NET SDK（本机 10.0.302 等 5 个版本齐备）：

```bat
cd /d D:\Software\AutoModeASUS
dotnet build AutoModeASUS.csproj -c Release
:: 产物在 bin\Release\AutoModeASUS.exe，复制到根目录部署
copy /Y bin\Release\AutoModeASUS.exe AutoModeASUS.exe
```

- 目标框架 **net48**（.NET Framework 4.8，Win11 自带），引用 System.Management / System.Windows.Forms / System.Drawing / System.Web.Extensions / Microsoft.CSharp / UIAutomationClient / UIAutomationTypes / WindowsBase，均为系统程序集，产出约 50KB 单 exe（含图标资源），零第三方依赖。
- 图标：`fan.ico`（五叶风扇，多尺寸 16~256px，由 `make_fan_icon.py` 生成，可改后重跑）；csproj 的 `<ApplicationIcon>fan.ico</ApplicationIcon>` 负责嵌入 exe；托盘图标由 `Program.cs` 的 `MakeIcon()` 运行时绘制（同款风扇几何，整体按模式变色：安静=绿 / 标准=橙 / 性能=红）。改 exe 图标后需重新编译才会生效。
- 注意：本机安全策略禁止直接调 `csc.exe` 编译任意代码，请一律走 `dotnet build` + csproj 标准通道。

## 依赖

- 无。exe 为 .NET Framework 4.8 目标，Windows 10/11 原生运行，不需要安装任何运行时。

## 日志与排障

- 日志：`log\auto-mode.log`（超 1MB 自动轮转为 .old，UTF-8 编码）
- 常见问题：
  - **读取模式失败 / 无效的方法参数**：确认华硕 System Control Interface 驱动正常（设备管理器里能看到 ATK 设备）。可用 `AutoModeASUS.exe diag` 逐层打印 WMI 枚举、实例路径、inParams/outParams 属性名及返回值。
  - **提示"已有实例在运行"但找不到进程**：单实例互斥锁名为 `AutoModeASUS_Singleton`（**与旧 Python 版同名，两版天然互斥**）。有残留进程时用 `Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match "AutoMode" }` 找到后杀之。
  - **开机自启不生效**：确认 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 里的 `AutoModeASUS` 条目值为 `"D:\Software\AutoModeASUS\AutoModeASUS.exe"`。
  - **想改空闲时长**：编辑 config.json 的 `IdleSeconds`，改完重启进程。
  - **托盘图标颜色与模式不符**：状态每 PollSeconds（10s）刷新一次，属正常轮询延迟。
  - **Ctrl+G 无反应**：查日志。① 热键被占用会提示"注册失败"，退出 Listary/XiaoYao 等同类工具后重启本程序；② 只有前台是文件对话框时才生效；③ 日志"未找到文件名编辑框/未识别到 WPS 文件浏览框"说明该对话框形态未覆盖。
  - **WPS 浏览框跳转失败**：确认 WPS 版本较新（自绘框 UIA 类名为 `KcfdFileDialog`）；旧版 WPS 走的是系统标准对话框，按 `#32770` 路径处理。

## 更新记录

| 日期 | 内容 |
|---|---|
| 2026-09-07 | 新增 `QuickJump.cs`：文件对话框 Ctrl+G 跳转到资源管理器当前文件夹；支持 WPS 自绘浏览框（Qt + UIA）；新增 `QuickJumpRules` 自定义窗口规则，可为其他程序的对话框扩展支持；托盘新增开关项并持久化到 config.json；清理 Python 旧版（`AutoMode.py`）、8 个 `diag_atk*.py` 诊断脚本、`system_test.py` 及测试输出文件；新增 `.gitignore` |
| 2026-08-10 | C# 移植版交付（托盘三模式切换、空闲自动切标准、手动干预检测、开机自启、日志轮转） |
| 2026-08-09 | Python 原型与 UX8406 WMI 通道探测（lite thermal policy，`DeviceID 0x00110019`） |

## 停止脚本 (stop.bat)

```bat
@echo off
echo. > "%~dp0stop.flag"
echo 已发送停止信号，AutoMode 将在下一个轮询周期退出。
```

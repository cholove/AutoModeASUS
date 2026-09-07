# AutoModeASUS — 华硕笔记本性能模式自动切换（C# 版）

适配机型：**华硕灵耀X双屏 (Zenbook Duo UX8406, 2024)**，已在实机验证。

> C# 移植版（2026-08-10 交付）。单 exe、零依赖、Win11 原生运行，直接替换原 Python 版。
> 原 Python 版 `AutoMode.py` 保留在目录内作对照，已停用。

## 功能

| 场景 | 动作 |
|---|---|
| 无人操作 ≥ 5 分钟（无鼠标/键盘输入） | 自动切换到**标准模式**（默认档位，风扇降速） |
| 检测到人工操作（鼠标/键盘） | 自动切回**活动偏好模式**（默认性能，托盘可改） |
| 托盘菜单手动切换 | 三模式一键切换：**性能 / 标准 / 安静**，选择立即生效并设为活动偏好 |
| 托盘「自动切换」开关 | 可暂停/恢复自动规则（勾选=自动开启，取消=纯手动） |
| 手动按 Fn+F 切换 | 程序以实际模式为新偏好，继续运行 |
| 托盘图标 | **绿叶形状**，中心色点实时反映当前模式（橙=标准 蓝=性能 绿=安静） |

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

源码：`AsusWmi.cs`（WMI 通道层）+ `Program.cs`（主程序）。需要已装 .NET SDK（本机 10.0.302 等 5 个版本齐备）：

```bat
cd /d D:\Software\AutoModeASUS
dotnet build AutoModeASUS.csproj -c Release
:: 产物在 bin\Release\AutoModeASUS.exe，复制到根目录部署
copy /Y bin\Release\AutoModeASUS.exe AutoModeASUS.exe
```

- 目标框架 **net48**（.NET Framework 4.8，Win11 自带），引用 System.Management / System.Windows.Forms / System.Drawing / System.Web.Extensions，均为系统程序集，产出约 40KB 单 exe（含图标资源），零第三方依赖。
- 图标：`leaf.ico`（多尺寸 16~256px，由 `make_leaf_icon.py` 生成，可改后重跑）；csproj 的 `<ApplicationIcon>leaf.ico</ApplicationIcon>` 负责嵌入 exe；托盘图标由 `Program.cs` 的 `MakeIcon()` 运行时绘制（绿叶 + 模式色点）。改图标后需重新编译才会生效。
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

## 停止脚本 (stop.bat)

```bat
@echo off
echo. > "%~dp0stop.flag"
echo 已发送停止信号，AutoMode 将在下一个轮询周期退出。
```

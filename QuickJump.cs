using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Automation;

namespace AutoModeASUS
{
    /// <summary>
    /// 文件对话框快速跳转（Listary 风格 Ctrl+G，最小核心实现）
    ///
    /// 触发：前台窗口是文件对话框（窗口类 #32770）时按 Ctrl+G。
    ///       另可选开启"自动跳转"：定时扫描新出现的文件对话框，出现即自动跳转（免 Ctrl+G）。
    /// 行为：取最近活动的 Windows 资源管理器窗口/标签页的当前文件夹，
    ///       写入对话框"文件名"编辑框并模拟回车，使对话框导航到该目录。
    /// 路径来源：Shell.Application COM（IShellWindows）枚举资源管理器窗口与 Win11 标签页
    ///           （每标签独立条目），按帧窗口 Z 序取最近活动帧，再用帧标题中的活动标签名
    ///           与各标签路径末级文件夹名匹配，精确锁定"当前活动标签"。
    /// 思路参考：https://github.com/lch319/XiaoYao_QuickJump （AHK 实现，本文件为其核心跳转的 C# 移植）
    /// </summary>
    internal static class QuickJump
    {
        private const int HOTKEY_ID = 0xA001;
        private const uint MOD_CONTROL = 0x0002;    // VK_CONTROL
        private const uint VK_G = 0x47;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_SETTEXT = 0x000C;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int VK_RETURN = 0x0D;
        private const int ID_FILE_COMBO = 0x47D;    // 1149, 标准"文件名"组合框控件 ID
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint SMTO_NORMAL = 0x0000;
        private const uint GA_ROOTOWNER = 3;
        private const uint GA_ROOT = 2;
        private const uint GW_OWNER = 4;

        private static bool _installed;
        private static HotkeyMessageFilter _filter;

        /// <summary>自定义对话框跳转规则（由 TrayContext 启动时注入, 修改需重启生效）。</summary>
        internal static List<Program.QuickJumpRule> Rules = new List<Program.QuickJumpRule>();

        // ---------------- 自动跳转（免 Ctrl+G）相关状态 ----------------

        private const int PUMP_INTERVAL_MS = 250;      // 对话框扫描间隔
        private const int AUTO_JUMP_DELAY_MS = 400;    // 对话框出现后等待其初始化
        private const int AUTO_JUMP_RETRY_MS = 500;    // 失败重试间隔
        private const int AUTO_JUMP_MAX_ATTEMPTS = 3;  // 最多尝试次数
        private const int RESTORE_DELAY_MS = 350;      // 跳转后恢复原文件名前的等待
        private const int WM_GETTEXT = 0x000D;

        internal static bool AutoJumpEnabled;

        private static System.Windows.Forms.Timer _pump;
        private static bool _autoBaselineDone;
        private static readonly Dictionary<IntPtr, DateTime> _handledDialogs = new Dictionary<IntPtr, DateTime>();
        private static readonly Dictionary<IntPtr, PendingJump> _pendingJumps = new Dictionary<IntPtr, PendingJump>();
        private static readonly List<PendingRestore> _pendingRestores = new List<PendingRestore>();

        private sealed class PendingJump
        {
            public DateTime Due;
            public int Attempts;
        }

        private sealed class PendingRestore
        {
            public DateTime Due;
            public IntPtr Edit;                 // Win32 编辑框（WPS 时为零）
            public AutomationElement UiaEdit;   // WPS/UIA 编辑框（Win32 时为 null）
            public string Original;             // 跳转前编辑框里的原文本（如另存为的建议文件名）
            public string Injected;             // 注入的目录路径
        }

        // ---------------- 安装 / 卸载 ----------------

        /// <summary>注册全局热键 Ctrl+G。失败（如被其他程序占用）返回 false。</summary>
        internal static bool Install()
        {
            if (_installed) return true;
            if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CONTROL, VK_G))
                return false;
            _filter = new HotkeyMessageFilter();
            Application.AddMessageFilter(_filter);
            _installed = true;
            EnsurePump();
            return true;
        }

        internal static void Uninstall()
        {
            if (!_installed) return;
            try { UnregisterHotKey(IntPtr.Zero, HOTKEY_ID); } catch { }
            if (_filter != null)
            {
                Application.RemoveMessageFilter(_filter);
                _filter = null;
            }
            _installed = false;
            MaybeStopPump();
        }

        // ---------------- 自动跳转开关 ----------------

        /// <summary>开启"对话框出现即自动跳转"（与 Ctrl+G 热键相互独立）。</summary>
        internal static void StartAutoWatch()
        {
            AutoJumpEnabled = true;
            _autoBaselineDone = false;   // 首次扫描记录基线, 不触发已存在的对话框
            EnsurePump();
            Program.Log("自动跳转: 已开启");
        }

        internal static void StopAutoWatch()
        {
            AutoJumpEnabled = false;
            _pendingJumps.Clear();
            _handledDialogs.Clear();
            _autoBaselineDone = false;
            Program.Log("自动跳转: 已关闭");
            MaybeStopPump();
        }

        /// <summary>自动跳转与热键共用的 UI 线程定时器（250ms）。</summary>
        private static void EnsurePump()
        {
            if (_pump != null) return;
            _pump = new System.Windows.Forms.Timer();
            _pump.Interval = PUMP_INTERVAL_MS;
            _pump.Tick += (s, e) => PumpTick();
            _pump.Start();
        }

        private static void MaybeStopPump()
        {
            if (_installed || AutoJumpEnabled) return;
            if (_pump == null) return;
            try { _pump.Stop(); _pump.Dispose(); } catch { }
            _pump = null;
            _pendingJumps.Clear();
            _pendingRestores.Clear();
        }

        /// <summary>
        /// RegisterHotKey(hWnd=NULL) 把 WM_HOTKEY 投递到线程消息队列，
        /// WinForms 消息循环经 IMessageFilter 截获。
        /// </summary>
        private sealed class HotkeyMessageFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && m.WParam.ToInt64() == HOTKEY_ID)
                {
                    TryJump(GetForegroundWindow(), true);
                    return true; // 已处理, 不再分发
                }
                return false;
            }
        }

        // ---------------- 核心逻辑 ----------------

        /// <summary>
        /// 对指定对话框执行跳转。logUnsupported=true 时对不支持的窗口写提示（仅热键路径）。
        /// 返回 true 表示已成功注入并触发导航。
        /// </summary>
        private static bool TryJump(IntPtr dlg, bool logUnsupported)
        {
            try
            {
                if (dlg == IntPtr.Zero) return false;

                var cls = new StringBuilder(64);
                GetClassName(dlg, cls, cls.Capacity);
                string className = cls.ToString();

                bool isStd = className == "#32770";
                bool isWps = className == "Qt5QWindowIcon" && IsWpsProcess(dlg);

                Program.QuickJumpRule rule = null;
                if (!isStd && !isWps)
                {
                    rule = MatchRule(dlg, className);
                    if (rule == null)
                    {
                        if (logUnsupported) LogUnsupported(dlg, className);
                        return false; // 非文件对话框(或无匹配规则) → 不做任何事
                    }
                }

                string path = GetActiveExplorerFolder();
                if (string.IsNullOrEmpty(path))
                {
                    Program.Log("快速跳转: 未获取到资源管理器当前文件夹");
                    return false;
                }
                if (!path.EndsWith("\\")) path += "\\"; // 末尾反斜杠 → 对话框按"进入文件夹"处理

                if (rule != null)
                {
                    return RuleJump(dlg, path, rule);
                }

                if (isWps)
                {
                    return WpsJump(dlg, path);
                }

                IntPtr edit = FindFileNameEdit(dlg);
                if (edit == IntPtr.Zero)
                {
                    Program.Log("快速跳转: 未找到对话框的文件名编辑框");
                    return false;
                }

                string original = GetEditText(edit);
                if (!SetTextWin32(edit, path)) return false;
                PostMessage(edit, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)1);
                PostMessage(edit, WM_KEYUP, (IntPtr)VK_RETURN, (IntPtr)0xC0000001);
                ScheduleRestore(edit, null, original, path);
                Program.Log("快速跳转 → {0}", path);
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("快速跳转异常: " + ex.Message);
                return false;
            }
        }

        // ---------------- 自动跳转：对话框检测与调度 ----------------

        private static void PumpTick()
        {
            try
            {
                if (AutoJumpEnabled) ScanDialogs();
                RunPendingJumps();
                RunPendingRestores();
            }
            catch (Exception ex)
            {
                Program.Log("自动跳转: 轮询异常 " + ex.Message);
            }
        }

        /// <summary>扫描所有顶层窗口, 把新出现的文件对话框加入待跳转队列。</summary>
        private static void ScanDialogs()
        {
            var found = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(64);
                GetClassName(h, sb, sb.Capacity);
                string cls = sb.ToString();

                if (cls == "#32770")
                {
                    if (IsStdFileDialog(h)) found.Add(h);
                }
                else if (cls == "Qt5QWindowIcon" && IsWpsProcess(h))
                {
                    try { if (FindWpsDialogElement(h) != null) found.Add(h); }
                    catch { }
                }
                else
                {
                    // 自定义规则窗口: 额外要求存在可见编辑框, 降低误触发主页/主窗口的概率
                    if (MatchRule(h, cls) != null && FindFirstVisibleEdit(h) != IntPtr.Zero)
                        found.Add(h);
                }
                return true;
            }, IntPtr.Zero);

            DateTime now = DateTime.Now;

            if (!_autoBaselineDone)
            {
                // 首次扫描把已存在的窗口记为基线, 避免程序启动瞬间对旧对话框误跳
                foreach (var h in found) _handledDialogs[h] = now;
                _autoBaselineDone = true;
                return;
            }

            foreach (var h in found)
            {
                if (_handledDialogs.ContainsKey(h) || _pendingJumps.ContainsKey(h)) continue;
                var pj = new PendingJump();
                pj.Due = now.AddMilliseconds(AUTO_JUMP_DELAY_MS);
                pj.Attempts = 0;
                _pendingJumps[h] = pj;
                Program.Log("自动跳转: 检测到对话框 (0x{0}), 准备跳转", h.ToInt64().ToString("X"));
            }

            // 清理已关闭的窗口(句柄可能被新对话框复用)与过期记录
            if (_handledDialogs.Count > 0)
            {
                var stale = new List<IntPtr>();
                foreach (var kv in _handledDialogs)
                    if (!IsWindow(kv.Key) || (now - kv.Value).TotalMinutes > 10) stale.Add(kv.Key);
                foreach (var h in stale) _handledDialogs.Remove(h);
            }
        }

        /// <summary>
        /// 标准 #32770 是否为文件对话框: 含文件名组合框(控件 1149) 或 ComboBoxEx32,
        /// 或同时具备 ShellView 与可见编辑框(文件夹选择框)。
        /// 消息框/选项框不含这些结构, 因此不会误触发。
        /// </summary>
        private static bool IsStdFileDialog(IntPtr h)
        {
            if (GetDlgItem(h, ID_FILE_COMBO) != IntPtr.Zero) return true;

            bool hasCombo = false;
            bool hasShellView = false;
            EnumChildWindows(h, (c, l) =>
            {
                var sb = new StringBuilder(32);
                GetClassName(c, sb, sb.Capacity);
                string cn = sb.ToString();
                if (cn == "ComboBoxEx32") { hasCombo = true; return false; }
                if (cn == "ShellView" || cn == "DirectUIHWND") hasShellView = true;
                return true;
            }, IntPtr.Zero);

            return hasCombo || (hasShellView && FindFirstVisibleEdit(h) != IntPtr.Zero);
        }

        private static void RunPendingJumps()
        {
            if (_pendingJumps.Count == 0) return;
            DateTime now = DateTime.Now;
            var done = new List<IntPtr>();

            foreach (var kv in _pendingJumps)
            {
                IntPtr h = kv.Key;
                if (!IsWindow(h)) { done.Add(h); continue; }
                if (now < kv.Value.Due) continue;

                bool ok = TryJump(h, false);
                if (ok)
                {
                    _handledDialogs[h] = now;
                    done.Add(h);
                }
                else if (++kv.Value.Attempts >= AUTO_JUMP_MAX_ATTEMPTS)
                {
                    _handledDialogs[h] = now; // 放弃, 避免无限重试
                    done.Add(h);
                    Program.Log("自动跳转: 已放弃该对话框 (0x{0})", h.ToInt64().ToString("X"));
                }
                else
                {
                    kv.Value.Due = now.AddMilliseconds(AUTO_JUMP_RETRY_MS);
                }
            }
            foreach (var h in done) _pendingJumps.Remove(h);
        }

        /// <summary>记录"跳转后需要恢复的原始文件名"(如另存为的建议名称)。</summary>
        private static void ScheduleRestore(IntPtr edit, AutomationElement uiaEdit, string original, string injected)
        {
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(injected)) return;
            if (original.Trim().Equals(injected.Trim(), StringComparison.OrdinalIgnoreCase)) return;

            var r = new PendingRestore();
            r.Due = DateTime.Now.AddMilliseconds(RESTORE_DELAY_MS);
            r.Edit = edit;
            r.UiaEdit = uiaEdit;
            r.Original = original;
            r.Injected = injected;
            _pendingRestores.Add(r);
        }

        private static void RunPendingRestores()
        {
            if (_pendingRestores.Count == 0) return;
            DateTime now = DateTime.Now;

            for (int i = _pendingRestores.Count - 1; i >= 0; i--)
            {
                var r = _pendingRestores[i];
                if (now < r.Due) continue;
                _pendingRestores.RemoveAt(i);

                try
                {
                    string cur = r.UiaEdit != null ? GetUiaValue(r.UiaEdit) : GetEditText(r.Edit);
                    // 跳转成功后文件名框通常为空; 若用户已输入内容则保持不动
                    if (!string.IsNullOrEmpty(cur)) continue;

                    if (r.UiaEdit != null)
                    {
                        var vp = r.UiaEdit.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                        if (vp != null) vp.SetValue(r.Original);
                    }
                    else
                    {
                        SetTextWin32(r.Edit, r.Original);
                    }
                }
                catch { }
            }
        }

        private static string GetUiaValue(AutomationElement el)
        {
            try
            {
                var vp = el.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                return vp == null ? null : vp.Current.Value;
            }
            catch { return null; }
        }

        /// <summary>跨进程读取 Edit 文本（GetWindowText 对他人进程控件不可靠, 改用 WM_GETTEXT）。</summary>
        private static string GetEditText(IntPtr edit)
        {
            if (edit == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(2048);
                IntPtr result;
                IntPtr ok = SendMessageTimeoutText(edit, WM_GETTEXT, (IntPtr)sb.Capacity, sb,
                    SMTO_ABORTIFHUNG | SMTO_NORMAL, 500, out result);
                return ok == IntPtr.Zero ? null : sb.ToString();
            }
            catch { return null; }
        }

        private static bool SetTextWin32(IntPtr edit, string text)
        {
            IntPtr result;
            IntPtr ok = SendMessageTimeoutW(edit, WM_SETTEXT, IntPtr.Zero, text,
                SMTO_ABORTIFHUNG | SMTO_NORMAL, 500, out result);
            if (ok != IntPtr.Zero) return true;
            Program.Log("快速跳转: 写入文本失败, 目标程序可能以管理员权限运行(需要本程序同权限)");
            return false;
        }

        // ---------------- 自定义窗口规则 ----------------

        private static Program.QuickJumpRule MatchRule(IntPtr hwnd, string className)
        {
            if (Rules == null || Rules.Count == 0) return null;
            string exe = GetProcessName(hwnd);
            if (exe == null) return null;

            foreach (var r in Rules)
            {
                if (r == null) continue;
                bool classOk = string.IsNullOrEmpty(r.WindowClass) ||
                    className.IndexOf(r.WindowClass, StringComparison.OrdinalIgnoreCase) >= 0;
                bool exeOk = string.IsNullOrEmpty(r.Exe) ||
                    exe.Equals(r.Exe.Trim(), StringComparison.OrdinalIgnoreCase);
                if (classOk && exeOk && (!string.IsNullOrEmpty(r.WindowClass) || !string.IsNullOrEmpty(r.Exe)))
                    return r;
            }
            return null;
        }

        /// <summary>按规则把路径注入目标窗口: uia=UIA 编辑框 SetValue, win32=WM_SETTEXT。</summary>
        private static bool RuleJump(IntPtr hwnd, string path, Program.QuickJumpRule rule)
        {
            string mode = string.IsNullOrEmpty(rule.Mode) ? "win32" : rule.Mode.Trim().ToLowerInvariant();
            bool done = mode == "uia" ? RuleJumpUia(hwnd, path, rule) : RuleJumpWin32(hwnd, path, rule);
            if (done) Program.Log("快速跳转(规则 {0}/{1}) → {2}", mode, rule.Name ?? "", path);
            return done;
        }

        private static bool RuleJumpWin32(IntPtr hwnd, string path, Program.QuickJumpRule rule)
        {
            IntPtr edit = FindFileNameEdit(hwnd);
            if (edit == IntPtr.Zero)
            {
                Program.Log("快速跳转(规则 win32): 未找到可见编辑框, 可尝试 mode=uia");
                return false;
            }
            if (!SetTextWin32(edit, path)) return false;
            PostMessage(edit, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)1);
            PostMessage(edit, WM_KEYUP, (IntPtr)VK_RETURN, (IntPtr)0xC0000001);
            return true;
        }

        private static bool RuleJumpUia(IntPtr hwnd, string path, Program.QuickJumpRule rule)
        {
            AutomationElement root;
            try { root = AutomationElement.FromHandle(hwnd); }
            catch (Exception ex)
            {
                Program.Log("快速跳转(规则 uia): UIA 获取失败 " + ex.Message);
                return false;
            }
            if (root == null) return false;

            AutomationElement edit = null;
            // 指定 AutomationId 优先
            if (!string.IsNullOrEmpty(rule.AutomationId))
            {
                try
                {
                    edit = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, rule.AutomationId.Trim()));
                }
                catch { }
            }
            if (edit == null)
            {
                try
                {
                    edit = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                }
                catch { }
            }
            if (edit == null)
            {
                Program.Log("快速跳转(规则 uia): 未找到 Edit 元素, 可在规则里填 automationId 指定");
                return false;
            }

            try
            {
                var vp = edit.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                if (vp != null)
                {
                    vp.SetValue(path);
                }
                else
                {
                    // 不支持 ValuePattern 时退化为 WM_SETTEXT 直写
                    IntPtr e = new IntPtr(edit.Current.NativeWindowHandle);
                    if (e == IntPtr.Zero || !SetTextWin32(e, path))
                    {
                        Program.Log("快速跳转(规则 uia): 该 Edit 不支持 ValuePattern");
                        return false;
                    }
                }
                int h = edit.Current.NativeWindowHandle;
                IntPtr target = h != 0 ? new IntPtr(h) : hwnd;
                try { edit.SetFocus(); } catch { }
                PostMessage(target, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)1);
                PostMessage(target, WM_KEYUP, (IntPtr)VK_RETURN, (IntPtr)0xC0000001);
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("快速跳转(规则 uia)异常: " + ex.Message);
                return false;
            }
        }

        private static string GetProcessName(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return null;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                    return p.ProcessName + ".exe";
            }
            catch { return null; }
        }

        // 未匹配时记录窗口特征, 方便用户据此写规则(同一窗口 10s 内只记一次)
        private static string _lastUnsupportedKey;
        private static DateTime _lastUnsupportedTime;

        private static void LogUnsupported(IntPtr hwnd, string className)
        {
            string exe = GetProcessName(hwnd) ?? "?";
            string key = className + "|" + exe;
            DateTime now = DateTime.Now;
            if (key == _lastUnsupportedKey && (now - _lastUnsupportedTime).TotalSeconds < 10) return;
            _lastUnsupportedKey = key;
            _lastUnsupportedTime = now;
            Program.Log("快速跳转: 窗口未匹配 (class={0} exe={1}), 如需支持请在 config.json 的 QuickJumpRules 中添加规则",
                className, exe);
        }

        // ---------------- WPS 自绘文件对话框（Qt + UIA） ----------------

        private static bool IsWpsProcess(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return false;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    return n == "wps" || n == "et" || n == "wpp" || n == "wpspdf";
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// WPS 文字/表格/演示/PDF 的自绘"打开/另存为"浏览框（Qt 窗口, 类名 Qt5QWindowIcon,
        /// UIA 根类名 KcfdFileDialog）。标准 #32770 手法无效, 需通过 UI Automation
        /// 定位筛选行编辑框(KcfdFilterWidget 内的 Edit), SetValue 后回车。
        /// 思路移植自 XiaoYao_QuickJump 辅助/WPS文件对话框.ahk。
        /// </summary>
        private static bool WpsJump(IntPtr foreground, string path)
        {
            try
            {
                AutomationElement dialog = FindWpsDialogElement(foreground);
                if (dialog == null)
                {
                    Program.Log("快速跳转(WPS): 未识别到 WPS 文件浏览框");
                    return false;
                }

                AutomationElement edit = FindWpsFileNameEdit(dialog);
                if (edit == null)
                {
                    Program.Log("快速跳转(WPS): 未找到文件名输入框");
                    return false;
                }

                var vp = edit.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                if (vp == null)
                {
                    Program.Log("快速跳转(WPS): 文件名输入框不支持 Value 模式");
                    return false;
                }
                string original = vp.Current.Value;
                vp.SetValue(path);

                // 激活对话框并把焦点给编辑框, 再模拟回车触发导航
                int dlgHwnd = dialog.Current.NativeWindowHandle;
                SetForegroundWindow(new IntPtr(dlgHwnd));
                try { edit.SetFocus(); } catch { }
                IntPtr editHwnd = new IntPtr(edit.Current.NativeWindowHandle);
                IntPtr target = editHwnd != IntPtr.Zero ? editHwnd : new IntPtr(dlgHwnd);
                PostMessage(target, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)1);
                PostMessage(target, WM_KEYUP, (IntPtr)VK_RETURN, (IntPtr)0xC0000001);
                ScheduleRestore(IntPtr.Zero, edit, original, path);
                Program.Log("快速跳转(WPS) → {0}", path);
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("快速跳转(WPS)异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>前台窗口本身或其 Owner/Parent 中, UIA 根类名为 KcfdFileDialog 的元素。</summary>
        private static AutomationElement FindWpsDialogElement(IntPtr foreground)
        {
            var candidates = new List<IntPtr> { foreground };
            IntPtr owner = GetWindow(foreground, GW_OWNER);
            if (owner != IntPtr.Zero) candidates.Add(owner);
            IntPtr parent = GetParent(foreground);
            if (parent != IntPtr.Zero) candidates.Add(parent);

            foreach (IntPtr h in candidates)
            {
                if (h == IntPtr.Zero || !IsWpsProcess(h)) continue;
                try
                {
                    var el = AutomationElement.FromHandle(h);
                    if (el != null && el.Current.ClassName == "KcfdFileDialog")
                        return el;
                }
                catch { }
            }
            return null;
        }

        private static AutomationElement FindWpsFileNameEdit(AutomationElement dialog)
        {
            // 优先: 筛选行 KcfdFilterWidget 内的 Edit
            try
            {
                var cond = new PropertyCondition(AutomationElement.ClassNameProperty, "KcfdFilterWidget");
                AutomationElement filter = dialog.FindFirst(TreeScope.Descendants, cond);
                if (filter != null)
                {
                    var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                    var inner = filter.FindFirst(TreeScope.Descendants, editCond);
                    if (inner != null && inner.Current.IsEnabled) return inner;
                }
            }
            catch { }

            // 兜底: 对话框内第一个可用 Edit
            try
            {
                var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var ed = dialog.FindFirst(TreeScope.Descendants, editCond);
                if (ed != null && ed.Current.IsEnabled) return ed;
            }
            catch { }
            return null;
        }

        // ---------------- 获取资源管理器当前路径 ----------------

        /// <summary>
        /// 取当前活动资源管理器文件夹(带尾部反斜杠), 失败返回 null。
        /// </summary>
        private static string GetActiveExplorerFolder()
        {
            return GetFolderViaShellWindows();
        }

        /// <summary>
        /// 通过 Shell.Application (IShellWindows) 枚举资源管理器窗口与 Win11 标签页(每个标签是独立 COM 条目)。
        /// 1) 按顶层帧窗口(GetAncestor GA_ROOT)的 Z 序取最近活动的帧;
        /// 2) 帧标题形如 "活动标签名 和 N 个其他选项卡 - 文件资源管理器", 解析活动标签显示名,
        ///    与该帧内各标签路径的末级文件夹名匹配 → 精确命中活动标签(Win11 多标签修复关键);
        /// 3) 标题匹配失败(盘符根/库等显示名与路径末级不一致)时取该帧第一个有效路径。
        /// 注: 曾尝试 UIA 读地址栏, 但新版 Win11 资源管理器地址栏为 WinUI TextBox,
        ///     Name='地址栏' 且 Value 为空, 不暴露路径, 实测不可行, 已移除。
        /// </summary>
        private static string GetFolderViaShellWindows()
        {
            // Z 序列表(最顶在前)
            var zOrder = new List<IntPtr>();
            EnumWindows((h, l) => { zOrder.Add(h); return true; }, IntPtr.Zero);

            // 按帧分组收集有效路径
            var frames = new Dictionary<IntPtr, List<string>>();
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return null;
                dynamic shell = Activator.CreateInstance(t);
                dynamic windows = shell.Windows();
                int count = windows.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic w = windows.Item(i);
                        string url = w.LocationURL as string;
                        if (string.IsNullOrEmpty(url) ||
                            !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;

                        int hwndVal;
                        try { hwndVal = Convert.ToInt32(w.HWND); } catch { continue; }
                        if (hwndVal <= 0) continue;

                        string path = UrlToLocalPath(url);
                        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

                        IntPtr frame = GetAncestor(new IntPtr(hwndVal), GA_ROOT);
                        if (frame == IntPtr.Zero) continue;

                        List<string> list;
                        if (!frames.TryGetValue(frame, out list))
                        {
                            list = new List<string>();
                            frames[frame] = list;
                        }
                        if (!list.Contains(path)) list.Add(path);
                    }
                    catch { /* 个别条目不可访问, 跳过 */ }
                }
            }
            catch (Exception ex)
            {
                Program.Log("快速跳转: Shell.Application 枚举失败 " + ex.Message);
                return null;
            }
            if (frames.Count == 0) return null;

            // 最近活动的帧(Z 序最靠前; 最小化/隐藏帧排最后)
            IntPtr bestFrame = IntPtr.Zero;
            int bestRank = int.MaxValue;
            foreach (var f in frames.Keys)
            {
                int rank = zOrder.IndexOf(f);
                if (rank < 0) rank = int.MaxValue - 1;
                if (rank < bestRank) { bestRank = rank; bestFrame = f; }
            }

            var paths = frames[bestFrame];

            // 用帧标题消歧活动标签
            var tsb = new StringBuilder(512);
            GetWindowText(bestFrame, tsb, tsb.Capacity);
            string activeName = ExtractActiveTabName(tsb.ToString());
            if (activeName != null)
            {
                foreach (var p in paths)
                {
                    string leaf;
                    try { leaf = Path.GetFileName(p.TrimEnd('\\')); } catch { continue; }
                    if (!string.IsNullOrEmpty(leaf) &&
                        leaf.Equals(activeName, StringComparison.OrdinalIgnoreCase))
                    {
                        Program.Log("快速跳转(活动标签[{0}]) → {1}", activeName, p);
                        return p.EndsWith("\\") ? p : p + "\\";
                    }
                }
            }

            string first = paths[0];
            Program.Log("快速跳转(首标签, 标题未匹配:{0}) → {1}", activeName ?? "?", first);
            return first.EndsWith("\\") ? first : first + "\\";
        }

        /// <summary>
        /// 从资源管理器帧标题提取活动标签显示名:
        /// "2026党务 和 1 个其他选项卡 - 文件资源管理器" → "2026党务";
        /// "AutoModeASUS - 文件资源管理器" → "AutoModeASUS"; 英文同理("X and 2 other tabs - Explorer")。
        /// </summary>
        private static string ExtractActiveTabName(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            int i = title.IndexOf(" - ", StringComparison.Ordinal);
            if (i >= 0) title = title.Substring(0, i);
            i = title.IndexOf(" 和 ", StringComparison.Ordinal);
            if (i >= 0) title = title.Substring(0, i);
            i = title.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) title = title.Substring(0, i);
            title = title.Trim();
            return title.Length == 0 ? null : title;
        }

        private static string UrlToLocalPath(string url)
        {
            try
            {
                var uri = new Uri(url);
                if (!uri.IsFile) return null;
                return uri.LocalPath; // 自动处理 URL 解码(中文/空格)与 UNC
            }
            catch { return null; }
        }

        // ---------------- 定位"文件名"编辑框 ----------------

        private static IntPtr FindFileNameEdit(IntPtr dlg)
        {
            // 1) 焦点控件: 文件对话框打开时焦点通常就在"文件名"框
            uint pid;
            GetWindowThreadProcessId(dlg, out pid);
            var gui = new GUITHREADINFO();
            gui.cbSize = (uint)Marshal.SizeOf(typeof(GUITHREADINFO));
            if (GetGUIThreadInfo(pid, ref gui) && gui.hwndFocus != IntPtr.Zero &&
                IsEditLike(gui.hwndFocus) && GetAncestor(gui.hwndFocus, GA_ROOTOWNER) == dlg)
                return gui.hwndFocus;

            // 2) 标准控件 ID 1149("文件名"组合框) 内的 Edit 子窗
            IntPtr combo = GetDlgItem(dlg, ID_FILE_COMBO);
            if (combo != IntPtr.Zero)
            {
                IntPtr e = FindFirstVisibleEdit(combo);
                if (e != IntPtr.Zero) return e;
                if (IsEditLike(combo)) return combo;
            }

            // 3) 兜底: 对话框内第一个可见 Edit
            return FindFirstVisibleEdit(dlg);
        }

        private static IntPtr FindFirstVisibleEdit(IntPtr parent)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (h, l) =>
            {
                if (IsWindowVisible(h) && IsEditLike(h)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsEditLike(IntPtr h)
        {
            var sb = new StringBuilder(32);
            GetClassName(h, sb, sb.Capacity);
            string c = sb.ToString();
            return c == "Edit" || c == "ComboBoxEdit";
        }

        // ---------------- Win32 ----------------

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public int rectLeft;
            public int rectTop;
            public int rectRight;
            public int rectBottom;
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, int msg, IntPtr wParam,
            string lParam, uint flags, uint timeout, out IntPtr lpdwResult);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, int msg, IntPtr wParam,
            StringBuilder lParam, uint flags, uint timeout, out IntPtr lpdwResult);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    }
}

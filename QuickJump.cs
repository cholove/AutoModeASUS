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
    /// 行为：取最近活动的 Windows 资源管理器窗口/标签页的当前文件夹，
    ///       写入对话框"文件名"编辑框并模拟回车，使对话框导航到该目录。
    /// 路径来源：Shell.Application COM（IShellWindows）枚举资源管理器窗口与 Win11 标签页，
    ///           再按窗口 Z 序（EnumWindows）判断"最近活动"。
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
        private const uint GW_OWNER = 4;

        private static bool _installed;
        private static HotkeyMessageFilter _filter;

        /// <summary>自定义对话框跳转规则（由 TrayContext 启动时注入, 修改需重启生效）。</summary>
        internal static List<Program.QuickJumpRule> Rules = new List<Program.QuickJumpRule>();

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
                    TryJumpNow();
                    return true; // 已处理, 不再分发
                }
                return false;
            }
        }

        // ---------------- 核心逻辑 ----------------

        private static void TryJumpNow()
        {
            try
            {
                IntPtr dlg = GetForegroundWindow();
                if (dlg == IntPtr.Zero) return;

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
                        LogUnsupported(dlg, className);
                        return; // 非文件对话框(或无匹配规则) → 不做任何事
                    }
                }

                string path = GetActiveExplorerFolder();
                if (string.IsNullOrEmpty(path))
                {
                    Program.Log("快速跳转: 未获取到资源管理器当前文件夹");
                    return;
                }
                if (!path.EndsWith("\\")) path += "\\"; // 末尾反斜杠 → 对话框按"进入文件夹"处理

                if (rule != null)
                {
                    RuleJump(dlg, path, rule);
                    return;
                }

                if (isWps)
                {
                    WpsJump(dlg, path);
                    return;
                }

                IntPtr edit = FindFileNameEdit(dlg);
                if (edit == IntPtr.Zero)
                {
                    Program.Log("快速跳转: 未找到对话框的文件名编辑框");
                    return;
                }

                if (!SetTextWin32(edit, path)) return;
                PostMessage(edit, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)1);
                PostMessage(edit, WM_KEYUP, (IntPtr)VK_RETURN, (IntPtr)0xC0000001);
                Program.Log("快速跳转 → {0}", path);
            }
            catch (Exception ex)
            {
                Program.Log("快速跳转异常: " + ex.Message);
            }
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
        private static void RuleJump(IntPtr hwnd, string path, Program.QuickJumpRule rule)
        {
            string mode = string.IsNullOrEmpty(rule.Mode) ? "win32" : rule.Mode.Trim().ToLowerInvariant();
            bool done = mode == "uia" ? RuleJumpUia(hwnd, path, rule) : RuleJumpWin32(hwnd, path, rule);
            if (done) Program.Log("快速跳转(规则 {0}/{1}) → {2}", mode, rule.Name ?? "", path);
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
        private static void WpsJump(IntPtr foreground, string path)
        {
            try
            {
                AutomationElement dialog = FindWpsDialogElement(foreground);
                if (dialog == null)
                {
                    Program.Log("快速跳转(WPS): 未识别到 WPS 文件浏览框");
                    return;
                }

                AutomationElement edit = FindWpsFileNameEdit(dialog);
                if (edit == null)
                {
                    Program.Log("快速跳转(WPS): 未找到文件名输入框");
                    return;
                }

                var vp = edit.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                if (vp == null)
                {
                    Program.Log("快速跳转(WPS): 文件名输入框不支持 Value 模式");
                    return;
                }
                vp.SetValue(path);

                // 激活对话框并把焦点给编辑框, 再模拟回车触发导航
                int dlgHwnd = dialog.Current.NativeWindowHandle;
                SetForegroundWindow(new IntPtr(dlgHwnd));
                try { edit.SetFocus(); } catch { }
                IntPtr editHwnd = new IntPtr(edit.Current.NativeWindowHandle);
                IntPtr target = editHwnd != IntPtr.Zero ? editHwnd : new IntPtr(dlgHwnd);
                PostMessage(target, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)1);
                PostMessage(target, WM_KEYUP, (IntPtr)VK_RETURN, (IntPtr)0xC0000001);
                Program.Log("快速跳转(WPS) → {0}", path);
            }
            catch (Exception ex)
            {
                Program.Log("快速跳转(WPS)异常: " + ex.Message);
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
        /// 通过 Shell.Application (IShellWindows) 枚举资源管理器窗口/Win11 标签页,
        /// 按 Z 序取最近活动的窗口的 LocationURL。
        /// 注: Win11 多标签共享同一帧 HWND 时无法精确区分活动标签, 取该 HWND 的第一个有效路径。
        /// </summary>
        private static string GetActiveExplorerFolder()
        {
            // Z 序列表(最顶在前)。对话框打开时, 其下方第一个资源管理器窗口即最近活动窗口。
            var zOrder = new List<IntPtr>();
            EnumWindows((h, l) => { zOrder.Add(h); return true; }, IntPtr.Zero);

            int bestRank = int.MaxValue;
            string bestPath = null;

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
                        var hwnd = new IntPtr(hwndVal);

                        string path = UrlToLocalPath(url);
                        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

                        int rank = zOrder.IndexOf(hwnd);
                        if (rank < 0) rank = int.MaxValue - 1; // 最小化/隐藏窗口排最后
                        if (rank < bestRank)
                        {
                            bestRank = rank;
                            bestPath = path;
                        }
                    }
                    catch { /* 个别窗口不可访问, 跳过 */ }
                }
            }
            catch (Exception ex)
            {
                Program.Log("快速跳转: Shell.Application 枚举失败 " + ex.Message);
            }
            return bestPath;
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
    }
}

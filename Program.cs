using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Management;
using Microsoft.Win32;

namespace AutoModeASUS
{
    /// <summary>
    /// AutoModeASUS - 华硕性能模式自动切换（C# 版）
    /// 功能：
    ///   1. 托盘三模式切换菜单（性能 / 标准 / 安静），RadioCheck 显示当前模式
    ///   2. 无人操作 IdleSeconds(默认300s=5分钟) → 自动切标准模式(0)
    ///   3. 检测到人工操作 → 自动切回活动偏好(默认性能 2)
    ///   4. Fn+F 手动切换 → 尊重手动，以实际档为新偏好继续自动
    ///   5. 自动切换开关（托盘可暂停）；单实例；开机自启；滚动日志
    /// 编译（本机唯一可行通道，csc 直接被安全策略拦截）：
    ///   dotnet build AutoModeASUS.csproj -c Release
    ///   产物 bin\Release\AutoModeASUS.exe → cp 到根目录（ApplicationIcon=leaf.ico）
    /// </summary>
    internal static class Program
    {
        // ---------------- 常量 ----------------
        private const string APP_NAME = "AutoModeASUS";
        private const string MUTEX_NAME = "AutoModeASUS_Singleton";
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string CONFIG_FILE = "config.json";
        private const string LOG_FILE = @"log\auto-mode.log";
        internal const string STOP_FLAG = "stop.flag";
        private const int LOG_MAX_KB = 1024;

        /// <summary>exe 所在目录（自启启动时工作目录可能是 System32，必须用绝对路径）</summary>
        internal static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;

        // ---------------- 配置 ----------------
        /// <summary>自定义对话框跳转规则: 前台窗口 class 含 WindowClass 且进程为 Exe 时, 按 Mode 注入路径。</summary>
        internal sealed class QuickJumpRule
        {
            // 字段值由 JavaScriptSerializer 反序列化赋值, 显式 = null 以抑制 CS0649
            public string Name = null;          // 备注名(日志显示)
            public string WindowClass = null;   // 窗口类名子串匹配, 如 Chrome_WidgetWin_1
            public string Exe = null;           // 进程名精确匹配(不区分大小写), 如 notepad++.exe
            public string Mode = null;          // win32=WM_SETTEXT+回车(默认); uia=UIA ValuePattern+回车
            public string AutomationId = null;  // 可选, mode=uia 时指定目标 Edit 的 AutomationId
        }

        internal sealed class Config
        {
            public int IdleSeconds = 300;       // 无人操作多少秒后切标准 (300 = 5分钟)
            public int PollSeconds = 10;        // 轮询间隔(秒)
            public int ActivitySeconds = 15;    // 空闲低于该秒数视为"有人在操作"
            public int IdleModeValue = 0;       // 空闲目标模式: 0=标准
            public int DefaultActiveValue = 2;  // 活动偏好模式: 2=性能
            public int QuickJumpEnabled = 1;    // 文件对话框 Ctrl+G 快速跳转: 1=开启
            public List<QuickJumpRule> QuickJumpRules = new List<QuickJumpRule>();
        }

        internal static Config LoadConfig()
        {
            var cfg = new Config();
            string path = Path.Combine(ExeDir, CONFIG_FILE);
            try
            {
                if (File.Exists(path))
                {
                    var jss = new JavaScriptSerializer();
                    var d = jss.Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(path, Encoding.UTF8));
                    SetInt(d, "IdleSeconds", v => cfg.IdleSeconds = v);
                    SetInt(d, "PollSeconds", v => cfg.PollSeconds = v);
                    SetInt(d, "ActivitySeconds", v => cfg.ActivitySeconds = v);
                    SetInt(d, "IdleModeValue", v => cfg.IdleModeValue = v);
                    SetInt(d, "DefaultActiveValue", v => cfg.DefaultActiveValue = v);
                    SetInt(d, "QuickJumpEnabled", v => cfg.QuickJumpEnabled = v);
                    object rules;
                    if (d.TryGetValue("QuickJumpRules", out rules) && rules != null)
                    {
                        try
                        {
                            var list = jss.Deserialize<List<QuickJumpRule>>(
                                jss.Serialize(rules));
                            if (list != null) cfg.QuickJumpRules = list;
                        }
                        catch (Exception ex2) { Log("QuickJumpRules 解析失败: " + ex2.Message); }
                    }
                }
                else
                {
                    File.WriteAllText(path,
                        "{\n  \"IdleSeconds\": 300,\n  \"PollSeconds\": 10,\n  \"ActivitySeconds\": 15,\n  \"IdleModeValue\": 0,\n  \"DefaultActiveValue\": 2,\n  \"QuickJumpEnabled\": 1,\n  \"QuickJumpRules\": []\n}\n",
                        Encoding.UTF8);
                }
            }
            catch (Exception ex) { Log("配置读取失败: " + ex.Message); }
            return cfg;
        }

        private static void SetInt(Dictionary<string, object> d, string key, Action<int> setter)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { setter(Convert.ToInt32(v)); } catch { }
            }
        }

        internal static void SaveConfig(Config cfg)
        {
            try
            {
                string rulesJson;
                try { rulesJson = new JavaScriptSerializer().Serialize(cfg.QuickJumpRules ?? new List<QuickJumpRule>()); }
                catch { rulesJson = "[]"; }
                if (string.IsNullOrEmpty(rulesJson)) rulesJson = "[]";

                // 手工序列化保持与默认模板一致的 PascalCase 字段名
                string json = "{\n" +
                    "  \"IdleSeconds\": " + cfg.IdleSeconds + ",\n" +
                    "  \"PollSeconds\": " + cfg.PollSeconds + ",\n" +
                    "  \"ActivitySeconds\": " + cfg.ActivitySeconds + ",\n" +
                    "  \"IdleModeValue\": " + cfg.IdleModeValue + ",\n" +
                    "  \"DefaultActiveValue\": " + cfg.DefaultActiveValue + ",\n" +
                    "  \"QuickJumpEnabled\": " + cfg.QuickJumpEnabled + ",\n" +
                    "  \"QuickJumpRules\": " + rulesJson + "\n" +
                    "}\n";
                File.WriteAllText(Path.Combine(ExeDir, CONFIG_FILE), json, Encoding.UTF8);
            }
            catch (Exception ex) { Log("配置保存失败: " + ex.Message); }
        }

        // ---------------- 日志 ----------------
        private static readonly object LogLock = new object();

        internal static void Log(string fmt, params object[] args)
        {
            lock (LogLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(ExeDir, "log"));
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                        string.Format(fmt, args) + Environment.NewLine;
                    File.AppendAllText(Path.Combine(ExeDir, LOG_FILE), line, Encoding.UTF8);
                    var fi = new FileInfo(Path.Combine(ExeDir, LOG_FILE));
                    if (fi.Length > LOG_MAX_KB * 1024)
                    {
                        try { File.Copy(fi.FullName, fi.FullName + ".old", true); } catch { }
                        try { File.WriteAllText(fi.FullName, string.Empty, Encoding.UTF8); } catch { }
                    }
                }
                catch { }
            }
        }

        // ---------------- 空闲检测 ----------------
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        internal static int GetIdleSeconds()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref lii)) return -1;
            return (int)(((uint)Environment.TickCount - lii.dwTime) / 1000u);
        }

        // ---------------- 单实例 / 自启 ----------------
        private static void InstallAutoStart()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
            {
                if (key == null) throw new Exception("无法打开 HKCU Run 键");
                key.SetValue(APP_NAME, "\"" + Application.ExecutablePath + "\"");
            }
        }

        private static void UninstallAutoStart()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
            {
                if (key != null) key.DeleteValue(APP_NAME, false);
            }
        }

        // ---------------- CLI ----------------
        private static int RunCli(string[] args)
        {
            switch (args[0])
            {
                case "get":
                    try
                    {
                        using (var w = new AsusWmi())
                        {
                            int m = w.GetMode();
                            Console.WriteLine("当前模式: {0} (值 {1})", AsusWmi.MODE_NAMES[m], m);
                        }
                        return 0;
                    }
                    catch (Exception ex) { Console.WriteLine("读取失败: " + ex.ToString()); return 1; }

                case "set":
                    if (args.Length < 2) { Console.WriteLine("用法: set balanced|silent|turbo|0|1|2"); return 1; }
                    int mode = ParseMode(args[1]);
                    if (mode < 0) { Console.WriteLine("未知模式: " + args[1]); return 1; }
                    try
                    {
                        using (var w = new AsusWmi()) w.SetMode(mode);
                        Console.WriteLine("已切换到: {0} (值 {1})", AsusWmi.MODE_NAMES[mode], mode);
                        return 0;
                    }
                    catch (Exception ex) { Console.WriteLine("切换失败: " + ex.ToString()); return 1; }

                case "diag":
                    DiagWmi();
                    return 0;

                case "--install":
                    try { InstallAutoStart(); Console.WriteLine("开机自启已安装: " + Application.ExecutablePath); return 0; }
                    catch (Exception ex) { Console.WriteLine("安装失败: " + ex.Message); return 1; }

                case "--uninstall":
                    try { UninstallAutoStart(); Console.WriteLine("开机自启已移除"); return 0; }
                    catch (Exception ex) { Console.WriteLine("移除失败: " + ex.Message); return 1; }

                default:
                    PrintHelp();
                    return 0;
            }
        }

        private static void DiagWmi()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\WMI");
                Console.WriteLine("scope 创建 OK");
                var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM AsusAtkWmi_WMNB"));
                int n = 0;
                foreach (ManagementObject mo in searcher.Get())
                {
                    n++;
                    Console.WriteLine("实例 #{0}: {1}", n, mo.Path.Path);
                    var p = mo.GetMethodParameters("DSTS");
                    Console.WriteLine("  DSTS inParams 属性: {0}", PropNames(p));
                    p["Device_ID"] = 0x00110019u;
                    Console.WriteLine("  Device_ID 赋值 OK");
                    var r = mo.InvokeMethod("DSTS", p, null);
                    Console.WriteLine("  DSTS outParams 属性: {0}", PropNames(r));
                    foreach (PropertyData pd in r.Properties)
                    {
                        try { Console.WriteLine("    {0} = {1} (0x{1:X8})", pd.Name, Convert.ToUInt64(r[pd.Name])); }
                        catch (Exception ex2) { Console.WriteLine("    {0} 读取失败: {1}", pd.Name, ex2.Message); }
                    }
                    break;
                }
                if (n == 0) Console.WriteLine("未枚举到任何实例");
            }
            catch (Exception ex) { Console.WriteLine("DIAG失败: " + ex.ToString()); }
        }

        private static string PropNames(ManagementBaseObject o)
        {
            var sb = new StringBuilder();
            foreach (PropertyData pd in o.Properties)
            {
                if (sb.Length > 0) sb.Append(",");
                sb.Append(pd.Name);
            }
            return sb.Length == 0 ? "(空)" : sb.ToString();
        }

        private static int ParseMode(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "0": case "balanced": case "standard": case "std": return AsusWmi.MODE_BALANCED;
                case "1": case "silent": case "quiet": return AsusWmi.MODE_SILENT;
                case "2": case "turbo": case "performance": case "perf": return AsusWmi.MODE_TURBO;
                default: return -1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("AutoModeASUS - 华硕性能模式自动切换 (UX8406 lite 通道)");
            Console.WriteLine("模式值: 0=标准  1=安静  2=性能(Turbo)");
            Console.WriteLine("用法:");
            Console.WriteLine("  AutoModeASUS.exe                后台运行(托盘+日志)");
            Console.WriteLine("  AutoModeASUS.exe get            读取当前性能模式");
            Console.WriteLine("  AutoModeASUS.exe set <模式>     设置模式: balanced|silent|turbo 或 0|1|2");
            Console.WriteLine("  AutoModeASUS.exe --install      安装开机自启");
            Console.WriteLine("  AutoModeASUS.exe --uninstall    移除开机自启");
            Console.WriteLine("配置文件: exe同目录 config.json;  日志: exe同目录 log/auto-mode.log");
        }

        // ---------------- 入口 ----------------
        [STAThread]
        private static int Main(string[] args)
        {
            // CLI 模式：不受单实例锁限制，输出走控制台
            if (args.Length > 0)
            {
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
                return RunCli(args);
            }

            // 托盘模式：单实例
            bool createdNew;
            using (var mutex = new Mutex(true, MUTEX_NAME, out createdNew))
            {
                if (!createdNew)
                {
                    Log("已有实例在运行, 本次退出");
                    return 1;
                }

                // 无控制台参数 → 隐藏控制台窗口（编译目标是 console 子系统，便于 get/set 输出）
                try { FreeConsole(); } catch { }

                var cfg = LoadConfig();
                Log("==== AutoMode 启动, 空闲阈值 {0}s, 轮询 {1}s, 托盘=有 ====",
                    cfg.IdleSeconds, cfg.PollSeconds);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext(cfg));
            }
            return 0;
        }

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();
    }

    /// <summary>托盘 + 状态机（生命周期 = ApplicationContext）</summary>
    internal sealed class TrayContext : ApplicationContext
    {
        private NotifyIcon _icon;
        private readonly Program.Config _cfg;
        private readonly System.Windows.Forms.Timer _timer;

        // 状态机变量
        private int _autoMode = -1;     // 脚本期望的模式; -1=尊重手动(无脚本目标)
        private int _activeMode = -1;   // 活动偏好档（切标准前用户所在档）
        private bool _paused;           // 自动切换开关(托盘)

        private readonly ToolStripMenuItem _miPerf;
        private readonly ToolStripMenuItem _miBal;
        private readonly ToolStripMenuItem _miSil;
        private readonly ToolStripMenuItem _miAuto;
        private readonly ToolStripMenuItem _miJump;
        private bool _jumpEnabled;

        public TrayContext(Program.Config cfg)
        {
            _cfg = cfg;
            _jumpEnabled = cfg.QuickJumpEnabled != 0;
            QuickJump.Rules = cfg.QuickJumpRules ?? new List<Program.QuickJumpRule>();

            _miPerf = new ToolStripMenuItem("性能模式", null, (s, e) => TraySetMode(AsusWmi.MODE_TURBO));
            _miBal = new ToolStripMenuItem("标准模式", null, (s, e) => TraySetMode(AsusWmi.MODE_BALANCED));
            _miSil = new ToolStripMenuItem("安静模式", null, (s, e) => TraySetMode(AsusWmi.MODE_SILENT));
            _miAuto = new ToolStripMenuItem("自动切换：5分钟无操作→标准模式", null,
                (s, e) =>
                {
                    _paused = !_paused;
                    RefreshUi(ReadModeSafe());
                    Program.Log(_paused ? "自动切换已暂停(纯手动)" : "自动切换已开启");
                })
            { Checked = true };

            _miJump = new ToolStripMenuItem("文件对话框快速跳转 (Ctrl+G)", null,
                (s, e) => ToggleQuickJump())
            { Checked = _jumpEnabled };

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("AutoMode 华硕性能模式") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miPerf);
            menu.Items.Add(_miBal);
            menu.Items.Add(_miSil);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miAuto);
            menu.Items.Add(_miJump);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("打开日志目录", null, (s, e) => OpenLogDir()));
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => ExitApp()));

            _icon = new NotifyIcon
            {
                Text = "AutoMode 华硕性能模式",
                Icon = MakeIcon(Color.FromArgb(80, 80, 80)),
                ContextMenuStrip = menu,
                Visible = true
            };

            _timer = new System.Windows.Forms.Timer { Interval = Math.Max(1000, _cfg.PollSeconds * 1000) };
            _timer.Tick += OnTick;
            _timer.Start();

            // 文件对话框快速跳转 (Ctrl+G)
            if (_jumpEnabled && !QuickJump.Install())
            {
                _jumpEnabled = false;
                _miJump.Checked = false;
                Program.Log("快速跳转: Ctrl+G 热键注册失败(可能被其他程序占用), 已禁用");
            }

            // 初始同步一次模式显示
            RefreshUi(ReadModeSafe());
        }

        // ---------------- 快速跳转开关 ----------------
        private void ToggleQuickJump()
        {
            if (_jumpEnabled)
            {
                QuickJump.Uninstall();
                _jumpEnabled = false;
                Program.Log("快速跳转已关闭");
            }
            else
            {
                if (!QuickJump.Install())
                {
                    Program.Log("快速跳转: Ctrl+G 热键注册失败(可能被其他程序占用)");
                    return;
                }
                _jumpEnabled = true;
                Program.Log("快速跳转已开启");
            }
            _miJump.Checked = _jumpEnabled;
            _cfg.QuickJumpEnabled = _jumpEnabled ? 1 : 0;
            Program.SaveConfig(_cfg);
        }

        // ---------------- 托盘动作 ----------------
        private void TraySetMode(int mode)
        {
            try
            {
                using (var w = new AsusWmi()) w.SetMode(mode);
                _activeMode = mode;   // 手动选择的模式成为活动偏好
                _autoMode = -1;       // 当前是手动结果, 不当作脚本目标
                Program.Log("托盘手动切换 → {0}(值 {1})", AsusWmi.MODE_NAMES[mode], mode);
                RefreshUi(mode);
            }
            catch (Exception ex) { Program.Log("托盘切换失败: " + ex.Message); }
        }

        private int ReadModeSafe()
        {
            try { using (var w = new AsusWmi()) return w.GetMode(); }
            catch { return -1; }
        }

        private void RefreshUi(int mode)
        {
            if (mode < 0) return;
            _miPerf.Checked = mode == AsusWmi.MODE_TURBO;
            _miBal.Checked = mode == AsusWmi.MODE_BALANCED;
            _miSil.Checked = mode == AsusWmi.MODE_SILENT;
            _miAuto.Checked = !_paused;
            _miAuto.Text = _paused ? "自动切换：已暂停(纯手动)"
                                   : "自动切换：5分钟无操作→标准模式";

            Color c = mode == AsusWmi.MODE_SILENT ? Color.FromArgb(0, 170, 60)
                    : mode == AsusWmi.MODE_BALANCED ? Color.FromArgb(255, 140, 0)
                    : Color.FromArgb(220, 40, 40);
            ReplaceIcon(MakeIcon(c));
            _icon.Text = "AutoMode 华硕性能模式 - " + AsusWmi.MODE_NAMES[mode];
        }

        private void ReplaceIcon(Icon newIcon)
        {
            Icon old = _icon.Icon;
            _icon.Icon = newIcon;
            if (old != null)
            {
                try { DestroyIcon(old.Handle); } catch { }
                old.Dispose();
            }
        }

        // ---------------- 状态机（每 PollSeconds 一次） ----------------
        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                // 停止信号（stop.bat 写 stop.flag）
                string stopPath = Path.Combine(Program.ExeDir, Program.STOP_FLAG);
                if (File.Exists(stopPath))
                {
                    try { File.Delete(stopPath); } catch { }
                    Program.Log("检测到 stop.flag, 退出");
                    ExitApp();
                    return;
                }

                int actual = ReadModeSafe();
                if (actual < 0) return; // 读取失败, 下轮再试
                RefreshUi(actual);

                if (_paused) return; // 自动切换已暂停 → 只刷新显示

                int idle = Program.GetIdleSeconds();

                // 手动干预检测: 实际值 != 脚本上次设置值 → 用户按了 Fn+F
                if (_autoMode >= 0 && actual != _autoMode)
                {
                    Program.Log("检测到手动切换(期望 {0}, 实际 {1}) → 尊重手动, 以实际档为新偏好",
                        _autoMode, actual);
                    _autoMode = -1;
                    _activeMode = actual;
                }

                if (idle >= _cfg.IdleSeconds)
                {
                    // 无人操作 → 切标准模式
                    if (actual != _cfg.IdleModeValue)
                    {
                        if (_activeMode < 0) _activeMode = actual;
                        try
                        {
                            using (var w = new AsusWmi()) w.SetMode(_cfg.IdleModeValue);
                            _autoMode = _cfg.IdleModeValue;
                            Program.Log("空闲 {0}s ≥ {1}s → 切标准模式(值 {2})",
                                idle, _cfg.IdleSeconds, _cfg.IdleModeValue);
                            RefreshUi(_cfg.IdleModeValue);
                        }
                        catch (Exception ex) { Program.Log("切标准模式失败: " + ex.Message); }
                    }
                    // 实际已是标准(可能是用户手动切的) → 保持不动
                }
                else if (idle < _cfg.ActivitySeconds && _autoMode == _cfg.IdleModeValue)
                {
                    // 恢复活动, 且标准是脚本切的 → 切回活动偏好
                    int target = (_activeMode >= 0 && _activeMode != _cfg.IdleModeValue)
                        ? _activeMode : _cfg.DefaultActiveValue;
                    try
                    {
                        using (var w = new AsusWmi()) w.SetMode(target);
                        _autoMode = target;
                        Program.Log("检测到活动(空闲 {0}s) → 切回 {1}(值 {2})",
                            idle, AsusWmi.MODE_NAMES[target], target);
                        RefreshUi(target);
                    }
                    catch (Exception ex) { Program.Log("切回活动模式失败: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Program.Log("轮询异常: " + ex.Message);
            }
        }

        // ---------------- 退出 / 杂项 ----------------
        private void OpenLogDir()
        {
            try { Process.Start("explorer.exe", Path.Combine(Program.ExeDir, "log")); }
            catch { }
        }

        private void ExitApp()
        {
            try { QuickJump.Uninstall(); } catch { }
            try { ReplaceIcon(null); } catch { }
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
            _timer.Stop();
            Application.Exit();
        }

        protected override void ExitThreadCore()
        {
            try { if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; } } catch { }
            base.ExitThreadCore();
        }

        // ---------------- 图标 ----------------
        // 绿叶形状（水平梭形贝塞尔） + 中心模式色点（橙=标准 蓝=性能 绿=安静）
        private static Icon MakeIcon(Color c)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    // 叶形路径
                    var path = new GraphicsPath();
                    var p0 = new PointF(5f, 16f);   // 叶柄端
                    var p1a = new PointF(12f, 5f);  // 上缘控制点
                    var p2a = new PointF(24f, 14f);
                    var p2 = new PointF(29f, 16f);  // 叶尖
                    var p2b = new PointF(24f, 18f);
                    var p1b = new PointF(12f, 27f); // 下缘控制点
                    path.AddBezier(p0, p1a, p2a, p2);
                    path.AddBezier(p2, p2b, p1b, p0);
                    path.CloseFigure();

                    // 渐变填充
                    using (var b = new LinearGradientBrush(
                        new Rectangle(0, 0, 32, 32),
                        Color.FromArgb(129, 199, 132), Color.FromArgb(27, 94, 32),
                        LinearGradientMode.Vertical))
                        g.FillPath(b, path);

                    // 主叶脉
                    using (var pen = new Pen(Color.FromArgb(180, 255, 255, 255), 1.5f))
                        g.DrawBezier(pen, new PointF(6f, 16f), new PointF(13f, 14.5f), new PointF(20f, 17f), new PointF(27.5f, 16f));

                    // 深绿描边
                    using (var pen = new Pen(Color.FromArgb(46, 90, 40), 1f))
                        g.DrawPath(pen, path);

                    // 中心模式色点 + 白描边
                    using (var b2 = new SolidBrush(c))
                        g.FillEllipse(b2, 12.5f, 12.5f, 7f, 7f);
                    using (var pen = new Pen(Color.White, 1.2f))
                        g.DrawEllipse(pen, 12.5f, 12.5f, 7f, 7f);
                }
                using (var small = new Bitmap(bmp, 16, 16))
                {
                    IntPtr h = small.GetHicon();
                    try { return Icon.FromHandle(h); }
                    catch { try { DestroyIcon(h); } catch { } throw; }
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}

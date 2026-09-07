using System;
using System.Management;

namespace AutoModeASUS
{
    /// <summary>
    /// 华硕 WMI 通道封装（灵耀X双屏 UX8406 lite thermal policy）。
    /// 关键点（实机验证）：
    /// 1. UX8406 走 lite 通道 DeviceID=0x00110019（普通机型 0x00120075 在本机返回 -2 不可用）
    /// 2. 必须用 ManagementObjectSearcher 枚举出的"实例对象"调 InvokeMethod
    ///    （自带 InstanceName="ACPI\PNP0C14\ATK_0" 上下文）；直接用类路径会报"无效的方法参数"
    /// 3. lite 模式值映射（与普通机型颠倒）：0=标准 / 1=安静 / 2=性能(Turbo)
    /// </summary>
    internal class AsusWmi : IDisposable
    {
        // lite 通道 DeviceID（UX8406）
        private const uint DEVID_LITE = 0x00110019;

        // lite 模式值映射（与普通机型颠倒！）
        public const int MODE_BALANCED = 0;
        public const int MODE_SILENT = 1;
        public const int MODE_TURBO = 2;

        public static readonly string[] MODE_NAMES = { "标准模式", "安静模式", "性能模式" };

        private ManagementObject _mo;

        public AsusWmi()
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            // 第一步：枚举出真实实例路径（直接用类路径调用会报"无效的方法参数"）
            string inst = null;
            var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM AsusAtkWmi_WMNB"));
            foreach (ManagementObject mo in searcher.Get())
            {
                inst = mo.Path.Path;
                break;
            }
            if (inst == null)
                throw new Exception("未找到 AsusAtkWmi_WMNB 实例，请确认华硕 System Control Interface 驱动正常");
            // 第二步：用实例路径新建对象再调方法（与 pythonnet 验证成功的路径完全同构）
            _mo = new ManagementObject(scope, new ManagementPath(inst), new ObjectGetOptions());
        }

        /// <summary>读取当前性能模式（DSTS 返回值的低字节）</summary>
        public int GetMode()
        {
            var p = _mo.GetMethodParameters("DSTS");
            p["Device_ID"] = DEVID_LITE;
            var r = _mo.InvokeMethod("DSTS", p, null);
            // 注意：该 provider 的输出参数名是 device_status（小写），不是 Status
            try
            {
                return (int)((uint)r["device_status"] & 0xFFu);
            }
            catch (Exception)
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (PropertyData pd in r.Properties) names.Add(pd.Name);
                throw new Exception("DSTS 输出参数中无 device_status, 实际参数: " + string.Join(",", names));
            }
        }

        /// <summary>设置性能模式（0=标准 1=安静 2=性能）</summary>
        public void SetMode(int mode)
        {
            if (mode < 0 || mode > 2)
                throw new ArgumentOutOfRangeException(nameof(mode), "模式值必须为 0/1/2");
            var p = _mo.GetMethodParameters("DEVS");
            p["Device_ID"] = DEVID_LITE;
            p["Control_status"] = (uint)mode;
            var r = _mo.InvokeMethod("DEVS", p, null);
            uint rv = GetReturnValue(r);
            // DEVS 成功返回 1；失败返回 0xFFFFFFFE(-2) 等
            if (rv != 1)
                throw new Exception("DEVS 写入失败, result=" + rv);
        }

        // WMI 方法返回值属性名在不同环境/方法下可能为 ReturnValue / Result / device_status，兼容读取
        private static uint GetReturnValue(ManagementBaseObject r)
        {
            foreach (string name in new[] { "ReturnValue", "Result", "device_status" })
            {
                try { return Convert.ToUInt32(r[name]); }
                catch { }
            }
            throw new Exception("无法读取 WMI 方法返回值");
        }

        public void Dispose()
        {
            if (_mo != null)
            {
                try { _mo.Dispose(); } catch { }
                _mo = null;
            }
        }
    }
}

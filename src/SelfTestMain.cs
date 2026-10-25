using System;

namespace UsbCheck
{
    /// <summary>控制台自检入口：验证检测算法（使用模拟设备，不访问真实磁盘）。</summary>
    internal static class SelfTestMain
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string output = args != null && args.Length > 0 ? args[0] : null;
            return SelfTest.Run(output);
        }
    }
}

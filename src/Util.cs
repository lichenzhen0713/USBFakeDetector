using System;

namespace UsbCheck
{
    internal static class AppInfo
    {
        public const string Name = "U盘扩容检测工具";
        public const string Version = "1.0.6";
    }

    internal static class Util
    {
        public static long AlignDown(long value, long alignment)
        {
            if (value <= 0) return 0;
            return value - (value % alignment);
        }

        public static long Gcd(long a, long b)
        {
            if (a < 0) a = -a;
            if (b < 0) b = -b;
            while (b != 0)
            {
                long t = a % b;
                a = b;
                b = t;
            }
            return a;
        }
    }

    internal static class Fmt
    {
        /// <summary>按 1 GB = 10^9 字节显示（与厂商标称容量一致）。</summary>
        public static string Size(long bytes)
        {
            if (bytes < 0) return "-";
            double gb = bytes / 1e9;
            if (gb >= 1.0) return gb.ToString("0.##") + " GB";
            double mb = bytes / 1e6;
            if (mb >= 1.0) return mb.ToString("0.#") + " MB";
            double kb = bytes / 1e3;
            if (kb >= 1.0) return kb.ToString("0.#") + " KB";
            return bytes.ToString() + " 字节";
        }

        /// <summary>按 GiB（1024 进制）显示。</summary>
        public static string SizeGiB(long bytes)
        {
            if (bytes < 0) return "-";
            double gib = bytes / 1073741824.0;
            if (gib >= 1.0) return gib.ToString("0.##") + " GiB";
            double mib = bytes / 1048576.0;
            if (mib >= 1.0) return mib.ToString("0.#") + " MiB";
            return (bytes / 1024.0).ToString("0.#") + " KiB";
        }

        public static string Exact(long bytes)
        {
            return bytes.ToString("N0") + " 字节";
        }

        public static string Duration(double seconds)
        {
            if (seconds < 1) return "不到 1 秒";
            if (seconds < 60) return seconds.ToString("0") + " 秒";
            if (seconds < 3600) return (seconds / 60).ToString("0.0") + " 分钟";
            if (seconds < 86400) return (seconds / 3600).ToString("0.0") + " 小时";
            return (seconds / 86400).ToString("0.0") + " 天";
        }
    }
}






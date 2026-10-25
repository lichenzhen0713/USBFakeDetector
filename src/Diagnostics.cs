using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace UsbCheck
{
    /// <summary>诊断 / 命令行检测入口，用于自动化测试与问题排查。</summary>
    internal static class Diagnostics
    {
        /// <summary>只读检查磁盘：打开、查询容量、读取扇区、尝试写方式打开，但不写入数据。</summary>
        public static void RunProbe(int diskIndex, string outputPath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("UsbFakeDriveDetector 诊断报告");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            try
            {
                sb.AppendLine("== 磁盘列表 ==");
                foreach (DiskInfo disk in DiskInventory.Enumerate())
                {
                    sb.AppendLine(string.Format(
                        "磁盘 {0}: 接口={1} 介质={2} 容量={3} 型号={4} 序列号={5} 卷={6}",
                        disk.Index, disk.InterfaceType, disk.MediaType, disk.Size,
                        disk.Model, disk.SerialNumber, disk.Letters));
                }
                sb.AppendLine();

                sb.AppendLine("== 只读打开磁盘 " + diskIndex.ToString() + " ==");
                using (RawDisk device = new RawDisk(diskIndex, false, 0))
                {
                    sb.AppendLine("容量=" + device.Length.ToString() + "（" + Fmt.Size(device.Length) + "）");
                    sb.AppendLine("容量来源=" + device.LengthSource);
                    sb.AppendLine("对齐单位=" + device.Unit.ToString());
                    byte[] buffer = new byte[device.Unit];
                    device.Read(0, buffer, buffer.Length);
                    sb.AppendLine("读取扇区 0 成功，前 16 字节=" + BitConverter.ToString(buffer, 0, 16));
                }
                sb.AppendLine();

                sb.AppendLine("== 以写方式打开（不写入）==");
                using (RawDisk device = new RawDisk(diskIndex, true, 0))
                {
                    sb.AppendLine("成功，容量=" + device.Length.ToString() + "，来源=" + device.LengthSource);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("错误：" + ex.Message);
                sb.AppendLine(ex.ToString());
            }

            WriteText(outputPath, sb.ToString());
        }

        /// <summary>命令行执行一次真实检测（会写入磁盘），生成报告与日志，返回进程退出码。</summary>
        public static int RunDetection(int diskIndex, bool full, string outputPath)
        {
            StringBuilder log = new StringBuilder();
            int exitCode = 1;

            try
            {
                List<DiskInfo> disks = DiskInventory.Enumerate();
                DiskInfo info = null;
                foreach (DiskInfo disk in disks)
                {
                    if (disk.Index == diskIndex) info = disk;
                }

                long claimed = info != null ? info.Size : 0;
                List<string> letters = info != null ? info.VolumeLetters : new List<string>();

                DetectionOptions options = new DetectionOptions();
                options.Full = full;
                options.SampleCount = 160;

                log.AppendLine("磁盘=" + diskIndex.ToString() + "，方式=" + (full ? "完整校验" : "快速扫描"));

                DetectionResult result;
                using (VolumeGuard guard = new VolumeGuard(letters, delegate(string s) { log.AppendLine(s); }))
                {
                    result = Detector.Run(
                        delegate(bool writable)
                        {
                            RawDisk raw = new RawDisk(diskIndex, writable, claimed);
                            if (writable) log.AppendLine("容量来源=" + raw.LengthSource);
                            return raw;
                        },
                        claimed,
                        options,
                        delegate(string s) { log.AppendLine(s); },
                        null,
                        CancellationToken.None);
                }

                WriteText(outputPath, ReportWriter.Build(result, info));
                WriteText(outputPath + ".log", log.ToString());

                if (result.Verdict == DetectionVerdict.Genuine) exitCode = 0;
                else if (result.Verdict == DetectionVerdict.Fake || result.Verdict == DetectionVerdict.Damaged) exitCode = 3;
            }
            catch (Exception ex)
            {
                log.AppendLine("ERROR: " + ex.ToString());
                WriteText(outputPath + ".log", log.ToString());
            }

            return exitCode;
        }

        /// <summary>命令行执行一次文件模式检测（只使用可用空间，不覆盖原有文件）。</summary>
        public static int RunFileDetection(string root, long maxBytes, string outputPath)
        {
            StringBuilder log = new StringBuilder();
            int exitCode = 1;

            try
            {
                if (!root.EndsWith("\\")) root += "\\";
                DriveInfo drive = new DriveInfo(root);
                long free = drive.AvailableFreeSpace;
                long margin = Math.Min(64L << 20, free / 10);
                long testSize = free - margin;
                if (maxBytes > 0 && maxBytes < testSize) testSize = maxBytes;
                testSize -= testSize % FileTestSet.BlockSize;
                if (testSize < 16L * 1024 * 1024)
                    throw new DeviceException("可用空间不足（" + Fmt.Size(free) + "），无法进行文件模式检测。");

                string letter = root.Substring(0, 2);
                List<DiskInfo> disks = DiskInventory.Enumerate();
                DiskInfo info = null;
                foreach (DiskInfo disk in disks)
                {
                    foreach (VolumeInfo volume in disk.Volumes)
                    {
                        if (string.Equals(volume.Letter, letter, StringComparison.OrdinalIgnoreCase)) info = disk;
                    }
                }

                FileTestSet set = new FileTestSet(root, testSize,
                    Guid.NewGuid().ToString("N").Substring(0, 8));

                DetectionOptions options = new DetectionOptions();
                options.Full = true;
                options.ModeName = "完整校验（文件模式）";
                options.MaxBytes = testSize;
                options.PreScan = false;

                DetectionResult result;
                try
                {
                    log.AppendLine("文件模式：卷=" + root + "，可用空间=" + Fmt.Size(free) +
                        "，测试容量=" + Fmt.Size(testSize));
                    result = Detector.Run(
                        set.Open,
                        info != null ? info.Size : testSize,
                        options,
                        delegate(string s) { log.AppendLine(s); },
                        null,
                        CancellationToken.None);

                    long used = Math.Max(0, drive.TotalSize - free);
                    if (result.EstimatedCapacity.HasValue)
                    {
                        result.EstimatedCapacity = Util.AlignDown(used + result.EstimatedCapacity.Value, FileTestSet.BlockSize);
                        result.Warnings.Add("文件模式按“原有数据占用 + 可用空间内可写范围”估算实际容量，受文件分配位置影响，可能存在偏差。");
                    }
                    else if (result.FirstBadOffset >= 0)
                    {
                        result.EstimatedCapacity = Util.AlignDown(used + result.FirstBadOffset, FileTestSet.BlockSize);
                        result.Warnings.Add("文件模式按“原有数据占用 + 可写范围”估算实际容量，受文件分配位置影响。");
                    }
                    result.Warnings.Add("文件模式只验证了可用空间，未测试原有文件占用的区域。");

                    if (set.DiskFull && result.Verdict != DetectionVerdict.Genuine)
                    {
                        result.Verdict = DetectionVerdict.Failed;
                        result.Error = "可用空间不足，测试未能写满。";
                    }
                }
                finally
                {
                    set.CloseAndDelete(delegate(string s) { log.AppendLine(s); });
                }

                WriteText(outputPath, ReportWriter.Build(result, info));
                WriteText(outputPath + ".log", log.ToString());

                if (result.Verdict == DetectionVerdict.Genuine) exitCode = 0;
                else if (result.Verdict == DetectionVerdict.Fake || result.Verdict == DetectionVerdict.Damaged) exitCode = 3;
            }
            catch (Exception ex)
            {
                log.AppendLine("ERROR: " + ex.ToString());
                WriteText(outputPath + ".log", log.ToString());
            }

            return exitCode;
        }

        /// <summary>安全模式：只写卷内空闲簇，快速判断实际容量。</summary>
        public static int RunSafeScan(string letter, string outputPath)
        {
            StringBuilder log = new StringBuilder();
            int exitCode = 1;

            try
            {
                if (letter.EndsWith("\\")) letter = letter.Substring(0, 2);
                if (!letter.EndsWith(":")) letter += ":";

                List<DiskInfo> disks = DiskInventory.Enumerate();
                DiskInfo info = null;
                foreach (DiskInfo disk in disks)
                {
                    foreach (VolumeInfo volume in disk.Volumes)
                    {
                        if (string.Equals(volume.Letter, letter, StringComparison.OrdinalIgnoreCase)) info = disk;
                    }
                }

                long claimed = info != null ? info.Size : 0;
                log.AppendLine("安全模式：卷=" + letter);
                DetectionResult result = SafeVolumeScanner.Scan(letter, claimed,
                    delegate(string s) { log.AppendLine(s); }, null, CancellationToken.None);

                WriteText(outputPath, ReportWriter.Build(result, info));
                WriteText(outputPath + ".log", log.ToString());

                if (result.Verdict == DetectionVerdict.Genuine) exitCode = 0;
                else if (result.Verdict == DetectionVerdict.Fake || result.Verdict == DetectionVerdict.Damaged) exitCode = 3;
            }
            catch (Exception ex)
            {
                log.AppendLine("ERROR: " + ex);
                WriteText(outputPath + ".log", log.ToString());
            }

            return exitCode;
        }

        private static void WriteText(string path, string text)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }
    }
}





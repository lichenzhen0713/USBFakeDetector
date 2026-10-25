using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace UsbCheck
{
    /// <summary>用模拟设备验证检测算法（无U盘、无管理员权限时也可运行）。</summary>
    internal static class SelfTest
    {
        public static int Run(string outputPath)
        {
            List<string> lines = new List<string>();
            int failures = 0;

            lines.Add("U盘扩容检测工具 - 算法自检");
            lines.Add("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            lines.Add("说明：使用内存模拟设备验证检测算法，不访问任何真实磁盘。");
            lines.Add("");

            try
            {
                TestPatterns(ref failures, lines);
                TestFileSet(ref failures, lines);
                TestQuickGenuine(ref failures, lines);
                TestQuickWrap(ref failures, lines);
                TestQuickDrop(ref failures, lines);
                TestFullGenuine(ref failures, lines);
                TestFullWrap(ref failures, lines);
                TestFullEarlyWrap(ref failures, lines);
                TestFullDrop(ref failures, lines);
                TestReport(ref failures, lines);
            }
            catch (Exception ex)
            {
                lines.Add("[异常] " + ex.ToString());
                failures++;
            }

            lines.Add("");
            lines.Add(failures == 0 ? "结论：全部自检通过。" : "结论：" + failures.ToString() + " 项自检失败。");
            string text = string.Join(Environment.NewLine, lines);
            Console.WriteLine(text);

            if (!string.IsNullOrEmpty(outputPath))
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(outputPath, text, new UTF8Encoding(true));
            }

            return failures == 0 ? 0 : 1;
        }

        private static void TestPatterns(ref int failures, List<string> lines)
        {
            lines.Add("-- 数据格式 --");

            byte[] run = Pattern.NewRunTag();
            byte[] tag = new byte[Pattern.BlockSize];
            long offset;
            int index;

            Pattern.BuildTag(tag, run, 1234L * Pattern.BlockSize, 7);
            bool tagOk = Pattern.TryParseTag(tag, run, out offset, out index);
            Check(tagOk && offset == 1234L * Pattern.BlockSize && index == 7,
                "采样标记可正确写入与解析", lines, ref failures);
            Check(!Pattern.TryParseTag(tag, Pattern.NewRunTag(), out offset, out index),
                "采样标记带本次运行 ID，其它运行无法误识别", lines, ref failures);

            tag[200] ^= 0x5A;
            Check(!Pattern.TryParseTag(tag, run, out offset, out index),
                "采样标记被篡改后可被校验和发现", lines, ref failures);

            byte[] stream = new byte[Pattern.BlockSize * 2];
            Pattern.FillStream(stream, 0, 4096, stream.Length, run);
            Check(Pattern.TryParseStreamHeader(stream, 0, run, out offset) && offset == 4096,
                "数据流块头可正确写入与解析", lines, ref failures);
            Check(Pattern.TryParseStreamHeader(stream, Pattern.BlockSize, run, out offset) && offset == 8192,
                "数据流块头偏移连续", lines, ref failures);
            Check(!Pattern.TryParseStreamHeader(stream, 0, Pattern.NewRunTag(), out offset),
                "数据流块头带本次运行 ID", lines, ref failures);
        }

        private static void TestFileSet(ref int failures, List<string> lines)
        {
            lines.Add("-- 文件模式（不覆盖原有文件）--");

            string dir = Path.Combine(Path.GetTempPath(), "usbcheck-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string keep = Path.Combine(dir, "keep.txt");
            File.WriteAllText(keep, "keep-me", Encoding.UTF8);

            long size = 16L << 20;
            FileTestSet set = new FileTestSet(dir + "\\", size, "selftest");
            bool dataOk = true;

            try
            {
                byte[] block = new byte[FileTestSet.BlockSize];
                for (int i = 0; i < block.Length; i++) block[i] = (byte)((i * 7 + 3) & 0xFF);

                using (IBlockDevice device = set.Open(true))
                {
                    for (long off = 0; off < size; off += block.Length)
                        device.Write(off, block, block.Length);
                    device.Flush();
                }

                using (IBlockDevice device = set.Open(false))
                {
                    byte[] read = new byte[block.Length];
                    for (long off = 0; off < size && dataOk; off += block.Length)
                    {
                        device.Read(off, read, read.Length);
                        for (int i = 0; i < read.Length; i++)
                        {
                            if (read[i] != block[i])
                            {
                                dataOk = false;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                dataOk = false;
                lines.Add("    异常：" + ex.Message);
            }
            finally
            {
                set.CloseAndDelete(delegate(string s) { lines.Add("    " + s); });
            }

            Check(dataOk, "文件模式写入/读回数据一致", lines, ref failures);
            Check(File.Exists(keep), "原有文件未被删除", lines, ref failures);
            Check(Directory.GetFiles(dir, "_usbcheck_*").Length == 0, "临时测试文件已清理", lines, ref failures);

            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
            lines.Add("");
        }

        private static void TestQuickGenuine(ref int failures, List<string> lines)
        {
            lines.Add("-- 正常盘 · 快速扫描 --");
            SimDisk disk = new SimDisk(32L << 20, 32L << 20, SimMode.Normal);
            DetectionResult result = RunQuick(disk, 96);
            Report(result, lines);
            Check(result.Verdict == DetectionVerdict.Genuine,
                "正常盘判定为正常（实际：" + result.VerdictText + "）", lines, ref failures);
        }

        private static void TestQuickWrap(ref int failures, List<string> lines)
        {
            lines.Add("-- 扩容盘（地址回绕）· 快速扫描 --");
            SimDisk disk = new SimDisk(32L << 20, 4L << 20, SimMode.Wrap);
            DetectionResult result = RunQuick(disk, 96);
            Report(result, lines);
            Check(result.Verdict == DetectionVerdict.Fake,
                "识别出扩容盘（实际：" + result.VerdictText + "）", lines, ref failures);
            Check(EstimateNear(result, 4L << 20),
                "真实容量估计约为 4 MB（实际：" + EstimateText(result) + "）", lines, ref failures);
        }

        private static void TestQuickDrop(ref int failures, List<string> lines)
        {
            lines.Add("-- 扩容盘（丢写）· 快速扫描 --");
            SimDisk disk = new SimDisk(32L << 20, 4L << 20, SimMode.Drop);
            DetectionResult result = RunQuick(disk, 96);
            Report(result, lines);
            Check(result.Verdict == DetectionVerdict.Fake,
                "识别出丢写型扩容盘（实际：" + result.VerdictText + "）", lines, ref failures);
            Check(EstimateNear(result, 4L << 20),
                "实际容量估计约为 4 MB（实际：" + EstimateText(result) + "）", lines, ref failures);
        }

        private static void TestFullGenuine(ref int failures, List<string> lines)
        {
            lines.Add("-- 正常盘 · 完整校验 --");
            SimDisk disk = new SimDisk(16L << 20, 16L << 20, SimMode.Normal);
            DetectionResult result = RunFull(disk, 0);
            Report(result, lines);
            Check(result.Verdict == DetectionVerdict.Genuine,
                "正常盘完整校验通过（实际：" + result.VerdictText + "）", lines, ref failures);
            Check(result.BytesRead == (16L << 20),
                "全部数据都被读回校验（实际：" + Fmt.Size(result.BytesRead) + "）", lines, ref failures);
        }

        private static void TestFullWrap(ref int failures, List<string> lines)
        {
            lines.Add("-- 扩容盘（地址回绕）· 完整校验 --");
            SimDisk disk = new SimDisk(32L << 20, 4L << 20, SimMode.Wrap);
            DetectionResult result = RunFull(disk, 0);
            Report(result, lines);
            Check(result.Verdict == DetectionVerdict.Fake,
                "识别出扩容盘（实际：" + result.VerdictText + "）", lines, ref failures);
            Check(EstimateNear(result, 4L << 20),
                "真实容量估计约为 4 MB（实际：" + EstimateText(result) + "）", lines, ref failures);
        }

        private static void TestFullEarlyWrap(ref int failures, List<string> lines)
        {
            lines.Add("-- 扩容盘（地址回绕）· 写入过程中提前发现 --");
            SimDisk disk = new SimDisk(256L << 20, 8L << 20, SimMode.Wrap);
            DetectionResult result = RunFull(disk, 0);
            Report(result, lines);
            Check(result.Verdict == DetectionVerdict.Fake,
                "识别出扩容盘（实际：" + result.VerdictText + "）", lines, ref failures);
            Check(EstimateNear(result, 8L << 20),
                "真实容量估计约为 8 MB（实际：" + EstimateText(result) + "）", lines, ref failures);
            Check(result.BytesWritten < (128L << 20),
                "确认回绕后提前终止，没有写满标称容量（已写：" + Fmt.Size(result.BytesWritten) + "）", lines, ref failures);
        }

        private static void TestFullDrop(ref int failures, List<string> lines)
        {
            lines.Add("-- 扩容盘（丢写）· 完整校验 --");
            SimDisk disk = new SimDisk(32L << 20, 4L << 20, SimMode.Drop);
            DetectionResult result = RunFull(disk, 0);
            Report(result, lines);
            Check(result.Verdict == DetectionVerdict.Fake,
                "识别出丢写型扩容盘（实际：" + result.VerdictText + "）", lines, ref failures);
            Check(result.FirstBadOffset >= (3L << 20) && result.FirstBadOffset <= (5L << 20),
                "首个错误位置接近 4 MB（实际：" + Fmt.Size(result.FirstBadOffset) + "）", lines, ref failures);
            Check(EstimateNear(result, 4L << 20),
                "实际容量估计约为 4 MB（实际：" + EstimateText(result) + "）", lines, ref failures);
            Check(result.BytesWritten < (16L << 20),
                "写入过程中提前终止（已写：" + Fmt.Size(result.BytesWritten) + "）", lines, ref failures);
        }

        private static void TestReport(ref int failures, List<string> lines)
        {
            lines.Add("-- 检测报告 --");
            SimDisk disk = new SimDisk(64L << 20, 8L << 20, SimMode.Wrap);
            DetectionResult result = RunQuick(disk, 96);
            string report = ReportWriter.Build(result, null);
            Report(result, lines);

            Check(report.Contains("U 盘 扩 容 检 测 报 告"), "报告包含标题", lines, ref failures);
            Check(report.Contains("疑似扩容盘"), "报告包含检测结论", lines, ref failures);
            Check(report.Contains("实际可用容量"), "报告包含真实容量推算", lines, ref failures);
            Check(report.Contains("检测证据"), "报告包含证据段落", lines, ref failures);
            Check(report.Contains("建议"), "报告包含建议段落", lines, ref failures);
        }

        private static DetectionResult RunQuick(SimDisk disk, int samples)
        {
            DetectionOptions options = new DetectionOptions();
            options.Full = false;
            options.SampleCount = samples;
            return Detector.Run(disk.Open, disk.ClaimedBytes, options, null, null, CancellationToken.None);
        }

        private static DetectionResult RunFull(SimDisk disk, long maxBytes)
        {
            DetectionOptions options = new DetectionOptions();
            options.Full = true;
            options.MaxBytes = maxBytes;
            return Detector.Run(disk.Open, disk.ClaimedBytes, options, null, null, CancellationToken.None);
        }

        private static bool EstimateNear(DetectionResult result, long expected)
        {
            if (!result.EstimatedCapacity.HasValue) return false;
            long value = result.EstimatedCapacity.Value;
            long tolerance = Math.Max(65536, expected / 16);
            return value >= expected - tolerance && value <= expected + tolerance;
        }

        private static string EstimateText(DetectionResult result)
        {
            if (!result.EstimatedCapacity.HasValue) return "无估计值";
            return Fmt.Size(result.EstimatedCapacity.Value);
        }

        private static void Report(DetectionResult result, List<string> lines)
        {
            lines.Add("   结果：" + result.VerdictText +
                "；写入 " + Fmt.Size(result.BytesWritten) +
                "，读回 " + Fmt.Size(result.BytesRead) +
                "，用时 " + Fmt.Duration(result.Elapsed.TotalSeconds));
            foreach (string line in result.Evidence) lines.Add("   证据：" + line);
            foreach (string line in result.Warnings) lines.Add("   提示：" + line);
            lines.Add("");
        }

        private static void Check(bool condition, string name, List<string> lines, ref int failures)
        {
            lines.Add((condition ? "  [通过] " : "  [失败] ") + name);
            if (!condition) failures++;
        }
    }
}



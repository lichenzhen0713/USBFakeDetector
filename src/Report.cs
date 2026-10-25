using System;
using System.IO;
using System.Text;

namespace UsbCheck
{
    internal static class ReportWriter
    {
        public static string ReportFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) documents = AppDomain.CurrentDomain.BaseDirectory;
            string folder = Path.Combine(documents, "U盘容量检测报告");
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static string Save(DetectionResult result, DiskInfo disk)
        {
            string folder = ReportFolder();
            string baseName = "U盘检测_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string file = Path.Combine(folder, baseName + ".txt");

            int index = 2;
            while (File.Exists(file))
            {
                file = Path.Combine(folder, baseName + "_" + index.ToString() + ".txt");
                index++;
            }

            File.WriteAllText(file, Build(result, disk), new UTF8Encoding(true));
            return file;
        }

        public static string Build(DetectionResult result, DiskInfo disk)
        {
            StringBuilder sb = new StringBuilder();
            bool safeMode = result.Mode != null && result.Mode.IndexOf("安全模式", StringComparison.Ordinal) >= 0;
            bool fileMode = result.Mode != null &&
                (result.Mode.IndexOf("文件模式", StringComparison.Ordinal) >= 0 ||
                 result.Mode.IndexOf("安全模式", StringComparison.Ordinal) >= 0);

            sb.AppendLine("================================================================");
            sb.AppendLine("                    U 盘 扩 容 检 测 报 告");
            sb.AppendLine("================================================================");
            sb.AppendLine();

            sb.AppendLine("一、基本信息");
            sb.AppendLine("  检测时间    ：" + result.Started.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("  检测方式    ：" + result.Mode);
            sb.AppendLine("  软件版本    ：" + AppInfo.Name + " " + AppInfo.Version);
            sb.AppendLine("  计算机名    ：" + Environment.MachineName);
            sb.AppendLine("  检测结论    ：" + result.VerdictText);
            sb.AppendLine();

            if (disk != null)
            {
                sb.AppendLine("二、设备信息");
                sb.AppendLine("  磁盘编号    ：" + disk.Index.ToString());
                sb.AppendLine("  盘符        ：" + disk.Letters);
                sb.AppendLine("  卷标        ：" + (string.IsNullOrEmpty(disk.Labels) ? "(无)" : disk.Labels));
                sb.AppendLine("  文件系统    ：" + (string.IsNullOrEmpty(disk.FileSystems) ? "(未知)" : disk.FileSystems));
                sb.AppendLine("  接口类型    ：" + disk.InterfaceType);
                sb.AppendLine("  型号        ：" + disk.Model);
                sb.AppendLine("  序列号      ：" + (string.IsNullOrEmpty(disk.SerialNumber) ? "(未知)" : disk.SerialNumber));
                sb.AppendLine("  声称容量    ：" + Fmt.Size(result.ClaimedBytes) + "（" +
                    Fmt.Exact(result.ClaimedBytes) + "，" + Fmt.SizeGiB(result.ClaimedBytes) + "）");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("二、设备信息");
                sb.AppendLine("  声称容量    ：" + Fmt.Size(result.ClaimedBytes) + "（" + Fmt.Exact(result.ClaimedBytes) + "）");
                sb.AppendLine();
            }

            sb.AppendLine("三、检测结果");
            sb.AppendLine("  结论        ：" + result.VerdictText);
            if (result.EstimatedCapacity.HasValue)
            {
                long estimate = result.EstimatedCapacity.Value;
                sb.AppendLine("  实际可用容量：约 " + Fmt.Size(estimate) + "（" + Fmt.Exact(estimate) +
                    "，" + Fmt.SizeGiB(estimate) + "）");
            }
            else if (result.Verdict == DetectionVerdict.Genuine)
            {
                sb.AppendLine("  实际可用容量：测试范围内未发现异常");
            }
            else
            {
                sb.AppendLine("  实际可用容量：未能推算");
            }
            if (result.FirstBadOffset >= 0)
                sb.AppendLine("  首个异常位置：" + Fmt.Size(result.FirstBadOffset) + "（" + Fmt.Exact(result.FirstBadOffset) + "）");
            if (safeMode)
                sb.AppendLine("  检测范围    ：卷内空闲簇（抽样，不含原有文件）");
            else if (fileMode)
                sb.AppendLine("  测试范围    ：" + Fmt.Size(result.TestedBytes) + "（只使用空闲区，不含原有文件）");
            sb.AppendLine("  写入数据量  ：" + Fmt.Size(result.BytesWritten) + "（" + Fmt.Exact(result.BytesWritten) + "）");
            sb.AppendLine("  读回数据量  ：" + Fmt.Size(result.BytesRead) + "（" + Fmt.Exact(result.BytesRead) + "）");
            sb.AppendLine("  检测用时    ：" + Fmt.Duration(result.Elapsed.TotalSeconds));
            if (result.SamplesTotal > 0)
                sb.AppendLine("  采样点      ：" + result.SamplesTotal.ToString() + " 个，正常 " + result.SamplesOk.ToString() + " 个");
            if (!string.IsNullOrEmpty(result.Error))
                sb.AppendLine("  错误信息    ：" + result.Error);
            sb.AppendLine();

            sb.AppendLine("四、检测证据");
            if (result.Evidence.Count == 0)
            {
                sb.AppendLine("  （无）");
            }
            else
            {
                for (int i = 0; i < result.Evidence.Count; i++)
                    sb.AppendLine("  " + (i + 1).ToString() + ". " + result.Evidence[i]);
            }
            sb.AppendLine();

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("五、提示");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
                sb.AppendLine();
            }

            sb.AppendLine("六、建议");
            switch (result.Verdict)
            {
                case DetectionVerdict.Fake:
                    sb.AppendLine("  - 该U盘实际容量小于标称容量，属于“扩容盘”，请不要存放任何重要数据。");
                    sb.AppendLine("  - 建议尽快联系商家退货或申请平台介入；扩容盘的数据随时可能丢失。");
                    sb.AppendLine("  - 若需继续使用，请按实际容量重新分区/格式化（不推荐）。");
                    break;
                case DetectionVerdict.Damaged:
                    sb.AppendLine("  - 数据无法在标称容量范围内正确读回，可能是扩容盘，也可能是闪存/主控故障。");
                    sb.AppendLine("  - 建议更换一块U盘复测，并避免在该盘上保存重要数据。");
                    break;
                case DetectionVerdict.Genuine:
                    sb.AppendLine("  - 本次检测未发现容量虚标。");
                    if (result.Mode != null && result.Mode.IndexOf("完整", StringComparison.Ordinal) < 0)
                        sb.AppendLine("  - 快速扫描为抽样检测，不能保证 100% 识别所有扩容盘；重要场合建议执行“完整校验”。");
                    break;
                case DetectionVerdict.Cancelled:
                    sb.AppendLine("  - 检测被取消，结果不完整，建议重新执行完整校验。");
                    break;
                default:
                    sb.AppendLine("  - 检测未能完成，请根据错误信息排查后重试。");
                    break;
            }
            sb.AppendLine();

            sb.AppendLine("七、说明");
            sb.AppendLine("  1. 本工具通过向U盘写入带唯一标记的数据并读回校验，判断实际可存储容量。");
            if (fileMode)
                sb.AppendLine("  2. 安全模式只写入文件系统标记为空闲的区域，不创建/删除文件；正常盘上原有文件不会被主动修改。但扩容盘仍可能损坏原有数据。");
            else
                sb.AppendLine("  2. 检测会覆盖整块磁盘（含分区表），原有数据无法恢复；检测后请重新格式化U盘。");
            sb.AppendLine("  3. 快速扫描为抽样检测，存在漏检可能；完整校验逐块写入并读回比对，结论更可靠。");
            sb.AppendLine("  4. 检测结果仅用于判断容量是否虚标，不包括闪存寿命、读写性能等质量检测。");
            sb.AppendLine();
            sb.AppendLine("================================================================");
            sb.AppendLine("报告结束");
            sb.AppendLine("================================================================");

            return sb.ToString();
        }
    }
}





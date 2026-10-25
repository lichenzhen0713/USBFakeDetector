using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace UsbCheck
{
    internal sealed class DetectionOptions
    {
        public bool Full;
        public int SampleCount = 160;
        public int ChunkSize = 4 * 1024 * 1024;
        public int WitnessBytes = 8 * 1024 * 1024;
        public long MaxBytes;
        public string ModeName;
        public bool PreScan = true;
    }

    internal enum DetectionVerdict
    {
        Unknown,
        Genuine,
        Fake,
        Damaged,
        Cancelled,
        Failed
    }

    internal sealed class ProgressInfo
    {
        public string Phase;
        public int Percent;
        public string Detail;
        public long Done;
        public long Total;
        public double Speed;
        public string Extra;
    }

    internal sealed class WrapHit
    {
        public readonly long S;
        public readonly long W;

        public WrapHit(long s, long w)
        {
            S = s;
            W = w;
        }
    }

    internal sealed class DetectionResult
    {
        public DetectionVerdict Verdict = DetectionVerdict.Unknown;
        public string Mode = "";
        public long ClaimedBytes;
        public long TestedBytes;
        public long? EstimatedCapacity;
        public long FirstBadOffset = -1;
        public long BytesWritten;
        public long BytesRead;
        public int SamplesTotal;
        public int SamplesOk;
        public string Error;
        public DateTime Started;
        public DateTime Finished;
        public readonly List<string> Evidence = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public TimeSpan Elapsed
        {
            get
            {
                if (Finished <= Started) return TimeSpan.Zero;
                return Finished - Started;
            }
        }

        public string VerdictText
        {
            get
            {
                switch (Verdict)
                {
                    case DetectionVerdict.Genuine: return "正常：未发现扩容迹象";
                    case DetectionVerdict.Fake: return "疑似扩容盘";
                    case DetectionVerdict.Damaged: return "设备异常：数据无法正确读回";
                    case DetectionVerdict.Cancelled: return "已取消";
                    case DetectionVerdict.Failed: return "检测失败";
                    default: return "未知";
                }
            }
        }
    }

    internal static class Detector
    {
        public static DetectionResult Run(
            Func<bool, IBlockDevice> open,
            long claimedBytes,
            DetectionOptions options,
            Action<string> log,
            IProgress<ProgressInfo> progress,
            CancellationToken token)
        {
            if (log == null) log = delegate(string s) { };
            DetectionResult result = new DetectionResult();
            result.ClaimedBytes = claimedBytes;
            result.Started = DateTime.Now;
            result.Mode = string.IsNullOrEmpty(options.ModeName)
                ? (options.Full ? "完整校验" : "快速扫描")
                : options.ModeName;

            try
            {
                if (options.Full)
                    RunFull(open, result, options, log, progress, token);
                else
                    RunQuick(open, result, options, log, progress, token);
            }
            catch (OperationCanceledException)
            {
                result.Verdict = DetectionVerdict.Cancelled;
                result.Warnings.Add("检测已被用户取消，U盘上可能残留部分测试数据。");
                log("检测已取消。");
            }
            catch (Exception ex)
            {
                result.Verdict = DetectionVerdict.Failed;
                result.Error = ex.Message;
                log("检测失败：" + ex.Message);
            }

            result.Finished = DateTime.Now;
            return result;
        }

        // ================================ 快速扫描 ================================

        private static void RunQuick(
            Func<bool, IBlockDevice> open,
            DetectionResult r,
            DetectionOptions opt,
            Action<string> log,
            IProgress<ProgressInfo> progress,
            CancellationToken ct)
        {
            byte[] runTag = Pattern.NewRunTag();
            long length;

            using (IBlockDevice device = open(false))
            {
                length = Util.AlignDown(device.Length, Pattern.BlockSize);
            }

            if (length < Pattern.BlockSize * 16L)
                throw new IOException("设备容量过小（小于 64 KB），无法进行检测。");

            r.TestedBytes = length;
            if (r.ClaimedBytes <= 0) r.ClaimedBytes = length;

            int wanted = opt.SampleCount;
            if (wanted < 24) wanted = 24;
            if (wanted > 512) wanted = 512;

            long[] offsets = BuildSampleOffsets(length, wanted);
            r.SamplesTotal = offsets.Length;

            log("设备报告容量：" + Fmt.Size(length) + "（" + Fmt.SizeGiB(length) + "），采样点 " + offsets.Length.ToString() + " 个。");
            log("步骤 1/3：写入采样标记 ...");

            long totalBytes = (long)offsets.Length * Pattern.BlockSize;
            long doneBytes = 0;
            bool[] wrote = new bool[offsets.Length];
            int writeErrors = 0;
            Stopwatch watch = Stopwatch.StartNew();

            using (IBlockDevice device = open(true))
            {
                byte[] buffer = new byte[Pattern.BlockSize];
                for (int i = 0; i < offsets.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        Pattern.BuildTag(buffer, runTag, offsets[i], i);
                        device.Write(offsets[i], buffer, Pattern.BlockSize);
                        wrote[i] = true;
                    }
                    catch (Exception ex)
                    {
                        writeErrors++;
                        log("警告：写入采样点 " + Fmt.Size(offsets[i]) + " 失败：" + ex.Message);
                    }
                    doneBytes += Pattern.BlockSize;
                    r.BytesWritten = doneBytes;
                    if ((i & 7) == 0 || i == offsets.Length - 1)
                        Report(progress, "写入采样标记", 3 + 32.0 * (i + 1) / offsets.Length, null, doneBytes, totalBytes, watch);
                }
                try
                {
                    device.Flush();
                }
                catch (Exception ex)
                {
                    log("警告：刷新缓存失败：" + ex.Message);
                }
            }

            log("步骤 2/3：读回并校验 ...");

            List<WrapHit> wrapHits = new List<WrapHit>();
            List<long> badOffsets = new List<long>();
            int okCount = 0;
            long readDone = 0;

            using (IBlockDevice device = open(false))
            {
                byte[] buffer = new byte[Pattern.BlockSize];
                for (int i = 0; i < offsets.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    long sampleOffset = offsets[i];

                    if (!wrote[i])
                    {
                        badOffsets.Add(sampleOffset);
                    }
                    else
                    {
                        try
                        {
                            device.Read(sampleOffset, buffer, Pattern.BlockSize);

                            long foundOffset;
                            int foundIndex;
                            bool valid = Pattern.TryParseTag(buffer, runTag, out foundOffset, out foundIndex);
                            if (valid && foundOffset == sampleOffset && foundIndex == i)
                            {
                                okCount++;
                            }
                            else
                            {
                                // 偶发的读错误不应被当成扩容证据，重读一次确认
                                device.Read(sampleOffset, buffer, Pattern.BlockSize);
                                valid = Pattern.TryParseTag(buffer, runTag, out foundOffset, out foundIndex);
                                if (valid && foundOffset == sampleOffset && foundIndex == i)
                                {
                                    okCount++;
                                }
                                else if (valid && foundOffset != sampleOffset)
                                {
                                    wrapHits.Add(new WrapHit(sampleOffset, foundOffset));
                                }
                                else
                                {
                                    badOffsets.Add(sampleOffset);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            badOffsets.Add(sampleOffset);
                            log("警告：读取采样点 " + Fmt.Size(sampleOffset) + " 失败：" + ex.Message);
                        }
                    }

                    readDone += Pattern.BlockSize;
                    r.BytesRead = readDone;
                    if ((i & 7) == 0 || i == offsets.Length - 1)
                        Report(progress, "校验采样标记", 36 + 30.0 * (i + 1) / offsets.Length, null, readDone, totalBytes, watch);
                }
            }

            r.SamplesOk = okCount;
            log("采样校验完成：正常 " + okCount.ToString() + " / " + offsets.Length.ToString() + " 个点。");
            if (writeErrors > 0)
                r.Warnings.Add("有 " + writeErrors.ToString() + " 个采样点写入失败，可能是坏块、写保护或实际容量不足。");

            if (okCount == 0 && writeErrors == offsets.Length)
            {
                r.Verdict = DetectionVerdict.Damaged;
                r.FirstBadOffset = offsets[0];
                r.Evidence.Add("全部 " + offsets.Length.ToString() + " 个采样点都无法写入，设备可能处于写保护状态或存在严重故障。");
                Report(progress, "检测完成", 100, null, totalBytes, totalBytes, null);
                return;
            }

            if (wrapHits.Count > 0)
            {
                r.Verdict = DetectionVerdict.Fake;
                r.EstimatedCapacity = EstimateCapacity(wrapHits, length);
                AddWrapEvidence(r, wrapHits);
            }
            else if (badOffsets.Count > 0)
            {
                badOffsets.Sort();
                r.FirstBadOffset = badOffsets[0];

                long lastGood = 0;
                for (int i = 0; i < offsets.Length; i++)
                    if (offsets[i] < r.FirstBadOffset) lastGood = offsets[i];

                log("正在二分定位实际容量（约 " + Fmt.Size(lastGood) + " 至 " + Fmt.Size(r.FirstBadOffset) + "）...");
                long boundary = RefineCapacity(open, lastGood, r.FirstBadOffset, runTag, log, ct);
                r.EstimatedCapacity = boundary;

                bool looksFake = boundary < length - length / 20;
                r.Verdict = looksFake ? DetectionVerdict.Fake : DetectionVerdict.Damaged;
                r.Evidence.Add(string.Format(
                    "在偏移 {0} 处数据可以正确写入并读回，在偏移 {1} 处无法读回；二分定位后，实际可写入容量约为 {2}。",
                    Fmt.Size(lastGood), Fmt.Size(r.FirstBadOffset), Fmt.Size(boundary)));
                if (looksFake)
                    r.Warnings.Add("实际容量远小于标称容量（" + Fmt.Size(length) + "），属于典型的扩容盘。");
                else
                    r.Warnings.Add("采样数据异常可能由扩容盘或闪存坏块导致；建议使用“完整校验”进一步确认。");
            }

            // 别名探测：向若干“常见真实容量”位置写入数据，检查偏移 0 是否被回绕覆盖。
            if (wrapHits.Count == 0 && okCount > 0)
            {
                log("步骤 3/3：别名探测（检查写入高地址的数据是否会回绕覆盖低地址）...");
                long? alias = ProbeAliases(open, length, r, log, progress, ct);
                if (alias.HasValue)
                {
                    r.Verdict = DetectionVerdict.Fake;
                    r.EstimatedCapacity = alias;
                    r.Evidence.Add(string.Format(
                        "别名验证：向偏移 {0} 写入数据后，偏移 0 处的内容立即变成了这份数据，证明设备在 {0} 附近发生了地址回绕。",
                        Fmt.Size(alias.Value)));
                    r.Evidence.Add("据此推算，真实容量约为 " + Fmt.Size(alias.Value) + "（或更小）。");
                }
            }

            if (r.Verdict == DetectionVerdict.Unknown)
            {
                r.Verdict = DetectionVerdict.Genuine;
                r.Evidence.Add("全部采样点写入的数据均能在原位置正确读回，别名探测也未发现地址回绕。");
                r.Warnings.Add("快速扫描属于抽样检查，不能保证 100% 识别所有扩容盘（尤其是真实容量接近标称值的假盘）。重要数据请使用“完整校验”。");
            }

            Report(progress, "检测完成", 100, null, totalBytes, totalBytes, null);
        }

        private static long[] BuildSampleOffsets(long length, int count)
        {
            List<long> list = new List<long>(count + 2);
            long span = length - Pattern.BlockSize;
            for (int i = 0; i < count; i++)
            {
                long offset = (long)((double)span * i / (count - 1));
                offset = Util.AlignDown(offset, Pattern.BlockSize);
                if (list.Count == 0 || offset > list[list.Count - 1]) list.Add(offset);
            }
            long last = Util.AlignDown(span, Pattern.BlockSize);
            if (list.Count == 0 || list[list.Count - 1] != last) list.Add(last);
            return list.ToArray();
        }

        private static long? ProbeAliases(
            Func<bool, IBlockDevice> open,
            long length,
            DetectionResult r,
            Action<string> log,
            IProgress<ProgressInfo> progress,
            CancellationToken ct)
        {
            long[] candidates = BuildAliasCandidates(length);
            if (candidates.Length == 0) return null;

            byte[] runTag = Pattern.NewRunTag();
            using (IBlockDevice device = open(true))
            {
                byte[] buffer = new byte[Pattern.BlockSize];
                Pattern.BuildTag(buffer, runTag, 0, -1000);
                device.Write(0, buffer, Pattern.BlockSize);
                device.Flush();

                for (int i = 0; i < candidates.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    long candidate = candidates[i];

                    try
                    {
                        Pattern.BuildTag(buffer, runTag, candidate, i);
                        device.Write(candidate, buffer, Pattern.BlockSize);
                        device.Flush();

                        device.Read(0, buffer, Pattern.BlockSize);
                        long foundOffset;
                        int foundIndex;
                        if (Pattern.TryParseTag(buffer, runTag, out foundOffset, out foundIndex) && foundOffset == candidate)
                        {
                            log("别名探测命中：偏移 0 处出现了原本写入 " + Fmt.Size(candidate) + " 的数据。");
                            long refined = RefineAlias(device, runTag, candidate, length, log, ct);
                            Report(progress, "别名探测", 100, null, i + 1, candidates.Length, null);
                            return refined;
                        }
                    }
                    catch (Exception ex)
                    {
                        log("警告：别名探测在 " + Fmt.Size(candidate) + " 处失败：" + ex.Message);
                    }

                    if ((i & 3) == 0)
                        Report(progress, "别名探测", 72 + 27.0 * i / candidates.Length, null, i + 1, candidates.Length, null);
                }
            }
            return null;
        }

        private static long RefineAlias(IBlockDevice device, byte[] runTag, long capacity, long length, Action<string> log, CancellationToken ct)
        {
            long best = capacity;
            int[] primes = new int[] { 2, 3, 5, 7, 11 };

            for (int round = 0; round < 8; round++)
            {
                bool shrunk = false;
                foreach (int prime in primes)
                {
                    ct.ThrowIfCancellationRequested();
                    if ((best % prime) != 0) continue;
                    long candidate = best / prime;
                    if (candidate < Pattern.BlockSize * 16L) continue;
                    if ((candidate % Pattern.BlockSize) != 0) continue;
                    if (candidate >= length - Pattern.BlockSize) continue;

                    if (AliasesAt(device, runTag, candidate))
                    {
                        log("进一步验证：偏移 " + Fmt.Size(candidate) + " 同样会回绕到起点，容量修正为 " + Fmt.Size(candidate) + "。");
                        best = candidate;
                        shrunk = true;
                        break;
                    }
                }
                if (!shrunk) break;
            }
            return best;
        }

        private static bool AliasesAt(IBlockDevice device, byte[] runTag, long offset)
        {
            byte[] buffer = new byte[Pattern.BlockSize];
            Pattern.BuildTag(buffer, runTag, 0, -2000);
            device.Write(0, buffer, Pattern.BlockSize);
            device.Flush();

            Pattern.BuildTag(buffer, runTag, offset, -2001);
            device.Write(offset, buffer, Pattern.BlockSize);
            device.Flush();

            device.Read(0, buffer, Pattern.BlockSize);
            long foundOffset;
            int foundIndex;
            return Pattern.TryParseTag(buffer, runTag, out foundOffset, out foundIndex) && foundOffset == offset;
        }

        private static long[] BuildAliasCandidates(long length)
        {
            SortedSet<long> set = new SortedSet<long>();
            long minimum = Math.Max(Pattern.BlockSize * 4L, length / 1024);

            for (int i = 1; i <= 15; i++)
                AddAliasCandidate(set, length / 16 * i, length, minimum);

            long[] sizes = new long[] { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512 };
            foreach (long g in sizes)
            {
                AddAliasCandidate(set, g * 1000000000L, length, minimum);
                AddAliasCandidate(set, g * 1073741824L, length, minimum);
            }

            return new List<long>(set).ToArray();
        }

        private static void AddAliasCandidate(SortedSet<long> set, long candidate, long length, long minimum)
        {
            if (candidate < minimum) return;
            if (candidate >= length - Pattern.BlockSize) return;
            if ((candidate % Pattern.BlockSize) != 0) return;
            set.Add(candidate);
        }

        // ================================ 完整校验 ================================

        private static void RunFull(
            Func<bool, IBlockDevice> open,
            DetectionResult r,
            DetectionOptions opt,
            Action<string> log,
            IProgress<ProgressInfo> progress,
            CancellationToken ct)
        {
            byte[] runTag = Pattern.NewRunTag();
            long length;

            using (IBlockDevice device = open(false))
            {
                length = Util.AlignDown(device.Length, Pattern.BlockSize);
            }

            if (length < 8L * 1024 * 1024)
                throw new IOException("设备容量过小（小于 8 MB），无法进行完整校验。");

            long testEnd = length;
            if (opt.MaxBytes > 0)
            {
                long cap = Util.AlignDown(opt.MaxBytes, Pattern.BlockSize);
                if (cap < 16L * 1024 * 1024) cap = 16L * 1024 * 1024;
                if (cap < testEnd) testEnd = cap;
            }
            r.TestedBytes = testEnd;
            if (r.ClaimedBytes <= 0) r.ClaimedBytes = length;

            if (opt.PreScan && PreScanForFake(open, testEnd, r, log, ct))
                return;

            int chunkBytes = (int)Util.AlignDown(Math.Max(64 * 1024, opt.ChunkSize), Pattern.BlockSize);
            if (chunkBytes <= 0) chunkBytes = 4 * 1024 * 1024;
            if (chunkBytes > 16 * 1024 * 1024) chunkBytes = 16 * 1024 * 1024;

            long witnessLength = Math.Min(opt.WitnessBytes, testEnd);
            witnessLength = Util.AlignDown(witnessLength, Pattern.BlockSize);
            if (witnessLength < Pattern.BlockSize * 16L)
                witnessLength = Math.Min(testEnd, Pattern.BlockSize * 16L);

            long checkEvery = Math.Max(64L << 20, Math.Min(1L << 30, testEnd / 64));
            if (checkEvery > testEnd) checkEvery = testEnd;

            byte[] witnessExpected = new byte[(int)witnessLength];
            byte[] witnessActual = new byte[(int)witnessLength];
            Pattern.FillStream(witnessExpected, 0, 0, (int)witnessLength, runTag);

            log("测试范围：" + Fmt.Size(testEnd) + "（" + Fmt.SizeGiB(testEnd) + "），数据块 4 KB。");
            log("步骤 1/2：写入测试数据并监控起始区域 ...");

            Stopwatch watch = Stopwatch.StartNew();
            long written = 0;
            long failedAt = -1;
            string failure = null;
            long dropAt = -1;
            bool diskFull = false;
            bool wrapFound = false;
            List<WrapHit> wrapHits = new List<WrapHit>();
            long nextCheck = witnessLength + checkEvery;
            int corruptSeen = 0;

            using (IBlockDevice device = open(true))
            {
                byte[] chunk = new byte[chunkBytes];
                byte[] probe = new byte[Pattern.BlockSize];
                for (long offset = 0; offset < testEnd; offset += chunkBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(chunkBytes, testEnd - offset);
                    Pattern.FillStream(chunk, 0, offset, count, runTag);

                    try
                    {
                        device.Write(offset, chunk, count);
                    }
                    catch (Exception ex)
                    {
                        failedAt = offset;
                        failure = ex.Message;
                        if (ex is DiskFullException) diskFull = true;
                        break;
                    }

                    written = offset + count;
                    r.BytesWritten = written;
                    Report(progress, "写入测试数据", 2 + 56.0 * written / testEnd, null, written, testEnd, watch);

                    // 写入后立即校验本块末端：可发现“超出真实容量后写入被静默丢弃”的扩容盘，
                    // 从而在真实容量附近提前终止，而不是写满整个标称容量。
                    long dropped = DetectDroppedWrite(device, chunk, probe, offset, count);
                    if (dropped >= 0)
                    {
                        dropAt = dropped;
                        log("写入到 " + Fmt.Size(dropped) + " 后，刚写入的数据无法读回，提前终止。");
                        break;
                    }

                    if (written >= nextCheck)
                    {
                        nextCheck = written + checkEvery;
                        List<WrapHit> hits;
                        int corrupt;
                        if (CheckWitness(device, witnessExpected, witnessActual, runTag, witnessLength, out hits, out corrupt))
                        {
                            wrapFound = true;
                            wrapHits = hits;
                            log("发现地址回绕：写入到 " + Fmt.Size(written) + " 时，起始区域被覆盖。");
                            break;
                        }
                        if (corrupt > 0)
                        {
                            corruptSeen = Math.Max(corruptSeen, corrupt);
                            log("警告：起始区域有 " + corrupt.ToString() + " 个数据块读回不一致（可能是坏块或写入未真正落盘）。");
                        }
                    }
                }

                if (!wrapFound && failedAt < 0)
                {
                    device.Flush();
                    List<WrapHit> hits;
                    int corrupt;
                    if (CheckWitness(device, witnessExpected, witnessActual, runTag, witnessLength, out hits, out corrupt))
                    {
                        wrapFound = true;
                        wrapHits = hits;
                        log("发现地址回绕：写入完成后，起始区域被覆盖。");
                    }
                    else if (corrupt > 0)
                    {
                        corruptSeen = Math.Max(corruptSeen, corrupt);
                        log("警告：起始区域有 " + corrupt.ToString() + " 个数据块读回不一致（可能是坏块或写入未真正落盘）。");
                    }
                }
            }

            if (dropAt >= 0)
            {
                long good = Math.Max(0, dropAt - Pattern.BlockSize);
                r.FirstBadOffset = dropAt;
                r.EstimatedCapacity = good;
                bool looksFake = dropAt < testEnd - testEnd / 20;
                r.Verdict = looksFake ? DetectionVerdict.Fake : DetectionVerdict.Damaged;
                r.Evidence.Add("写入到 " + Fmt.Size(dropAt) + " 后，刚写入的数据无法正确读回；设备实际只能保存约 " +
                    Fmt.Size(good) + " 的数据。");
                if (looksFake)
                    r.Warnings.Add("实际容量远小于标称容量（" + Fmt.Size(testEnd) + "），属于典型的扩容盘。");
                else
                    r.Warnings.Add("末端数据无法保留，可能是扩容盘或闪存故障。");
                if (written < testEnd)
                    r.Warnings.Add("检测在写入 " + Fmt.Size(written) + " 后提前终止，没有写满标称容量。");
                log("数据无法保留，提前结束检测。");
                return;
            }

            if (wrapFound)
            {
                using (IBlockDevice probeDevice = open(false))
                {
                    ProbeHits(probeDevice, written, runTag, 64, wrapHits);
                }
                r.Verdict = DetectionVerdict.Fake;
                r.EstimatedCapacity = EstimateCapacity(wrapHits, testEnd);
                AddWrapEvidence(r, wrapHits);
                if (written < testEnd)
                    r.Warnings.Add("已确认地址回绕，检测在写入 " + Fmt.Size(written) + " 后提前结束。");
                log("已确认地址回绕，提前结束检测。");
                return;
            }

            if (failedAt >= 0)
            {
                if (diskFull)
                {
                    r.Verdict = DetectionVerdict.Failed;
                    r.Error = "可用空间不足，无法写满测试范围：" + failure;
                    r.Warnings.Add("请清理U盘空间后重试，或改用“整盘模式”（会清空磁盘）。");
                    log("可用空间不足，停止检测。");
                    return;
                }

                r.Verdict = DetectionVerdict.Damaged;
                r.FirstBadOffset = failedAt;
                if (failedAt > 0) r.EstimatedCapacity = failedAt;
                r.Evidence.Add("在第 " + Fmt.Size(failedAt) + " 字节处写入失败：" + failure);
                r.Warnings.Add("写入失败可能是扩容盘（真实容量不足）导致，也可能是闪存或主控故障。");
                log("写入失败，停止检测。");
                return;
            }

            if (corruptSeen > 0)
                r.Warnings.Add("写入阶段曾检测到起始区域数据不一致，可能是坏块。");

            log("写入完成，共 " + Fmt.Size(written) + "。步骤 2/2：读回并逐块比对 ...");

            long verified = 0;
            long firstBad = -1;
            List<WrapHit> readHits = new List<WrapHit>();

            using (IBlockDevice device = open(false))
            {
                byte[] chunk = new byte[chunkBytes];
                byte[] expected = new byte[chunkBytes];
                Stopwatch readWatch = Stopwatch.StartNew();

                for (long offset = 0; offset < testEnd; offset += chunkBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(chunkBytes, testEnd - offset);
                    device.Read(offset, chunk, count);
                    Pattern.FillStream(expected, 0, offset, count, runTag);

                    int badBlock = FindBadBlock(expected, chunk, count);
                    if (badBlock >= 0)
                    {
                        log("在偏移 " + Fmt.Size(offset + badBlock) + " 处读回不一致，重读一次确认 ...");
                        device.Read(offset, chunk, count);
                        badBlock = FindBadBlock(expected, chunk, count);
                    }

                    if (badBlock >= 0)
                    {
                        firstBad = offset + badBlock;
                        for (int pos = badBlock; pos < count; pos += Pattern.BlockSize)
                        {
                            long foundOffset;
                            if (Pattern.TryParseStreamHeader(chunk, pos, runTag, out foundOffset))
                            {
                                long sampleOffset = offset + pos;
                                if (foundOffset != sampleOffset)
                                    readHits.Add(new WrapHit(sampleOffset, foundOffset));
                            }
                        }
                        break;
                    }

                    verified = offset + count;
                    r.BytesRead = verified;
                    Report(progress, "读回校验", 58 + 41.0 * verified / testEnd, null, verified, testEnd, readWatch);
                }

                if (firstBad < 0)
                {
                    r.Verdict = DetectionVerdict.Genuine;
                    r.BytesRead = testEnd;
                    r.Evidence.Add("共 " + Fmt.Size(testEnd) + " 数据写入后，全部能够逐字节正确读回。");
                    Report(progress, "检测完成", 100, null, testEnd, testEnd, null);
                    return;
                }

                log("在偏移 " + Fmt.Size(firstBad) + " 处发现数据不一致，收集更多证据 ...");
                ProbeHits(device, testEnd, runTag, 96, readHits);
            }

            if (readHits.Count > 0)
            {
                r.Verdict = DetectionVerdict.Fake;
                r.EstimatedCapacity = EstimateCapacity(readHits, testEnd);
                AddWrapEvidence(r, readHits);
            }
            else
            {
                r.Verdict = DetectionVerdict.Damaged;
                r.FirstBadOffset = firstBad;
                r.EstimatedCapacity = firstBad;
                r.Evidence.Add("从偏移 " + Fmt.Size(firstBad) + " 开始，读回的数据与写入的数据不一致，说明该位置之后的数据实际并未保存。");
                r.Warnings.Add("数据在某一位置后全部/部分失效，可能是扩容盘，也可能是闪存坏块；建议更换U盘并复测。");
            }

            Report(progress, "检测完成", 100, null, testEnd, testEnd, null);
        }

        private static bool CheckWitness(
            IBlockDevice device,
            byte[] expected,
            byte[] actual,
            byte[] runTag,
            long witnessLength,
            out List<WrapHit> hits,
            out int corrupt)
        {
            device.Read(0, actual, (int)witnessLength);
            hits = new List<WrapHit>();
            corrupt = 0;

            for (int pos = 0; pos < witnessLength; pos += Pattern.BlockSize)
            {
                long foundOffset;
                if (Pattern.TryParseStreamHeader(actual, pos, runTag, out foundOffset))
                {
                    if (foundOffset == pos)
                    {
                        if (!Pattern.BlockEquals(expected, pos, actual, pos)) corrupt++;
                    }
                    else
                    {
                        hits.Add(new WrapHit(pos, foundOffset));
                    }
                }
                else
                {
                    corrupt++;
                }
            }

            return hits.Count > 0;
        }

        private static int FindBadBlock(byte[] expected, byte[] actual, int count)
        {
            for (int pos = 0; pos < count; pos += Pattern.BlockSize)
            {
                if (!Pattern.BlockEquals(expected, pos, actual, pos)) return pos;
            }
            return -1;
        }

        /// <summary>
        /// 完整校验开始前的快速抽查：64 个采样点即可判定大多数扩容盘，
        /// 避免在假盘上写满标称容量（268 GB 的假盘只需几秒）。
        /// 返回 true 表示已得出结论，无需继续完整写入。
        /// </summary>
        private static bool PreScanForFake(
            Func<bool, IBlockDevice> open,
            long length,
            DetectionResult r,
            Action<string> log,
            CancellationToken ct)
        {
            int sampleCount = 64;
            long[] offsets = BuildSampleOffsets(length, sampleCount);
            byte[] runTag = Pattern.NewRunTag();
            bool[] wrote = new bool[offsets.Length];
            int writeErrors = 0;

            log("快速预扫描：抽查 " + offsets.Length.ToString() + " 个位置，判断是否存在明显扩容 ...");

            using (IBlockDevice device = open(true))
            {
                byte[] tag = new byte[Pattern.BlockSize];
                for (int i = 0; i < offsets.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        Pattern.BuildTag(tag, runTag, offsets[i], i);
                        device.Write(offsets[i], tag, Pattern.BlockSize);
                        wrote[i] = true;
                        r.BytesWritten += Pattern.BlockSize;
                    }
                    catch (Exception ex)
                    {
                        writeErrors++;
                        log("预扫描写入失败（" + Fmt.Size(offsets[i]) + "）：" + ex.Message);
                    }
                }

                try
                {
                    device.Flush();
                }
                catch
                {
                }
            }

            if (writeErrors == offsets.Length)
            {
                log("预扫描所有点都无法写入，交由完整校验报告错误。");
                return false;
            }

            List<WrapHit> wraps = new List<WrapHit>();
            List<long> bad = new List<long>();
            int ok = 0;

            using (IBlockDevice device = open(false))
            {
                byte[] read = new byte[Pattern.BlockSize];
                for (int i = 0; i < offsets.Length; i++)
                {
                    if (!wrote[i])
                    {
                        bad.Add(offsets[i]);
                        continue;
                    }

                    try
                    {
                        device.Read(offsets[i], read, Pattern.BlockSize);
                    }
                    catch
                    {
                        bad.Add(offsets[i]);
                        continue;
                    }

                    long foundOffset;
                    int foundIndex;
                    if (Pattern.TryParseTag(read, runTag, out foundOffset, out foundIndex))
                    {
                        if (foundOffset == offsets[i] && foundIndex == i)
                        {
                            ok++;
                            r.BytesRead += Pattern.BlockSize;
                        }
                        else if (foundOffset != offsets[i]) wraps.Add(new WrapHit(offsets[i], foundOffset));
                        else bad.Add(offsets[i]);
                    }
                    else
                    {
                        bad.Add(offsets[i]);
                    }
                }
            }

            if (wraps.Count > 0)
            {
                r.Verdict = DetectionVerdict.Fake;
                r.EstimatedCapacity = EstimateCapacity(wraps, length);
                AddWrapEvidence(r, wraps);
                r.Warnings.Add("快速预扫描发现地址回绕，已提前结束，未写满标称容量。");
                log("预扫描发现地址回绕，判定为扩容盘。");
                return true;
            }

            int threshold = Math.Max(8, offsets.Length / 8);
            if (bad.Count >= threshold)
            {
                bad.Sort();
                long firstBad = bad[0];
                long lastGood = 0;
                for (int i = 0; i < offsets.Length; i++)
                {
                    if (offsets[i] < firstBad) lastGood = offsets[i];
                }

                log("预扫描发现 " + bad.Count.ToString() + " 个异常点，正在二分定位实际容量 ...");
                long boundary = RefineCapacity(open, lastGood, firstBad, runTag, log, ct);

                r.FirstBadOffset = firstBad;
                r.EstimatedCapacity = boundary;
                r.Verdict = boundary < length - length / 10
                    ? DetectionVerdict.Fake
                    : DetectionVerdict.Damaged;
                r.Evidence.Add("预扫描在 " + Fmt.Size(firstBad) + " 处发现数据无法读回；二分定位后，实际可写入容量约为 " +
                    Fmt.Size(boundary) + "。");
                if (r.Verdict == DetectionVerdict.Fake)
                    r.Warnings.Add("实际容量远小于标称容量（" + Fmt.Size(length) + "），属于典型的扩容盘；已提前结束，未写满标称容量。");
                else
                    r.Warnings.Add("末端区域存在异常，可能是扩容盘或闪存故障。");
                log("预扫描确认容量异常，提前结束。");
                return true;
            }

            if (bad.Count > 0)
                log("预扫描发现 " + bad.Count.ToString() + " 个异常点（未达判定阈值），继续完整校验。");

            if (bad.Count == 0)
            {
                // 采样全部正常时，再做一轮别名探测，识别“回绕型”扩容盘
                long? alias = ProbeAliases(open, length, r, log, null, ct);
                if (alias.HasValue)
                {
                    r.Verdict = DetectionVerdict.Fake;
                    r.EstimatedCapacity = alias;
                    r.Evidence.Add("快速预扫描的别名探测确认设备在 " + Fmt.Size(alias.Value) + " 附近发生了地址回绕。");
                    r.Warnings.Add("已提前结束，未写满标称容量。");
                    log("预扫描别名探测发现地址回绕，判定为扩容盘。");
                    return true;
                }
            }

            return false;
        }

        /// <summary>读取一个 4KB 块并与期望数据比较；偶发错误重试一次。</summary>
        private static bool BlockAt(IBlockDevice device, byte[] expected, byte[] probe, long deviceOffset, int expectedOffset)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    device.Read(deviceOffset, probe, Pattern.BlockSize);
                }
                catch
                {
                    if (attempt == 0)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    return false;
                }

                if (Pattern.BlockEquals(expected, expectedOffset, probe, 0)) return true;
                if (attempt == 0) Thread.Sleep(50);
            }
            return false;
        }

        /// <summary>
        /// 检查刚写入的数据块末端是否真的保留下来。返回第一个无法读回的偏移；全部正常返回 -1。
        /// 用于发现“超出真实容量后写入被静默丢弃”的扩容盘并提前终止。
        /// </summary>
        private static long DetectDroppedWrite(IBlockDevice device, byte[] expected, byte[] probe, long offset, int count)
        {
            long lastOffset = offset + count - Pattern.BlockSize;
            if (BlockAt(device, expected, probe, lastOffset, count - Pattern.BlockSize)) return -1;

            int low = 0;
            int high = count - Pattern.BlockSize;
            if (!BlockAt(device, expected, probe, offset, 0)) return offset;

            while (low + Pattern.BlockSize < high)
            {
                int middle = ((low + high) / 2 / Pattern.BlockSize) * Pattern.BlockSize;
                if (middle <= low) break;
                if (BlockAt(device, expected, probe, offset + middle, middle)) low = middle;
                else high = middle;
            }
            return offset + high;
        }

        /// <summary>在 [low, high] 区间二分写入测试标记，定位仍然可以正常保存数据的最大偏移。</summary>
        private static long RefineCapacity(
            Func<bool, IBlockDevice> open,
            long low,
            long high,
            byte[] runTag,
            Action<string> log,
            CancellationToken ct)
        {
            low = Util.AlignDown(low, Pattern.BlockSize);
            high = Util.AlignDown(high, Pattern.BlockSize);
            if (high - low < Pattern.BlockSize * 8L) return low;

            using (IBlockDevice device = open(true))
            {
                byte[] tag = new byte[Pattern.BlockSize];
                byte[] read = new byte[Pattern.BlockSize];

                for (int i = 0; i < 40 && high - low > Pattern.BlockSize * 8L; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    long middle = Util.AlignDown(low + (high - low) / 2, Pattern.BlockSize);
                    if (middle <= low || middle >= high) break;

                    bool ok = false;
                    try
                    {
                        Pattern.BuildTag(tag, runTag, middle, 7000 + i);
                        device.Write(middle, tag, Pattern.BlockSize);
                        device.Flush();
                        device.Read(middle, read, Pattern.BlockSize);
                        long found;
                        int index;
                        ok = Pattern.TryParseTag(read, runTag, out found, out index) && found == middle;
                    }
                    catch (Exception ex)
                    {
                        log("边界探测在 " + Fmt.Size(middle) + " 处失败：" + ex.Message);
                    }

                    if (ok) low = middle;
                    else high = middle;
                }
            }

            return low;
        }

        private static void ProbeHits(IBlockDevice device, long range, byte[] runTag, int count, List<WrapHit> hits)
        {
            if (range < Pattern.BlockSize * 8L) return;
            byte[] buffer = new byte[Pattern.BlockSize];
            long span = range - Pattern.BlockSize;

            for (int i = 0; i < count; i++)
            {
                long offset = Util.AlignDown((long)((double)span * i / (count - 1)), Pattern.BlockSize);
                try
                {
                    device.Read(offset, buffer, Pattern.BlockSize);
                }
                catch
                {
                    continue;
                }
                long foundOffset;
                if (Pattern.TryParseStreamHeader(buffer, 0, runTag, out foundOffset) && foundOffset != offset)
                    hits.Add(new WrapHit(offset, foundOffset));
            }
        }

        internal static long? EstimateCapacity(List<WrapHit> hits, long limit)
        {
            long gcd = 0;
            int used = 0;

            foreach (WrapHit hit in hits)
            {
                if (hit.W <= hit.S) continue;
                long distance = hit.W - hit.S;
                if (distance <= 0 || distance >= limit) continue;
                gcd = Util.Gcd(gcd, distance);
                used++;
            }

            if (used == 0 || gcd <= 0) return null;
            if (gcd >= limit) return null;
            gcd = Util.AlignDown(gcd, Pattern.BlockSize);
            if (gcd <= 0) return null;
            return gcd;
        }

        internal static void AddWrapEvidence(DetectionResult r, List<WrapHit> hits)
        {
            int shown = 0;
            int valid = 0;

            foreach (WrapHit hit in hits)
            {
                if (hit.W <= hit.S) continue;
                valid++;
                if (r.FirstBadOffset < 0 || hit.S < r.FirstBadOffset) r.FirstBadOffset = hit.S;
                if (shown < 3)
                {
                    r.Evidence.Add(string.Format(
                        "本应写入 {1} 的数据出现在了偏移 {0} 处（回绕差值 {2}）——设备把高地址的数据写到了低地址。",
                        Fmt.Size(hit.S), Fmt.Size(hit.W), Fmt.Size(hit.W - hit.S)));
                    shown++;
                }
            }

            if (valid > shown)
                r.Evidence.Add("共发现 " + valid.ToString() + " 处地址回绕证据。");

            if (r.EstimatedCapacity.HasValue)
                r.Evidence.Add("按回绕周期推算，真实可用容量约为 " + Fmt.Size(r.EstimatedCapacity.Value) +
                    "（" + Fmt.SizeGiB(r.EstimatedCapacity.Value) + "，估算值，可能略高于实际值）。");
        }

        private static void Report(
            IProgress<ProgressInfo> progress,
            string phase,
            double percent,
            string detail,
            long done,
            long total,
            Stopwatch watch)
        {
            if (progress == null) return;

            ProgressInfo info = new ProgressInfo();
            info.Phase = phase;
            info.Percent = (int)Math.Round(Math.Max(0, Math.Min(100, percent)));
            info.Detail = detail;
            info.Done = done;
            info.Total = total;

            if (watch != null && done > 0)
            {
                double seconds = watch.Elapsed.TotalSeconds;
                if (seconds > 0.15)
                {
                    info.Speed = done / seconds;
                    if (total > done)
                        info.Extra = "剩余约 " + Fmt.Duration((total - done) / info.Speed);
                }
            }

            try
            {
                progress.Report(info);
            }
            catch
            {
                // 进度回调异常不应影响检测
            }
        }
    }
}






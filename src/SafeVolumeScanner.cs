using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UsbCheck
{
    /// <summary>按字节访问卷的空闲簇，用于“保留原有文件”的安全检测。</summary>
    internal sealed class VolumeProbe : IDisposable
    {
        public string Letter;
        public long Length;
        public int ClusterSize;
        public int TotalClusters;
        public byte[] Bitmap;
        public int BitmapLcnOffset;
        public int MinDataLcn = 2;
        public bool ExFat;
        public long ClusterHeapOffset;

        private SafeFileHandle _handle;
        private AlignedBuffer _buffer;
        private readonly string _path;

        public VolumeProbe(string letter)
        {
            Letter = letter;
            _path = @"\\.\" + letter;
        }

        public void Open(Action<string> log)
        {
            _handle = Native.CreateFileW(
                _path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH,
                IntPtr.Zero);

            if (_handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                throw new DeviceException("无法打开卷 " + Letter + "：" +
                    new Win32Exception(code).Message + "（错误码 " + code.ToString() + "）");
            }

            uint sectorsPerCluster;
            uint bytesPerSector;
            uint freeClusters;
            uint totalClusters;
            string root = Letter + "\\";
            if (!Native.GetDiskFreeSpaceW(root, out sectorsPerCluster, out bytesPerSector, out freeClusters, out totalClusters))
            {
                throw new DeviceException("无法获取卷 " + Letter + " 的簇大小：" + Native.LastErrorText());
            }

            ClusterSize = (int)(sectorsPerCluster * bytesPerSector);
            if (ClusterSize < 4096 || (ClusterSize % 4096) != 0)
                throw new DeviceException("暂不支持簇大小 " + ClusterSize.ToString() + " 字节的卷。");

            Length = QueryLength();
            if (Length <= 0)
                Length = (long)totalClusters * ClusterSize;
            TotalClusters = (int)(Length / ClusterSize);
            if (TotalClusters <= 0)
                throw new DeviceException("卷容量异常，无法检测。");

            // NTFS/FAT：FSCTL 返回的是文件系统当前位图（准确），在锁定/卸载前读取即可。
            // exFAT：FSCTL 不支持，必须在卸载卷、元数据全部落盘后再解析，
            // 否则刚创建的文件可能还没写进位图，测试标记会误写到它的簇上。
            bool exfat = ExFatBitmap.IsExFat(this);

            if (!exfat)
            {
                string bitmapError;
                Bitmap = TryReadBitmap(out bitmapError);
                if (Bitmap == null)
                {
                    throw new DeviceException("读取卷空闲位图失败：" + bitmapError +
                        "。该文件系统可能不支持空闲位图。");
                }
                ValidateBitmap();

                Native.FlushFileBuffers(_handle);
                LockVolume(log);
                DismountVolume(log);
            }
            else
            {
                Native.FlushFileBuffers(_handle);
                LockVolume(log);
                DismountVolume(log);

                int exfatClusters;
                Bitmap = ExFatBitmap.Read(this, out exfatClusters);
                BitmapLcnOffset = 2;
                TotalClusters = exfatClusters + 2;
                if (log != null) log("已解析 exFAT 空闲位图（" + exfatClusters.ToString() + " 个数据簇）。");
                ValidateBitmap();
            }
        }

        public long FreeBytes
        {
            get
            {
                long free = 0;
                for (int i = 0; i < TotalClusters; i++)
                    if (!Allocated(i)) free++;
                return free * ClusterSize;
            }
        }

        /// <summary>簇号 → 卷内字节偏移（exFAT 的数据区从 ClusterHeapOffset 开始）。</summary>
        public long OffsetOfCluster(long cluster)
        {
            if (ExFat) return ClusterHeapOffset + (cluster - 2) * ClusterSize;
            return cluster * ClusterSize;
        }

        public long ClusterOfOffset(long offset)
        {
            if (ExFat) return (offset - ClusterHeapOffset) / ClusterSize + 2;
            return offset / ClusterSize;
        }

        public bool Allocated(int cluster)
        {
            if (cluster < MinDataLcn || cluster >= TotalClusters) return true;
            int bit = cluster - BitmapLcnOffset;
            int index = bit >> 3;
            if (index < 0 || index >= Bitmap.Length) return true;
            return (Bitmap[index] & (1 << (bit & 7))) != 0;
        }

        /// <summary>位图必须至少有一个已分配簇；否则说明解析错误，绝不能继续写入。</summary>
        private void ValidateBitmap()
        {
            long allocated = 0;
            for (int i = MinDataLcn; i < TotalClusters; i++)
                if (Allocated(i)) allocated++;

            if (allocated == 0)
                throw new DeviceException("空闲位图校验失败（没有发现任何已分配簇），已中止以避免破坏文件系统。");
        }

        private long QueryLength()
        {
            IntPtr buffer = Marshal.AllocHGlobal(16);
            try
            {
                int returned;
                if (Native.DeviceIoControl(_handle, Native.IOCTL_DISK_GET_LENGTH_INFO,
                        IntPtr.Zero, 0, buffer, 16, out returned, IntPtr.Zero) && returned >= 8)
                {
                    long length = Marshal.ReadInt64(buffer);
                    if (length > 0) return length;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return 0;
        }

        private byte[] TryReadBitmap(out string error)
        {
            error = null;
            int bitmapBytes = (TotalClusters + 7) / 8;
            byte[] bitmap = new byte[bitmapBytes];
            const int maxChunk = 1 << 20;

            IntPtr inBuffer = Marshal.AllocHGlobal(8);
            IntPtr outBuffer = Marshal.AllocHGlobal(16 + maxChunk);
            try
            {
                int lcn = 0;
                while (lcn < TotalClusters)
                {
                    int remaining = (TotalClusters - lcn + 7) / 8;
                    int request = Math.Min(maxChunk, remaining);
                    Marshal.WriteInt64(inBuffer, lcn);

                    int returned;
                    if (!Native.DeviceIoControl(_handle, 0x0009006Fu, inBuffer, 8, outBuffer, 16 + request,
                            out returned, IntPtr.Zero))
                    {
                        error = Native.LastErrorText();
                        return null;
                    }

                    int got = returned - 16;
                    if (got <= 0) break;
                    if (got > request) got = request;

                    Marshal.Copy(new IntPtr(outBuffer.ToInt64() + 16), bitmap, lcn / 8, got);
                    lcn += got * 8;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inBuffer);
                Marshal.FreeHGlobal(outBuffer);
            }

            return bitmap;
        }

        private void LockVolume(Action<string> log)
        {
            int returned;
            bool locked = Native.DeviceIoControl(_handle, Native.FSCTL_LOCK_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);

            if (!locked)
            {
                Thread.Sleep(300);
                locked = Native.DeviceIoControl(_handle, Native.FSCTL_LOCK_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
            }

            if (!locked)
            {
                throw new DeviceException("无法锁定卷 " + Letter +
                    "，请关闭占用该卷的程序后重试（右键U盘→弹出可强制释放）。");
            }
            if (log != null) log("卷 " + Letter + " 已锁定，开始读取空闲位图。");
        }

        private void DismountVolume(Action<string> log)
        {
            int returned;
            bool dismounted = Native.DeviceIoControl(_handle, Native.FSCTL_DISMOUNT_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);

            if (!dismounted)
            {
                Thread.Sleep(300);
                dismounted = Native.DeviceIoControl(_handle, Native.FSCTL_DISMOUNT_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
            }

            if (!dismounted)
            {
                throw new DeviceException("无法卸载卷 " + Letter + "，请关闭占用该卷的程序后重试。");
            }
        }

        private void EnsureBuffer(int count)
        {
            if (_buffer != null && _buffer.Size >= count) return;
            if (_buffer != null) _buffer.Dispose();
            _buffer = new AlignedBuffer(Math.Max(count, 4096));
        }

        private void Check(long offset, int count)
        {
            if (offset < 0 || (offset % 4096) != 0 || (count % 4096) != 0)
                throw new DeviceException("卷读写要求 4KB 对齐。");
            if (offset + count > Length)
                throw new DeviceException("卷读写超出容量。");
        }

        public void Read(long offset, byte[] buffer, int count)
        {
            Check(offset, count);
            EnsureBuffer(count);
            for (int attempt = 0; ; attempt++)
            {
                string error = null;
                if (Native.SetFilePointerEx(_handle, offset, IntPtr.Zero, Native.FILE_BEGIN))
                {
                    int got;
                    if (Native.ReadFile(_handle, _buffer.Pointer, count, out got, IntPtr.Zero) && got == count)
                    {
                        _buffer.CopyTo(buffer, count);
                        return;
                    }
                    error = Native.LastErrorText();
                }
                else
                {
                    error = Native.LastErrorText();
                }

                if (attempt >= 1) throw new DeviceException("读取卷失败（偏移 " + offset.ToString() + "）：" + error);
                Thread.Sleep(60);
            }
        }

        /// <summary>读取任意偏移的数据（内部按 4KB 对齐读取后截取）。</summary>
        public byte[] ReadAligned(long offset, int count)
        {
            long start = offset & ~4095L;
            int delta = (int)(offset - start);
            int total = (int)(((delta + count + 4095L) / 4096) * 4096);
            byte[] temp = new byte[total];
            Read(start, temp, total);
            byte[] result = new byte[count];
            Buffer.BlockCopy(temp, delta, result, 0, count);
            return result;
        }

        public void Write(long offset, byte[] buffer, int count)
        {
            Check(offset, count);
            EnsureBuffer(count);
            for (int attempt = 0; ; attempt++)
            {
                _buffer.CopyFrom(buffer, count);
                string error = null;
                if (Native.SetFilePointerEx(_handle, offset, IntPtr.Zero, Native.FILE_BEGIN))
                {
                    int written;
                    if (Native.WriteFile(_handle, _buffer.Pointer, count, out written, IntPtr.Zero) && written == count)
                        return;
                    error = Native.LastErrorText();
                }
                else
                {
                    error = Native.LastErrorText();
                }

                if (attempt >= 1) throw new DeviceException("写入卷失败（偏移 " + offset.ToString() + "）：" + error);
                Thread.Sleep(60);
            }
        }

        public void Flush()
        {
            // 卷被卸载后 FlushFileBuffers 可能返回“设备未就绪”；
            // 写入本身使用 WRITE_THROUGH，失败可忽略。
            Native.FlushFileBuffers(_handle);
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                _buffer.Dispose();
                _buffer = null;
            }
            if (_handle != null)
            {
                _handle.Dispose();
                _handle = null;
            }
        }
    }

    /// <summary>
    /// 安全模式：读取文件系统空闲簇位图，只向“标记为空闲”的簇写入测试标记。
    /// 不创建/删除文件，不修改文件系统元数据；正常盘上原有文件不受影响。
    /// </summary>
    internal static class SafeVolumeScanner
    {
        private sealed class FreeRun
        {
            public long Start;
            public int Length;
        }

        public static DetectionResult Scan(
            string volumeLetter,
            long claimedBytes,
            Action<string> log,
            IProgress<ProgressInfo> progress,
            CancellationToken token)
        {
            if (log == null) log = delegate(string s) { };
            DetectionResult r = new DetectionResult();
            r.Mode = "安全模式（保留文件）";
            r.ClaimedBytes = claimedBytes;
            r.Started = DateTime.Now;

            try
            {
                using (VolumeProbe volume = new VolumeProbe(volumeLetter))
                {
                    volume.Open(log);
                    r.TestedBytes = volume.Length;
                    if (r.ClaimedBytes <= 0) r.ClaimedBytes = volume.Length;

                    List<FreeRun> runs = BuildFreeRuns(volume);
                    if (runs.Count == 0)
                        throw new DeviceException("卷内没有可用的空闲空间，无法进行安全模式检测。");

                    log(string.Format("卷 {0}：容量 {1}，簇 {2} 字节，空闲 {3}，空闲区段 {4} 个。",
                        volumeLetter, Fmt.Size(volume.Length), volume.ClusterSize,
                        Fmt.Size(volume.FreeBytes), runs.Count));

                    Report(progress, "读取空闲位图", 8, null, 0, 1, null);

                    int sampleCount = 128;
                    List<long> sampleLcns = new List<long>();
                    HashSet<long> seen = new HashSet<long>();
                    for (int i = 0; i < sampleCount; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        long target = (long)i * (volume.TotalClusters - 1) / (sampleCount - 1);
                        long lcn;
                        if (!TryFindFree(volume, runs, target, out lcn)) continue;
                        if (seen.Add(lcn)) sampleLcns.Add(lcn);
                    }

                    if (sampleLcns.Count < 8)
                        throw new DeviceException("可用的空闲区太少，无法进行安全模式检测。");

                    byte[] runTag = Pattern.NewRunTag();
                    byte[] tag = new byte[Pattern.BlockSize];
                    byte[] read = new byte[Pattern.BlockSize];

                    List<WrapHit> wrapHits = new List<WrapHit>();
                    int okCount = 0;
                    int tested = 0;
                    long firstBad = -1;
                    long lastGood = 0;

                    log("步骤 1/2：从低到高写入并逐点校验采样标记（发现异常立即停止）...");
                    for (int i = 0; i < sampleLcns.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        long offset = volume.OffsetOfCluster(sampleLcns[i]);
                        tested++;
                        bool ok = false;

                        try
                        {
                            Pattern.BuildTag(tag, runTag, offset, i);
                            volume.Write(offset, tag, Pattern.BlockSize);
                            r.BytesWritten += Pattern.BlockSize;

                            volume.Read(offset, read, Pattern.BlockSize);
                            long foundOffset;
                            int foundIndex;
                            if (Pattern.TryParseTag(read, runTag, out foundOffset, out foundIndex))
                            {
                                if (foundOffset == offset && foundIndex == i) ok = true;
                                else if (foundOffset != offset) wrapHits.Add(new WrapHit(offset, foundOffset));
                            }

                            if (!ok && wrapHits.Count == 0)
                            {
                                volume.Read(offset, read, Pattern.BlockSize);
                                if (Pattern.TryParseTag(read, runTag, out foundOffset, out foundIndex) &&
                                    foundOffset == offset && foundIndex == i)
                                    ok = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            log("警告：采样点 " + Fmt.Size(offset) + " 读写失败：" + ex.Message);
                        }

                        if (ok)
                        {
                            okCount++;
                            lastGood = offset;
                            r.BytesRead += Pattern.BlockSize;
                        }
                        else
                        {
                            firstBad = offset;
                            break;
                        }

                        if ((i & 7) == 0 || i == sampleLcns.Count - 1)
                            Report(progress, "写入并校验采样标记", 10 + 55.0 * (i + 1) / sampleLcns.Count, null,
                                i + 1, sampleLcns.Count, null);
                    }

                    r.SamplesTotal = tested;
                    r.SamplesOk = okCount;
                    log("采样完成：正常 " + okCount.ToString() + " / " + tested.ToString() + " 个。" +
                        (firstBad >= 0 ? "在 " + Fmt.Size(firstBad) + " 处停止。" : ""));

                    if (wrapHits.Count > 0)
                    {
                        r.Verdict = DetectionVerdict.Fake;
                        r.EstimatedCapacity = Detector.EstimateCapacity(wrapHits, volume.Length);
                        Detector.AddWrapEvidence(r, wrapHits);
                        r.Warnings.Add("安全模式只写入空闲区；扩容盘仍可能损坏原有数据，建议先备份。");
                        return Finish(r);
                    }

                    if (firstBad >= 0)
                    {
                        log("步骤 3/3：二分定位实际容量（约 " + Fmt.Size(lastGood) + " 至 " + Fmt.Size(firstBad) + "）...");
                        long boundary = RefineBoundary(volume, runs, runTag, tag, read, lastGood, firstBad, log, token);

                        r.FirstBadOffset = firstBad;
                        r.EstimatedCapacity = boundary;
                        bool looksFake = boundary < volume.Length - volume.Length / 10;
                        r.Verdict = looksFake ? DetectionVerdict.Fake : DetectionVerdict.Damaged;
                        r.Evidence.Add("在偏移 " + Fmt.Size(lastGood) + " 处空闲区数据可以正确读回，在 " +
                            Fmt.Size(firstBad) + " 处无法读回；二分定位后实际可写入容量约为 " + Fmt.Size(boundary) + "。");
                        if (looksFake)
                            r.Warnings.Add("实际容量远小于标称容量（" + Fmt.Size(volume.Length) + "），属于典型的扩容盘。");
                        else
                            r.Warnings.Add("末端空闲区数据异常，可能是扩容盘或闪存故障。");
                        r.Warnings.Add("安全模式只写入空闲簇，未测试原有文件占用的区域。");
                        return Finish(r);
                    }

                    r.Verdict = DetectionVerdict.Genuine;
                    r.Evidence.Add("在卷的空闲区域抽查 " + tested.ToString() + " 个点，写入的数据均能正确读回。");
                    r.Warnings.Add("安全模式只写入文件系统标记为空闲的簇，属于抽样检查；回绕型扩容盘可能无法识别，建议用“快速扫描”复测。");
                    return Finish(r);
                }
            }
            catch (OperationCanceledException)
            {
                r.Verdict = DetectionVerdict.Cancelled;
                r.Warnings.Add("检测已被用户取消。");
                log("检测已取消。");
            }
            catch (Exception ex)
            {
                r.Verdict = DetectionVerdict.Failed;
                r.Error = ex.Message;
                log("安全模式检测失败：" + ex.Message);
            }

            return Finish(r);
        }

        private static DetectionResult Finish(DetectionResult r)
        {
            r.Finished = DateTime.Now;
            return r;
        }

        private static List<FreeRun> BuildFreeRuns(VolumeProbe volume)
        {
            List<FreeRun> runs = new List<FreeRun>();
            int i = 0;
            int total = volume.TotalClusters;
            while (i < total)
            {
                while (i < total && volume.Allocated(i)) i++;
                int start = i;
                while (i < total && !volume.Allocated(i)) i++;
                if (i > start) runs.Add(new FreeRun { Start = start, Length = i - start });
            }
            return runs;
        }

        private static bool TryFindFree(VolumeProbe volume, List<FreeRun> runs, long targetLcn, out long lcn)
        {
            lcn = -1;
            // 不碰卷的前 8 MB（引导区/FAT/根目录/MFT 等元数据），即使位图异常也不会写坏文件系统
            long floorLcn = volume.ExFat
                ? (long)volume.MinDataLcn
                : Math.Max((long)volume.MinDataLcn, (8L << 20) / volume.ClusterSize);
            if (targetLcn < floorLcn) targetLcn = floorLcn;
            if (targetLcn >= volume.TotalClusters) targetLcn = volume.TotalClusters - 1;

            int low = 0;
            int high = runs.Count - 1;
            int index = -1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                FreeRun run = runs[middle];
                if (run.Start + run.Length <= targetLcn) low = middle + 1;
                else { index = middle; high = middle - 1; }
            }

            if (index < 0) return false;
            FreeRun chosen = runs[index];
            long candidate = Math.Max(targetLcn, chosen.Start);
            if (candidate >= chosen.Start + chosen.Length) return false;
            lcn = candidate;
            return true;
        }

        private static long RefineBoundary(
            VolumeProbe volume,
            List<FreeRun> runs,
            byte[] runTag,
            byte[] tag,
            byte[] read,
            long lastGood,
            long firstBad,
            Action<string> log,
            CancellationToken token)
        {
            long low = lastGood;
            long high = firstBad;

            for (int step = 0; step < 40 && high - low > volume.ClusterSize * 2L; step++)
            {
                token.ThrowIfCancellationRequested();
                long middle = low + (high - low) / 2;
                long lcn;
                if (!TryFindFree(volume, runs, volume.ClusterOfOffset(middle), out lcn)) break;

                long offset = volume.OffsetOfCluster(lcn);
                if (offset <= low || offset >= high) break;

                bool ok = false;
                try
                {
                    Pattern.BuildTag(tag, runTag, offset, 9000 + step);
                    volume.Write(offset, tag, Pattern.BlockSize);
                    volume.Flush();
                    volume.Read(offset, read, Pattern.BlockSize);
                    long foundOffset;
                    int foundIndex;
                    ok = Pattern.TryParseTag(read, runTag, out foundOffset, out foundIndex) && foundOffset == offset;
                }
                catch (Exception ex)
                {
                    log("边界探测失败（" + Fmt.Size(offset) + "）：" + ex.Message);
                }

                if (ok) low = offset;
                else high = offset;
            }

            return low + Pattern.BlockSize;
        }

        private static void Report(
            IProgress<ProgressInfo> progress,
            string phase,
            double percent,
            string detail,
            long done,
            long total,
            double? speed)
        {
            if (progress == null) return;
            ProgressInfo info = new ProgressInfo();
            info.Phase = phase;
            info.Percent = (int)Math.Round(Math.Max(0, Math.Min(100, percent)));
            info.Detail = detail;
            info.Done = done;
            info.Total = total;
            info.Speed = speed ?? 0;
            try { progress.Report(info); } catch { }
        }
    }
}











using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbCheck
{
    internal sealed class DiskFullException : DeviceException
    {
        public DiskFullException(string message) : base(message) { }
    }

    /// <summary>
    /// 文件模式测试集：在目标卷根目录创建若干临时文件，拼成一个连续的逻辑空间。
    /// 只写空闲空间，不删除或覆盖原有文件（正常盘上）。检测完成后由调用方删除临时文件。
    /// </summary>
    internal sealed class FileTestSet : IDisposable
    {
        private sealed class Segment
        {
            public string Path;
            public long PlannedLength;
            public long ActualLength;
            public SafeFileHandle WriteHandle;
            public SafeFileHandle ReadHandle;
        }

        public const int BlockSize = 4096;
        private const int SegmentSize = 1 << 30; // 1 GiB，兼容 FAT32 单文件 4 GiB 限制

        private readonly List<Segment> _segments = new List<Segment>();
        private readonly List<string> _created = new List<string>();
        private readonly object _sync = new object();
        private readonly long _length;
        private readonly string _root;
        private readonly string _runId;
        private long _written;
        private AlignedBuffer _io;
        private bool _disposed;

        public FileTestSet(string volumeRoot, long length, string runId)
        {
            if (string.IsNullOrEmpty(volumeRoot)) throw new ArgumentException("volumeRoot");
            if (!volumeRoot.EndsWith("\\")) volumeRoot += "\\";
            if ((length % BlockSize) != 0) length -= length % BlockSize;
            if (length < BlockSize * 16) throw new ArgumentException("测试容量过小。");

            _root = volumeRoot;
            _length = length;
            _runId = runId;

            int count = (int)((length + SegmentSize - 1) / SegmentSize);
            for (int i = 0; i < count; i++)
            {
                Segment segment = new Segment();
                segment.Path = volumeRoot + "_usbcheck_" + runId + "_" + (i + 1).ToString("000") + ".bin";
                segment.PlannedLength = Math.Min((long)SegmentSize, length - (long)i * SegmentSize);
                _segments.Add(segment);
            }
        }

        public long Length { get { return _length; } }
        public string Root { get { return _root; } }
        public bool DiskFull { get; private set; }
        public List<string> CreatedFiles { get { return _created; } }

        public IBlockDevice Open(bool writable)
        {
            return new FileSetDevice(this, writable);
        }

        internal void Write(long offset, byte[] buffer, int count)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("FileTestSet");
                if ((offset % BlockSize) != 0 || (count % BlockSize) != 0)
                    throw new DeviceException("文件模式要求 4KB 对齐读写。");
                if (offset != _written)
                    throw new DeviceException("文件模式只支持顺序写入。");
                if (offset + count > _length)
                    throw new DeviceException("写入超出测试范围。");

                int source = 0;
                long position = offset;

                while (count > 0)
                {
                    int index = (int)(position / SegmentSize);
                    long local = position % SegmentSize;
                    Segment segment = _segments[index];
                    EnsureWriteHandle(segment, index);

                    long available = segment.PlannedLength - local;
                    if (available <= 0) throw new DeviceException("测试文件分段异常。");
                    int chunk = (int)Math.Min((long)count, available);

                    WriteSegment(segment, local, buffer, source, chunk);
                    source += chunk;
                    count -= chunk;
                    position += chunk;
                    _written += chunk;
                    segment.ActualLength = Math.Max(segment.ActualLength, local + chunk);
                }
            }
        }

        internal void Read(long offset, byte[] buffer, int count)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("FileTestSet");
                if ((offset % BlockSize) != 0 || (count % BlockSize) != 0)
                    throw new DeviceException("文件模式要求 4KB 对齐读写。");
                if (offset + count > _length)
                    throw new DeviceException("读取超出测试范围。");

                int destination = 0;
                long position = offset;

                while (count > 0)
                {
                    int index = (int)(position / SegmentSize);
                    long local = position % SegmentSize;
                    Segment segment = _segments[index];

                    if (segment.ActualLength <= local)
                        throw new DeviceException("测试文件尚未写入到该位置。");

                    long available = segment.ActualLength - local;
                    int chunk = (int)Math.Min((long)count, available);

                    EnsureReadHandle(segment);
                    ReadSegment(segment, local, buffer, destination, chunk);

                    destination += chunk;
                    count -= chunk;
                    position += chunk;
                }
            }
        }

        internal void Flush()
        {
            lock (_sync)
            {
                foreach (Segment segment in _segments)
                {
                    if (segment.WriteHandle == null) continue;
                    if (!Native.FlushFileBuffers(segment.WriteHandle))
                    {
                        int code = Marshal.GetLastWin32Error();
                        if (code != 1 && code != 50)
                            throw new DeviceException("刷新测试文件失败：" + new Win32Exception(code).Message);
                    }
                }
            }
        }

        private void EnsureWriteHandle(Segment segment, int index)
        {
            if (segment.WriteHandle != null) return;

            segment.WriteHandle = Native.CreateFileW(
                segment.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.CREATE_ALWAYS,
                Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH,
                IntPtr.Zero);

            if (segment.WriteHandle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                segment.WriteHandle.Dispose();
                segment.WriteHandle = null;
                throw new DeviceException("无法创建测试文件 " + segment.Path + "：" +
                    new Win32Exception(code).Message + "（错误码 " + code.ToString() + "）");
            }

            if (!_created.Contains(segment.Path)) _created.Add(segment.Path);
        }

        private void EnsureReadHandle(Segment segment)
        {
            if (segment.ReadHandle != null) return;

            segment.ReadHandle = Native.CreateFileW(
                segment.Path,
                Native.GENERIC_READ,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                Native.FILE_FLAG_NO_BUFFERING,
                IntPtr.Zero);

            if (segment.ReadHandle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                segment.ReadHandle.Dispose();
                segment.ReadHandle = null;
                throw new DeviceException("无法读取测试文件 " + segment.Path + "：" +
                    new Win32Exception(code).Message + "（错误码 " + code.ToString() + "）");
            }
        }

        private AlignedBuffer IO(int size)
        {
            if (_io == null || _io.Size < size)
            {
                if (_io != null) _io.Dispose();
                _io = new AlignedBuffer(size);
            }
            return _io;
        }

        private void WriteSegment(Segment segment, long localOffset, byte[] buffer, int sourceOffset, int count)
        {
            AlignedBuffer io = IO(count);
            Marshal.Copy(buffer, sourceOffset, io.Pointer, count);

            if (!Native.SetFilePointerEx(segment.WriteHandle, localOffset, IntPtr.Zero, Native.FILE_BEGIN))
                throw new DeviceException("定位测试文件失败：" + Native.LastErrorText());

            int written;
            if (!Native.WriteFile(segment.WriteHandle, io.Pointer, count, out written, IntPtr.Zero) || written != count)
            {
                int code = Marshal.GetLastWin32Error();
                if (code == Native.ERROR_DISK_FULL || code == Native.ERROR_HANDLE_DISK_FULL)
                {
                    DiskFull = true;
                    throw new DiskFullException("可用空间不足，无法写入测试文件。");
                }
                throw new DeviceException("写入测试文件失败：" +
                    new Win32Exception(code).Message + "（错误码 " + code.ToString() + "）");
            }
        }

        private void ReadSegment(Segment segment, long localOffset, byte[] buffer, int destinationOffset, int count)
        {
            AlignedBuffer io = IO(count);

            if (!Native.SetFilePointerEx(segment.ReadHandle, localOffset, IntPtr.Zero, Native.FILE_BEGIN))
                throw new DeviceException("定位测试文件失败：" + Native.LastErrorText());

            int got;
            if (!Native.ReadFile(segment.ReadHandle, io.Pointer, count, out got, IntPtr.Zero) || got != count)
                throw new DeviceException("读取测试文件失败（偏移 " + localOffset.ToString() + "）：" + Native.LastErrorText());

            Marshal.Copy(io.Pointer, buffer, destinationOffset, count);
        }

        /// <summary>关闭句柄并删除本次创建的临时文件。</summary>
        public void CloseAndDelete(Action<string> log)
        {
            List<string> paths = new List<string>();
            lock (_sync)
            {
                foreach (Segment segment in _segments)
                {
                    if (segment.WriteHandle != null)
                    {
                        segment.WriteHandle.Dispose();
                        segment.WriteHandle = null;
                    }
                    if (segment.ReadHandle != null)
                    {
                        segment.ReadHandle.Dispose();
                        segment.ReadHandle = null;
                    }
                }
                if (_io != null)
                {
                    _io.Dispose();
                    _io = null;
                }
                paths.AddRange(_created);
                _created.Clear();
                _disposed = true;
            }

            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    if (log != null) log("警告：无法删除临时文件 " + path + "：" + ex.Message);
                }
            }
        }

        public void Dispose()
        {
            CloseAndDelete(null);
        }
    }

    internal sealed class FileSetDevice : IBlockDevice
    {
        private readonly FileTestSet _set;
        private readonly bool _writable;

        public FileSetDevice(FileTestSet set, bool writable)
        {
            _set = set;
            _writable = writable;
        }

        public long Length { get { return _set.Length; } }
        public int Unit { get { return FileTestSet.BlockSize; } }

        public void Read(long offset, byte[] buffer, int count)
        {
            _set.Read(offset, buffer, count);
        }

        public void Write(long offset, byte[] buffer, int count)
        {
            if (!_writable) throw new DeviceException("该句柄为只读。");
            _set.Write(offset, buffer, count);
        }

        public void Flush()
        {
            _set.Flush();
        }

        public void Dispose()
        {
        }
    }
}

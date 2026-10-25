using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UsbCheck
{
    internal interface IBlockDevice : IDisposable
    {
        long Length { get; }
        int Unit { get; }
        void Read(long offset, byte[] buffer, int count);
        void Write(long offset, byte[] buffer, int count);
        void Flush();
    }

    internal class DeviceException : IOException
    {
        public DeviceException(string message) : base(message) { }
    }

    /// <summary>直接访问 \\.\PhysicalDriveN 的块设备，使用 NO_BUFFERING 绕过系统缓存。</summary>
    internal sealed class RawDisk : IBlockDevice
    {
        private readonly SafeFileHandle _handle;
        private readonly long _length;
        private readonly string _lengthSource;
        private readonly int _unit;
        private readonly string _path;
        private AlignedBuffer _buffer;

        public RawDisk(int diskIndex, bool writable) : this(diskIndex, writable, 0L)
        {
        }

        public RawDisk(int diskIndex, bool writable, long fallbackLength)
        {
            _path = @"\\.\PhysicalDrive" + diskIndex.ToString();
            uint access = Native.GENERIC_READ | (writable ? Native.GENERIC_WRITE : 0u);
            _handle = Native.CreateFileW(
                _path,
                access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH,
                IntPtr.Zero);

            if (_handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                string extra = "";
                if (code == Native.ERROR_ACCESS_DENIED || code == Native.ERROR_SHARING_VIOLATION)
                    extra = "\r\n请关闭正在使用该U盘的程序（资源管理器窗口、杀毒软件扫描、迅雷等），或拔插一次后重试。";
                _handle.Dispose();
                throw new DeviceException("无法打开物理磁盘 " + diskIndex.ToString() + "：" +
                    new Win32Exception(code).Message + "（错误码 " + code.ToString() + "）" + extra);
            }

            long length;
            string source;
            int lastError;
            if (TryQueryLength(out length, out source, out lastError))
            {
                _length = length;
                _lengthSource = source;
            }
            else if (fallbackLength > 0)
            {
                // 少数 USB 主控不支持上述任何容量查询，退回系统报告的容量
                _length = fallbackLength;
                _lengthSource = "系统报告容量（备用）";
            }
            else
            {
                _handle.Dispose();
                throw new DeviceException("无法获取磁盘 " + diskIndex.ToString() +
                    " 的容量（已尝试 IOCTL_DISK_GET_LENGTH_INFO / GetFileSizeEx / IOCTL_DISK_GET_DRIVE_GEOMETRY_EX）：" +
                    new Win32Exception(lastError).Message + "（错误码 " + lastError.ToString() + "）");
            }

            _unit = QueryUnit();
        }

        public long Length { get { return _length; } }
        public int Unit { get { return _unit; } }
        public string Path { get { return _path; } }
        public string LengthSource { get { return _lengthSource; } }

        /// <summary>依次尝试三种方式查询磁盘容量，兼容不支持 GetFileSizeEx 的 USB 主控。</summary>
        private bool TryQueryLength(out long length, out string source, out int lastError)
        {
            length = 0;
            source = null;
            lastError = 0;
            IntPtr buffer = Marshal.AllocHGlobal(32);
            try
            {
                int returned;

                if (Native.DeviceIoControl(_handle, Native.IOCTL_DISK_GET_LENGTH_INFO,
                        IntPtr.Zero, 0, buffer, 32, out returned, IntPtr.Zero))
                {
                    if (returned >= 8)
                    {
                        long value = Marshal.ReadInt64(buffer);
                        if (value > 0)
                        {
                            length = value;
                            source = "IOCTL_DISK_GET_LENGTH_INFO";
                            return true;
                        }
                    }
                }
                else
                {
                    lastError = Marshal.GetLastWin32Error();
                }

                long fileSize;
                if (Native.GetFileSizeEx(_handle, out fileSize) && fileSize > 0)
                {
                    length = fileSize;
                    source = "GetFileSizeEx";
                    return true;
                }
                if (lastError == 0) lastError = Marshal.GetLastWin32Error();

                if (Native.DeviceIoControl(_handle, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                        IntPtr.Zero, 0, buffer, 32, out returned, IntPtr.Zero))
                {
                    if (returned >= 32)
                    {
                        long value = Marshal.ReadInt64(buffer, 24);
                        if (value > 0)
                        {
                            length = value;
                            source = "IOCTL_DISK_GET_DRIVE_GEOMETRY_EX";
                            return true;
                        }
                    }
                }
                else if (lastError == 0)
                {
                    lastError = Marshal.GetLastWin32Error();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return false;
        }

        private int QueryUnit()
        {
            IntPtr buffer = Marshal.AllocHGlobal(32);
            try
            {
                int returned;
                if (Native.DeviceIoControl(_handle, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY,
                        IntPtr.Zero, 0, buffer, 32, out returned, IntPtr.Zero) && returned >= 24)
                {
                    int sector = Marshal.ReadInt32(buffer, 20);
                    if (sector >= 512 && sector <= 65536)
                    {
                        int unit = 4096;
                        while (unit < sector) unit *= 2;
                        return unit;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return 4096;
        }

        public static bool IsWritable(int diskIndex)
        {
            string path = @"\\.\PhysicalDrive" + diskIndex.ToString();
            using (SafeFileHandle handle = Native.CreateFileW(path, Native.GENERIC_READ,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;
                IntPtr buffer = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(buffer, 0);
                    int returned;
                    if (!Native.DeviceIoControl(handle, Native.IOCTL_DISK_IS_WRITABLE,
                            IntPtr.Zero, 0, buffer, 4, out returned, IntPtr.Zero))
                        return false;
                    return Marshal.ReadInt32(buffer) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        private void EnsureBuffer(int count)
        {
            if (_buffer != null && _buffer.Size >= count) return;
            if (_buffer != null) _buffer.Dispose();
            _buffer = new AlignedBuffer(Math.Max(count, 4096));
        }

        private void CheckRange(long offset, int count)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException("offset");
            if (count <= 0 || (count % _unit) != 0 || (offset % _unit) != 0)
                throw new ArgumentException("读写必须以 " + _unit.ToString() + " 字节对齐。");
            if (offset + count > _length)
                throw new ArgumentOutOfRangeException("offset", "读写范围超出设备长度。");
        }

        private bool Seek(long offset)
        {
            return Native.SetFilePointerEx(_handle, offset, IntPtr.Zero, Native.FILE_BEGIN);
        }

        public void Read(long offset, byte[] buffer, int count)
        {
            CheckRange(offset, count);
            EnsureBuffer(count);
            for (int attempt = 0; ; attempt++)
            {
                string error = null;
                if (Seek(offset))
                {
                    int got;
                    bool ok = Native.ReadFile(_handle, _buffer.Pointer, count, out got, IntPtr.Zero);
                    if (ok && got == count)
                    {
                        _buffer.CopyTo(buffer, count);
                        return;
                    }
                    error = ok ? "读取长度不足（" + got.ToString() + "/" + count.ToString() + " 字节）" : Native.LastErrorText();
                }
                else
                {
                    error = Native.LastErrorText();
                }

                if (attempt >= 1)
                    throw new DeviceException("读取失败（偏移 " + offset.ToString() + "）：" + error);
                Thread.Sleep(250);
            }
        }

        public void Write(long offset, byte[] buffer, int count)
        {
            CheckRange(offset, count);
            EnsureBuffer(count);
            for (int attempt = 0; ; attempt++)
            {
                _buffer.CopyFrom(buffer, count);
                string error = null;
                if (Seek(offset))
                {
                    int wrote;
                    bool ok = Native.WriteFile(_handle, _buffer.Pointer, count, out wrote, IntPtr.Zero);
                    if (ok && wrote == count) return;
                    error = ok ? "写入长度不足（" + wrote.ToString() + "/" + count.ToString() + " 字节）" : Native.LastErrorText();
                }
                else
                {
                    error = Native.LastErrorText();
                }

                if (attempt >= 1)
                    throw new DeviceException("写入失败（偏移 " + offset.ToString() + "）：" + error);
                Thread.Sleep(250);
            }
        }

        public void Flush()
        {
            if (!Native.FlushFileBuffers(_handle))
            {
                int code = Marshal.GetLastWin32Error();
                if (code == 1 || code == 50) return; // 部分设备不支持刷新
                throw new DeviceException("刷新磁盘缓存失败：" + new Win32Exception(code).Message);
            }
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                _buffer.Dispose();
                _buffer = null;
            }
            _handle.Dispose();
        }
    }

    /// <summary>锁定并卸载卷，使物理磁盘可以独占写入；Dispose 时释放（卷会自动重新挂载）。</summary>
    internal sealed class VolumeGuard : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new List<SafeFileHandle>();

        public VolumeGuard(IEnumerable<string> volumeLetters, Action<string> log)
        {
            if (volumeLetters == null) return;
            foreach (string letter in volumeLetters)
            {
                if (string.IsNullOrEmpty(letter)) continue;
                string path = @"\\.\" + letter;
                SafeFileHandle handle = Native.CreateFileW(path,
                    Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    log("警告：无法打开卷 " + letter + "：" + Native.LastErrorText());
                    handle.Dispose();
                    continue;
                }

                int returned;
                bool locked = Native.DeviceIoControl(handle, Native.FSCTL_LOCK_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
                bool dismounted = Native.DeviceIoControl(handle, Native.FSCTL_DISMOUNT_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
                if (!dismounted)
                {
                    Thread.Sleep(300);
                    dismounted = Native.DeviceIoControl(handle, Native.FSCTL_DISMOUNT_VOLUME,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
                }
                if (!locked) log("提示：卷 " + letter + " 锁定失败（可能有程序正在占用）。");
                if (!dismounted) log("提示：卷 " + letter + " 卸载失败，写入可能被系统拒绝。");
                _handles.Add(handle);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _handles.Count; i++)
                _handles[i].Dispose();
            _handles.Clear();
        }
    }
}

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbCheck
{
    /// <summary>Win32 原生调用封装。</summary>
    internal static class Native
    {
        public const uint GENERIC_READ = 0x80000000u;
        public const uint GENERIC_WRITE = 0x40000000u;
        public const uint FILE_SHARE_READ = 0x00000001u;
        public const uint FILE_SHARE_WRITE = 0x00000002u;
        public const uint OPEN_EXISTING = 3u;
        public const uint CREATE_ALWAYS = 2u;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000u;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000u;

        public const uint FSCTL_LOCK_VOLUME = 0x00090018u;
        public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020u;
        public const uint IOCTL_DISK_IS_WRITABLE = 0x00070024u;
        public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000u;
        public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0u;
        public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405Cu;

        public const uint FILE_BEGIN = 0u;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_SHARING_VIOLATION = 32;
        public const int ERROR_DISK_FULL = 112;
        public const int ERROR_HANDLE_DISK_FULL = 39;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(
            SafeFileHandle hFile,
            IntPtr lpBuffer,
            int nNumberOfBytesToRead,
            out int lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(
            SafeFileHandle hFile,
            IntPtr lpBuffer,
            int nNumberOfBytesToWrite,
            out int lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            IntPtr lpNewFilePointer,
            uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceW(
            string lpRootPathName,
            out uint lpSectorsPerCluster,
            out uint lpBytesPerSector,
            out uint lpNumberOfFreeClusters,
            out uint lpTotalNumberOfClusters);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        public static string LastErrorText()
        {
            int code = Marshal.GetLastWin32Error();
            return new Win32Exception(code).Message + "（错误码 " + code.ToString() + "）";
        }
    }

    /// <summary>按 4KB 对齐的非托管缓冲区，NO_BUFFERING 读写时要求缓冲区页对齐。</summary>
    internal sealed class AlignedBuffer : IDisposable
    {
        private const int Alignment = 4096;
        private IntPtr _raw;
        private readonly IntPtr _aligned;
        private readonly int _size;

        public AlignedBuffer(int size)
        {
            _size = size;
            _raw = Marshal.AllocHGlobal(size + Alignment);
            long p = _raw.ToInt64();
            long a = (p + Alignment - 1) & ~((long)Alignment - 1);
            _aligned = new IntPtr(a);
        }

        public IntPtr Pointer { get { return _aligned; } }
        public int Size { get { return _size; } }

        public void CopyFrom(byte[] source, int count)
        {
            if (count > _size) throw new ArgumentOutOfRangeException("count");
            Marshal.Copy(source, 0, _aligned, count);
        }

        public void CopyTo(byte[] destination, int count)
        {
            if (count > _size) throw new ArgumentOutOfRangeException("count");
            Marshal.Copy(_aligned, destination, 0, count);
        }

        public void Dispose()
        {
            if (_raw != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_raw);
                _raw = IntPtr.Zero;
            }
        }
    }
}

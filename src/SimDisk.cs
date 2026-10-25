using System;

namespace UsbCheck
{
    internal enum SimMode
    {
        Normal,
        Wrap,
        Drop
    }

    /// <summary>
    /// 仅用于自检的模拟U盘。RealBytes 小于 ClaimedBytes 时：
    /// Wrap 表示高地址写入回绕覆盖低地址（扩容盘常见行为）；
    /// Drop 表示超出真实容量的写入被静默丢弃，读取返回 0。
    /// </summary>
    internal sealed class SimDisk
    {
        public readonly long ClaimedBytes;
        public readonly long RealBytes;
        public readonly SimMode Mode;
        private readonly byte[] _data;
        private readonly object _sync = new object();
        public long Writes;
        public long WrappedWrites;
        public long DroppedWrites;

        public SimDisk(long claimedBytes, long realBytes, SimMode mode)
        {
            if (claimedBytes <= 0 || realBytes <= 0) throw new ArgumentOutOfRangeException("claimedBytes");
            if ((claimedBytes % Pattern.BlockSize) != 0 || (realBytes % Pattern.BlockSize) != 0)
                throw new ArgumentException("模拟容量必须是 4KB 的整数倍。");
            if (realBytes > claimedBytes) throw new ArgumentException("真实容量不能大于标称容量。");
            ClaimedBytes = claimedBytes;
            RealBytes = realBytes;
            Mode = mode;
            _data = new byte[realBytes];
        }

        public IBlockDevice Open(bool writable)
        {
            return new SimBlockDevice(this);
        }

        internal byte[] Data { get { return _data; } }
        internal object Sync { get { return _sync; } }
    }

    internal sealed class SimBlockDevice : IBlockDevice
    {
        private readonly SimDisk _disk;

        public SimBlockDevice(SimDisk disk)
        {
            _disk = disk;
        }

        public long Length { get { return _disk.ClaimedBytes; } }
        public int Unit { get { return Pattern.BlockSize; } }

        public void Read(long offset, byte[] buffer, int count)
        {
            Check(offset, count);
            lock (_disk.Sync)
            {
                for (int pos = 0; pos < count; pos += Pattern.BlockSize)
                {
                    long logical = offset + pos;
                    if (_disk.Mode == SimMode.Drop && logical >= _disk.RealBytes)
                    {
                        Array.Clear(buffer, pos, Pattern.BlockSize);
                    }
                    else
                    {
                        long physical = logical % _disk.RealBytes;
                        Buffer.BlockCopy(_disk.Data, (int)physical, buffer, pos, Pattern.BlockSize);
                    }
                }
            }
        }

        public void Write(long offset, byte[] buffer, int count)
        {
            Check(offset, count);
            lock (_disk.Sync)
            {
                for (int pos = 0; pos < count; pos += Pattern.BlockSize)
                {
                    long logical = offset + pos;
                    _disk.Writes++;
                    if (logical >= _disk.RealBytes)
                    {
                        if (_disk.Mode == SimMode.Drop)
                        {
                            _disk.DroppedWrites++;
                            continue;
                        }
                        _disk.WrappedWrites++;
                    }
                    long physical = logical % _disk.RealBytes;
                    Buffer.BlockCopy(buffer, pos, _disk.Data, (int)physical, Pattern.BlockSize);
                }
            }
        }

        private void Check(long offset, int count)
        {
            if (offset < 0 || count <= 0) throw new ArgumentOutOfRangeException("offset");
            if ((offset % Pattern.BlockSize) != 0 || (count % Pattern.BlockSize) != 0)
                throw new ArgumentException("模拟设备只接受 4KB 对齐的读写。");
            if (offset + count > _disk.ClaimedBytes) throw new ArgumentOutOfRangeException("offset");
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}

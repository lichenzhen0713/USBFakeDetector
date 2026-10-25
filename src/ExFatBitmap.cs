using System;

namespace UsbCheck
{
    /// <summary>解析 exFAT 分配位图（Windows 的 FSCTL_GET_VOLUME_BITMAP 不支持 exFAT）。</summary>
    internal static class ExFatBitmap
    {
        public static bool IsExFat(VolumeProbe volume)
        {
            try
            {
                byte[] boot = volume.ReadAligned(0, 512);
                return boot.Length >= 11 &&
                    boot[3] == (byte)'E' && boot[4] == (byte)'X' && boot[5] == (byte)'F' &&
                    boot[6] == (byte)'A' && boot[7] == (byte)'T' && boot[8] == (byte)' ' &&
                    boot[9] == (byte)' ' && boot[10] == (byte)' ';
            }
            catch
            {
                return false;
            }
        }

        public static byte[] Read(VolumeProbe volume, out int totalClusters)
        {
            byte[] boot = volume.ReadAligned(0, 512);
            if (boot.Length < 512 || boot[3] != (byte)'E' || boot[4] != (byte)'X' ||
                boot[5] != (byte)'F' || boot[6] != (byte)'A' || boot[7] != (byte)'T')
            {
                throw new DeviceException("不是 exFAT 卷。");
            }

            int sectorShift = boot[108];
            int clusterShift = boot[109];
            if (sectorShift < 9 || sectorShift > 12 || clusterShift > 25)
                throw new DeviceException("exFAT 参数异常。");

            int sectorSize = 1 << sectorShift;
            int clusterSize = sectorSize << clusterShift;
            if (clusterSize != volume.ClusterSize)
                throw new DeviceException("exFAT 簇大小与系统报告不一致。");

            long fatOffset = (long)BitConverter.ToUInt32(boot, 80) * sectorSize;
            long clusterHeapOffset = (long)BitConverter.ToUInt32(boot, 88) * sectorSize;
            volume.ExFat = true;
            volume.ClusterHeapOffset = clusterHeapOffset;
            uint clusterCountValue = BitConverter.ToUInt32(boot, 92);
            int rootCluster = (int)BitConverter.ToUInt32(boot, 96);

            if (clusterCountValue == 0 || clusterCountValue > 0x7FFFFFF0u)
                throw new DeviceException("exFAT 簇数量异常。");
            int clusterCount = (int)clusterCountValue;

            int bitmapCluster = -1;
            long bitmapLength = 0;

            int cluster = rootCluster;
            int guard = 0;
            while (cluster >= 2 && guard++ < 100000)
            {
                byte[] data = ReadCluster(volume, clusterHeapOffset, cluster, clusterSize);
                bool endOfDirectory = false;

                for (int pos = 0; pos + 32 <= data.Length; pos += 32)
                {
                    byte type = data[pos];
                    if (type == 0x00)
                    {
                        endOfDirectory = true;
                        break;
                    }

                    if (type == 0x81 && bitmapCluster < 2)
                    {
                        bitmapCluster = (int)BitConverter.ToUInt32(data, pos + 20);
                        bitmapLength = BitConverter.ToInt64(data, pos + 24);
                        break;
                    }
                }

                if (bitmapCluster >= 2 || endOfDirectory) break;
                cluster = NextCluster(volume, fatOffset, cluster);
            }

            if (bitmapCluster < 2)
                throw new DeviceException("无法定位 exFAT 空闲位图文件。");
            if (bitmapLength <= 0)
                throw new DeviceException("exFAT 空闲位图长度无效。");

            int bytes = (clusterCount + 7) / 8;
            byte[] bitmap = new byte[bytes];
            int written = 0;
            cluster = bitmapCluster;
            guard = 0;

            while (cluster >= 2 && written < bytes && guard++ < 1000000)
            {
                int take = Math.Min(clusterSize, bytes - written);
                byte[] data = ReadCluster(volume, clusterHeapOffset, cluster, take);
                Buffer.BlockCopy(data, 0, bitmap, written, take);
                written += take;
                cluster = NextCluster(volume, fatOffset, cluster);
            }

            if (written < bytes)
                throw new DeviceException("exFAT 空闲位图读取不完整。");

            totalClusters = clusterCount;
            return bitmap;
        }

        private static byte[] ReadCluster(VolumeProbe volume, long heapOffset, int cluster, int count)
        {
            if (cluster < 2) throw new DeviceException("exFAT 簇号无效。");
            long offset = heapOffset + (long)(cluster - 2) * volume.ClusterSize;
            return volume.ReadAligned(offset, count);
        }

        private static int NextCluster(VolumeProbe volume, long fatOffset, int cluster)
        {
            byte[] entry = volume.ReadAligned(fatOffset + (long)cluster * 4, 4);
            uint next = BitConverter.ToUInt32(entry, 0);
            if (next >= 0xFFFFFFF8u || next < 2u || next > int.MaxValue) return -1;
            return (int)next;
        }
    }
}


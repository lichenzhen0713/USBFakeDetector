using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;

namespace UsbCheck
{
    internal sealed class VolumeInfo
    {
        public string Letter = "";
        public string Label = "";
        public string FileSystem = "";
        public long Size;
        public long FreeSpace;
    }

    internal sealed class DiskInfo
    {
        public int Index;
        public string Model = "";
        public string InterfaceType = "";
        public string MediaType = "";
        public string SerialNumber = "";
        public long Size;
        public bool IsSystem;
        public readonly List<VolumeInfo> Volumes = new List<VolumeInfo>();

        public bool IsUsb
        {
            get { return string.Equals(InterfaceType, "USB", StringComparison.OrdinalIgnoreCase); }
        }

        public string Letters
        {
            get
            {
                if (Volumes.Count == 0) return "(无盘符)";
                string s = "";
                foreach (VolumeInfo v in Volumes)
                {
                    if (s.Length > 0) s += " ";
                    s += v.Letter;
                }
                return s;
            }
        }

        public string Labels
        {
            get
            {
                if (Volumes.Count == 0) return "";
                string s = "";
                foreach (VolumeInfo v in Volumes)
                {
                    if (string.IsNullOrEmpty(v.Label)) continue;
                    if (s.Length > 0) s += ", ";
                    s += v.Label;
                }
                return s;
            }
        }

        public string FileSystems
        {
            get
            {
                if (Volumes.Count == 0) return "";
                string s = "";
                foreach (VolumeInfo v in Volumes)
                {
                    if (string.IsNullOrEmpty(v.FileSystem)) continue;
                    if (s.Length > 0) s += ", ";
                    s += v.FileSystem;
                }
                return s;
            }
        }

        public List<string> VolumeLetters
        {
            get
            {
                List<string> list = new List<string>();
                foreach (VolumeInfo v in Volumes)
                {
                    if (v.Letter.Length == 2 && v.Letter[1] == ':') list.Add(v.Letter);
                }
                return list;
            }
        }
    }

    internal static class DiskInventory
    {
        public static List<DiskInfo> Enumerate()
        {
            List<DiskInfo> disks = new List<DiskInfo>();
            Dictionary<int, DiskInfo> byIndex = new Dictionary<int, DiskInfo>();

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive"))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject mo in items)
                {
                    using (mo)
                    {
                        DiskInfo disk = new DiskInfo();
                        disk.Index = ToInt(mo["Index"]);
                        disk.Model = Clean(ToStr(mo["Model"]));
                        disk.InterfaceType = Clean(ToStr(mo["InterfaceType"]));
                        disk.MediaType = Clean(ToStr(mo["MediaType"]));
                        disk.SerialNumber = Clean(ToStr(mo["SerialNumber"]));
                        disk.Size = ToLong(mo["Size"]);
                        disks.Add(disk);
                        byIndex[disk.Index] = disk;
                    }
                }
            }

            Dictionary<string, int> partToDisk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDriveToDiskPartition"))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject mo in items)
                {
                    using (mo)
                    {
                        string antecedent = ToStr(mo["Antecedent"]);
                        string dependent = ToStr(mo["Dependent"]);
                        int diskIndex = ParsePhysicalDrive(antecedent);
                        string partition = ExtractQuoted(dependent);
                        if (diskIndex >= 0 && partition != null) partToDisk[partition] = diskIndex;
                    }
                }
            }

            Dictionary<string, VolumeInfo> volumeByLetter = new Dictionary<string, VolumeInfo>(StringComparer.OrdinalIgnoreCase);
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDisk"))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject mo in items)
                {
                    using (mo)
                    {
                        VolumeInfo volume = new VolumeInfo();
                        volume.Letter = Clean(ToStr(mo["DeviceID"]));
                        volume.Label = Clean(ToStr(mo["VolumeName"]));
                        volume.FileSystem = Clean(ToStr(mo["FileSystem"]));
                        volume.Size = ToLong(mo["Size"]);
                        volume.FreeSpace = ToLong(mo["FreeSpace"]);
                        if (volume.Letter.Length > 0) volumeByLetter[volume.Letter] = volume;
                    }
                }
            }

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject mo in items)
                {
                    using (mo)
                    {
                        string partition = ExtractQuoted(ToStr(mo["Antecedent"]));
                        string letter = ExtractQuoted(ToStr(mo["Dependent"]));
                        if (partition == null || letter == null) continue;

                        int diskIndex;
                        if (!partToDisk.TryGetValue(partition, out diskIndex)) continue;

                        DiskInfo disk;
                        if (!byIndex.TryGetValue(diskIndex, out disk)) continue;

                        VolumeInfo volume;
                        if (!volumeByLetter.TryGetValue(letter, out volume)) continue;

                        bool exists = false;
                        foreach (VolumeInfo v in disk.Volumes) if (v.Letter == volume.Letter) exists = true;
                        if (!exists) disk.Volumes.Add(volume);
                    }
                }
            }

            string systemRoot = "";
            try
            {
                systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
                systemRoot = systemRoot.TrimEnd('\\');
            }
            catch
            {
            }

            foreach (DiskInfo disk in disks)
            {
                disk.Volumes.Sort(delegate(VolumeInfo a, VolumeInfo b) { return string.CompareOrdinal(a.Letter, b.Letter); });
                foreach (VolumeInfo v in disk.Volumes)
                {
                    if (systemRoot.Length > 0 && string.Equals(v.Letter, systemRoot, StringComparison.OrdinalIgnoreCase))
                        disk.IsSystem = true;
                }
            }

            disks.Sort(delegate(DiskInfo a, DiskInfo b) { return a.Index.CompareTo(b.Index); });
            return disks;
        }

        private static int ParsePhysicalDrive(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            Match m = Regex.Match(path, @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return -1;
            int index;
            return int.TryParse(m.Groups[1].Value, out index) ? index : -1;
        }

        private static string ExtractQuoted(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            int first = value.IndexOf('"');
            if (first < 0) return null;
            int second = value.IndexOf('"', first + 1);
            if (second < 0) return null;
            return value.Substring(first + 1, second - first - 1);
        }

        private static string Clean(string s)
        {
            if (s == null) return "";
            return s.Trim().Trim('\0');
        }

        private static int ToInt(object value)
        {
            try { return Convert.ToInt32(value); }
            catch { return 0; }
        }

        private static long ToLong(object value)
        {
            try { return Convert.ToInt64(value); }
            catch { return 0; }
        }

        private static string ToStr(object value)
        {
            return value == null ? "" : value.ToString();
        }
    }
}

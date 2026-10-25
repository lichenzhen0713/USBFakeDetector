using System;
using System.Security.Cryptography;

namespace UsbCheck
{
    /// <summary>
    /// 测试数据的生成与识别。
    /// 所有测试块都带有本次运行独有的 runTag 与逻辑偏移，因此一旦数据出现在别的偏移上，
    /// 就能判断出设备把地址“回绕”到了前面（扩容盘的典型行为）。
    /// </summary>
    internal static class Pattern
    {
        public const int BlockSize = 4096;

        // 采样标记（快速扫描）布局
        public const int TagPayloadStart = 48;
        public const int TagChecksumPos = BlockSize - 8;
        public const int TagSalt = 0x54414731;   // "TAG1"

        // 连续数据流（完整校验）布局
        public const int StreamPayloadStart = 32;
        public const int StreamSalt = 0x5354524D; // "STRM"

        public static byte[] NewRunTag()
        {
            byte[] b = new byte[8];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(b);
            }
            return b;
        }

        public static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public static ulong SeedFor(byte[] runTag, long offset, int salt)
        {
            ulong r = BitConverter.ToUInt64(runTag, 0);
            ulong h = r ^ (ulong)offset ^ (ulong)(uint)salt;
            return Mix(Mix(h) + (h * 0x9E3779B97F4A7C15UL));
        }

        private static unsafe void FillPayloadPtr(byte* dest, int count, ulong seed)
        {
            ulong a = Mix(seed + 0x9E3779B97F4A7C15UL);
            ulong b = Mix(a ^ 0xD1B54A32D192ED03UL);
            ulong c = Mix(b + 0x94D049BB133111EBUL);
            ulong d = Mix(c ^ 0xBF58476D1CE4E5B9UL);

            ulong* q = (ulong*)dest;
            int words = count >> 3;
            int i = 0;
            while (i + 4 <= words)
            {
                q[i] = a;
                q[i + 1] = b;
                q[i + 2] = c;
                q[i + 3] = d;
                i += 4;
            }
            while (i < words)
            {
                q[i] = (i & 3) == 0 ? a : (i & 3) == 1 ? b : (i & 3) == 2 ? c : d;
                i++;
            }

            int rem = count & 7;
            if (rem > 0)
            {
                byte* tail = dest + (words << 3);
                byte* src = (byte*)&a;
                for (int k = 0; k < rem; k++) tail[k] = src[k];
            }
        }

        public static unsafe void FillPayload(byte[] buffer, int start, int count, ulong seed)
        {
            fixed (byte* p = buffer)
            {
                FillPayloadPtr(p + start, count, seed);
            }
        }

        private static unsafe ulong Fnv1aPtr(byte* data, int count)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < count; i++)
            {
                h ^= data[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        // ------------------------- 快速扫描用标记 -------------------------

        public static unsafe void BuildTag(byte[] buffer, byte[] runTag, long offset, int index)
        {
            fixed (byte* p = buffer)
            {
                p[0] = (byte)'F';
                p[1] = (byte)'K';
                p[2] = (byte)'T';
                p[3] = (byte)'1';
                *(int*)(p + 4) = 1;
                *(long*)(p + 8) = offset;
                *(int*)(p + 16) = index;
                *(int*)(p + 20) = BlockSize;
                for (int i = 0; i < 8; i++) p[24 + i] = runTag[i];
                *(long*)(p + 32) = TagSalt;
                *(long*)(p + 40) = 0;

                FillPayloadPtr(p + TagPayloadStart, TagChecksumPos - TagPayloadStart,
                    SeedFor(runTag, offset, TagSalt));
                *(ulong*)(p + TagChecksumPos) = Fnv1aPtr(p, TagChecksumPos);
            }
        }

        public static unsafe bool TryParseTag(byte[] buffer, byte[] runTag, out long offset, out int index)
        {
            offset = 0;
            index = 0;
            fixed (byte* p = buffer)
            {
                if (p[0] != (byte)'F' || p[1] != (byte)'K' || p[2] != (byte)'T' || p[3] != (byte)'1')
                    return false;
                if (*(int*)(p + 4) != 1) return false;
                if (*(int*)(p + 20) != BlockSize) return false;
                if (*(long*)(p + 32) != TagSalt) return false;
                for (int i = 0; i < 8; i++)
                    if (p[24 + i] != runTag[i]) return false;

                ulong expected = *(ulong*)(p + TagChecksumPos);
                if (expected != Fnv1aPtr(p, TagChecksumPos)) return false;

                long o = *(long*)(p + 8);
                if (o < 0 || (o % BlockSize) != 0) return false;
                offset = o;
                index = *(int*)(p + 16);
                return true;
            }
        }

        // ------------------------- 完整校验用数据流 -------------------------

        public static unsafe void FillStream(byte[] buffer, int bufferOffset, long deviceOffset, int count, byte[] runTag)
        {
            fixed (byte* p = buffer)
            {
                byte* basePtr = p + bufferOffset;
                for (int pos = 0; pos < count; pos += BlockSize)
                {
                    byte* b = basePtr + pos;
                    b[0] = (byte)'F';
                    b[1] = (byte)'K';
                    b[2] = (byte)'S';
                    b[3] = (byte)'1';
                    for (int i = 0; i < 8; i++) b[4 + i] = runTag[i];
                    *(long*)(b + 12) = deviceOffset + pos;
                    *(int*)(b + 20) = StreamSalt;
                    *(int*)(b + 24) = BlockSize;
                    *(int*)(b + 28) = 0;
                    FillPayloadPtr(b + StreamPayloadStart, BlockSize - StreamPayloadStart,
                        SeedFor(runTag, deviceOffset + pos, StreamSalt));
                }
            }
        }

        public static unsafe bool TryParseStreamHeader(byte[] buffer, int bufferOffset, byte[] runTag, out long offset)
        {
            offset = 0;
            fixed (byte* p = buffer)
            {
                byte* b = p + bufferOffset;
                if (b[0] != (byte)'F' || b[1] != (byte)'K' || b[2] != (byte)'S' || b[3] != (byte)'1')
                    return false;
                for (int i = 0; i < 8; i++)
                    if (b[4 + i] != runTag[i]) return false;
                if (*(int*)(b + 20) != StreamSalt) return false;
                if (*(int*)(b + 24) != BlockSize) return false;
                long o = *(long*)(b + 12);
                if (o < 0 || (o % BlockSize) != 0) return false;
                offset = o;
                return true;
            }
        }

        public static unsafe bool BlockEquals(byte[] a, int aOffset, byte[] b, int bOffset)
        {
            fixed (byte* pa = a)
            fixed (byte* pb = b)
            {
                ulong* x = (ulong*)(pa + aOffset);
                ulong* y = (ulong*)(pb + bOffset);
                for (int i = 0; i < BlockSize / 8; i++)
                {
                    if (x[i] != y[i]) return false;
                }
            }
            return true;
        }
    }
}

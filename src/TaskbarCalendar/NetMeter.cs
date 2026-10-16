using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace TaskbarCalendar
{
    /// <summary>Bytes/s across physical adapters (Ethernet, Wi-Fi). Virtual, VPN and filter interfaces are skipped
    /// so traffic isn't counted twice.</summary>
    class NetMeter
    {
        // MIB_IF_ROW2 layout (x64), read by offset rather than marshalled: only four fields are needed.
        const int RowSize = 1352, OffIndex = 8, OffType = 1128, OffFlags = 1152, OffInOctets = 1208, OffOutOctets = 1280;
        const int TableRowsOffset = 8;
        const byte FlagHardware = 0x1, FlagFilter = 0x2;
        const int TypeLoopback = 24;

        [DllImport("iphlpapi.dll")] static extern int GetIfTable2(out IntPtr table);
        [DllImport("iphlpapi.dll")] static extern int GetIfEntry2(IntPtr row);
        [DllImport("iphlpapi.dll")] static extern void FreeMibTable(IntPtr table);

        List<uint> adapters = new List<uint>();
        (double Time, ulong Rx, ulong Tx)? last;
        readonly IntPtr row = Marshal.AllocHGlobal(RowSize);

        public NetMeter() { FindAdapters(); }

        public void FindAdapters()
        {
            if (GetIfTable2(out var table) != 0) return;
            var found = new List<uint>();
            try
            {
                var count = Marshal.ReadInt32(table);
                for (int i = 0; i < count; i++)
                {
                    var r = table + TableRowsOffset + i * RowSize;
                    var flags = Marshal.ReadByte(r, OffFlags);
                    if ((flags & FlagHardware) != 0 && (flags & FlagFilter) == 0 && Marshal.ReadInt32(r, OffType) != TypeLoopback)
                        found.Add((uint)Marshal.ReadInt32(r, OffIndex));
                }
            }
            finally
            {
                FreeMibTable(table);
            }
            if (!found.SequenceEqual(adapters))
            {
                adapters = found;
                last = null;   // totals aren't comparable across a different adapter set
            }
        }

        public void Reset() => last = null;

        /// <summary>(up, down) in bytes/s since the previous call, or null on the first call.</summary>
        public (double Up, double Down)? Sample()
        {
            ulong rx = 0, tx = 0;
            foreach (var index in adapters)
            {
                for (int i = 0; i < RowSize; i += 8) Marshal.WriteInt64(row, i, 0);
                Marshal.WriteInt32(row, OffIndex, (int)index);
                if (GetIfEntry2(row) != 0) continue;
                rx += (ulong)Marshal.ReadInt64(row, OffInOctets);
                tx += (ulong)Marshal.ReadInt64(row, OffOutOctets);
            }
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var previous = last;
            last = (now, rx, tx);
            if (previous == null || now <= previous.Value.Time) return null;
            var elapsed = now - previous.Value.Time;
            return (Math.Max(0, (double)tx - previous.Value.Tx) / elapsed, Math.Max(0, (double)rx - previous.Value.Rx) / elapsed);
        }

        public static string Format(double bytesPerSecond) =>
            bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / 1024 / 1024:0.00} MB/s" : $"{bytesPerSecond / 1024:0.0} KB/s";
    }
}

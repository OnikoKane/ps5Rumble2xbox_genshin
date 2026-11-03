// ds5shm — READ-ONLY spike: map HIDMaestro's shared-memory output ring and
// decode the virtual DualSense's USB output report 0x02 (rumble + adaptive
// trigger effect bytes).
//
// Layout (verbatim from FlexInput crates/hidmaestro/src/shm.rs, itself a port of
// HIDMaestro SharedMemoryIO.cs / driver.h):
//   Section name: Global\HIDMaestroOutput{i}   size 16904 bytes
//   header:  off 0  u32 Head (monotonic driver writes)
//   slots:   64 x 264 bytes at off 8:
//              +0 u32 SeqNo  +4 u8 Source  +5 u8 ReportId  +6 u16 DataSize  +8 [256] Data
//   Data[] is the output report with the Report ID STRIPPED (ReportId holds it).
//   For a DualSense USB 0x02 report (DataSize 47), within Data[]:
//      [0]     validFlag0   bit0 COMPATIBLE_VIBRATION, bit1 HAPTICS_SELECT,
//                           bit2 RIGHT_TRIGGER valid, bit3 LEFT_TRIGGER valid
//      [2]     right/small motor
//      [3]     left/large motor
//      [10..20] right trigger effect (11 bytes)
//      [21..31] left  trigger effect (11 bytes)
//   (RID-inclusive report puts these at +1; DualSenseBridge output.py uses the
//    common-block offsets 0/2/3/10../21.. on a report whose r[0] is the RID.)
//
// Usage:
//   ds5shm                       scan Global\HIDMaestroOutput0..15, dump last frames + decode
//   ds5shm --index 0 --dump      only index 0
//   ds5shm --index 0 --watch --seconds 20   poll for new frames (see Head climb)
//   ds5shm --selftest-decode    decode a built-in crafted 0x02 report (parser check)
//   ds5shm --hex <94 hex chars> decode a provided 47-byte Data[] blob
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ds5Shm;

internal static class Program
{
    private const int FILE_MAP_READ = 0x0004;
    private const int OUTPUT_SIZE = 16904;
    private const int HEADER_SIZE = 8;
    private const int SLOT_SIZE = 264;
    private const int SLOTS = 64;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr baseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private sealed class Section : IDisposable
    {
        public string Name = "";
        public uint Index;
        private IntPtr _view;
        public bool Ok => _view != IntPtr.Zero;

        public static Section? Open(uint index)
        {
            string name = $@"Global\HIDMaestroOutput{index}";
            IntPtr h = OpenFileMappingW(FILE_MAP_READ, false, name);
            if (h == IntPtr.Zero) return null;
            IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (UIntPtr)OUTPUT_SIZE);
            CloseHandle(h);
            if (v == IntPtr.Zero) return null;
            return new Section { Name = name, Index = index, _view = v };
        }

        public uint Head => (uint)Marshal.ReadInt32(_view, 0);
        public byte U8(int off) => Marshal.ReadByte(_view, off);
        public ushort U16(int off) => (ushort)Marshal.ReadInt16(_view, off);
        public uint U32(int off) => (uint)Marshal.ReadInt32(_view, off);

        public byte[] ReadData(int off, int n)
        {
            var b = new byte[n];
            Marshal.Copy(IntPtr.Add(_view, off), b, 0, n);
            return b;
        }

        /// <summary>Read the frame for absolute sequence `seq`; null if slot's SeqNo differs.</summary>
        public (byte src, byte rid, byte[] data)? Frame(uint seq)
        {
            int slot = (int)((seq - 1) % SLOTS);
            int b = HEADER_SIZE + slot * SLOT_SIZE;
            if (U32(b) != seq) return null;
            byte src = U8(b + 4);
            byte rid = U8(b + 5);
            int sz = U16(b + 6);
            if (sz < 0 || sz > 256) sz = 0;
            return (src, rid, ReadData(b + 8, sz));
        }

        public void Dispose()
        {
            if (_view != IntPtr.Zero) { UnmapViewOfFile(_view); _view = IntPtr.Zero; }
        }
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length >= 2 && args[0] == "--hex")
        {
            DecodeHelper.DecodeDs5(ParseHex(args[1]), 0);
            return 0;
        }
        if (args.Contains("--selftest-decode"))
        {
            Console.WriteLine("=== selftest-decode: crafted 0x02 report (R=Vibration z3 s4 f20, L=Feedback z4 s5) ===");
            DecodeHelper.DecodeDs5(DecodeHelper.CraftSample(), 0);
            return 0;
        }

        int only = ArgInt(args, "--index", -1);
        bool watch = args.Contains("--watch");
        int seconds = ArgInt(args, "--seconds", 8);
        int dump = ArgInt(args, "--dump-n", 8);

        var found = new List<Section>();
        int lo = only >= 0 ? only : 0;
        int hi = only >= 0 ? only : 15;
        for (int i = lo; i <= hi; i++)
        {
            var s = Section.Open((uint)i);
            if (s != null) { found.Add(s); }
        }

        if (found.Count == 0)
        {
            Console.WriteLine("[FAIL] 未找到任何 Global\\HIDMaestroOutput{i}（i=0..15）共享内存段。");
            Console.WriteLine("  - 说明当前没有 HIDMaestro 虚拟设备在运行（当前虚拟 DS5 可能是 usbip 复合设备）。");
            Console.WriteLine("  - 或权限不足（需 Authenticated Users 可读；一般无需管理员）。");
            return 10;
        }

        Console.WriteLine($"=== 找到 {found.Count} 个 HIDMaestro 输出段 ===");
        foreach (var s in found)
        {
            uint head = s.Head;
            Console.WriteLine($"\n[{s.Name}]  Head={head}  (累计驱动写入帧数；0=从未写入)");
            if (head == 0) { Console.WriteLine("  (空，无输出报告)"); continue; }

            uint start = head > (uint)dump ? head - (uint)dump + 1 : 1;
            for (uint seq = start; seq <= head; seq++)
            {
                var f = s.Frame(seq);
                if (f == null) continue;
                Console.WriteLine($"  seq={seq} src={f.Value.src} rid=0x{f.Value.rid:X2} len={f.Value.data.Length}");
                if (f.Value.rid == 0x02) DecodeHelper.DecodeDs5(f.Value.data, 2);
            }
        }

        if (watch)
        {
            Console.WriteLine($"\n=== watch {seconds}s（只打印新帧）===");
            var last = new Dictionary<uint, uint>();
            foreach (var s in found) last[s.Index] = s.Head;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastBeat = 0.0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                foreach (var s in found)
                {
                    uint head = s.Head;
                    uint prev = last[s.Index];
                    if (head == prev) continue;
                    if (head > prev + SLOTS) prev = head - SLOTS; // skip overwritten
                    for (uint seq = prev + 1; seq <= head; seq++)
                    {
                        var f = s.Frame(seq);
                        if (f == null) continue;
                        if (f.Value.rid != 0x02) continue;
                        Console.WriteLine($"  [new] idx={s.Index} seq={seq} rid=0x02 len={f.Value.data.Length}");
                        DecodeHelper.DecodeDs5(f.Value.data, 4);
                    }
                    last[s.Index] = head;
                }
                if (sw.Elapsed.TotalSeconds - lastBeat >= 2.0)
                {
                    lastBeat = sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"  [diag t={sw.Elapsed.TotalSeconds:F0}s] " +
                        string.Join("  ", found.ConvertAll(s => $"idx{s.Index}:Head={s.Head}")));
                }
                Thread.Sleep(8);
            }
        }

        foreach (var s in found) s.Dispose();
        return 0;
    }

    private static int ArgInt(string[] a, string key, int def)
    {
        int i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : def;
    }

    private static byte[] ParseHex(string s)
    {
        var clean = new System.Text.StringBuilder();
        foreach (char c in s) if (Uri.IsHexDigit(c)) clean.Append(c);
        string h = clean.ToString();
        var o = new byte[h.Length / 2];
        for (int i = 0; i < o.Length; i++) o[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
        return o;
    }
}

internal static class DecodeHelper
{
    public static void DecodeDs5(byte[] d, int indent)
    {
        string pad = new string(' ', indent);
        if (d.Length < 32)
        {
            Console.WriteLine($"{pad}[decode] len={d.Length} <32，无法解扳机块");
            return;
        }
        byte flag0 = d[0];
        Console.WriteLine($"{pad}flag0=0x{flag0:X2}  compatVib={B(flag0,0)} hapticsSel={B(flag0,1)} rTrigValid={B(flag0,2)} lTrigValid={B(flag0,3)}");
        Console.WriteLine($"{pad}motor  R(small)={d[2]}  L(large)={d[3]}");
        var rt = Slice(d, 10);
        var lt = Slice(d, 21);
        Console.WriteLine($"{pad}R trigger raw[10..20]= {Hex(rt)}");
        Console.WriteLine($"{pad}  -> {Parse(rt)}");
        Console.WriteLine($"{pad}L trigger raw[21..31]= {Hex(lt)}");
        Console.WriteLine($"{pad}  -> {Parse(lt)}");
    }

    private static string B(byte v, int bit) => ((v >> bit) & 1) == 1 ? "1" : "0";
    private static byte[] Slice(byte[] d, int off)
    {
        var b = new byte[11];
        Array.Copy(d, off, b, 0, Math.Min(11, d.Length - off));
        return b;
    }
    private static string Hex(byte[] b) => string.Join(" ", Array.ConvertAll(b, x => x.ToString("x2")));

    private static string Parse(byte[] b)
    {
        byte mode = b[0];
        int zones = b[1] | (b[2] << 8);
        int lo = zones == 0 ? 0 : TrailingZeros(zones);
        int hi = zones == 0 ? 0 : 15 - LeadingZeros16(zones);
        uint forces = (uint)(b[3] | (b[4] << 8) | (b[5] << 16) | (b[6] << 24));
        int forceAt(int z) => (int)((forces >> (3 * z)) & 0x7);
        int low3 = b[3] & 0x7, high3 = (b[3] >> 3) & 0x7;

        switch (mode)
        {
            case 0x05: return "Off (0x05)";
            case 0x00: return "Off/空 (0x00)";
            case 0x21:
                return $"Feedback(0x21) start=z{lo} strength=f{forceAt(lo)}" + N(lo, forceAt(lo));
            case 0x22:
                return $"Bow(0x22) start=z{lo} end=z{hi} strength=f{low3} snap=f{high3}";
            case 0x23:
                return $"Galloping(0x23) start=z{lo} end=z{hi} foot1=f{high3} foot2=f{low3} freq={b[4]}";
            case 0x25:
                return $"Weapon(0x25) start=z{lo} end=z{hi} strength=f{b[3]}";
            case 0x26:
                return $"Vibration(0x26) start=z{lo} strength=f{forceAt(lo)} freq={b[9]}" + N(lo, forceAt(lo));
            case 0x27:
                return $"Machine(0x27) start=z{lo} end=z{hi} ampA=f{low3} ampB=f{high3} freq={b[4]} period={b[5]}";
            default:
                return $"Unknown(0x{mode:X2})";
        }
    }

    private static string N(int zone, int force) => $"  (start/9={zone / 9.0:F2}, strength/7={force / 7.0:F2})";

    private static int TrailingZeros(int v)
    {
        int n = 0;
        while (((v >> n) & 1) == 0 && n < 16) n++;
        return n;
    }
    private static int LeadingZeros16(int v)
    {
        int n = 0;
        for (int i = 15; i >= 0; i--) { if (((v >> i) & 1) == 1) break; n++; }
        return n;
    }

    /// <summary>47-byte crafted report: R=Vibration z3 s4 f20, L=Feedback z4 s5.</summary>
    public static byte[] CraftSample()
    {
        var d = new byte[47];
        d[0] = 0x0C;  // right+left trigger valid
        d[2] = 0; d[3] = 0;
        // R: 0x26 start z3 (bit3), strength 4 -> force (4-1)=3 at slot 3 => 3<<9
        d[10] = 0x26; d[11] = 0x08; d[12] = 0x00;
        int fR = 3 << 9; d[13] = (byte)(fR & 0xFF); d[14] = (byte)((fR >> 8) & 0xFF); d[15] = (byte)((fR >> 16) & 0xFF); d[16] = (byte)((fR >> 24) & 0xFF);
        d[19] = 20; // freq
        // L: 0x21 Feedback start z4 (bit4), strength 5 -> force (5-1)=4 at slot 4 => 4<<12
        d[21] = 0x21; d[22] = 0x10; d[23] = 0x00;
        int fL = 4 << 12; d[24] = (byte)(fL & 0xFF); d[25] = (byte)((fL >> 8) & 0xFF); d[26] = (byte)((fL >> 16) & 0xFF); d[27] = (byte)((fL >> 24) & 0xFF);
        return d;
    }
}

internal static class ArrayExt
{
    public static bool Contains<T>(this T[] a, T v) => Array.IndexOf(a, v) >= 0;
}

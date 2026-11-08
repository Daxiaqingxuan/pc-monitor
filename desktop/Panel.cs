// Panel.cs - the web dashboard, hosted inside the widget process.
//
// What this replaces: server.py shipped its own Python interpreter (16 MB
// private before a single sample was taken) and kept 900 full sample dicts
// in a deque (~6 MB with interpreter overhead). Everything the page needs is
// native here - PDH counters for network and disk, a toolhelp walk for the
// process table, a 900-row ring of fixed-size structs (~86 KB), and a bare
// HTTP/1.0 listener following the pattern TempsServer already proved.
//
// The wire format is byte-compatible with the Python version: same endpoints,
// same field names, same nesting. web/ is untouched.
//
// Language level is C# 5, the same as Widget.cs: no string interpolation,
// no ?. operator, no out-var, no auto-property initialisers.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace HardwareMonitor
{
    // ------------------------------------------------------- native (panel)
    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    internal static class Native2
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr proc, out FILETIME create,
            out FILETIME exit, out FILETIME kernel, out FILETIME user);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetProcessMemoryInfo(IntPtr proc,
            ref PROCESS_MEMORY_COUNTERS counters, uint size);

        [DllImport("powrprof.dll")]
        public static extern int CallNtPowerInformation(int level, IntPtr input, int inputSize,
            IntPtr output, int outputSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformationEx(int relationshipType,
            IntPtr buffer, ref uint length);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    /// <summary>Monotonic seconds, shared by every sampler on the tick thread.</summary>
    internal static class Clock
    {
        private static readonly double Freq = (double)System.Diagnostics.Stopwatch.Frequency;

        public static double Now
        {
            get { return System.Diagnostics.Stopwatch.GetTimestamp() / Freq; }
        }
    }

    // ------------------------------------------------------------- sample
    /// <summary>
    /// One tick's worth of everything the page shows. Built on the sampler
    /// thread, consumed by the HTTP thread; the string fragments are immutable
    /// so they can be shared with every SSE subscriber without copying.
    /// </summary>
    internal sealed class PanelSample
    {
        public long T;
        public int CpuPct, CpuClk;
        public double[] Cores = new double[0];
        public double? CpuTemp, CpuW;
        public int GpuPct;
        public double? GpuTemp, GpuHot, GpuW;
        public long VramUsed, VramTotal;
        public string EngJson = "{}";
        public long MemUsed, MemTotal;
        // long, not int: a PCIe 4 SSD reads past 2 GB/s, which overflows
        // Int32 and would surface as a negative rate on the chart.
        public long NetRx, NetTx, DiskRead, DiskWrite;
        // False when the user switched the group off: the row is then written
        // as null instead of 0 so the archive shows a gap, not a flat line.
        public bool NetOn, DiskOn;
        public double DiskBusy;
        public double? Board, Ssd, FanCpu, FanSys;
        public string FansJson = "[]";
        public string TopJson = "[]";
        public string TopBrief = "(无归属)";
        public string TempSrc = "none";
        public bool Elevated;
        public int Cost;
    }

    // --------------------------------------------------------------- io
    /// <summary>
    /// Network throughput and disk activity from PDH raw counters, the same
    /// four counter paths the Python collector used.
    ///
    /// "Bytes .../sec" is a BULK_COUNT counter: its raw value is a running byte
    /// total, so the rate is a delta over wall clock. "% Disk Time" is a TIMER
    /// counter: its raw value is accumulated 100 ns busy time. Reading raw and
    /// dividing ourselves sidesteps the formatted-value bugs this machine shows
    /// on the WDDM counter set.
    /// </summary>
    internal sealed class IoSampler : IDisposable
    {
        private const string NET_RX = @"\Network Interface(*)\Bytes Received/sec";
        private const string NET_TX = @"\Network Interface(*)\Bytes Sent/sec";
        private const string DISK_R = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
        private const string DISK_W = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
        private const string DISK_T = @"\PhysicalDisk(_Total)\% Disk Time";

        // Virtual adapters would double-count the same packets when a VPN or
        // Hyper-V switch is up.
        private static readonly string[] Skip = { "loopback", "isatap", "teredo",
            "pseudo", "veth", "vmware", "virtualbox", "hyper-v", "wsl", "tap", "wintun" };

        private sealed class Counter
        {
            public IntPtr Handle;
            public string Path;
            public char Kind;
        }

        private IntPtr _query;
        private List<Counter> _counters = new List<Counter>();
        private Dictionary<string, long> _prev = new Dictionary<string, long>();
        private double _prevT;
        private readonly IntPtr _raw;

        public long Rx, Tx, DiskRead, DiskWrite;
        public double Busy;
        public string Error;

        public IoSampler()
        {
            _raw = Marshal.AllocHGlobal(64);
            Build();
        }

        public static string[] Expand(string path)
        {
            int need = 0;
            Native.PdhExpandWildCardPathW(null, path, null, ref need, 0);
            if (need <= 0) return new string[0];
            char[] buf = new char[need + 8];
            if (Native.PdhExpandWildCardPathW(null, path, buf, ref need, 0) != 0) return new string[0];
            List<string> list = new List<string>();
            // The buffer is a MULTI_SZ: several NUL-terminated paths, closed by a
            // final empty string. Truncating at the first NUL - as this used to -
            // left only the first adapter, and on a box whose Ethernet port was
            // unplugged that was the dead one, so the network graph read 0 while
            // the Wi-Fi carried all the traffic. Split the whole buffer instead;
            // the padding after the final NUL collapses into empty entries.
            string all = new string(buf);
            foreach (string p in all.Split('\0'))
                if (!String.IsNullOrEmpty(p)) list.Add(p);
            return list.ToArray();
        }

        private void Build()
        {
            IntPtr q;
            if (Native.PdhOpenQueryW(null, IntPtr.Zero, out q) != 0)
            {
                Error = "query-failed";
                return;
            }

            List<Counter> list = new List<Counter>();
            AddGroup(q, list, NET_RX, 'r');
            AddGroup(q, list, NET_TX, 't');

            // _Total is a fixed instance, so no wildcard expansion is needed.
            AddOne(q, list, DISK_R, 'R');
            AddOne(q, list, DISK_W, 'W');
            AddOne(q, list, DISK_T, 'B');

            if (list.Count == 0)
            {
                Native.PdhCloseQuery(q);
                Error = "no-counters";
                return;
            }

            _query = q;
            _counters = list;
            Native.PdhCollectQueryData(_query);
            _prevT = 0;
        }

        private static void AddGroup(IntPtr q, List<Counter> list, string path, char kind)
        {
            foreach (string p in Expand(path))
            {
                string lower = p.ToLowerInvariant();
                bool skip = false;
                foreach (string k in Skip)
                    if (lower.IndexOf(k, StringComparison.Ordinal) >= 0) { skip = true; break; }
                if (skip) continue;
                AddOne(q, list, p, kind);
            }
        }

        private static void AddOne(IntPtr q, List<Counter> list, string path, char kind)
        {
            IntPtr h;
            if (Native.PdhAddCounterW(q, path, IntPtr.Zero, out h) != 0) return;
            Counter c = new Counter();
            c.Handle = h;
            c.Path = path;
            c.Kind = kind;
            list.Add(c);
        }

        public void Sample()
        {
            if (_query == IntPtr.Zero) return;
            if (Native.PdhCollectQueryData(_query) != 0) return;

            double now = Clock.Now;
            double elapsed = _prevT > 0 ? now - _prevT : 0;
            if (elapsed < 0.05) elapsed = 0;

            Dictionary<string, long> fresh = new Dictionary<string, long>(_counters.Count);
            double rx = 0, tx = 0, dr = 0, dw = 0, busy = 0;

            foreach (Counter c in _counters)
            {
                if (Native.PdhGetRawCounterValue(c.Handle, IntPtr.Zero, _raw) != 0) continue;
                long raw = Marshal.ReadInt64(_raw, 16);   // PDH_RAW_COUNTER.FirstValue
                fresh[c.Path] = raw;
                if (elapsed <= 0) continue;

                long old;
                if (!_prev.TryGetValue(c.Path, out old)) continue;
                long delta = raw - old;
                if (delta < 0) delta = 0;

                switch (c.Kind)
                {
                    case 'r': rx += delta / elapsed; break;
                    case 't': tx += delta / elapsed; break;
                    case 'R': dr += delta / elapsed; break;
                    case 'W': dw += delta / elapsed; break;
                    case 'B': busy += 100.0 * delta / (elapsed * 1e7); break;
                }
            }

            _prev = fresh;
            _prevT = now;

            Rx = (long)rx;
            Tx = (long)tx;
            DiskRead = (long)dr;
            DiskWrite = (long)dw;
            Busy = Math.Round(Math.Min(busy, 100.0), 1);
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                foreach (Counter c in _counters) Native.PdhRemoveCounter(c.Handle);
                Native.PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
            if (_raw != IntPtr.Zero) Marshal.FreeHGlobal(_raw);
        }
    }

    // ---------------------------------------------------------- sys info
    /// <summary>
    /// The static facts the page shows once: names, core count, installed RAM.
    /// Read from the registry rather than WMI - a WMI query costs ~200 ms and
    /// starts a service, and none of this changes while the process is alive.
    /// </summary>
    internal static class SysInfo
    {
        private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        private const string OsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        private const string GpuClass =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static string Str(string subkey, string name)
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + subkey, name, null);
                return v == null ? "" : v.ToString();
            }
            catch { return ""; }
        }

        public static string CpuName()
        {
            string s = Str(CpuKey, "ProcessorNameString").Trim();
            return s.Length == 0 ? "CPU" : s;
        }

        public static int RatedClock()
        {
            int mhz;
            if (Int32.TryParse(Str(CpuKey, "~MHz"), out mhz)) return mhz;
            return 0;
        }

        public static string OsName()
        {
            string name = Str(OsKey, "ProductName");
            string build = Str(OsKey, "CurrentBuild");
            int no;
            // The registry still says "Windows 10" on build 22000 and later.
            if (Int32.TryParse(build, out no) && no >= 22000 &&
                name.StartsWith("Windows 10", StringComparison.Ordinal))
                name = "Windows 11" + name.Substring("Windows 10".Length);
            if (build.Length > 0) name = name + " (build " + build + ")";
            name = name.Trim();
            return name.Length == 0 ? "Windows" : name;
        }

        /// <summary>The discrete adapter with the most dedicated memory, and its size.</summary>
        public static void Gpu(out string name, out long vram)
        {
            name = "GPU";
            vram = 0;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(GpuClass))
                {
                    if (k == null) return;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        if (!sub.StartsWith("0", StringComparison.Ordinal)) continue;
                        using (RegistryKey d = k.OpenSubKey(sub))
                        {
                            if (d == null) continue;
                            object desc = d.GetValue("DriverDesc");
                            object size = d.GetValue("HardwareInformation.qwMemorySize");
                            if (desc == null || size == null) continue;
                            long v;
                            try { v = Convert.ToInt64(size); }
                            catch { continue; }
                            if (v > vram) { vram = v; name = desc.ToString(); }
                        }
                    }
                }
            }
            catch { }
        }

        public static int PhysicalCores()
        {
            // RelationProcessorCore = 0. The call then returns one record per
            // physical core, each starting with { Relationship, Size }.
            const int RelationProcessorCore = 0;
            uint len = 0;
            Native2.GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return 0;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!Native2.GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len))
                    return 0;
                int cores = 0;
                int off = 0;
                while (off + 8 <= (int)len)
                {
                    int rel = Marshal.ReadInt32(buf, off);
                    int size = Marshal.ReadInt32(buf, off + 4);
                    if (size < 8) break;          // malformed: stop instead of looping
                    if (rel == RelationProcessorCore) cores++;
                    off += size;
                }
                return cores;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    // ------------------------------------------------------------- vram
    /// <summary>
    /// Dedicated video memory in use. Unlike the byte counters above, this one
    /// is an instantaneous count, so no delta is taken - just the largest value
    /// across the adapter instances.
    /// </summary>
    internal sealed class VramSampler : IDisposable
    {
        private const string MEM_PATH = @"\GPU Adapter Memory(*)\Dedicated Usage";

        private IntPtr _query;
        private readonly List<IntPtr> _counters = new List<IntPtr>();
        private readonly IntPtr _raw;

        public long Used;

        public VramSampler()
        {
            _raw = Marshal.AllocHGlobal(64);
            string[] paths = IoSampler.Expand(MEM_PATH);
            if (paths.Length == 0) return;

            IntPtr q;
            if (Native.PdhOpenQueryW(null, IntPtr.Zero, out q) != 0) return;
            foreach (string p in paths)
            {
                IntPtr h;
                if (Native.PdhAddCounterW(q, p, IntPtr.Zero, out h) == 0) _counters.Add(h);
            }
            if (_counters.Count == 0)
            {
                Native.PdhCloseQuery(q);
                return;
            }
            _query = q;
            Native.PdhCollectQueryData(_query);
        }

        public void Sample()
        {
            if (_query == IntPtr.Zero) return;
            if (Native.PdhCollectQueryData(_query) != 0) return;
            long best = 0;
            foreach (IntPtr h in _counters)
            {
                if (Native.PdhGetRawCounterValue(h, IntPtr.Zero, _raw) != 0) continue;
                long v = Marshal.ReadInt64(_raw, 16);
                if (v > best) best = v;
            }
            Used = best;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                foreach (IntPtr h in _counters) Native.PdhRemoveCounter(h);
                Native.PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
            if (_raw != IntPtr.Zero) Marshal.FreeHGlobal(_raw);
        }
    }

    // ------------------------------------------------------------ clocks
    /// <summary>
    /// Effective clock per processor. CallNtPowerInformation(11) is the only
    /// interface that reports the boost clock actually reached; the registry
    /// only knows the rated base clock.
    /// </summary>
    internal static class CpuClock
    {
        private const int ProcessorInformation = 11;

        public static void Read(out int current, out int rated)
        {
            current = 0;
            rated = 0;
            int n = Environment.ProcessorCount;
            if (n <= 0) return;
            int stride = Marshal.SizeOf(typeof(PROCESSOR_POWER_INFORMATION));
            int size = stride * n;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Native2.CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buf, size) != 0)
                    return;
                for (int i = 0; i < n; i++)
                {
                    int cur = Marshal.ReadInt32(buf, i * stride + 8);   // CurrentMhz
                    int mx = Marshal.ReadInt32(buf, i * stride + 4);    // MaxMhz
                    if (cur > current) current = cur;
                    if (mx > rated) rated = mx;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    // ---------------------------------------------------------- processes
    /// <summary>
    /// Top processes by CPU, aggregated by executable name the way the Python
    /// collector did: a browser's 30 helper processes read as one "msedge".
    ///
    /// Percent is machine-wide (divided by the logical processor count), which
    /// matches the CPU figure above it in the panel - not the per-core scale
    /// Task Manager uses. Walking the table costs a few ms, so it only runs
    /// every third tick.
    /// </summary>
    internal sealed class ProcSampler
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint QUERY_LIMITED = 0x1000;
        private const int MaxRows = 24;

        private readonly int _logical;
        private Dictionary<string, double> _prev = new Dictionary<string, double>();
        private double _stamp;

        public string Json = "[]";
        /// <summary>First five rows as "name 42%" - the spike log uses this
        /// directly instead of re-parsing the JSON it just built.</summary>
        public string Brief = "";
        public int Count;

        public ProcSampler(int logical)
        {
            _logical = Math.Max(1, logical);
        }

        private sealed class Row
        {
            public string Name;
            public double Sec;
            public double Mem;
        }

        public void Sample()
        {
            Dictionary<string, Row> agg = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            IntPtr snap = Native2.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return;

            try
            {
                PROCESSENTRY32W entry = new PROCESSENTRY32W();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                bool ok = Native2.Process32FirstW(snap, ref entry);
                while (ok)
                {
                    // Copy both fields BEFORE advancing: Process32NextW refills
                    // `entry`, so reading szExeFile afterwards pairs this pid's
                    // CPU time with the next process's name.
                    uint pid = entry.th32ProcessID;
                    string name = entry.szExeFile;
                    ok = Native2.Process32NextW(snap, ref entry);
                    if (pid <= 4) continue;

                    IntPtr h = Native2.OpenProcess(QUERY_LIMITED, false, pid);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        if (name != null && name.Length > 4 &&
                            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name = name.Substring(0, name.Length - 4);
                        if (name == null) name = "?";

                        Row slot;
                        if (!agg.TryGetValue(name, out slot))
                        {
                            slot = new Row();
                            slot.Name = name;
                            agg[name] = slot;
                        }

                        FILETIME c, e, k, u;
                        if (Native2.GetProcessTimes(h, out c, out e, out k, out u))
                        {
                            long kern = ((long)k.High << 32) | k.Low;
                            long user = ((long)u.High << 32) | u.Low;
                            slot.Sec += (kern + user) / 1e7;      // 100 ns -> seconds
                        }

                        PROCESS_MEMORY_COUNTERS pmc = new PROCESS_MEMORY_COUNTERS();
                        pmc.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS));
                        if (Native2.GetProcessMemoryInfo(h, ref pmc, pmc.cb))
                            slot.Mem += pmc.WorkingSetSize.ToUInt64();
                    }
                    catch { }
                    finally { Native.CloseHandle(h); }
                }
            }
            finally { Native.CloseHandle(snap); }

            double now = Clock.Now;
            double elapsed = _stamp > 0 ? now - _stamp : 0;
            _stamp = now;

            if (elapsed <= 0)
            {
                foreach (Row r in agg.Values) _prev[r.Name] = r.Sec;
                return;
            }

            List<Row> rows = new List<Row>(agg.Count);
            Dictionary<string, double> nextPrev = new Dictionary<string, double>(agg.Count);
            foreach (Row r in agg.Values)
            {
                // Snapshot the cumulative seconds *before* overwriting the field:
                // _prev has to hold totals, or the next delta would be nonsense.
                nextPrev[r.Name] = r.Sec;
                double prev;
                if (!_prev.TryGetValue(r.Name, out prev)) prev = r.Sec;
                double spent = r.Sec - prev;
                if (spent < 0) spent = 0;
                r.Sec = Math.Min(100.0, 100.0 * spent / (elapsed * _logical));
                rows.Add(r);
            }
            _prev = nextPrev;

            rows.Sort(delegate(Row a, Row b)
            {
                int byCpu = b.Sec.CompareTo(a.Sec);
                return byCpu != 0 ? byCpu : b.Mem.CompareTo(a.Mem);
            });

            Count = rows.Count;
            int take = Math.Min(MaxRows, rows.Count);
            StringBuilder sb = new StringBuilder(take * 48 + 4);
            StringBuilder brief = new StringBuilder(120);
            sb.Append('[');
            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(',');
                Row r = rows[i];
                sb.Append("{\"n\":\"").Append(Wire.Escape(r.Name)).Append("\",\"u\":")
                  .Append(Wire.Num(r.Sec, "0.0")).Append(",\"m\":")
                  .Append(((long)r.Mem).ToString(CultureInfo.InvariantCulture)).Append('}');
                if (i < 5)
                {
                    if (i > 0) brief.Append(", ");
                    brief.Append(r.Name).Append(' ').Append(Math.Round(r.Sec).ToString("0", CultureInfo.InvariantCulture)).Append('%');
                }
            }
            sb.Append(']');
            Json = sb.ToString();
            Brief = brief.Length == 0 ? "(无归属)" : brief.ToString();
        }
    }

    // ----------------------------------------------------------- history
    /// <summary>
    /// The 900-row trend ring plus its on-disk mirror.
    ///
    /// Rows are fixed-size structs, not sample objects: 900 x 96 bytes is 86 KB
    /// instead of the ~6 MB the Python deque of dicts cost. The chart only ever
    /// needs these sixteen numbers, so nothing else is retained - per-core loads
    /// and the process table live exactly one tick.
    /// </summary>
    internal sealed class HistoryStore
    {
        public const int Max = 900;
        public const double Gap = 5.0;
        private const int KeepDays = 14;

        private struct Row
        {
            public long T;
            public float Cu, Ct, Cc, Cp;
            public float Gu, Gt, Gh, Gp;
            public long Vu, Vt, Mu, Mt;
            // Nullable: rows written before the network/disk sampler existed
            // have no nr/nt/dr/dw at all. Zero would draw them as a flat line
            // pinned to the floor; null leaves a gap, which is what really
            // happened.
            public long? Nr, Nt, Dr, Dw;
            public float Db;
        }

        private readonly Row[] _rows = new Row[Max];
        private int _head;
        private int _count;
        private readonly object _gate = new object();

        private readonly string _dataDir;
        private readonly object _fileGate = new object();
        private double _lastWrite;
        private string _day;
        private string _prunedDay;
        private StreamWriter _fh;

        // Running totals for today's summary, rebuilt from disk on start.
        private readonly object _sumGate = new object();
        private string _sumDay;
        private int _n;
        private double? _cpuTemp, _gpuTemp, _cpuLoad, _gpuLoad;
        private long _cpuTempT, _gpuTempT, _cpuLoadT, _gpuLoadT;
        private double _cpuW, _gpuW;
        private int _cpuWN, _gpuWN;
        private int _busy, _run, _runBest;
        private long _firstT, _lastT;

        public int Primed;
        public int Restored;

        public HistoryStore(string dataDir)
        {
            _dataDir = dataDir;
            _sumDay = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------- append
        /// <summary>Store one tick. Returns the compact on-disk row if it was due.</summary>
        public bool Add(PanelSample s)
        {
            Row r = new Row();
            r.T = s.T;
            r.Cu = (float)s.CpuPct;
            r.Ct = F(s.CpuTemp);
            r.Cc = (float)s.CpuClk;
            r.Cp = F(s.CpuW);
            r.Gu = (float)s.GpuPct;
            r.Gt = F(s.GpuTemp);
            r.Gh = F(s.GpuHot);
            r.Gp = F(s.GpuW);
            r.Vu = s.VramUsed;
            r.Vt = s.VramTotal;
            r.Mu = s.MemUsed;
            r.Mt = s.MemTotal;
            r.Nr = s.NetOn ? s.NetRx : (long?)null;
            r.Nt = s.NetOn ? s.NetTx : (long?)null;
            r.Dr = s.DiskOn ? s.DiskRead : (long?)null;
            r.Dw = s.DiskOn ? s.DiskWrite : (long?)null;
            r.Db = s.DiskOn ? (float)s.DiskBusy : Single.NaN;

            lock (_gate)
            {
                _rows[_head] = r;
                _head = (_head + 1) % Max;
                if (_count < Max) _count++;
            }

            double now = Clock.Now;
            bool due;
            lock (_fileGate)
            {
                due = now - _lastWrite >= Gap;
                if (due) _lastWrite = now;
            }
            if (due) Persist(r);
            return due;
        }

        private static float F(double? v)
        {
            return v == null ? Single.NaN : (float)v.Value;
        }

        // ------------------------------------------------------------ disk
        private string PathFor(string day)
        {
            return Path.Combine(_dataDir, "history-" + day + ".ndjson");
        }

        private void Persist(Row r)
        {
            try
            {
                string day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (_fh == null || day != _day)
                {
                    if (_fh != null) { try { _fh.Close(); } catch { } _fh = null; }
                    if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
                    _day = day;
                    _fh = new StreamWriter(new FileStream(PathFor(day), FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    _fh.AutoFlush = true;
                    // Midnight is the only moment a new file appears, so it is
                    // also the only moment the retention window can have moved.
                    // Pruning here means a widget that never restarts still
                    // stops accumulating old files.
                    if (_prunedDay != day) { _prunedDay = day; Prune(); }
                }
                _fh.WriteLine(RowJson(r));
                SummaryAdd(r);
            }
            catch { }
        }

        private static string RowJson(Row r)
        {
            StringBuilder sb = new StringBuilder(220);
            sb.Append("{\"t\":").Append(r.T.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"cu\":").Append((int)r.Cu);
            sb.Append(",\"ct\":").Append(J(r.Ct));
            sb.Append(",\"cc\":").Append((int)r.Cc);
            sb.Append(",\"cp\":").Append(J(r.Cp));
            sb.Append(",\"gu\":").Append((int)r.Gu);
            sb.Append(",\"gt\":").Append(J(r.Gt));
            sb.Append(",\"gh\":").Append(J(r.Gh));
            sb.Append(",\"gp\":").Append(J(r.Gp));
            sb.Append(",\"vu\":").Append(r.Vu.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"vt\":").Append(r.Vt.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mu\":").Append(r.Mu.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mt\":").Append(r.Mt.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"nr\":").Append(I(r.Nr));
            sb.Append(",\"nt\":").Append(I(r.Nt));
            sb.Append(",\"dr\":").Append(I(r.Dr));
            sb.Append(",\"dw\":").Append(I(r.Dw));
            sb.Append(",\"db\":").Append(J(r.Db));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Two-decimal float for the file, null when the sensor was absent.</summary>
        private static string J(float v)
        {
            if (Single.IsNaN(v)) return "null";
            return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Integer for the file, null when the field was not sampled.</summary>
        private static string I(long? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "null";
        }

        private static long? ParseLong(string line, string key)
        {
            string raw = Wire.Scalar(line, key);
            if (raw == null) return null;
            long v;
            if (!Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return null;
            return v;
        }

        // ------------------------------------------------------------ prime
        /// <summary>
        /// Reload the newest rows from yesterday and today so a restart keeps
        /// the chart instead of starting at an empty grid.
        /// </summary>
        public void Prime()
        {
            List<Row> rows = new List<Row>();
            for (int back = 1; back >= 0; back--)
            {
                string day = DateTime.Now.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string path = PathFor(day);
                if (!File.Exists(path)) continue;
                try
                {
                    // Only the tail matters; a day is ~17k rows.
                    string[] lines;
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        List<string> keep = new List<string>();
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            keep.Add(line);
                            if (keep.Count > Max) keep.RemoveAt(0);
                        }
                        lines = keep.ToArray();
                    }
                    foreach (string line in lines)
                    {
                        Row r;
                        if (ParseRow(line, out r)) rows.Add(r);
                    }
                }
                catch { }
            }

            if (rows.Count == 0) return;
            if (rows.Count > Max) rows.RemoveRange(0, rows.Count - Max);

            lock (_gate)
            {
                _head = 0;
                _count = 0;
                foreach (Row r in rows)
                {
                    _rows[_head] = r;
                    _head = (_head + 1) % Max;
                    if (_count < Max) _count++;
                }
            }
            Primed = rows.Count;
        }

        /// <summary>
        /// Read one on-disk row. Every value in these rows is a flat scalar, so
        /// a keyed scan is enough - no need for a general JSON parser.
        /// </summary>
        private static bool ParseRow(string line, out Row r)
        {
            r = new Row();
            long t;
            if (!Wire.TryLong(line, "t", out t)) return false;
            r.T = t;
            r.Cu = (float)Wire.Val(line, "cu", 0);
            r.Ct = (float)Wire.ValOrNaN(line, "ct");
            r.Cc = (float)Wire.Val(line, "cc", 0);
            r.Cp = (float)Wire.ValOrNaN(line, "cp");
            r.Gu = (float)Wire.Val(line, "gu", 0);
            r.Gt = (float)Wire.ValOrNaN(line, "gt");
            r.Gh = (float)Wire.ValOrNaN(line, "gh");
            r.Gp = (float)Wire.ValOrNaN(line, "gp");
            r.Vu = Wire.ValLong(line, "vu", 0);
            r.Vt = Wire.ValLong(line, "vt", 0);
            r.Mu = Wire.ValLong(line, "mu", 0);
            r.Mt = Wire.ValLong(line, "mt", 0);
            r.Nr = ParseLong(line, "nr");
            r.Nt = ParseLong(line, "nt");
            r.Dr = ParseLong(line, "dr");
            r.Dw = ParseLong(line, "dw");
            r.Db = (float)Wire.ValOrNaN(line, "db");
            return true;
        }

        /// <summary>Rebuild today's summary from the file (called once at start).</summary>
        public void RebuildSummary()
        {
            string path = PathFor(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!File.Exists(path)) return;
            int n = 0;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        Row r;
                        if (ParseRow(line, out r)) { SummaryAdd(r); n++; }
                    }
                }
            }
            catch { }
            Restored = n;
        }

        /// <summary>Drop history files older than the retention window.</summary>
        public void Prune()
        {
            try
            {
                if (!Directory.Exists(_dataDir)) return;
                string cutoff = DateTime.Now.AddDays(-KeepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                foreach (string path in Directory.GetFiles(_dataDir, "history-*.ndjson"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name.Length <= 8) continue;
                    string day = name.Substring(8);
                    if (String.CompareOrdinal(day, cutoff) < 0)
                    {
                        try { File.Delete(path); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public long FileBytes()
        {
            try
            {
                string path = PathFor(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                FileInfo fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        // ---------------------------------------------------------- summary
        private void SummaryAdd(Row r)
        {
            lock (_sumGate)
            {
                string day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (day != _sumDay)
                {
                    _sumDay = day;
                    _n = 0; _cpuTemp = null; _gpuTemp = null; _cpuLoad = null; _gpuLoad = null;
                    _cpuW = 0; _gpuW = 0; _cpuWN = 0; _gpuWN = 0;
                    _busy = 0; _run = 0; _runBest = 0; _firstT = 0; _lastT = 0;
                }
                _n++;
                if (_firstT == 0) _firstT = r.T;
                _lastT = r.T;

                if (!Single.IsNaN(r.Ct) && r.Ct > 0 && (_cpuTemp == null || r.Ct > _cpuTemp)) { _cpuTemp = r.Ct; _cpuTempT = r.T; }
                if (!Single.IsNaN(r.Gt) && r.Gt > 0 && (_gpuTemp == null || r.Gt > _gpuTemp)) { _gpuTemp = r.Gt; _gpuTempT = r.T; }
                if (r.Cu > 0 && (_cpuLoad == null || r.Cu > _cpuLoad)) { _cpuLoad = r.Cu; _cpuLoadT = r.T; }
                if (r.Gu > 0 && (_gpuLoad == null || r.Gu > _gpuLoad)) { _gpuLoad = r.Gu; _gpuLoadT = r.T; }

                if (!Single.IsNaN(r.Cp) && r.Cp > 0) { _cpuW += r.Cp; _cpuWN++; }
                if (!Single.IsNaN(r.Gp) && r.Gp > 0) { _gpuW += r.Gp; _gpuWN++; }

                bool hot = r.Cu >= 80 || r.Gu >= 80;
                if (hot)
                {
                    _busy++;
                    _run++;
                    if (_run > _runBest) _runBest = _run;
                }
                else _run = 0;
            }
        }

        public string SummaryJson()
        {
            lock (_sumGate)
            {
                StringBuilder sb = new StringBuilder(240);
                sb.Append("{\"day\":\"").Append(_sumDay).Append("\"");
                sb.Append(",\"samples\":").Append(_n);
                sb.Append(",\"gap\":").Append(Gap.ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(",\"from\":").Append(_n > 0 ? _firstT.ToString(CultureInfo.InvariantCulture) : "null");
                sb.Append(",\"to\":").Append(_n > 0 ? _lastT.ToString(CultureInfo.InvariantCulture) : "null");
                // A missing temperature must stay null all the way out. Coalescing
                // it to 0 here made the summary claim "max 0°C" on a machine with
                // no sensor library - a number that is wrong beats no number.
                sb.Append(",\"cpuTemp\":{\"max\":").Append(_cpuTemp == null ? "null" : Math.Round(_cpuTemp.Value, 1).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(",\"at\":").Append(_cpuTemp == null ? "null" : _cpuTempT.ToString(CultureInfo.InvariantCulture)).Append('}');
                sb.Append(",\"gpuTemp\":{\"max\":").Append(_gpuTemp == null ? "null" : Math.Round(_gpuTemp.Value, 1).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(",\"at\":").Append(_gpuTemp == null ? "null" : _gpuTempT.ToString(CultureInfo.InvariantCulture)).Append('}');
                sb.Append(",\"cpuLoad\":{\"max\":").Append(Math.Round(_cpuLoad ?? 0, 1).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(",\"at\":").Append(_cpuLoad == null ? "null" : _cpuLoadT.ToString(CultureInfo.InvariantCulture)).Append('}');
                sb.Append(",\"gpuLoad\":{\"max\":").Append(Math.Round(_gpuLoad ?? 0, 1).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(",\"at\":").Append(_gpuLoad == null ? "null" : _gpuLoadT.ToString(CultureInfo.InvariantCulture)).Append('}');
                sb.Append(",\"busyMin\":").Append(Math.Round(_busy * Gap / 60.0, 1).ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(",\"busyRunMin\":").Append(Math.Round(_runBest * Gap / 60.0, 1).ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(",\"avgCpuW\":").Append(_cpuWN == 0 ? "null" : Math.Round(_cpuW / _cpuWN, 1).ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(",\"avgGpuW\":").Append(_gpuWN == 0 ? "null" : Math.Round(_gpuW / _gpuWN, 1).ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(",\"bytes\":").Append(FileBytes().ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------ charts
        /// <summary>
        /// The chart-ready array. Historical rows carry no per-core loads and no
        /// process table - they are not persisted, and the chart does not need
        /// them. The final entry is replaced by the live sample so the page's
        /// first paint shows real per-core bars instead of an empty row.
        /// </summary>
        public string HistoryJson(string liveJson)
        {
            Row[] copy = new Row[Max];
            int n;
            lock (_gate)
            {
                n = _count;
                int start = (_head - _count + Max) % Max;
                for (int i = 0; i < n; i++) copy[i] = _rows[(start + i) % Max];
            }

            bool live = !String.IsNullOrEmpty(liveJson) && liveJson != "null" && n > 0;
            StringBuilder sb = new StringBuilder(n * 300 + 32);
            sb.Append('[');
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                if (live && i == n - 1) sb.Append(liveJson);
                else sb.Append(HistRowJson(copy[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string HistRowJson(Row r)
        {
            StringBuilder sb = new StringBuilder(280);
            sb.Append("{\"type\":\"sample\",\"hist\":true,\"t\":").Append(r.T.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"cpu\":{\"u\":").Append((int)r.Cu).Append(",\"cores\":[],\"clk\":")
              .Append((int)r.Cc).Append(",\"temp\":").Append(J2(r.Ct))
              .Append(",\"power\":").Append(J2(r.Cp)).Append('}');
            sb.Append(",\"gpu\":{\"u\":").Append((int)r.Gu).Append(",\"temp\":").Append(J2(r.Gt))
              .Append(",\"hotspot\":").Append(J2(r.Gh)).Append(",\"power\":").Append(J2(r.Gp))
              .Append(",\"vramUsed\":").Append(r.Vu.ToString(CultureInfo.InvariantCulture))
              .Append(",\"vramTotal\":").Append(r.Vt.ToString(CultureInfo.InvariantCulture))
              .Append(",\"eng\":{}}");
            sb.Append(",\"mem\":{\"used\":").Append(r.Mu.ToString(CultureInfo.InvariantCulture))
              .Append(",\"total\":").Append(r.Mt.ToString(CultureInfo.InvariantCulture)).Append('}');
            sb.Append(",\"net\":{\"rx\":").Append(I(r.Nr))
              .Append(",\"tx\":").Append(I(r.Nt)).Append('}');
            sb.Append(",\"disk\":{\"r\":").Append(I(r.Dr))
              .Append(",\"w\":").Append(I(r.Dw))
              .Append(",\"busy\":").Append(J2(r.Db)).Append('}');
            sb.Append(",\"aux\":{},\"top\":[],\"tempSrc\":\"hist\",\"elevated\":false,\"cost\":0}");
            return sb.ToString();
        }

        /// <summary>1-decimal value for the chart payload.</summary>
        private static string J2(float v)
        {
            if (Single.IsNaN(v)) return "null";
            return Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
        }

        // ----------------------------------------------------------- archive
        /// <summary>
        /// Down-sampled view over the on-disk archive. "1h"/"6h"/"24h"/"7d"
        /// reduce the window to at most 720 buckets, so a week of 5-second
        /// rows (about 121k lines) still arrives as a chart-sized array.
        ///
        /// The in-memory ring only holds 900 ticks (75 minutes at the default
        /// cadence); before this existed the other thirteen days of data were
        /// written and never read. Unknown ranges fall back to the ring.
        /// </summary>
        public string ArchiveJson(string range)
        {
            double hours;
            if (!RangeHours(range, out hours)) return HistoryJson(null);

            long now = NowMs();
            long from = now - (long)(hours * 3600000.0);
            const int Buckets = 720;
            const int F = 17;
            double bucketMs = Math.Max(1000.0, (now - from) / (double)Buckets);

            double[,] sum = new double[Buckets, F];
            int[,] cnt = new int[Buckets, F];
            int[] rows = new int[Buckets];
            long[] first = new long[Buckets];

            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime startDay = epoch.AddMilliseconds(from).ToLocalTime().Date;
            DateTime today = DateTime.Now.Date;
            for (DateTime d = startDay; d <= today; d = d.AddDays(1))
            {
                string path = PathFor(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (!File.Exists(path)) continue;
                ScanDay(path, from, now, bucketMs, Buckets, sum, cnt, rows, first);
            }

            StringBuilder sb = new StringBuilder(Buckets * 220);
            sb.Append('[');
            bool started = false;
            for (int b = 0; b < Buckets; b++)
            {
                if (rows[b] == 0) continue;
                Row r = new Row();
                r.T = first[b];
                r.Cu = (float)V(sum[b, 0], cnt[b, 0]);
                r.Ct = (float)V(sum[b, 1], cnt[b, 1]);
                r.Cc = (float)V(sum[b, 2], cnt[b, 2]);
                r.Cp = (float)V(sum[b, 3], cnt[b, 3]);
                r.Gu = (float)V(sum[b, 4], cnt[b, 4]);
                r.Gt = (float)V(sum[b, 5], cnt[b, 5]);
                r.Gh = (float)V(sum[b, 6], cnt[b, 6]);
                r.Gp = (float)V(sum[b, 7], cnt[b, 7]);
                r.Vu = (long)V(sum[b, 8], cnt[b, 8]);
                r.Vt = (long)V(sum[b, 9], cnt[b, 9]);
                r.Mu = (long)V(sum[b, 10], cnt[b, 10]);
                r.Mt = (long)V(sum[b, 11], cnt[b, 11]);
                r.Nr = cnt[b, 12] == 0 ? (long?)null : (long)(sum[b, 12] / cnt[b, 12]);
                r.Nt = cnt[b, 13] == 0 ? (long?)null : (long)(sum[b, 13] / cnt[b, 13]);
                r.Dr = cnt[b, 14] == 0 ? (long?)null : (long)(sum[b, 14] / cnt[b, 14]);
                r.Dw = cnt[b, 15] == 0 ? (long?)null : (long)(sum[b, 15] / cnt[b, 15]);
                r.Db = (float)V(sum[b, 16], cnt[b, 16]);

                if (started) sb.Append(',');
                started = true;
                sb.Append(HistRowJson(r));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static double V(double s, int n)
        {
            return n == 0 ? Double.NaN : s / n;
        }

        private static void ScanDay(string path, long from, long now, double bucketMs, int buckets,
                                    double[,] sum, int[,] cnt, int[] rows, long[] first)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length < 8) continue;
                        // Cheap leading-timestamp test before the full parse:
                        // a 7-day window touches ~120k rows and most of the
                        // old-day file is outside the requested window.
                        long t = LeadingT(line);
                        if (t < from || t > now) continue;

                        Row r;
                        if (!ParseRow(line, out r)) continue;
                        int b = (int)((r.T - from) / bucketMs);
                        if (b < 0 || b >= buckets) continue;
                        if (rows[b] == 0) first[b] = r.T;
                        rows[b]++;

                        if (!Single.IsNaN(r.Cu)) { sum[b, 0] += r.Cu; cnt[b, 0]++; }
                        if (!Single.IsNaN(r.Ct)) { sum[b, 1] += r.Ct; cnt[b, 1]++; }
                        if (!Single.IsNaN(r.Cc)) { sum[b, 2] += r.Cc; cnt[b, 2]++; }
                        if (!Single.IsNaN(r.Cp)) { sum[b, 3] += r.Cp; cnt[b, 3]++; }
                        if (!Single.IsNaN(r.Gu)) { sum[b, 4] += r.Gu; cnt[b, 4]++; }
                        if (!Single.IsNaN(r.Gt)) { sum[b, 5] += r.Gt; cnt[b, 5]++; }
                        if (!Single.IsNaN(r.Gh)) { sum[b, 6] += r.Gh; cnt[b, 6]++; }
                        if (!Single.IsNaN(r.Gp)) { sum[b, 7] += r.Gp; cnt[b, 7]++; }
                        sum[b, 8] += r.Vu; cnt[b, 8]++;
                        sum[b, 9] += r.Vt; cnt[b, 9]++;
                        sum[b, 10] += r.Mu; cnt[b, 10]++;
                        sum[b, 11] += r.Mt; cnt[b, 11]++;
                        if (r.Nr != null) { sum[b, 12] += r.Nr.Value; cnt[b, 12]++; }
                        if (r.Nt != null) { sum[b, 13] += r.Nt.Value; cnt[b, 13]++; }
                        if (r.Dr != null) { sum[b, 14] += r.Dr.Value; cnt[b, 14]++; }
                        if (r.Dw != null) { sum[b, 15] += r.Dw.Value; cnt[b, 15]++; }
                        if (!Single.IsNaN(r.Db)) { sum[b, 16] += r.Db; cnt[b, 16]++; }
                    }
                }
            }
            catch { }
        }

        private static long LeadingT(string line)
        {
            int i = line.IndexOf(':');
            if (i < 0) return 0;
            long v = 0;
            int n = 0;
            for (i++; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch < '0' || ch > '9') break;
                v = v * 10 + (ch - '0');
                n++;
            }
            return n > 0 ? v : 0;
        }

        private static bool RangeHours(string range, out double hours)
        {
            hours = 0;
            if (String.IsNullOrEmpty(range) || range.Length < 2) return false;
            string unit = range.Substring(range.Length - 1).ToLowerInvariant();
            double n;
            if (!Double.TryParse(range.Substring(0, range.Length - 1), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out n)) return false;
            if (n <= 0) return false;
            if (unit == "h") hours = n;
            else if (unit == "d") hours = n * 24;
            else if (unit == "m") hours = n / 60.0;
            else return false;
            double cap = KeepDays * 24;
            if (hours > cap) hours = cap;
            return true;
        }

        private static long NowMs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }
    }

    // --------------------------------------------------------- spike log
    /// <summary>
    /// One line per CPU spike, at most every 10 s, naming the top five
    /// processes of that moment - so "刚才怎么又飙了" is answerable from the
    /// file instead of guesswork.
    /// </summary>
    internal static class SpikeLog
    {
        private static double _last;
        private static readonly object Gate = new object();

        public static void Maybe(PanelSample s, string dataDir)
        {
            if (s.CpuPct < 70) return;
            lock (Gate)
            {
                double now = Clock.Now;
                if (now - _last < 10) return;
                _last = now;
            }
            try
            {
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                string path = Path.Combine(dataDir,
                    "spikes-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                StringBuilder sb = new StringBuilder(180);
                sb.Append(DateTime.Now.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.Append("  CPU ").Append(s.CpuPct).Append("%  GPU ").Append(s.GpuPct).Append("%  ");
                sb.Append(String.IsNullOrEmpty(s.TopBrief) ? "(无归属)" : s.TopBrief);
                sb.Append('\n');
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------- wire
    /// <summary>
    /// String helpers for the JSON this assembly emits and re-reads. Hand
    /// written on purpose: every payload has a fixed shape, and a reflective
    /// serialiser would cost more than the ring buffer it is feeding.
    /// </summary>
    internal static class Wire
    {
        public static string Num(double v, string fmt)
        {
            if (Double.IsNaN(v) || Double.IsInfinity(v)) return "null";
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        public static string Opt(double? v, string fmt)
        {
            if (v == null) return "null";
            return Num(v.Value, fmt);
        }

        public static string Escape(string s)
        {
            if (String.IsNullOrEmpty(s)) return "";
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string rep = null;
                if (c == '"') rep = "\\\"";
                else if (c == '\\') rep = "\\\\";
                else if (c == '\n') rep = "\\n";
                else if (c == '\r') rep = "\\r";
                else if (c == '\t') rep = "\\t";
                else if (c < ' ') rep = "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);
                if (rep != null)
                {
                    if (sb == null) { sb = new StringBuilder(s.Length + 8); sb.Append(s, 0, i); }
                    sb.Append(rep);
                }
                else if (sb != null) sb.Append(c);
            }
            return sb == null ? s : sb.ToString();
        }

        /// <summary>Raw text of `"key": value` in a flat object, null when absent or null.</summary>
        public static string Scalar(string line, string key)
        {
            if (line == null) return null;
            string pat = "\"" + key + "\":";
            int at = line.IndexOf(pat, StringComparison.Ordinal);
            if (at < 0) return null;
            int i = at + pat.Length;
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length) return null;
            if (line[i] == 'n') return null;                 // null literal
            if (line[i] == '"')
            {
                int e = line.IndexOf('"', i + 1);
                if (e < 0) return null;
                return line.Substring(i + 1, e - i - 1);
            }
            int s = i;
            while (i < line.Length && line[i] != ',' && line[i] != '}') i++;
            return line.Substring(s, i - s).Trim();
        }

        public static bool TryLong(string line, string key, out long v)
        {
            v = 0;
            string raw = Scalar(line, key);
            if (raw == null) return false;
            return Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        public static long ValLong(string line, string key, long fallback)
        {
            long v;
            return TryLong(line, key, out v) ? v : fallback;
        }

        public static double Val(string line, string key, double fallback)
        {
            string raw = Scalar(line, key);
            if (raw == null) return fallback;
            double v;
            return Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static double ValOrNaN(string line, string key)
        {
            string raw = Scalar(line, key);
            if (raw == null) return Double.NaN;
            double v;
            return Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : Double.NaN;
        }
    }

    // ------------------------------------------------------------- http
    /// <summary>
    /// The dashboard: static files plus seven endpoints over a bare TcpListener,
    /// following the pattern TempsServer already proved on this machine.
    ///
    /// HTTP/1.0 with Connection: close is deliberate. Keep-alive would mean a
    /// per-connection state machine and pipelining rules for no real gain - the
    /// page pulls four files once and then holds a single SSE stream open.
    /// </summary>
    internal sealed class PanelServer : IDisposable
    {
        private sealed class Sse
        {
            public Stream Stream;
            public readonly Queue<string> Queue = new Queue<string>(32);
            public volatile bool Dead;
        }

        private System.Net.Sockets.TcpListener _listener;
        private int _port;
        private readonly bool _lan;
        private readonly string _webDir;
        private readonly HistoryStore _hist;
        private readonly object _subGate = new object();
        private readonly List<Sse> _subs = new List<Sse>();

        private volatile string _infoJson = "null";
        private volatile string _sampleJson = "null";
        private volatile int _interval = 1000;

        public string Error;

        public PanelServer(int port, bool lan, string webDir, HistoryStore hist)
        {
            _port = port;
            _lan = lan;
            _webDir = webDir;
            _hist = hist;
        }

        public int Port { get { return _port; } }

        public void SetInfo(string json) { _infoJson = json; }
        public void SetInterval(int ms) { _interval = ms; }

        public bool Start()
        {
            for (int i = 0; i < 30; i++)
            {
                int port = _port + i;
                try
                {
                    System.Net.IPAddress addr = _lan ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback;
                    _listener = new System.Net.Sockets.TcpListener(addr, port);
                    _listener.Start();
                    _port = port;
                    Thread t = new Thread(Accept);
                    t.IsBackground = true;
                    t.Name = "panel-http";
                    t.Start();
                    Log.Write("panel on " + (_lan ? "0.0.0.0:" : "127.0.0.1:") + port);
                    return true;
                }
                catch
                {
                    _listener = null;
                }
            }
            Error = "no-free-port";
            Log.Write("panel: no free port from " + _port);
            return false;
        }

        private void Accept()
        {
            while (_listener != null)
            {
                System.Net.Sockets.TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { break; }
                try
                {
                    Thread t = new Thread(Handle);
                    t.IsBackground = true;
                    t.Name = "panel-conn";
                    t.Start(c);
                }
                catch { try { c.Close(); } catch { } }
            }
        }

        // --------------------------------------------------------- publish
        /// <summary>Serialise one tick and fan it out to every open stream.</summary>
        public void Publish(PanelSample s)
        {
            string json = BuildSample(s);
            _sampleJson = json;
            lock (_subGate)
            {
                for (int i = _subs.Count - 1; i >= 0; i--)
                {
                    Sse sub = _subs[i];
                    if (sub.Dead) { _subs.RemoveAt(i); continue; }
                    lock (sub.Queue)
                    {
                        // A sleeping phone cannot be allowed to grow the queue
                        // without bound: drop the oldest tick instead.
                        if (sub.Queue.Count >= 64) sub.Queue.Dequeue();
                        sub.Queue.Enqueue(json);
                    }
                }
            }
        }

        private static string BuildSample(PanelSample s)
        {
            StringBuilder sb = new StringBuilder(1400);
            sb.Append("{\"type\":\"sample\",\"t\":").Append(s.T.ToString(CultureInfo.InvariantCulture));

            sb.Append(",\"cpu\":{\"u\":").Append(s.CpuPct).Append(",\"cores\":[");
            for (int i = 0; i < s.Cores.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append((int)Math.Round(s.Cores[i]));
            }
            sb.Append("],\"clk\":").Append(s.CpuClk)
              .Append(",\"temp\":").Append(Wire.Opt(s.CpuTemp, "0.0"))
              .Append(",\"power\":").Append(Wire.Opt(s.CpuW, "0.0")).Append('}');

            sb.Append(",\"gpu\":{\"u\":").Append(s.GpuPct)
              .Append(",\"temp\":").Append(Wire.Opt(s.GpuTemp, "0.0"))
              .Append(",\"hotspot\":").Append(Wire.Opt(s.GpuHot, "0.0"))
              .Append(",\"power\":").Append(Wire.Opt(s.GpuW, "0.0"))
              .Append(",\"vramUsed\":").Append(s.VramUsed.ToString(CultureInfo.InvariantCulture))
              .Append(",\"vramTotal\":").Append(s.VramTotal.ToString(CultureInfo.InvariantCulture))
              .Append(",\"eng\":").Append(s.EngJson).Append('}');

            sb.Append(",\"mem\":{\"used\":").Append(s.MemUsed.ToString(CultureInfo.InvariantCulture))
              .Append(",\"total\":").Append(s.MemTotal.ToString(CultureInfo.InvariantCulture)).Append('}');

            sb.Append(",\"net\":{\"rx\":").Append(s.NetRx).Append(",\"tx\":").Append(s.NetTx).Append('}');
            sb.Append(",\"disk\":{\"r\":").Append(s.DiskRead).Append(",\"w\":").Append(s.DiskWrite)
              .Append(",\"busy\":").Append(Wire.Num(s.DiskBusy, "0.0")).Append('}');

            sb.Append(",\"aux\":{\"board\":").Append(Wire.Opt(s.Board, "0.0"))
              .Append(",\"ssd\":").Append(Wire.Opt(s.Ssd, "0.0"))
              .Append(",\"fanCpu\":").Append(Wire.Opt(s.FanCpu, "0"))
              .Append(",\"fanSys\":").Append(Wire.Opt(s.FanSys, "0"))
              .Append(",\"fans\":").Append(s.FansJson).Append('}');

            sb.Append(",\"top\":").Append(s.TopJson);
            sb.Append(",\"tempSrc\":\"").Append(Wire.Escape(s.TempSrc)).Append('"');
            sb.Append(",\"elevated\":").Append(s.Elevated ? "true" : "false");
            sb.Append(",\"cost\":").Append(s.Cost).Append('}');
            return sb.ToString();
        }

        // -------------------------------------------------------- connection
        private void Handle(object state)
        {
            System.Net.Sockets.TcpClient client = (System.Net.Sockets.TcpClient)state;
            System.Net.Sockets.NetworkStream st = null;
            bool handed = false;
            try
            {
                st = client.GetStream();
                st.ReadTimeout = 8000;
                st.WriteTimeout = 8000;

                // One read usually returns headers *and* body: a 19-byte POST
                // body arrives in the same segment as its request line. Keep
                // whatever followed the blank line instead of reading again,
                // or the second read blocks forever on bytes already consumed.
                byte[] buf = new byte[8192];
                int total = ReadHead(st, buf);
                if (total <= 0) return;
                int headLen = HeadEnd(buf, total);
                if (headLen < 0) headLen = total;
                string head = Encoding.UTF8.GetString(buf, 0, headLen);

                string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;
                string[] parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0];
                string path = parts[1];
                string query = "";
                int q = path.IndexOf('?');
                if (q >= 0)
                {
                    query = path.Substring(q + 1);
                    path = path.Substring(0, q);
                }
                try { path = Uri.UnescapeDataString(path); }
                catch { }

                int length = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    int c = lines[i].IndexOf(':');
                    if (c <= 0) continue;
                    if (String.Equals(lines[i].Substring(0, c).Trim(), "Content-Length",
                                      StringComparison.OrdinalIgnoreCase))
                        Int32.TryParse(lines[i].Substring(c + 1).Trim(), out length);
                }

                if (method == "POST")
                {
                    if (length > 65536) length = 65536;
                    byte[] body = new byte[length];
                    int got = Math.Min(total - headLen, length);
                    if (got > 0) Array.Copy(buf, headLen, body, 0, got);
                    while (got < length)
                    {
                        int n = st.Read(body, got, length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    Post(st, path, Encoding.UTF8.GetString(body, 0, got));
                    return;
                }

                if (path == "/api/stream")
                {
                    handed = true;      // the SSE writer owns the socket from here
                    Stream(st);
                    return;
                }

                Get(st, path, query);
            }
            catch { }
            finally
            {
                if (!handed)
                {
                    try { if (st != null) st.Close(); } catch { }
                    try { client.Close(); } catch { }
                }
            }
        }

        /// <summary>Read until the blank line that ends the headers; returns bytes read.</summary>
        private static int ReadHead(Stream st, byte[] buf)
        {
            int len = 0;
            while (len < buf.Length)
            {
                int n;
                try { n = st.Read(buf, len, buf.Length - len); }
                catch { break; }
                if (n <= 0) break;
                len += n;
                // Search the whole buffer, not its tail: a POST body usually
                // arrives in the same segment, so the final four bytes are the
                // body, not the separator.
                if (HeadEnd(buf, len) >= 0) break;
            }
            return len;
        }

        /// <summary>End offset of the header block including its blank line, or -1.</summary>
        private static int HeadEnd(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
            {
                if (buf[i - 3] == 13 && buf[i - 2] == 10 && buf[i - 1] == 13 && buf[i] == 10)
                    return i + 1;
            }
            return -1;
        }

        private void Get(Stream st, string path, string query)
        {
            if (path == "/api/ping")
            {
                Json(st, "{\"ok\":true,\"app\":\"hw-monitor\"}");
                return;
            }
            if (path == "/api/info")
            {
                Json(st, "{\"info\":" + _infoJson + ",\"collector\":" + CollectorJson() + "}");
                return;
            }
            if (path == "/api/settings")
            {
                Json(st, Settings.SnapshotJson());
                return;
            }
            if (path == "/api/snapshot")
            {
                Json(st, "{\"info\":" + _infoJson + ",\"sample\":" + _sampleJson +
                         ",\"settings\":" + Settings.SnapshotJson() +
                         ",\"collector\":" + CollectorJson() + "}");
                return;
            }
            if (path == "/api/history")
            {
                string range = Param(query, "range");
                // "1h"/"6h"/"24h"/"7d" read the ndjson archive; anything else
                // (or nothing) keeps the old in-memory ring the page already
                // knows how to replay.
                string samples;
                if (range != null && range != "live") samples = _hist.ArchiveJson(range);
                else samples = _hist.HistoryJson(_sampleJson);
                string body = "{\"info\":" + _infoJson +
                              ",\"samples\":" + samples +
                              ",\"range\":" + (range == null ? "null" : "\"" + Wire.Escape(range) + "\"") +
                              ",\"collector\":" + CollectorJson() + "}";
                Json(st, body);
                return;
            }
            if (path == "/api/summary")
            {
                Json(st, _hist.SummaryJson());
                return;
            }
            if (path == "/favicon.ico") { Static(st, "favicon.svg"); return; }
            Static(st, path.Length <= 1 ? "index.html" : path.Substring(1));
        }

        private static string CollectorJson()
        {
            return "{\"alive\":true,\"restarts\":0,\"error\":null}";
        }

        /// <summary>One value out of an "a=1&amp;b=2" query string.</summary>
        private static string Param(string query, string key)
        {
            if (String.IsNullOrEmpty(query)) return null;
            foreach (string part in query.Split('&'))
            {
                int e = part.IndexOf('=');
                string k = e < 0 ? part : part.Substring(0, e);
                if (!String.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (e < 0) return "";
                string v = part.Substring(e + 1);
                try { return Uri.UnescapeDataString(v); }
                catch { return v; }
            }
            return null;
        }

        private void Post(Stream st, string path, string body)
        {
            if (path == "/api/settings")
            {
                string key = Wire.Scalar(body, "key");
                string value = Wire.Scalar(body, "value");
                if (key == null || value == null)
                {
                    Json(st, "{\"error\":\"bad request\"}", 400);
                    return;
                }
                if (!Settings.Set(key, value))
                {
                    Json(st, "{\"error\":\"unknown key\"}", 400);
                    return;
                }
                Json(st, "{\"ok\":true,\"settings\":" + Settings.SnapshotJson() + "}");
                return;
            }
            if (path != "/api/config")
            {
                Json(st, "{\"error\":\"not found\"}", 404);
                return;
            }
            int ms = 0;
            string raw = Wire.Scalar(body, "intervalMs");
            if (raw == null || !Int32.TryParse(raw, out ms))
            {
                Json(st, "{\"error\":\"bad request\"}", 400);
                return;
            }
            ms = Math.Max(250, Math.Min(10000, ms));
            _interval = ms;
            OnInterval(ms);
            Json(st, "{\"ok\":true,\"interval\":" + ms + "}");
        }

        /// <summary>Raised after the page changes the sample rate.</summary>
        public event Action<int> IntervalChanged;

        private void OnInterval(int ms)
        {
            Action<int> h = IntervalChanged;
            if (h != null) h(ms);
        }

        // ------------------------------------------------------------ reply
        private static void Json(Stream st, string body)
        {
            Json(st, body, 200);
        }

        private static void Json(Stream st, string body, int status)
        {
            byte[] data = Encoding.UTF8.GetBytes(body);
            Send(st, status, "application/json; charset=utf-8", data);
        }

        private static void Send(Stream st, int status, string mime, byte[] body)
        {
            string head = "HTTP/1.0 " + status + " " + Reason(status) + "\r\n" +
                          "Content-Type: " + mime + "\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            st.Write(hb, 0, hb.Length);
            if (body.Length > 0) st.Write(body, 0, body.Length);
            st.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                default: return "OK";
            }
        }

        private void Static(Stream st, string rel)
        {
            if (_webDir == null)
            {
                Json(st, "{\"error\":\"not found\"}", 404);
                return;
            }
            string safe;
            try
            {
                safe = Path.GetFullPath(Path.Combine(_webDir, rel.Replace('/', '\\')));
            }
            catch
            {
                Json(st, "{\"error\":\"forbidden\"}", 403);
                return;
            }
            string root = Path.GetFullPath(_webDir);
            // Compare against the directory *with* its trailing separator:
            // a bare prefix test also accepts a sibling like "...\web-evil".
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;
            if (!safe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                Json(st, "{\"error\":\"forbidden\"}", 403);
                return;
            }
            if (Directory.Exists(safe)) safe = Path.Combine(safe, "index.html");
            if (!File.Exists(safe))
            {
                Json(st, "{\"error\":\"not found\"}", 404);
                return;
            }

            string ext = Path.GetExtension(safe).ToLowerInvariant();
            string mime = "application/octet-stream";
            if (ext == ".html") mime = "text/html; charset=utf-8";
            else if (ext == ".js") mime = "application/javascript; charset=utf-8";
            else if (ext == ".css") mime = "text/css; charset=utf-8";
            else if (ext == ".svg") mime = "image/svg+xml";
            else if (ext == ".json") mime = "application/json; charset=utf-8";
            else if (ext == ".ico") mime = "image/x-icon";
            else if (ext == ".woff2") mime = "font/woff2";

            try
            {
                byte[] data = File.ReadAllBytes(safe);
                Send(st, 200, mime, data);
            }
            catch
            {
                Json(st, "{\"error\":\"not found\"}", 404);
            }
        }

        // -------------------------------------------------------------- sse
        private void Stream(System.Net.Sockets.NetworkStream st)
        {
            string head = "HTTP/1.0 200 OK\r\n" +
                          "Content-Type: text/event-stream; charset=utf-8\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: keep-alive\r\n" +
                          "X-Accel-Buffering: no\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            try
            {
                st.Write(hb, 0, hb.Length);
                st.Flush();
            }
            catch { return; }

            Sse sub = new Sse();
            sub.Stream = st;
            lock (_subGate) _subs.Add(sub);

            try
            {
                string hello = "data: {\"type\":\"hello\",\"interval\":" + _interval +
                               ",\"collector\":\"online\"}\n\n";
                if (!Write(sub, hello)) return;

                double beat = Clock.Now;
                while (!sub.Dead)
                {
                    string msg = null;
                    lock (sub.Queue)
                    {
                        if (sub.Queue.Count > 0) msg = sub.Queue.Dequeue();
                    }
                    if (msg != null)
                    {
                        if (!Write(sub, "data: " + msg + "\n\n")) break;
                        beat = Clock.Now;
                    }
                    else if (Clock.Now - beat > 10)
                    {
                        if (!Write(sub, ": ping\n\n")) break;
                        beat = Clock.Now;
                    }
                    else
                    {
                        Thread.Sleep(40);
                    }
                }
            }
            catch { }
            finally
            {
                sub.Dead = true;
                lock (_subGate) _subs.Remove(sub);
                try { st.Close(); } catch { }
            }
        }

        private static bool Write(Sse sub, string text)
        {
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(text);
                sub.Stream.Write(b, 0, b.Length);
                sub.Stream.Flush();
                return true;
            }
            catch
            {
                sub.Dead = true;
                return false;
            }
        }

        public void Dispose()
        {
            System.Net.Sockets.TcpListener l = _listener;
            _listener = null;
            if (l != null) { try { l.Stop(); } catch { } }
            lock (_subGate)
            {
                foreach (Sse sub in _subs) sub.Dead = true;
                _subs.Clear();
            }
        }
    }
}

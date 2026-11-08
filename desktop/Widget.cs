// Widget.cs - desktop floating panel for the hardware monitor.
//
// One process does everything: CPU / memory through native APIs, GPU load
// through PDH, temperatures through LibreHardwareMonitorLib loaded by
// reflection. The PowerShell version of this widget needed a language host
// (~160 MB) plus a Python collector (~29 MB) plus TempHelper.exe (~35 MB);
// this build replaces all three with roughly 60 MB total, and a sample costs
// a couple of milliseconds instead of 320.
//
// Language level is C# 5: that is what the .NET Framework compiler behind
// Add-Type accepts (tools\build-widget.ps1 drives it, no SDK required).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// AssemblyTitle becomes the Win32 FileDescription in the version resource,
// which is the name Task Manager shows for the background process.
[assembly: AssemblyTitle("CPU-GPU 监视器")]
[assembly: AssemblyProduct("CPU-GPU Monitor")]
[assembly: AssemblyDescription("CPU/GPU temperature & usage desktop widget")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyCopyright("MIT License")]

namespace HardwareMonitor
{
    // ------------------------------------------------------------------ native
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    internal static class Native
    {
        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX st);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        // (-1, -1) tells Windows to trim the working set: idle pages go to the
        // standby list and come back as cheap soft faults on next touch.
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhOpenQueryW(string source, IntPtr userData, out IntPtr query);

        // The buffer holds a MULTI_SZ style list, so it must be an array: a
        // StringBuilder would be truncated at the first embedded NUL and only
        // the first instance path would survive.
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhExpandWildCardPathW(string machine, string path, [Out] char[] buf, ref int len, int flags);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhAddCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern int PdhCollectQueryData(IntPtr query);

        // PDH_RAW_COUNTER: CStatus @0, TimeStamp(FILETIME) @4, FirstValue @16,
        // SecondValue @24, MultiCount @32. The struct is only ever read, so a
        // flat buffer keeps the layout out of the picture.
        [DllImport("pdh.dll")]
        public static extern int PdhGetRawCounterValue(IntPtr counter, IntPtr type, IntPtr value);

        [DllImport("pdh.dll")]
        public static extern int PdhRemoveCounter(IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern int PdhCloseQuery(IntPtr query);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb;
            public string lpFile;
            public string lpParameters;
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        // ShellExecuteEx gives a real success/failure answer (unlike
        // ShellExecute), which is what makes "user declined UAC" detectable.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

        public const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
        public const uint SEE_MASK_NOASYNC = 0x00000100;
        public const int ERROR_CANCELLED = 1223;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int W, H;
            public SIZE(int w, int h) { W = w; H = h; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        public const uint ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;

        // Hand the finished 32bpp premultiplied-alpha bitmap to the window.
        // Unlike TransparencyKey, this composites every pixel's own alpha with
        // what is actually behind the window, so antialiased text edges blend
        // into the wallpaper instead of being keyed out or fringed.
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size,
                                                      IntPtr srcDc, ref POINT src, uint key,
                                                      ref BLENDFUNCTION blend, uint flags);
    }

    // -------------------------------------------------------------- cpu / mem
    internal sealed class CpuSampler
    {
        // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION is 48 bytes on x64.
        private const int RECORD = 48;
        private readonly int _n = Environment.ProcessorCount;
        private readonly IntPtr _buf;
        private readonly int _size;
        private long[,] _prev;

        public CpuSampler()
        {
            _size = RECORD * _n;
            _buf = Marshal.AllocHGlobal(_size);
        }

        public int Count { get { return _n; } }

        private long[,] Read()
        {
            int ret;
            if (Native.NtQuerySystemInformation(8, _buf, _size, out ret) != 0) return null;
            long[,] r = new long[_n, 3];
            for (int i = 0; i < _n; i++)
            {
                IntPtr p = new IntPtr(_buf.ToInt64() + i * RECORD);
                r[i, 0] = Marshal.ReadInt64(p, 0);    // IdleTime
                r[i, 1] = Marshal.ReadInt64(p, 8);    // KernelTime
                r[i, 2] = Marshal.ReadInt64(p, 16);   // UserTime
            }
            return r;
        }

        public double Sample(out double[] cores)
        {
            cores = new double[_n];
            long[,] cur = Read();
            if (cur == null) return 0;
            if (_prev == null)
            {
                _prev = cur;
                return 0;
            }
            long busyAll = 0, totalAll = 0;
            for (int i = 0; i < _n; i++)
            {
                long idle = cur[i, 0] - _prev[i, 0];
                long total = (cur[i, 1] - _prev[i, 1]) + (cur[i, 2] - _prev[i, 2]);
                if (total <= 0) { cores[i] = 0; continue; }
                long busy = total - idle;
                double pct = 100.0 * busy / total;
                cores[i] = Math.Min(100.0, Math.Max(0.0, pct));
                busyAll += busy;
                totalAll += total;
            }
            _prev = cur;
            return totalAll <= 0 ? 0 : 100.0 * busyAll / totalAll;
        }
    }

    internal static class Memory
    {
        public static void Read(out double total, out double used)
        {
            MEMORYSTATUSEX st = new MEMORYSTATUSEX();
            st.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            total = 0;
            used = 0;
            if (!Native.GlobalMemoryStatusEx(ref st)) return;
            total = st.ullTotalPhys;
            used = st.ullTotalPhys - st.ullAvailPhys;
        }
    }

    // -------------------------------------------------------------------- gpu
    internal sealed class GpuSampler
    {
        // WDDM performance counters. Two quirks, both documented in the README:
        // a wildcard PdhAddCounter reports zero instances, so paths must be
        // expanded by hand, and PdhGetFormattedCounterValue always returns 0.0
        // for this counter set on Windows 11, so raw values are used instead.
        private const string ENGINE_PATH = @"\GPU Engine(*)\Utilization Percentage";
        private const int MAX_PATHS = 4096;

        private sealed class Counter
        {
            public IntPtr Handle;
            public string Path;
            public string Kind;
        }

        private IntPtr _query;
        private List<Counter> _counters = new List<Counter>();
        private string _luid;
        private Dictionary<string, long> _prev = new Dictionary<string, long>();
        private double _prevT;
        private readonly IntPtr _raw;

        public GpuSampler()
        {
            _raw = Marshal.AllocHGlobal(64);
        }

        private static string[] Expand(string path)
        {
            int need = 0;
            Native.PdhExpandWildCardPathW(null, path, null, ref need, 0);
            if (need <= 0) return new string[0];
            char[] buf = new char[need + 8];
            if (Native.PdhExpandWildCardPathW(null, path, buf, ref need, 0) != 0) return new string[0];
            int take = Math.Min(need + 8, buf.Length);
            string all = new string(buf, 0, take);
            List<string> list = new List<string>();
            foreach (string p in all.Split('\0'))
                if (!String.IsNullOrEmpty(p)) list.Add(p);
            if (list.Count > MAX_PATHS) list.RemoveRange(MAX_PATHS, list.Count - MAX_PATHS);
            return list.ToArray();
        }

        private static string KindOf(string path)
        {
            int at = path.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "Other";
            string rest = path.Substring(at + 9);
            int close = rest.IndexOf(')');
            return (close < 0 ? rest : rest.Substring(0, close)).Trim();
        }

        private static string LuidOf(string path)
        {
            string lower = path.ToLowerInvariant();
            int at = lower.IndexOf("_luid_", StringComparison.Ordinal);
            if (at < 0) return null;
            string rest = lower.Substring(at + 6);
            int phys = rest.IndexOf("_phys_", StringComparison.Ordinal);
            if (phys >= 0) rest = rest.Substring(0, phys);
            int eng = rest.IndexOf("_eng", StringComparison.Ordinal);
            if (eng >= 0) rest = rest.Substring(0, eng);
            return rest;
        }

        public void Rebuild()
        {
            string[] paths = Expand(ENGINE_PATH);
            if (paths.Length == 0) return;

            IntPtr q;
            if (Native.PdhOpenQueryW(null, IntPtr.Zero, out q) != 0) return;

            List<Counter> list = new List<Counter>();
            foreach (string path in paths)
            {
                IntPtr h;
                if (Native.PdhAddCounterW(q, path, IntPtr.Zero, out h) != 0) continue;
                Counter c = new Counter();
                c.Handle = h;
                c.Path = path.ToLowerInvariant();
                c.Kind = KindOf(path);
                list.Add(c);
            }
            if (list.Count == 0)
            {
                Native.PdhCloseQuery(q);
                return;
            }

            // The discrete card exposes far more engine instances than the
            // integrated one; that is the adapter worth showing.
            Dictionary<string, int> byLuid = new Dictionary<string, int>();
            foreach (Counter c in list)
            {
                string luid = LuidOf(c.Path);
                if (luid == null) continue;
                int n;
                byLuid.TryGetValue(luid, out n);
                byLuid[luid] = n + 1;
            }
            string best = null;
            int bestCount = -1;
            foreach (KeyValuePair<string, int> kv in byLuid)
            {
                if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }
            }

            Native.PdhCollectQueryData(q);
            Thread.Sleep(120);
            Native.PdhCollectQueryData(q);

            // Swap in the new query, then release the old one.
            IntPtr old = _query;
            List<Counter> oldList = _counters;
            _query = q;
            _counters = list;
            _luid = best;
            _prev.Clear();
            _prevT = 0;

            if (old != IntPtr.Zero)
            {
                foreach (Counter c in oldList) Native.PdhRemoveCounter(c.Handle);
                Native.PdhCloseQuery(old);
            }
        }

        public string Info = "";
        /// <summary>Per-engine utilisation, pre-serialised for the web panel.</summary>
        public string EngJson = "{}";
        /// <summary>LUID of the adapter being reported, for the panel's info block.</summary>
        public string Luid { get { return _luid; } }

        public double Sample()
        {
            if (_query == IntPtr.Zero || _counters.Count == 0)
            {
                Info = "no-query";
                return 0;
            }
            if (Native.PdhCollectQueryData(_query) != 0) return 0;

            double now = Now;
            double elapsed = _prevT > 0 ? now - _prevT : 0;
            double ticks = elapsed > 0.05 ? elapsed * 1e7 : -1;

            Dictionary<string, long> fresh = new Dictionary<string, long>(_counters.Count);
            Dictionary<string, double> agg = new Dictionary<string, double>();
            string luid = _luid;

            foreach (Counter c in _counters)
            {
                if (Native.PdhGetRawCounterValue(c.Handle, IntPtr.Zero, _raw) != 0) continue;
                long busy = Marshal.ReadInt64(_raw, 16);
                fresh[c.Path] = busy;
                if (ticks <= 0 || luid == null || c.Path.IndexOf(luid, StringComparison.Ordinal) < 0) continue;
                long prev;
                if (!_prev.TryGetValue(c.Path, out prev)) continue;
                long delta = busy - prev;
                if (delta <= 0) continue;
                double pct = Math.Min(100.0, 100.0 * delta / ticks);
                double sum;
                agg.TryGetValue(c.Kind, out sum);
                agg[c.Kind] = sum + pct;
            }

            int prevCount = _prev.Count;
            _prev = fresh;
            _prevT = now;

            double max = 0;
            foreach (double v in agg.Values) if (v > max) max = v;

            StringBuilder sb = new StringBuilder();
            sb.Append("ctr=").Append(_counters.Count).Append(" luid=").Append(luid ?? "null");
            sb.Append(" prev=").Append(prevCount).Append(" agg=");
            foreach (KeyValuePair<string, double> kv in agg)
                sb.Append(kv.Key).Append(':').Append(kv.Value.ToString("0.0", CultureInfo.InvariantCulture)).Append(' ');
            Info = sb.ToString();
            EngJson = BuildEng(agg);

            return Math.Min(100.0, max);
        }

        /// <summary>
        /// The per-engine breakdown the panel's GPU tooltip reads. Built here
        /// because the aggregate dictionary is already in hand; rebuilding it
        /// later would mean a second pass over the counters.
        /// </summary>
        private static string BuildEng(Dictionary<string, double> agg)
        {
            StringBuilder sb = new StringBuilder(96);
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, double> kv in agg)
            {
                if (kv.Value <= 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key).Append("\":")
                  .Append(Math.Round(kv.Value, 1).ToString("0.0", CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            return sb.ToString();
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

        public static double Now
        {
            get { return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency; }
        }
    }

    // ------------------------------------------------------------ temperature
    internal sealed class TempReader
    {
        // LibreHardwareMonitor is the only usable source of temperatures on
        // Windows. Its library is loaded by reflection so a missing or broken
        // install degrades to "no temperature" instead of failing to start.
        private object _computer;
        private Type _computerType;
        private Type _hardwareInterface;
        private readonly Dictionary<Type, MethodInfo> _updateM = new Dictionary<Type, MethodInfo>();
        private readonly Dictionary<Type, PropertyInfo> _sensors = new Dictionary<Type, PropertyInfo>();
        private readonly Dictionary<Type, PropertyInfo> _sensorType = new Dictionary<Type, PropertyInfo>();
        private readonly Dictionary<Type, PropertyInfo> _name = new Dictionary<Type, PropertyInfo>();
        private readonly Dictionary<Type, PropertyInfo> _value = new Dictionary<Type, PropertyInfo>();
        private readonly Dictionary<Type, string> _kind = new Dictionary<Type, string>();

        public string Src = "none";
        public string Error;
        public string Flags;   // diag: IsMotherboardEnabled/IsStorageEnabled read back after Open
        public string Kinds;   // diag: hardware kinds seen in the last Walk

        // Gates the two expensive subtrees. Storage.Update() issues an NVMe
        // SMART pass-through; SuperIO.Update() walks the SMBus. Neither is
        // harmful, but a user who turns the row off should not pay for it.
        public volatile bool WantStorage = true;
        public volatile bool WantFans = true;

        private readonly Dictionary<Type, PropertyInfo> _sub = new Dictionary<Type, PropertyInfo>();
        private readonly object _gate = new object();
        private Acc _last;

        /// <summary>Everything read in one pass over the sensor tree (mirrors TempHelper.cs).</summary>
        internal sealed class Acc
        {
            public double? Cpu, Gpu, Hot, CpuW, GpuW, Board, Ssd, FanCpu, FanSys;

            /// <summary>Every Fan sensor as (name, rpm), in tree order. FanCpu/FanSys
            /// above are only aggregates; the web panel wants the individual headers
            /// so a single stalled fan is visible instead of hidden by the max.</summary>
            public readonly List<string> FanNames = new List<string>();
            public readonly List<double> FanRpm = new List<double>();
        }

        /// <summary>Most recent sample; consumed by TempsServer (no re-walk on HTTP).</summary>
        public Acc LastAcc()
        {
            lock (_gate) { return _last; }
        }

        private static string FindDll()
        {
            string env = Environment.GetEnvironmentVariable("LHM_DLL");
            if (!String.IsNullOrEmpty(env) && File.Exists(env)) return env;
            List<string> candidates = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, @"Programs\LibreHardwareMonitor\LibreHardwareMonitorLib.dll"));
            string here = AppDomain.CurrentDomain.BaseDirectory;
            candidates.Add(Path.Combine(here, "LibreHardwareMonitorLib.dll"));
            candidates.Add(Path.Combine(here, @"..\LibreHardwareMonitorLib.dll"));
            candidates.Add(@"C:\Program Files\LibreHardwareMonitor\LibreHardwareMonitorLib.dll");
            foreach (string c in candidates)
            {
                try { if (File.Exists(Path.GetFullPath(c))) return Path.GetFullPath(c); }
                catch { }
            }
            return null;
        }

        public bool Init()
        {
            string dll = FindDll();
            if (dll == null) { Error = "lhm-missing"; return false; }
            try
            {
                Assembly asm = Assembly.LoadFrom(dll);
                _computerType = asm.GetType("LibreHardwareMonitor.Hardware.Computer", false);
                _hardwareInterface = asm.GetType("LibreHardwareMonitor.Hardware.IHardware", false);
                if (_computerType == null) { Error = "lhm-type-missing"; return false; }

                // Motherboard (SuperIO) and Storage touch extra drivers and can fail
                // on locked-down machines; if the full open throws, fall back to
                // CPU + GPU so the two headline temperatures keep working.
                if (OpenFull(true)) { Src = "lhm"; return true; }
                if (OpenFull(false)) { Src = "lhm-lite"; return true; }
                return false;
            }
            catch (Exception ex)
            {
                Error = "lhm-fail:" + Root(ex);
                _computer = null;
                return false;
            }
        }

        private bool OpenFull(bool full)
        {
            try
            {
                _computer = Activator.CreateInstance(_computerType);
                SetBool("IsCpuEnabled", true);
                SetBool("IsGpuEnabled", true);
                SetBool("IsMotherboardEnabled", full);
                SetBool("IsMemoryEnabled", false);
                SetBool("IsStorageEnabled", full);
                SetBool("IsNetworkEnabled", false);
                SetBool("IsControllerEnabled", false);
                MethodInfo open = _computerType.GetMethod("Open");
                if (open == null) { Error = "lhm-no-open"; return false; }
                open.Invoke(_computer, null);
                try
                {
                    object mb = _computerType.GetProperty("IsMotherboardEnabled").GetValue(_computer, null);
                    object sto = _computerType.GetProperty("IsStorageEnabled").GetValue(_computer, null);
                    Flags = "mb=" + mb + ",sto=" + sto;
                }
                catch (Exception ex) { Flags = "flags-fail:" + Root(ex); }
                return true;
            }
            catch (Exception ex)
            {
                Error = "lhm-fail:" + Root(ex);
                _computer = null;
                return false;
            }
        }

        public void Close()
        {
            if (_computer == null) return;
            try
            {
                MethodInfo m = _computerType.GetMethod("Close");
                if (m != null) m.Invoke(_computer, null);
            }
            catch { }
            _computer = null;
        }

        private void SetBool(string name, bool value)
        {
            PropertyInfo p = _computerType.GetProperty(name);
            if (p != null && p.CanWrite) p.SetValue(_computer, value, null);
        }

        private static string Root(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ":" + ex.Message;
        }

        private T Cached<T>(Dictionary<Type, T> cache, Type t, string prop) where T : MemberInfo
        {
            T found;
            if (cache.TryGetValue(t, out found)) return found;
            T p = t.GetProperty(prop) as T;
            cache[t] = p;
            return p;
        }

        /// <summary>IHardware.Update via GetMethod (it is a method, not a property).</summary>
        private MethodInfo UpdateMethod(Type hwType)
        {
            MethodInfo m;
            if (_updateM.TryGetValue(hwType, out m)) return m;
            m = hwType.GetMethod("Update", Type.EmptyTypes);
            if (m == null && _hardwareInterface != null)
                m = _hardwareInterface.GetMethod("Update", Type.EmptyTypes);
            _updateM[hwType] = m;
            return m;
        }

        private string KindOf(object hw)
        {
            Type t = hw.GetType();
            string k;
            if (_kind.TryGetValue(t, out k)) return k;
            PropertyInfo p = t.GetProperty("HardwareType");
            k = p == null ? "" : Convert.ToString(p.GetValue(hw, null));
            _kind[t] = k;
            return k;
        }

        private string SensorTypeOf(object sensor)
        {
            Type t = sensor.GetType();
            PropertyInfo p = Cached(_sensorType, t, "SensorType");
            if (p == null) return "";
            object v = p.GetValue(sensor, null);
            return v == null ? "" : Convert.ToString(v);
        }

        private bool ReadValue(object sensor, out string name, out double value)
        {
            name = null;
            value = 0;
            Type t = sensor.GetType();
            PropertyInfo pName = Cached(_name, t, "Name");
            PropertyInfo pVal = Cached(_value, t, "Value");
            if (pName == null || pVal == null) return false;
            object v = pVal.GetValue(sensor, null);
            if (v == null) return false;
            value = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            name = Convert.ToString(pName.GetValue(sensor, null)) ?? "";
            return true;
        }

        public void Read(out double? cpu, out double? gpu, out double? hot)
        {
            Acc acc = Sample();
            cpu = acc.Cpu;
            gpu = acc.Gpu;
            hot = acc.Hot;
        }

        /// <summary>One pass over the sensor tree; also cached for the HTTP feed.</summary>
        public Acc Sample()
        {
            Acc acc = new Acc();
            Kinds = null;
            if (_computer != null)
            {
                try
                {
                    PropertyInfo pHw = _computerType.GetProperty("Hardware");
                    System.Collections.IEnumerable hws = (System.Collections.IEnumerable)pHw.GetValue(_computer, null);
                    if (hws != null) Walk(hws, 0, acc);
                }
                catch (Exception ex)
                {
                    Error = "read-fail:" + Root(ex);
                }
            }
            lock (_gate) { _last = acc; }
            return acc;
        }

        private void Walk(System.Collections.IEnumerable hws, int depth, Acc acc)
        {
            foreach (object hw in hws)
            {
                string kind = KindOf(hw) ?? "";
                Kinds = Kinds == null ? kind : Kinds + "," + kind;

                if (kind == "Storage" && !WantStorage) continue;

                // Update is a METHOD, not a property - a GetProperty lookup here
                // silently returns null and the sensor tree never refreshes
                // (all values stay at their Open-time snapshot).
                MethodInfo up = UpdateMethod(hw.GetType());
                if (up != null) { try { up.Invoke(hw, null); } catch { } }

                ReadSensors(hw, kind, acc);

                if (depth >= 2) continue;
                PropertyInfo pSub = Cached(_sub, hw.GetType(), "SubHardware");
                if (pSub == null) continue;
                System.Collections.IEnumerable subs = null;
                try { subs = pSub.GetValue(hw, null) as System.Collections.IEnumerable; }
                catch { }
                if (subs != null) Walk(subs, depth + 1, acc);
            }
        }

        private void ReadSensors(object hw, string kind, Acc acc)
        {
            bool isCpu = kind == "Cpu";
            bool isGpu = kind.StartsWith("Gpu", StringComparison.Ordinal);
            bool isBoard = kind == "Motherboard" || kind == "SuperIO";
            bool isStorage = kind == "Storage";
            if (!isCpu && !isGpu && !isBoard && !isStorage) return;

            PropertyInfo pSens = Cached(_sensors, hw.GetType(), "Sensors");
            if (pSens == null) return;
            System.Collections.IEnumerable sensors = null;
            try { sensors = pSens.GetValue(hw, null) as System.Collections.IEnumerable; }
            catch { }
            if (sensors == null) return;

            double? pkg = null, hwMax = null, core = null, spot = null;
            double? boardNamed = null, boardAny = null;

            foreach (object s in sensors)
            {
                string type = SensorTypeOf(s);
                string name;
                double v;

                if (type == "Temperature")
                {
                    if (!ReadValue(s, out name, out v)) continue;
                    if (v <= 0 || v >= 150) continue;
                    if (name == null) name = "";

                    if (isCpu)
                    {
                        if (Has(name, "Tctl") || Has(name, "Package") ||
                            Has(name, "Core Average") || Has(name, "CPU Total"))
                        {
                            if (pkg == null || v > pkg.Value) pkg = v;
                        }
                        if (hwMax == null || v > hwMax.Value) hwMax = v;
                    }
                    else if (isGpu)
                    {
                        if (Has(name, "GPU Core"))
                        {
                            if (core == null || v > core.Value) core = v;
                        }
                        else if (Has(name, "Hot") && Has(name, "Spot"))
                        {
                            if (spot == null || v > spot.Value) spot = v;
                        }
                    }
                    else if (isBoard)
                    {
                        // SuperIO exposes several rails (System / CPU / VRM / Chipset).
                        // "System" or "Chipset" is what a board temperature means; when
                        // neither exists keep the hottest non-CPU rail as a fallback.
                        if (Has(name, "System") || Has(name, "Chipset") ||
                            Has(name, "Motherboard") || Has(name, "Mainboard"))
                        {
                            if (boardNamed == null || v > boardNamed.Value) boardNamed = v;
                        }
                        else if (!Has(name, "CPU"))
                        {
                            if (boardAny == null || v > boardAny.Value) boardAny = v;
                        }
                    }
                    else if (isStorage)
                    {
                        // NVMe exposes "Composite Temperature" plus its own "Warning" /
                        // "Critical" *thresholds* (74 / 89 C) - those are limits, not
                        // readings. Drop anything that is a limit.
                        if (Has(name, "Warning") || Has(name, "Critical") ||
                            Has(name, "Threshold") || Has(name, "Limit"))
                            continue;
                        if (acc.Ssd == null || v > acc.Ssd.Value) acc.Ssd = v;
                    }
                    continue;
                }

                if (type == "Power")
                {
                    if (!ReadValue(s, out name, out v)) continue;
                    if (v <= 0 || v >= 1000) continue;
                    if (isCpu)
                    {
                        if (acc.CpuW == null || v > acc.CpuW.Value) acc.CpuW = v;
                    }
                    else if (isGpu)
                    {
                        if (acc.GpuW == null || v > acc.GpuW.Value) acc.GpuW = v;
                    }
                    continue;
                }

                if (type == "Fan")
                {
                    if (!WantFans) continue;
                    if (!ReadValue(s, out name, out v)) continue;
                    if (v < 0 || v > 10000) continue;
                    if (name == null) name = "";
                    acc.FanNames.Add(name);
                    acc.FanRpm.Add(v);
                    if (Has(name, "CPU"))
                    {
                        if (acc.FanCpu == null || v > acc.FanCpu.Value) acc.FanCpu = v;
                    }
                    // "System fan" means a header on the motherboard. The GPU
                    // reports its own fan through the same sensor type, and it
                    // is usually the fastest one, so including it made the page
                    // label a 1340 RPM graphics card fan as the system fan while
                    // the actual CPU header sat at 856. Keep them apart; the GPU
                    // fan still has its own tile in the fan card.
                    else if (!Has(name, "GPU") && (acc.FanSys == null || v > acc.FanSys.Value))
                    {
                        acc.FanSys = v;
                    }
                }
            }

            if (isCpu)
            {
                double? pick = pkg != null ? pkg : hwMax;
                if (pick != null && (acc.Cpu == null || pick.Value > acc.Cpu.Value)) acc.Cpu = pick;
            }
            else if (isGpu)
            {
                if (core != null && (acc.Gpu == null || core.Value > acc.Gpu.Value)) acc.Gpu = core;
                if (spot != null && (acc.Hot == null || spot.Value > acc.Hot.Value)) acc.Hot = spot;
            }
            else if (isBoard)
            {
                double? pick = boardNamed != null ? boardNamed : boardAny;
                if (pick != null && (acc.Board == null || pick.Value > acc.Board.Value)) acc.Board = pick;
            }
        }

        private static bool Has(string s, string part)
        {
            return s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------- json feed
        private static string Num(double? v)
        {
            if (v == null) return "null";
            return Math.Round(v.Value, 1).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Int(double? v)
        {
            if (v == null) return "null";
            return Math.Round(v.Value).ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>Minimal JSON string escaping - sensor names are plain ASCII in
        /// practice, but a vendor board can name a header anything.</summary>
        private static string Esc(string s)
        {
            if (String.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append('?');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Same shape as TempHelper's line protocol, so the collector can
        /// treat both sources identically. Never null; missing values are null.</summary>
        public string Json()
        {
            Acc a = LastAcc();
            StringBuilder sb = new StringBuilder(280);
            sb.Append("{\"type\":\"temps\",\"cpu\":").Append(Num(a.Cpu));
            sb.Append(",\"gpu\":").Append(Num(a.Gpu));
            sb.Append(",\"hot\":").Append(Num(a.Hot));
            sb.Append(",\"cpuW\":").Append(Num(a.CpuW));
            sb.Append(",\"gpuW\":").Append(Num(a.GpuW));
            sb.Append(",\"board\":").Append(Num(a.Board));
            sb.Append(",\"ssd\":").Append(Num(a.Ssd));
            sb.Append(",\"fanCpu\":").Append(Int(a.FanCpu));
            sb.Append(",\"fanSys\":").Append(Int(a.FanSys));
            // Raw per-header RPM. Kept out of the scalar fields above so an older
            // panel build can still parse this payload.
            sb.Append(",\"fans\":[");
            for (int i = 0; i < a.FanRpm.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"n\":\"").Append(Esc(a.FanNames[i])).Append("\",\"v\":")
                  .Append(Math.Round(a.FanRpm[i]).ToString("0", CultureInfo.InvariantCulture))
                  .Append('}');
            }
            sb.Append(']');
            sb.Append(",\"src\":\"").Append(Src == "lhm" ? "lhm-widget" : Src).Append("\"");
            sb.Append(",\"flags\":\"").Append(Flags ?? "").Append("\"");
            sb.Append(",\"kinds\":\"").Append(Kinds ?? "").Append("\"");
            if (Error != null)
                sb.Append(",\"err\":\"").Append(Error.Replace("\\", "/").Replace("\"", "'")).Append("\"");
            sb.Append('}');
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------ temps feed
    // Tiny HTTP endpoint on 127.0.0.1:8788 so the web panel's collector can reuse
    // THIS process's sensor tree instead of spawning its own TempHelper.exe -
    // one LHM instance instead of two saves ~55 MB of committed memory. It is
    // hand-rolled over TcpListener on purpose: HttpListener would need a URL ACL
    // or admin rights, TcpListener on loopback needs neither. The JSON is the
    // last sampled snapshot, so a request costs nothing and never touches LHM.
    internal sealed class TempsServer
    {
        private readonly TempReader _temps;
        private System.Net.Sockets.TcpListener _listener;

        public TempsServer(TempReader temps) { _temps = temps; }

        public void Start()
        {
            try
            {
                _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 8788);
                _listener.Start();
                Thread t = new Thread(Loop);
                t.IsBackground = true;
                t.Name = "temps-feed";
                t.Start();
                Log.Write("temps feed on 127.0.0.1:8788");
            }
            catch (Exception ex)
            {
                _listener = null;
                Log.Write("temps feed unavailable: " + ex.Message);
            }
        }

        public void Stop()
        {
            if (_listener == null) return;
            try { _listener.Stop(); }
            catch { }
            _listener = null;
        }

        private void Loop()
        {
            while (_listener != null)
            {
                System.Net.Sockets.TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { break; }  // listener stopped: shutting down

                try
                {
                    using (c)
                    using (System.Net.Sockets.NetworkStream st = c.GetStream())
                    {
                        // Drain the request line; we serve the same body regardless.
                        byte[] buf = new byte[2048];
                        try { st.Read(buf, 0, buf.Length); }
                        catch { }

                        string body = _temps.Json();
                        byte[] outB = Encoding.ASCII.GetBytes(body);
                        string head = "HTTP/1.0 200 OK\r\nContent-Type: application/json\r\n" +
                                      "Content-Length: " + outB.Length + "\r\nConnection: close\r\n\r\n";
                        byte[] headB = Encoding.ASCII.GetBytes(head);
                        st.Write(headB, 0, headB.Length);
                        st.Write(outB, 0, outB.Length);
                        st.Flush();
                    }
                }
                catch { }
            }
        }
    }

    // --------------------------------------------------------------- snapshot
    internal sealed class Snapshot
    {
        public double Cpu;
        public double[] Cores = new double[0];
        public double Gpu;
        public double? CpuTemp;
        public double? GpuTemp;
        public double? GpuHot;
        public double MemUsed;
        public double MemTotal;
        public string TempSrc = "none";
        public bool Elevated;
        public int Cost;
    }

    // -------------------------------------------------------------- mem trim
    internal static class MemTrim
    {
        private static long _next;
        // Ten minutes apart: trimming drops the working set by ~85% within a
        // second, and the pages refault from the standby list for free, but a
        // window being dragged right then would feel the refault. Keeping the
        // interval long makes that collision vanishingly rare.
        private static readonly long Interval = TimeSpan.TicksPerMinute * 10;
        private static readonly long Warmup = TimeSpan.TicksPerMinute;

        /// <summary>
        /// Hands idle pages back to the OS every few minutes.
        ///
        /// A long-lived WinForms process holds a lot of memory it will never
        /// touch again: JIT'd one-shot paths, closed LHM sensor snapshots, the
        /// first paint's buffers. Those pages sit in the working set and count
        /// as resident memory even though nothing reads them, so trimming keeps
        /// the reported size close to the private-bytes floor. Pages go to the
        /// standby list, not to disk, so touching them again is a soft fault.
        /// </summary>
        public static void Maybe()
        {
            long now = DateTime.UtcNow.Ticks;
            if (_next == 0) { _next = now + Warmup; return; }
            if (now < _next) return;
            _next = now + Interval;
            try
            {
                Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(),
                                               (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }

    internal sealed class Sampler : IDisposable
    {
        private readonly CpuSampler _cpu = new CpuSampler();
        private readonly GpuSampler _gpu = new GpuSampler();
        private readonly TempReader _temps = new TempReader();

        // Built on the sampler thread rather than in the constructor: opening
        // the PDH queries walks every GPU engine instance, and doing that
        // before the window exists made the launch feel slower than it was.
        private IoSampler _io;
        private VramSampler _vram;
        private ProcSampler _proc;
        private HistoryStore _hist;
        private PanelServer _panel;
        private TempsServer _feed;

        private int _interval;
        private Thread _thread;
        private volatile bool _stop;
        private long _vramTotal;
        private int _clock;
        private int _clkRated;

        public volatile Snapshot Current = new Snapshot();
        public volatile int Version;
        public long LastTicks;
        public volatile string GpuInfo = "";

        private readonly bool _panelWanted;
        private readonly bool _lan;
        public int PanelPort;

        public Sampler(int intervalMs, bool panel, bool lan)
        {
            _interval = Math.Max(300, intervalMs);
            _panelWanted = panel;
            _lan = lan;
        }

        public void Start()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "sampler";
            // Startup opens a kernel driver and walks the entire sensor tree.
            // Below normal keeps that from competing with whatever the user is
            // doing while the window is still appearing.
            _thread.Priority = ThreadPriority.BelowNormal;
            _thread.Start();
        }

        private void Loop()
        {
            // Let the window paint its first frame first. TempReader.Init()
            // enumerates SuperIO through the SMBus and probes NVMe for SMART,
            // which is a second or two of solid CPU; overlapping that with
            // WinForms' own startup made the launch feel like a stall.
            for (int i = 0; i < 5 && !_stop; i++) Thread.Sleep(100);

            // Two calls: the first only establishes the baseline.
            double[] cores;
            _cpu.Sample(out cores);
            _cpu.Sample(out cores);

            _gpu.Rebuild();
            if (!_temps.Init()) Log.Write("temps: " + _temps.Error);

            // Expose the sensor tree so a standalone consumer - or the Python
            // panel, if it is ever run again - can read temperatures without
            // keeping a second LibreHardwareMonitor instance alive.
            _feed = new TempsServer(_temps);
            _feed.Start();

            _io = new IoSampler();
            _vram = new VramSampler();
            _proc = new ProcSampler(_cpu.Count);

            // Read the adapter's dedicated memory once, here rather than inside
            // BuildInfoJson: with --no-panel that method never runs, and every
            // history row would then persist vramTotal = 0.
            string adapterName;
            SysInfo.Gpu(out adapterName, out _vramTotal);
            Log.Write("sys: " + SysInfo.PhysicalCores() + " physical / " + _cpu.Count +
                      " logical, vram " + (_vramTotal / 1048576) + " MB, gpu " + adapterName);

            string dataDir = Program.DataDir;
            _hist = new HistoryStore(dataDir);
            _hist.Prime();
            _hist.Prune();
            _hist.RebuildSummary();
            Log.Write("history: primed " + _hist.Primed + " rows, today " + _hist.Restored);

            if (_panelWanted)
            {
                _panel = new PanelServer(Program.PanelPort, _lan, Program.WebDir, _hist);
                _panel.SetInfo(BuildInfoJson());
                _panel.SetInterval(_interval);
                _panel.IntervalChanged += delegate(int ms) { _interval = ms; };
                if (_panel.Start())
                {
                    PanelPort = _panel.Port;
                    // Rebuild now that the real port is known: the first pass
                    // still had the preferred 8770, which is off by one when a
                    // previous instance left the port busy.
                    _panel.SetInfo(BuildInfoJson());
                    if (_lan)
                        Log.Write("panel: LAN mode - reachable from the network, NO password");
                }
                else Log.Write("panel: " + _panel.Error);
            }

            int tick = 0;
            while (!_stop)
            {
                double started = GpuSampler.Now;
                Snapshot prev = Current;
                Snapshot s = new Snapshot();

                // A disabled row keeps the previous value so the widget does
                // not flicker to zero; the sampler simply stops paying for it.
                if (Settings.Cpu) s.Cpu = _cpu.Sample(out s.Cores);
                else { s.Cpu = prev.Cpu; s.Cores = prev.Cores; }

                if (Settings.Gpu)
                {
                    // Rebuilding the PDH query costs a PdhExpandWildCardPath over
                    // every GPU engine instance plus a 120 ms settle. Ten seconds
                    // was needlessly eager - new engine instances only appear when
                    // a process starts rendering, and the aggregate is unaffected.
                    if (tick > 0 && tick % 60 == 0) _gpu.Rebuild();
                    s.Gpu = _gpu.Sample();
                    GpuInfo = _gpu.Info;
                }
                else s.Gpu = prev.Gpu;

                if (Settings.Mem) Memory.Read(out s.MemTotal, out s.MemUsed);
                else { s.MemTotal = prev.MemTotal; s.MemUsed = prev.MemUsed; }

                // One LHM walk feeds CPU/GPU/board/SSD temperatures and the
                // fan tree. Skip it only when every consumer is switched off.
                _temps.WantStorage = Settings.DiskTemp;
                _temps.WantFans = Settings.Fans;
                bool needTemp = Settings.CpuTemp || Settings.GpuTemp ||
                                Settings.Board || Settings.DiskTemp || Settings.Fans;
                if (needTemp && (tick % 2 == 0 || tick == 0))
                {
                    double? gc, gg, gh;
                    _temps.Read(out gc, out gg, out gh);
                    s.CpuTemp = gc;
                    s.GpuTemp = gg;
                    s.GpuHot = gh;
                }
                else
                {
                    s.CpuTemp = prev.CpuTemp;
                    s.GpuTemp = prev.GpuTemp;
                    s.GpuHot = prev.GpuHot;
                }
                s.TempSrc = _temps.Src;
                s.Elevated = Program.Elevated;
                s.Cost = (int)((GpuSampler.Now - started) * 1000);

                Current = s;
                LastTicks = DateTime.UtcNow.Ticks;
                Version++;
                PublishPanel(s, tick);
                tick++;
                MemTrim.Maybe();

                double rest = _interval / 1000.0 - (GpuSampler.Now - started);
                if (rest > 0) Thread.Sleep((int)(rest * 1000));
            }
            _temps.Close();
            _gpu.Dispose();
        }

        public void Dispose()
        {
            _stop = true;
            if (_feed != null) _feed.Stop();
            if (_panel != null) _panel.Dispose();
            if (_io != null) _io.Dispose();
            if (_vram != null) _vram.Dispose();
        }

        // ---------------------------------------------------------- panel
        /// <summary>
        /// Hand one tick to the web panel. The expensive parts run on a slower
        /// cadence than the display: PDH queries and the process walk happen
        /// every third tick, because a three-second trend line cannot resolve
        /// faster and the walk alone costs a few milliseconds.
        /// </summary>
        private void PublishPanel(Snapshot s, int tick)
        {
            if (_hist == null) return;

            bool wantIo = Settings.Net || Settings.Disk;
            if (tick % 3 == 0)
            {
                if (wantIo) _io.Sample();
                if (Settings.Proc) _proc.Sample();
                int cur, rated;
                CpuClock.Read(out cur, out rated);
                if (cur > 0) _clock = cur;
                if (rated > 0) _clkRated = rated;
            }
            if (Settings.Gpu) _vram.Sample();

            TempReader.Acc acc = _temps.LastAcc();
            PanelSample ps = new PanelSample();
            ps.T = UnixMs();
            ps.CpuPct = (int)Math.Round(s.Cpu);
            ps.Cores = s.Cores;
            ps.CpuClk = _clock > 0 ? _clock : _clkRated;
            ps.CpuTemp = Settings.CpuTemp ? s.CpuTemp : (double?)null;
            ps.CpuW = acc == null || !Settings.CpuTemp ? null : acc.CpuW;
            ps.GpuPct = (int)Math.Round(s.Gpu);
            ps.GpuTemp = Settings.GpuTemp ? s.GpuTemp : (double?)null;
            ps.GpuHot = Settings.GpuTemp ? s.GpuHot : (double?)null;
            ps.GpuW = acc == null || !Settings.GpuTemp ? null : acc.GpuW;
            ps.VramUsed = Settings.Gpu ? _vram.Used : 0;
            ps.VramTotal = _vramTotal;
            ps.EngJson = Settings.Gpu ? _gpu.EngJson : "{}";
            ps.MemUsed = (long)s.MemUsed;
            ps.MemTotal = (long)s.MemTotal;
            ps.NetOn = Settings.Net && wantIo;
            ps.DiskOn = Settings.Disk && wantIo;
            ps.NetRx = ps.NetOn ? _io.Rx : 0;
            ps.NetTx = ps.NetOn ? _io.Tx : 0;
            ps.DiskRead = ps.DiskOn ? _io.DiskRead : 0;
            ps.DiskWrite = ps.DiskOn ? _io.DiskWrite : 0;
            ps.DiskBusy = ps.DiskOn ? _io.Busy : 0;
            ps.Board = acc == null || !Settings.Board ? null : acc.Board;
            ps.Ssd = acc == null || !Settings.DiskTemp ? null : acc.Ssd;
            ps.FanCpu = acc == null || !Settings.Fans ? null : acc.FanCpu;
            ps.FanSys = acc == null || !Settings.Fans ? null : acc.FanSys;
            ps.FansJson = Settings.Fans ? FansJson(acc) : "[]";
            ps.TopJson = _proc == null || !Settings.Proc ? "[]" : _proc.Json;
            ps.TopBrief = _proc == null ? "(无归属)" : _proc.Brief;
            ps.TempSrc = s.TempSrc;
            ps.Elevated = s.Elevated;
            ps.Cost = s.Cost;

            _hist.Add(ps);
            SpikeLog.Maybe(ps, Program.DataDir);
            if (_panel != null) _panel.Publish(ps);
        }

        private static string FansJson(TempReader.Acc acc)
        {
            if (acc == null || acc.FanNames.Count == 0) return "[]";
            StringBuilder sb = new StringBuilder(acc.FanNames.Count * 30 + 4);
            sb.Append('[');
            for (int i = 0; i < acc.FanNames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"n\":\"").Append(Wire.Escape(acc.FanNames[i])).Append("\",\"v\":")
                  .Append(Math.Round(acc.FanRpm[i]).ToString("0", CultureInfo.InvariantCulture))
                  .Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static long UnixMs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }

        /// <summary>The one-shot info block the page asks for on load.</summary>
        private string BuildInfoJson()
        {
            string gpuName;
            long vram;
            SysInfo.Gpu(out gpuName, out vram);
            if (vram > 0) _vramTotal = vram;

            double memTotal, memUsed;
            Memory.Read(out memTotal, out memUsed);

            string luid = _gpu.Luid;
            StringBuilder sb = new StringBuilder(340);
            sb.Append("{\"cpuName\":\"").Append(Wire.Escape(SysInfo.CpuName())).Append('"');
            sb.Append(",\"cores\":").Append(SysInfo.PhysicalCores());
            sb.Append(",\"logical\":").Append(_cpu.Count);
            sb.Append(",\"baseClock\":").Append(SysInfo.RatedClock());
            sb.Append(",\"gpuName\":\"").Append(Wire.Escape(gpuName)).Append('"');
            sb.Append(",\"gpuLuid\":");
            if (luid == null) sb.Append("null");
            else sb.Append("\"luid_").Append(Wire.Escape(luid)).Append('"');
            sb.Append(",\"vramTotal\":").Append(vram.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"ramTotal\":").Append(((long)memTotal).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"osName\":\"").Append(Wire.Escape(SysInfo.OsName())).Append('"');
            sb.Append(",\"host\":\"").Append(Wire.Escape(Environment.MachineName)).Append('"');
            // The page shows a warning when the dashboard is reachable from
            // the network: it has no password, and that must not be silent.
            sb.Append(",\"lan\":").Append(_lan ? "true" : "false");
            sb.Append(",\"port\":").Append(PanelPort);
            sb.Append('}');
            return sb.ToString();
        }
    }

    // ----------------------------------------------------------------- config
    internal sealed class Config
    {
        public int FontSize = 11;
        public double Alpha = 0.88;
        public bool TopMost = true;
        public bool Pure;
        public int X = -1;
        public int Y = -1;
        public bool ShowMem = true;
        public bool ShowGraph = true;

        public static string Path_
        {
            get { return Path.Combine(Program.Dir, "widget-config.json"); }
        }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(Path_)) return c;
                Dictionary<string, string> kv = Json.ReadFlat(File.ReadAllText(Path_));
                string v;
                if (kv.TryGetValue("fontSize", out v)) c.FontSize = (int)Num(v, c.FontSize);
                if (kv.TryGetValue("alpha", out v)) c.Alpha = Num(v, c.Alpha);
                if (kv.TryGetValue("topMost", out v)) c.TopMost = Bool(v, c.TopMost);
                if (kv.TryGetValue("pure", out v)) c.Pure = Bool(v, c.Pure);
                if (kv.TryGetValue("x", out v)) c.X = (int)Num(v, c.X);
                if (kv.TryGetValue("y", out v)) c.Y = (int)Num(v, c.Y);
                if (kv.TryGetValue("showMem", out v)) c.ShowMem = Bool(v, c.ShowMem);
                if (kv.TryGetValue("showGraph", out v)) c.ShowGraph = Bool(v, c.ShowGraph);
            }
            catch (Exception ex) { Log.Write("config read failed: " + ex.Message); }
            return c;
        }

        private static double Num(string s, double fallback)
        {
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }

        private static bool Bool(string s, bool fallback)
        {
            if (s == null) return fallback;
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "1") return true;
            if (s == "false" || s == "0") return false;
            return fallback;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"fontSize\": ").Append(FontSize).Append(",\n");
                sb.Append("  \"alpha\": ").Append(Alpha.ToString("0.00", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"topMost\": ").Append(TopMost ? "true" : "false").Append(",\n");
                sb.Append("  \"pure\": ").Append(Pure ? "true" : "false").Append(",\n");
                sb.Append("  \"x\": ").Append(X).Append(",\n");
                sb.Append("  \"y\": ").Append(Y).Append(",\n");
                sb.Append("  \"showMem\": ").Append(ShowMem ? "true" : "false").Append(",\n");
                sb.Append("  \"showGraph\": ").Append(ShowGraph ? "true" : "false").Append("\n");
                sb.Append("}\n");
                File.WriteAllText(Path_, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("config save failed: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------- settings
    /// <summary>
    /// The collection switches and the UI language, shared by the desktop
    /// menu and the web panel. Deliberately a separate file from
    /// widget-config.json: window geometry and collection policy have
    /// different lifetimes, and merging them would let a stale copy of one
    /// overwrite the other.
    ///
    /// Every field is volatile: the menu thread writes, the sampler thread
    /// reads on every tick, and a torn read here would silently re-enable a
    /// sensor the user just turned off.
    /// </summary>
    internal static class Settings
    {
        public static volatile bool Cpu = true;
        public static volatile bool CpuTemp = true;
        public static volatile bool Gpu = true;
        public static volatile bool GpuTemp = true;
        public static volatile bool Mem = true;
        public static volatile bool Net = true;
        public static volatile bool Disk = true;
        public static volatile bool DiskTemp = true;
        public static volatile bool Fans = true;
        public static volatile bool Board = true;
        public static volatile bool Proc = true;
        public static volatile string Lang = "zh";

        /// <summary>Raised after any field changes, on the changing thread.</summary>
        public static event Action Changed;

        public static string FilePath
        {
            get { return Path.Combine(Program.Dir, "settings.json"); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                Dictionary<string, string> kv = Json.ReadFlat(File.ReadAllText(FilePath));
                string v;
                if (kv.TryGetValue("lang", out v)) Lang = v == "en" ? "en" : "zh";
                Cpu = Read(kv, "cpu", Cpu);
                CpuTemp = Read(kv, "cpuTemp", CpuTemp);
                Gpu = Read(kv, "gpu", Gpu);
                GpuTemp = Read(kv, "gpuTemp", GpuTemp);
                Mem = Read(kv, "mem", Mem);
                Net = Read(kv, "net", Net);
                Disk = Read(kv, "disk", Disk);
                DiskTemp = Read(kv, "diskTemp", DiskTemp);
                Fans = Read(kv, "fans", Fans);
                Board = Read(kv, "board", Board);
                Proc = Read(kv, "proc", Proc);
            }
            catch (Exception ex) { Log.Write("settings read failed: " + ex.Message); }
        }

        private static bool Read(Dictionary<string, string> kv, string key, bool fallback)
        {
            string v;
            if (!kv.TryGetValue(key, out v)) return fallback;
            if (v == null) return fallback;
            v = v.Trim().ToLowerInvariant();
            if (v == "true" || v == "1") return true;
            if (v == "false" || v == "0") return false;
            return fallback;
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder(320);
                sb.Append("{\n");
                sb.Append("  \"lang\": \"").Append(Lang).Append("\",\n");
                sb.Append("  \"cpu\": ").Append(B(Cpu)).Append(",\n");
                sb.Append("  \"cpuTemp\": ").Append(B(CpuTemp)).Append(",\n");
                sb.Append("  \"gpu\": ").Append(B(Gpu)).Append(",\n");
                sb.Append("  \"gpuTemp\": ").Append(B(GpuTemp)).Append(",\n");
                sb.Append("  \"mem\": ").Append(B(Mem)).Append(",\n");
                sb.Append("  \"net\": ").Append(B(Net)).Append(",\n");
                sb.Append("  \"disk\": ").Append(B(Disk)).Append(",\n");
                sb.Append("  \"diskTemp\": ").Append(B(DiskTemp)).Append(",\n");
                sb.Append("  \"fans\": ").Append(B(Fans)).Append(",\n");
                sb.Append("  \"board\": ").Append(B(Board)).Append(",\n");
                sb.Append("  \"proc\": ").Append(B(Proc)).Append("\n");
                sb.Append("}\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
        }

        private static string B(bool v) { return v ? "true" : "false"; }

        /// <summary>The switches as one flat JSON object, for GET /api/settings.</summary>
        public static string SnapshotJson()
        {
            StringBuilder sb = new StringBuilder(220);
            sb.Append("{\"lang\":\"").Append(Lang).Append('"');
            sb.Append(",\"cpu\":").Append(B(Cpu));
            sb.Append(",\"cpuTemp\":").Append(B(CpuTemp));
            sb.Append(",\"gpu\":").Append(B(Gpu));
            sb.Append(",\"gpuTemp\":").Append(B(GpuTemp));
            sb.Append(",\"mem\":").Append(B(Mem));
            sb.Append(",\"net\":").Append(B(Net));
            sb.Append(",\"disk\":").Append(B(Disk));
            sb.Append(",\"diskTemp\":").Append(B(DiskTemp));
            sb.Append(",\"fans\":").Append(B(Fans));
            sb.Append(",\"board\":").Append(B(Board));
            sb.Append(",\"proc\":").Append(B(Proc));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Current state of one switch, for menu check marks.</summary>
        public static bool Get(string key)
        {
            switch (key)
            {
                case "cpu": return Cpu;
                case "cpuTemp": return CpuTemp;
                case "gpu": return Gpu;
                case "gpuTemp": return GpuTemp;
                case "mem": return Mem;
                case "net": return Net;
                case "disk": return Disk;
                case "diskTemp": return DiskTemp;
                case "fans": return Fans;
                case "board": return Board;
                case "proc": return Proc;
                case "lang": return Lang == "en";
                default: return false;
            }
        }

        /// <summary>
        /// Apply one key. Returns false for an unknown key so the HTTP layer
        /// can answer 400 instead of pretending the write succeeded.
        /// </summary>
        public static bool Set(string key, string value)
        {
            if (key == null) return false;
            string v = value == null ? "" : value.Trim().ToLowerInvariant();
            bool b = v == "true" || v == "1";
            switch (key)
            {
                case "cpu": Cpu = b; break;
                case "cpuTemp": CpuTemp = b; break;
                case "gpu": Gpu = b; break;
                case "gpuTemp": GpuTemp = b; break;
                case "mem": Mem = b; break;
                case "net": Net = b; break;
                case "disk": Disk = b; break;
                case "diskTemp": DiskTemp = b; break;
                case "fans": Fans = b; break;
                case "board": Board = b; break;
                case "proc": Proc = b; break;
                case "lang": Lang = v == "en" ? "en" : "zh"; break;
                default: return false;
            }
            Save();
            Action h = Changed;
            if (h != null) h();
            return true;
        }
    }

    /// <summary>Two-string lookup. Short by design: the widget only paints a
    /// handful of words, and a resource file would be more machinery than it
    /// is worth at this size.</summary>
    internal static class L
    {
        public static bool Zh { get { return Settings.Lang != "en"; } }
        public static string T(string zh, string en) { return Zh ? zh : en; }
    }

    // Hand-rolled reader for the flat config object; avoids pulling a JSON
    // library into a process that is trying to stay small.
    internal static class Json
    {
        public static Dictionary<string, string> ReadFlat(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            int i = 0;
            while (i < text.Length)
            {
                int k = text.IndexOf('"', i);
                if (k < 0) break;
                int k2 = text.IndexOf('"', k + 1);
                if (k2 < 0) break;
                string key = text.Substring(k + 1, k2 - k - 1);
                int colon = text.IndexOf(':', k2);
                if (colon < 0) break;
                int v = colon + 1;
                while (v < text.Length && Char.IsWhiteSpace(text[v])) v++;
                string val;
                if (v < text.Length && text[v] == '"')
                {
                    int e = text.IndexOf('"', v + 1);
                    if (e < 0) break;
                    val = text.Substring(v + 1, e - v - 1);
                    i = e + 1;
                }
                else
                {
                    int e = v;
                    while (e < text.Length && text[e] != ',' && text[e] != '}') e++;
                    val = text.Substring(v, e - v).Trim();
                    i = e;
                }
                d[key] = val;
            }
            return d;
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
                    File.AppendAllText(Path.Combine(Program.Dir, "widget.log"), line, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    // ----------------------------------------------------------------- widget
    internal sealed class WidgetForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(20, 22, 26);
        private static readonly Color Edge = Color.FromArgb(58, 62, 70);
        private static readonly Color Text_ = Color.FromArgb(232, 234, 237);
        private static readonly Color Dim = Color.FromArgb(136, 142, 152);
        private static readonly Color Track = Color.FromArgb(48, 52, 60);
        private static readonly Color Halo = Color.FromArgb(190, 0, 0, 0);
        private static readonly Color CpuColor = Color.FromArgb(34, 211, 238);
        private static readonly Color GpuColor = Color.FromArgb(167, 139, 250);
        private static readonly Color Warn = Color.FromArgb(245, 158, 11);
        private static readonly Color Danger = Color.FromArgb(239, 68, 68);

        private const int HistMax = 120;

        private readonly Config _cfg;
        private readonly bool _persist;
        private readonly Sampler _sampler;

        private Bitmap _frame;
        private double _scale = 1.0;
        private Font _fNum, _fUnit, _fLabel, _fSmall, _fTemp;
        private SolidBrush _bText, _bDim, _bTrack, _bHalo;
        private readonly StringFormat _typo = StringFormat.GenericTypographic;
        private RectangleF _toggleRect;

        private readonly List<double> _histCpu = new List<double>();
        private readonly List<double> _histGpu = new List<double>();
        private int _seenVersion = -1;
        private bool _alive;
        private bool _dragging;
        private Point _dragFrom;
        private bool _togglePending;
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();

        public WidgetForm(Config cfg, Sampler sampler, bool persist)
        {
            _cfg = cfg;
            _sampler = sampler;
            _persist = persist;
            _scale = cfg.FontSize / 11.0;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = cfg.TopMost;
            Text = "CPU-GPU 监视器";
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            _menu.ShowImageMargin = false;

            MakeFonts();
            ApplyLook();

            // A change made in the browser must reach this window too: the
            // page and the widget share one settings file.
            Settings.Changed += OnSettingsChanged;

            _tick.Interval = 200;
            _tick.Tick += OnTick;
        }

        private bool _localSettings;

        private void OnSettingsChanged()
        {
            if (_localSettings || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { AfterSettingsChange(); }); }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Settings.Changed -= OnSettingsChanged;
            base.OnFormClosed(e);
        }

        // WS_EX_LAYERED lets UpdateLayeredWindow hand the window its finished
        // 32bpp alpha surface. Fully transparent pixels become click-through
        // automatically, which is what the "完全透明" mode relies on.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;
                return cp;
            }
        }

        // -------------------------------------------------------------- layout
        private static int RowCount()
        {
            int n = 0;
            if (Settings.Cpu) n++;
            if (Settings.Gpu) n++;
            return n;
        }

        /// <summary>
        /// True when at least one switch that can only be served by the
        /// LibreHardwareMonitor bridge is on. Used to decide whether an empty
        /// temperature corner deserves an explanation in the footer.
        /// </summary>
        private static bool WantsTemps()
        {
            return Settings.CpuTemp || Settings.GpuTemp || Settings.DiskTemp ||
                   Settings.Fans || Settings.Board;
        }

        private Size WidgetSize()
        {
            int w = (int)Math.Round(238 * _scale);
            int rows = RowCount();
            int h;
            if (rows == 0) h = _cfg.ShowGraph ? 84 : 56;
            else h = (_cfg.ShowGraph ? 143 : 96) - (2 - rows) * 32;
            return new Size(w, (int)Math.Round(h * _scale));
        }

        private void MakeFonts()
        {
            DisposeFonts();
            GraphicsUnit px = GraphicsUnit.Pixel;
            // Integer pixel sizes only: GDI+ renders fractional pixel sizes
            // slightly soft, which reads as blur at these small sizes.
            _fNum = new Font("Consolas", Snap(16), FontStyle.Bold, px);
            _fUnit = new Font("Consolas", Snap(8.5), FontStyle.Regular, px);
            _fLabel = new Font("Microsoft YaHei UI", Snap(11.5), FontStyle.Regular, px);
            _fSmall = new Font("Microsoft YaHei UI", Snap(9.5), FontStyle.Regular, px);
            _fTemp = new Font("Consolas", Snap(12.5), FontStyle.Bold, px);
            _bText = new SolidBrush(Text_);
            _bDim = new SolidBrush(Dim);
            _bTrack = new SolidBrush(Track);
            _bHalo = new SolidBrush(Halo);
        }

        private static float Snap(double px)
        {
            float v = (float)Math.Round(px);
            return v < 7f ? 7f : v;
        }

        private void DisposeFonts()
        {
            if (_fNum == null) return;
            _fNum.Dispose(); _fUnit.Dispose(); _fLabel.Dispose(); _fSmall.Dispose(); _fTemp.Dispose();
            _bText.Dispose(); _bDim.Dispose(); _bTrack.Dispose(); _bHalo.Dispose();
        }

        private static GraphicsPath RoundPath(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color UsageColor(double pct, Color baseColor)
        {
            if (pct >= 90) return Danger;
            if (pct >= 70) return Warn;
            return baseColor;
        }

        private static Color TempColor(double? t)
        {
            if (t == null) return Dim;
            if (t.Value >= 85) return Danger;
            if (t.Value >= 70) return Warn;
            return Text_;
        }

        private static string Bytes(double b)
        {
            if (b <= 0) return "0G";
            return (b / 1073741824.0).ToString("N1", CultureInfo.InvariantCulture) + "G";
        }

        // ------------------------------------------------------------- painting
        private void RenderFrame(Graphics g, int W, int H, bool withEdge)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Grayscale antialiasing, not ClearType: on a per-pixel-alpha
            // surface ClearType writes sub-pixel colour fringes that were
            // tuned for an opaque backdrop, and over other windows the glyphs
            // come out smeared and discoloured. Grayscale AA keeps the glyph
            // colour pure white and puts the softness in the alpha channel,
            // which the desktop then blends correctly.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            float sc = (float)_scale;
            float pad = 12 * sc;

            if (withEdge)
            {
                using (GraphicsPath bgPath = RoundPath(0.5f, 0.5f, W - 1, H - 1, 9 * sc))
                {
                    using (SolidBrush b = new SolidBrush(Bg)) g.FillPath(b, bgPath);
                    using (Pen p = new Pen(Edge, 1)) g.DrawPath(p, bgPath);
                }
            }

            float rowH = 32 * sc;
            float barH = 5 * sc;
            float labelW = 28 * sc;
            float numColW = 32 * sc;
            float numRight = pad + labelW + numColW;
            float barW = W - pad * 2;

            bool halo = !withEdge;

            Snapshot s = _sampler.Current;
            string[] names = { "CPU", "GPU" };
            double[] vals = { s.Cpu, s.Gpu };
            double?[] temps = { s.CpuTemp, s.GpuTemp };
            Color[] colors = { CpuColor, GpuColor };
            bool[] shown = { Settings.Cpu, Settings.Gpu };
            bool[] tempOn = { Settings.CpuTemp, Settings.GpuTemp };

            float y = 9 * sc;
            bool any = false;
            for (int i = 0; i < 2; i++)
            {
                if (!shown[i]) continue;
                any = true;
                double pct = Math.Max(0.0, Math.Min(100.0, vals[i]));

                DrawText(g, names[i], _fLabel, _bDim, pad, y + 3 * sc, halo);

                string numStr = ((int)Math.Round(pct)).ToString(CultureInfo.InvariantCulture);
                float nw = g.MeasureString(numStr, _fNum, 1000, _typo).Width;
                DrawText(g, numStr, _fNum, _bText, numRight - nw, y - 1 * sc, halo);
                DrawText(g, "%", _fUnit, _bDim, numRight + 1 * sc, y + 4 * sc, halo);

                float barY = y + 21 * sc;
                using (GraphicsPath track = RoundPath(pad, barY, barW, barH, barH / 2))
                    g.FillPath(_bTrack, track);
                float fillW = (float)Math.Round(barW * pct / 100.0, 1);
                if (fillW >= 1)
                {
                    using (GraphicsPath fill = RoundPath(pad, barY, Math.Max(fillW, barH), barH, barH / 2))
                    using (SolidBrush b = new SolidBrush(UsageColor(pct, colors[i])))
                        g.FillPath(b, fill);
                }

                // No reading means no glyph at all. An em dash in this corner is
                // the very first thing a fresh install shows, and two of them read
                // as "this thing is broken". An empty corner just looks like the
                // temperature is not part of the layout; the footer explains why
                // once, and the menu has the switch.
                double? t = tempOn[i] ? temps[i] : (double?)null;
                if (t != null)
                {
                    string tStr = ((int)Math.Round(t.Value)).ToString(CultureInfo.InvariantCulture) + "°C";
                    using (SolidBrush tb = new SolidBrush(TempColor(t)))
                    {
                        float tw = g.MeasureString(tStr, _fTemp, 1000, _typo).Width;
                        DrawText(g, tStr, _fTemp, tb, W - pad - tw, y + 1 * sc, halo);
                    }
                }

                y += rowH;
            }

            if (!any)
            {
                DrawText(g, L.T("检测项已全部关闭", "all sections off"), _fLabel, _bDim, pad, y + 3 * sc, halo);
                y += rowH;
            }

            float footY = H - 16 * sc;
            float chevX = W - pad - 5 * sc;
            float chevY = H - 11 * sc;
            _toggleRect = new RectangleF(W - pad - 15 * sc, H - 21 * sc, 17 * sc, 19 * sc);

            if (_cfg.ShowGraph)
            {
                float gh = H - y - 24 * sc;
                if (gh > 10)
                {
                    float gy = y + 1 * sc;
                    float gw = W - pad * 2;

                    using (Pen grid = new Pen(Track, 1))
                    {
                        grid.DashStyle = DashStyle.Dot;
                        g.DrawLine(grid, pad, gy + gh / 2, pad + gw, gy + gh / 2);
                    }

                    DrawSeries(g, _histCpu, CpuColor, pad, gy, gw, gh, sc);
                    DrawSeries(g, _histGpu, GpuColor, pad, gy, gw, gh, sc);
                }
            }

            // Say what is missing, in the corner, once - now that the rows no
            // longer carry an em dash, this line is the only hint that a
            // temperature could be here at all.
            string statStr = "";
            if (!_alive) statStr = L.T("采集中断", "stalled");
            else if (WantsTemps())
            {
                if (s.TempSrc == "none") statStr = L.T("需装 LHM", "needs LHM");
                else if (s.TempSrc != "lhm") statStr = L.T("温度需提权", "needs admin");
            }

            float statW = statStr.Length > 0
                ? g.MeasureString(statStr, _fSmall, 1000, _typo).Width
                : 0;

            // The footer is 214 px wide at 100% and the RAM string eats about
            // two thirds of it. When both want the same line the explanation
            // wins: a missing temperature needs an explanation far more than
            // the RAM number needs to be on screen every second.
            if (_cfg.ShowMem && Settings.Mem)
            {
                string ramStr = L.T("内存 ", "RAM ") + Bytes(s.MemUsed) + " / " + Bytes(s.MemTotal);
                float ramW = g.MeasureString(ramStr, _fSmall, 1000, _typo).Width;
                float ramLimit = statW > 0 ? chevX - 10 * sc - statW - 7 * sc : chevX;
                if (pad + ramW <= ramLimit)
                    DrawText(g, ramStr, _fSmall, _bDim, pad, footY, halo);
            }

            if (statW > 0)
                DrawText(g, statStr, _fSmall, _bDim, chevX - 10 * sc - statW, footY, halo);

            PointF[] chev;
            if (_cfg.ShowGraph)
            {
                chev = new PointF[]
                {
                    new PointF(chevX - 4 * sc, chevY - 2 * sc),
                    new PointF(chevX, chevY + 2 * sc),
                    new PointF(chevX + 4 * sc, chevY - 2 * sc)
                };
            }
            else
            {
                chev = new PointF[]
                {
                    new PointF(chevX - 2 * sc, chevY - 4 * sc),
                    new PointF(chevX + 2 * sc, chevY),
                    new PointF(chevX - 2 * sc, chevY + 4 * sc)
                };
            }
            if (halo)
            {
                using (Pen hp = new Pen(Color.FromArgb(200, 0, 0, 0), (float)(3.6 * sc)))
                {
                    hp.StartCap = LineCap.Round;
                    hp.EndCap = LineCap.Round;
                    hp.LineJoin = LineJoin.Round;
                    g.DrawLines(hp, chev);
                }
            }
            using (Pen cp = new Pen(Dim, (float)(1.4 * sc)))
            {
                cp.StartCap = LineCap.Round;
                cp.EndCap = LineCap.Round;
                cp.LineJoin = LineJoin.Round;
                g.DrawLines(cp, chev);
            }
        }

        private void DrawSeries(Graphics g, List<double> list, Color color,
                                float pad, float gy, float gw, float gh, float sc)
        {
            int n = list.Count;
            if (n < 2) return;
            PointF[] pts = new PointF[n];
            int offset = HistMax - n;
            for (int i = 0; i < n; i++)
            {
                float x = pad + (float)((offset + i) / (double)(HistMax - 1)) * gw;
                float v = (float)Math.Min(100.0, list[i]);
                float yy = gy + gh - (float)(v / 100.0) * gh;
                pts[i] = new PointF(x, yy);
            }
            using (Pen p = new Pen(color, (float)(1.5 * sc)))
            {
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, pts);
            }
        }

        private void DrawText(Graphics g, string text, Font font, Brush brush, float x, float y, bool halo)
        {
            // Whole pixels: half-pixel origins would soften every glyph.
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            if (halo)
            {
                // A 1px cross-shaped shadow instead of the old 8-way outline:
                // same legibility on bright wallpaper, half the thickness, so
                // small glyphs keep their shape.
                g.DrawString(text, font, _bHalo, ix, iy + 1, _typo);
                g.DrawString(text, font, _bHalo, ix, iy - 1, _typo);
                g.DrawString(text, font, _bHalo, ix + 1, iy, _typo);
                g.DrawString(text, font, _bHalo, ix - 1, iy, _typo);
            }
            g.DrawString(text, font, brush, ix, iy, _typo);
        }

        private void BuildFrame()
        {
            Size sz = WidgetSize();
            if (_frame != null && (_frame.Width != sz.Width || _frame.Height != sz.Height))
            {
                _frame.Dispose();
                _frame = null;
            }
            if (_frame == null) _frame = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_frame))
            {
                g.Clear(Color.Transparent);
                RenderFrame(g, sz.Width, sz.Height, !_cfg.Pure);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The visible surface comes entirely from UpdateLayeredWindow;
            // GDI painting here would only fight it.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

        // Push the current frame to the screen.
        private void ApplyFrame()
        {
            if (_frame == null || !IsHandleCreated) return;
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr bmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                bmp = _frame.GetHbitmap(Color.FromArgb(0));
                oldBmp = Native.SelectObject(memDc, bmp);
                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(Left, Top);
                Native.SIZE size = new Native.SIZE(_frame.Width, _frame.Height);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                blend.SourceConstantAlpha = _cfg.Pure
                    ? (byte)255
                    : (byte)Math.Round(255.0 * Math.Max(0.15, Math.Min(1.0, _cfg.Alpha)));
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0,
                                           ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (oldBmp != IntPtr.Zero) Native.SelectObject(memDc, oldBmp);
                if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ---------------------------------------------------------------- look
        private void ApplyLook()
        {
            // One code path for both modes: the frame bitmap decides what is
            // drawn (panel or no panel), SourceConstantAlpha decides how
            // strongly the whole surface blends with the desktop. No
            // TransparencyKey and no Region clipping - both of those defeat
            // antialiasing and were the reason the text looked rough.
            Size sz = WidgetSize();
            Size = sz;
            if (_frame != null) { _frame.Dispose(); _frame = null; }
            BuildFrame();
            ApplyFrame();
        }

        private void ToggleGraph()
        {
            int right = Right;
            int top = Top;
            _cfg.ShowGraph = !_cfg.ShowGraph;
            ApplyLook();
            Location = new Point(right - Width, top);
            SaveConfig();
        }

        private void SaveConfig()
        {
            if (_persist) _cfg.Save();
        }

        private void ChangeFont(int delta)
        {
            int right = Right;
            int top = Top;
            _cfg.FontSize = Math.Max(8, Math.Min(28, _cfg.FontSize + delta));
            _scale = _cfg.FontSize / 11.0;
            MakeFonts();
            ApplyLook();
            Location = new Point(right - Width, top);
            SaveConfig();
        }

        // ---------------------------------------------------------------- input
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_toggleRect.Contains(e.X, e.Y))
            {
                _togglePending = true;
                _dragging = false;
                return;
            }
            _dragging = true;
            _dragFrom = new Point(e.X, e.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging || e.Button != MouseButtons.Left) return;
            Point p = PointToScreen(new Point(e.X, e.Y));
            Location = new Point(p.X - _dragFrom.X, p.Y - _dragFrom.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                if (_togglePending)
                {
                    _togglePending = false;
                    ToggleGraph();
                    return;
                }
                if (_dragging)
                {
                    _dragging = false;
                    _cfg.X = Location.X;
                    _cfg.Y = Location.Y;
                    SaveConfig();
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                BuildMenu();
                _menu.Show(this, e.Location);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                double d = e.Delta > 0 ? 0.05 : -0.05;
                _cfg.Alpha = Math.Round(Math.Max(0.15, Math.Min(1.0, _cfg.Alpha + d)), 2);
                if (!_cfg.Pure) ApplyFrame();
            }
            else
            {
                ChangeFont(e.Delta > 0 ? 1 : -1);
            }
            SaveConfig();
        }

        // ----------------------------------------------------------------- menu
        private static ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            if (onClick != null) mi.Click += onClick;
            return mi;
        }

        private static ToolStripMenuItem Header(string text)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Enabled = false;
            return mi;
        }

        private void BuildMenu()
        {
            _menu.Items.Clear();
            _menu.Items.Add(Header(L.T("字体  ", "Font  ") + _cfg.FontSize + " px"));
            _menu.Items.Add(Item(L.T("  放大字号", "  Larger"), delegate { ChangeFont(1); }));
            _menu.Items.Add(Item(L.T("  缩小字号", "  Smaller"), delegate { ChangeFont(-1); }));
            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add(Header(L.T("背景透明度  ", "Opacity  ") + (int)Math.Round(_cfg.Alpha * 100) + "%"));
            _menu.Items.Add(Item(L.T("  更不透明", "  More opaque"), delegate
            {
                _cfg.Alpha = Math.Round(Math.Min(1.0, _cfg.Alpha + 0.07), 2);
                if (!_cfg.Pure) ApplyFrame();
                SaveConfig();
            }));
            _menu.Items.Add(Item(L.T("  更透明", "  More transparent"), delegate
            {
                _cfg.Alpha = Math.Round(Math.Max(0.15, _cfg.Alpha - 0.07), 2);
                if (!_cfg.Pure) ApplyFrame();
                SaveConfig();
            }));
            ToolStripMenuItem pure = Item(L.T("完全透明背景（只留文字）", "Transparent background (text only)"), delegate
            {
                _cfg.Pure = !_cfg.Pure;
                ApplyLook();
                SaveConfig();
            });
            pure.Checked = _cfg.Pure;
            _menu.Items.Add(pure);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem graph = Item(L.T("趋势图", "Trend graph"), delegate { ToggleGraph(); });
            graph.Checked = _cfg.ShowGraph;
            _menu.Items.Add(graph);

            ToolStripMenuItem mem = Item(L.T("显示内存行", "Show memory line"), delegate
            {
                _cfg.ShowMem = !_cfg.ShowMem;
                if (_frame != null) { _frame.Dispose(); _frame = null; }
                BuildFrame();
                ApplyFrame();
                SaveConfig();
            });
            mem.Checked = _cfg.ShowMem;
            _menu.Items.Add(mem);

            ToolStripMenuItem top = Item(L.T("总在最前", "Always on top"), delegate
            {
                _cfg.TopMost = !_cfg.TopMost;
                TopMost = _cfg.TopMost;
                SaveConfig();
            });
            top.Checked = _cfg.TopMost;
            _menu.Items.Add(top);

            _menu.Items.Add(new ToolStripSeparator());

            // Same switches the web panel exposes, writing the same settings
            // file: turning a sensor off here must also stop the sampler, not
            // merely hide the row.
            ToolStripMenuItem sensors = new ToolStripMenuItem(L.T("检测项", "Sensors"));
            AddToggle(sensors, "cpu", L.T("CPU 占用", "CPU load"), Settings.Cpu);
            AddToggle(sensors, "cpuTemp", L.T("CPU 温度 / 功耗", "CPU temp / power"), Settings.CpuTemp);
            AddToggle(sensors, "gpu", L.T("GPU 占用", "GPU load"), Settings.Gpu);
            AddToggle(sensors, "gpuTemp", L.T("GPU 温度 / 热点 / 功耗", "GPU temp / hotspot / power"), Settings.GpuTemp);
            AddToggle(sensors, "mem", L.T("内存", "Memory"), Settings.Mem);
            AddToggle(sensors, "net", L.T("网络速率", "Network"), Settings.Net);
            AddToggle(sensors, "disk", L.T("磁盘读写 / 活动", "Disk I/O"), Settings.Disk);
            AddToggle(sensors, "diskTemp", L.T("SSD 温度（读 SMART）", "SSD temp (SMART)"), Settings.DiskTemp);
            AddToggle(sensors, "fans", L.T("风扇转速", "Fans"), Settings.Fans);
            AddToggle(sensors, "board", L.T("主板温度", "Motherboard temp"), Settings.Board);
            AddToggle(sensors, "proc", L.T("进程占用榜", "Process list"), Settings.Proc);
            _menu.Items.Add(sensors);

            ToolStripMenuItem lang = new ToolStripMenuItem(L.T("语言 / Language", "Language / 语言"));
            ToolStripMenuItem zh = Item("中文", delegate { ChangeLang("zh"); });
            zh.Checked = Settings.Lang != "en";
            ToolStripMenuItem en = Item("English", delegate { ChangeLang("en"); });
            en.Checked = Settings.Lang == "en";
            lang.DropDownItems.Add(zh);
            lang.DropDownItems.Add(en);
            _menu.Items.Add(lang);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(Item(L.T("打开网页面板", "Open dashboard"), delegate { OpenWeb(); }));

            if (!Program.Elevated)
                _menu.Items.Add(Item(L.T("以管理员身份重启（读 CPU 温度）", "Restart as administrator (CPU temp)"),
                                     delegate { RestartElevated(); }));

            _menu.Items.Add(Item(L.T("退出", "Quit"), delegate { Close(); }));
        }

        private void AddToggle(ToolStripMenuItem parent, string key, string text, bool on)
        {
            ToolStripMenuItem mi = Item(text, delegate { ChangeToggle(key); });
            mi.Checked = on;
            parent.DropDownItems.Add(mi);
        }

        private void ChangeToggle(string key)
        {
            bool on = !Settings.Get(key);
            _localSettings = true;
            try { Settings.Set(key, on ? "true" : "false"); }
            finally { _localSettings = false; }
            AfterSettingsChange();
        }

        private void ChangeLang(string lang)
        {
            _localSettings = true;
            try { Settings.Set("lang", lang); }
            finally { _localSettings = false; }
            AfterSettingsChange();
        }

        /// <summary>Re-render at the new size and keep the right edge anchored -
        /// the window lives in the top-right corner, so it must grow leftwards.</summary>
        private void AfterSettingsChange()
        {
            if (!IsHandleCreated) return;
            int right = Right, top = Top;
            ApplyLook();
            Location = new Point(right - Width, top);
        }

        private void OpenWeb()
        {
            // Ask the sampler which port it actually bound: 8770 is only the
            // start of the scan, so a busy port moves the panel to 8771+.
            int port = _sampler.PanelPort > 0 ? _sampler.PanelPort : Program.PanelPort;
            try
            {
                using (System.Net.Sockets.TcpClient tcp = new System.Net.Sockets.TcpClient())
                {
                    tcp.Connect("127.0.0.1", port);
                }
                Process.Start(new ProcessStartInfo("http://127.0.0.1:" + port + "/") { UseShellExecute = true });
            }
            catch
            {
                // Never fall back to launching the exe again: the mutex would
                // make the new copy reclaim this one, killing the panel the
                // user is trying to open.
                MessageBox.Show(L.T("网页面板还没有就绪（端口 " + port + " 未监听）。\n\n" +
                                    "如果是刚启动，请等几秒再试；若菜单里显示面板已关闭，\n" +
                                    "用「启动监测器.bat」重新启动即可。",
                                    "The dashboard is not ready (nothing listening on port " + port + ").\n\n" +
                                    "Give it a few seconds after launch. If it never comes up, start\n" +
                                    "the widget again with 启动监测器.bat."),
                                L.T("CPU / GPU 监测器", "CPU / GPU Monitor"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void RestartElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.Arguments = "--no-elevate";
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Program.Dir;
                Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                Log.Write("elevate failed: " + ex.Message);
            }
        }

        // A clone of the current frame, for tests that need the rendered image.
        public Bitmap FrameCopy()
        {
            BuildFrame();
            return (Bitmap)_frame.Clone();
        }

        // ----------------------------------------------------------------- tick
        private void OnTick(object sender, EventArgs e)
        {
            int v = _sampler.Version;
            long last = _sampler.LastTicks;
            bool wasAlive = _alive;
            // No sample yet is not the same as a dead sampler: only complain
            // once real data has stopped arriving.
            _alive = last == 0 || (DateTime.UtcNow.Ticks - last) < TimeSpan.TicksPerSecond * 4;
            if (v == _seenVersion && wasAlive == _alive) return;
            if (v != _seenVersion)
            {
                Snapshot s = _sampler.Current;
                _histCpu.Add(s.Cpu);
                _histGpu.Add(s.Gpu);
                while (_histCpu.Count > HistMax) _histCpu.RemoveAt(0);
                while (_histGpu.Count > HistMax) _histGpu.RemoveAt(0);
                _seenVersion = v;
            }
            BuildFrame();
            ApplyFrame();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyFrame();
            _tick.Start();
            Log.Write("widget shown " + Size.Width + "x" + Size.Height + " scale=" + _scale.ToString("0.00", CultureInfo.InvariantCulture));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _tick.Stop();
            _cfg.X = Location.X;
            _cfg.Y = Location.Y;
            SaveConfig();
            _sampler.Dispose();
            Log.Write("widget closed");
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeFonts();
                if (_frame != null) _frame.Dispose();
                _typo.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- program
    internal static class Program
    {
        public static string Dir;
        public static string MonitorDir;
        /// <summary>Folder holding index.html / app.js, resolved once at start-up.</summary>
        public static string WebDir;
        /// <summary>Where history and spike logs go; overridable for tests.</summary>
        public static string DataDir;
        /// <summary>Preferred dashboard port; the server walks upward if taken.</summary>
        public static int PanelPort = 8770;
        public static bool Elevated;

        /// <summary>
        /// The dashboard files are plain files on disk, not embedded in the exe,
        /// so a copy that gets moved away from its folder still needs to find
        /// them. Probed in order: beside the exe, beside the root, one level up.
        /// The first hit wins; with no hit we keep the first candidate so the
        /// "folder missing" log line names a path the user can act on.
        /// </summary>
        private static string ResolveWebDir(string exeDir)
        {
            string up = Path.GetDirectoryName(exeDir);
            string[] probe = new string[]
            {
                Path.Combine(exeDir, "web"),
                Path.Combine(MonitorDir, "web"),
                Path.Combine(String.IsNullOrEmpty(up) ? exeDir : up, "web")
            };
            for (int i = 0; i < probe.Length; i++)
            {
                try { if (Directory.Exists(probe[i])) return probe[i]; }
                catch { }
            }
            return probe[0];
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // Render at native pixels if Windows display scaling is ever
            // raised above 100% - without this the DWM would bitmap-stretch
            // the whole window and every glyph would go soft.
            try { Native.SetProcessDPIAware(); }
            catch { }

            // The exe lives in ..\bin, but its settings, log and test images
            // belong in desktop\ next to the old PowerShell widget, so an
            // existing widget-config.json (position, font size) keeps working.
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string parent = Path.GetDirectoryName(exeDir);
            if (String.Equals(Path.GetFileName(exeDir), "bin", StringComparison.OrdinalIgnoreCase) && parent != null)
            {
                MonitorDir = parent;
                string desk = Path.Combine(parent, "desktop");
                Dir = Directory.Exists(desk) ? desk : parent;
            }
            else
            {
                // Anywhere else: treat the exe's own folder as the root. Using
                // its parent here used to send data\ and web\ to the folder
                // *above* wherever the user happened to unzip, which for a
                // Desktop copy meant C:\Users\<name>\data.
                Dir = exeDir;
                MonitorDir = exeDir;
            }
            WebDir = ResolveWebDir(exeDir);
            Elevated = IsElevated();

            bool selftest = false, smoke = false, diag = false, capture = false, noElevate = false;
            bool panel = true, panelSet = false, lan = false, headless = false;
            int autoClose = 0, fontOverride = 0, interval = 1000;
            double alphaOverride = 0;
            bool persist = true;
            string dataOverride = null;
            int portOverride = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--selftest": selftest = true; persist = false; break;
                    case "--smoke": smoke = true; persist = false; break;
                    case "--capture": capture = true; persist = false; break;
                    case "--diag": diag = true; persist = false; break;
                    case "--no-elevate": noElevate = true; break;
                    case "--no-save": persist = false; break;
                    case "--panel": panel = true; panelSet = true; break;
                    case "--no-panel": panel = false; panelSet = true; break;
                    case "--lan": lan = true; break;
                    case "--headless": headless = true; break;
                    case "--data-dir":
                        if (i + 1 < args.Length) dataOverride = args[i + 1];
                        i++;
                        break;
                    case "--port":
                        if (i + 1 < args.Length) int.TryParse(args[i + 1], out portOverride);
                        i++;
                        break;
                    case "--font-size":
                        if (i + 1 < args.Length) int.TryParse(args[i + 1], out fontOverride);
                        i++;
                        break;
                    case "--alpha":
                        if (i + 1 < args.Length)
                            double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out alphaOverride);
                        i++;
                        break;
                    case "--interval-ms":
                        if (i + 1 < args.Length) int.TryParse(args[i + 1], out interval);
                        i++;
                        break;
                    case "--autoclose":
                        if (i + 1 < args.Length) int.TryParse(args[i + 1], out autoClose);
                        i++;
                        break;
                }
            }
            if (autoClose > 0 || fontOverride > 0 || alphaOverride > 0) persist = false;

            DataDir = String.IsNullOrEmpty(dataOverride)
                ? Path.Combine(MonitorDir, "data")
                : dataOverride;
            int panelPort = portOverride > 0 ? portOverride : 8770;
            Program.PanelPort = panelPort;

            // Test and screenshot runs must not squat on the dashboard port -
            // that would leave the real panel unreachable while they finish.
            if (!panelSet && (selftest || diag || smoke || capture || headless || autoClose > 0)) panel = false;

            // CPU temperature needs administrator rights, so ask once, before
            // any window or mutex exists: the elevated copy becomes the real
            // process and this one just steps aside. Declining UAC is fine -
            // the panel simply runs with GPU temperature only.
            if (!selftest && !diag && !smoke && !capture && autoClose == 0 && !noElevate && !Elevated)
            {
                if (RelaunchElevated(args)) return 0;
            }

            Config cfg = Config.Load();
            Settings.Load();
            if (fontOverride > 0) cfg.FontSize = fontOverride;
            if (alphaOverride > 0) cfg.Alpha = alphaOverride;

            if (selftest) return SelfTest(cfg);
            if (diag) return Diag(interval);

            // One panel at a time, shared with the PowerShell build so an old
            // copy cannot sit invisible on top of the new one.
            bool testRun = smoke || capture || headless || autoClose > 0;
            string mutexName = testRun ? @"Local\HardwareMonitorWidget-test" : @"Local\HardwareMonitorWidget";
            bool created;
            using (Mutex mutex = new Mutex(false, mutexName, out created))
            {
                bool own = false;
                try { own = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { own = true; }
                if (!own)
                {
                    // A test run is never allowed to reclaim: Reclaim() matches by
                    // image name and window title, so a --headless endpoint test
                    // would happily kill the widget the user is actually using.
                    // Yield instead, and let the caller retry once ours exit.
                    if (testRun)
                    {
                        Log.Write("another test instance is running; exiting");
                        return 0;
                    }
                    if (!Reclaim()) return 0;
                    try { own = mutex.WaitOne(4000); } catch { own = false; }
                    if (!own)
                    {
                        MessageBox.Show("无法关闭正在运行的面板（大概是权限不同）。\n\n请在现有面板上右键 → 退出，然后再启动。",
                                        "CPU / GPU 监测器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 0;
                    }
                }

                Sampler sampler = new Sampler(interval, panel, lan);
                sampler.Start();

                // Headless: serve the panel and keep sampling, but never create
                // a window. CI and the endpoint tests need this - a winexe with
                // a form would block on a desktop that does not exist.
                if (headless)
                {
                    Wait(autoClose > 0 ? autoClose * 1000 : 4000);
                    sampler.Dispose();
                    return 0;
                }

                Application.EnableVisualStyles();
                using (WidgetForm form = new WidgetForm(cfg, sampler, persist))
                {
                    if (cfg.X >= 0 && cfg.Y >= 0)
                        form.Location = new Point(cfg.X, cfg.Y);
                    else
                    {
                        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                        form.Location = new Point(wa.Right - form.Width - 28, wa.Top + 28);
                    }

                    if (smoke)
                    {
                        form.Show();
                        Wait(1200);
                        WriteSmoke(form);
                        form.Close();
                        return 0;
                    }

                    if (capture)
                    {
                        // Grab the composited desktop under the panel: this is
                        // the only way to prove the transparent modes really do
                        // let the wallpaper through instead of painting a box.
                        form.Show();
                        Wait(2200);
                        Rectangle b = form.Bounds;
                        using (Bitmap shot = new Bitmap(b.Width, b.Height))
                        {
                            using (Graphics g = Graphics.FromImage(shot))
                                g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                            shot.Save(Path.Combine(Dir, "capture.png"), ImageFormat.Png);
                        }
                        Say(String.Format("CAPTURE OK {0}x{1} pure={2} alpha={3}",
                            b.Width, b.Height, cfg.Pure, cfg.Alpha.ToString("0.00", CultureInfo.InvariantCulture)));
                        form.Close();
                        return 0;
                    }

                    if (autoClose > 0)
                    {
                        System.Windows.Forms.Timer close = new System.Windows.Forms.Timer();
                        close.Interval = autoClose * 1000;
                        close.Tick += delegate
                        {
                            close.Stop();
                            form.Close();
                        };
                        close.Start();
                    }

                    Application.Run(form);
                }
            }
            return 0;
        }

        private static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Start a second, elevated copy of this exe. "--no-elevate" is passed
        // along so the child never tries to elevate again (which would loop if
        // the token somehow stays filtered).
        private static bool RelaunchElevated(string[] args)
        {
            try
            {
                StringBuilder quoted = new StringBuilder("--no-elevate");
                foreach (string a in args)
                    quoted.Append(' ').Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);

                Native.SHELLEXECUTEINFO info = new Native.SHELLEXECUTEINFO();
                info.cbSize = Marshal.SizeOf(typeof(Native.SHELLEXECUTEINFO));
                info.fMask = Native.SEE_MASK_NOCLOSEPROCESS | Native.SEE_MASK_NOASYNC;
                info.lpVerb = "runas";
                info.lpFile = Application.ExecutablePath;
                info.lpParameters = quoted.ToString();
                info.lpDirectory = Dir;
                info.nShow = 1;

                if (!Native.ShellExecuteExW(ref info))
                {
                    Log.Write("elevation declined: error " + Marshal.GetLastWin32Error());
                    return false;
                }
                if (info.hProcess != IntPtr.Zero) Native.CloseHandle(info.hProcess);
                Log.Write("relaunched elevated");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("elevation failed: " + ex.Message);
                return false;
            }
        }

        private static void Wait(int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }
        }

        private static void WriteSmoke(WidgetForm form)
        {
            try
            {
                // The window surface is a layered bitmap now, so DrawToBitmap
                // would come back blank; save the frame itself instead.
                using (Bitmap bmp = form.FrameCopy())
                {
                    bmp.Save(Path.Combine(Dir, "smoketest.png"), ImageFormat.Png);
                }
                Log.Write("SMOKE OK size=" + form.Width + "x" + form.Height);
            }
            catch (Exception ex)
            {
                Log.Write("smoke failed: " + ex.Message);
            }
        }

        // Kill a stale panel so a re-launch actually replaces it. Only touches
        // processes that are clearly ours: same exe name, or a top-level window
        // titled "CPU / GPU" (the PowerShell build).
        private static bool Reclaim()
        {
            List<int> targets = new List<int>();
            foreach (Process p in Process.GetProcessesByName("CPU-GPU监视器"))
                if (p.Id != Process.GetCurrentProcess().Id) targets.Add(p.Id);

            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                StringBuilder sb = new StringBuilder(128);
                Native.GetWindowTextW(hwnd, sb, 128);
                if (sb.ToString() != "CPU / GPU") return true;
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid > 0 && (int)pid != Process.GetCurrentProcess().Id) targets.Add((int)pid);
                return true;
            }, IntPtr.Zero);

            if (targets.Count == 0) return true;

            DialogResult ans = MessageBox.Show(
                "监测器已经在运行。\n\n要关掉现有实例、换成这一份吗？\n（刚更新过版本的话，选「是」即可替换）",
                "CPU / GPU 监测器", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) return false;

            foreach (int pid in targets)
            {
                try
                {
                    Process.GetProcessById(pid).Kill();
                    Log.Write("replaced running instance pid=" + pid);
                }
                catch (Exception ex) { Log.Write("kill " + pid + " failed: " + ex.Message); }
            }
            Thread.Sleep(700);
            return true;
        }

        // ---------------------------------------------------------------- modes
        // A winexe has no console, so diagnostics go to widget.log as well.
        private static void Say(string msg)
        {
            Log.Write(msg);
            try { Console.WriteLine(msg); } catch { }
        }

        private static int Diag(int interval)
        {
            Sampler s = new Sampler(interval, false, false);
            s.Start();
            Thread.Sleep(2600);
            Snapshot snap = s.Current;
            Say(String.Format(CultureInfo.InvariantCulture,
                "diag cpu={0:0.0}% gpu={1:0.0}% mem={2:0.0}/{3:0.0}GB temp={4}/{5} src={6} ver={7} cost={8}ms",
                snap.Cpu, snap.Gpu, snap.MemUsed / 1073741824.0, snap.MemTotal / 1073741824.0,
                Fmt(snap.CpuTemp), Fmt(snap.GpuTemp), snap.TempSrc, s.Version, snap.Cost)
                + "  |  " + s.GpuInfo);
            s.Dispose();
            return 0;
        }

        private static string Fmt(double? v)
        {
            return v == null ? "null" : v.Value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static int SelfTest(Config cfg)
        {
            Sampler fake = new Sampler(1000, false, false);
            Snapshot s = new Snapshot();
            s.Cpu = 37; s.Gpu = 64;
            s.CpuTemp = 52; s.GpuTemp = 61; s.GpuHot = 69;
            s.MemUsed = 12.4 * 1073741824.0; s.MemTotal = 31.0 * 1073741824.0;
            s.TempSrc = "lhm";
            fake.Current = s;
            fake.Version = 1;
            fake.LastTicks = DateTime.UtcNow.Ticks;

            WidgetForm form = new WidgetForm(cfg, fake, false);
            // seed the history through the same path the timer would use
            for (int i = 0; i < 90; i++)
            {
                Snapshot h = new Snapshot();
                h.Cpu = 30 + 25 * Math.Sin(i / 7.0) + 6 * Math.Sin(i / 2.1);
                h.Gpu = 45 + 30 * Math.Sin(i / 11.0 + 1) + 8 * Math.Sin(i / 3.3);
                h.CpuTemp = 52; h.GpuTemp = 61;
                h.MemUsed = s.MemUsed; h.MemTotal = s.MemTotal;
                h.TempSrc = "lhm";
                fake.Current = h;
                fake.Version = i + 2;
                typeof(WidgetForm).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, new object[] { null, EventArgs.Empty });
            }
            fake.Current = s;
            fake.Version = 999;
            fake.LastTicks = DateTime.UtcNow.Ticks;
            typeof(WidgetForm).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { null, EventArgs.Empty });
            typeof(WidgetForm).GetMethod("BuildFrame", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, null);

            Bitmap frame = (Bitmap)typeof(WidgetForm)
                .GetField("_frame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            frame.Save(Path.Combine(Dir, "selftest.png"), ImageFormat.Png);

            // no-panel variant on a light background, to check contrast
            Size sz = new Size(frame.Width, frame.Height);
            using (Bitmap light = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(light))
                {
                    g.Clear(Color.FromArgb(238, 240, 244));
                    typeof(WidgetForm).GetMethod("RenderFrame", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, new object[] { g, sz.Width, sz.Height, false });
                }
                light.Save(Path.Combine(Dir, "selftest-light.png"), ImageFormat.Png);
            }

            // What a first run looks like with no LibreHardwareMonitor: the
            // temperature corner must be blank, not a pair of em dashes, and the
            // footer carries the one-line hint. Kept as a fixture so this state
            // can be inspected on a machine that does have the sensor library.
            Snapshot cold = new Snapshot();
            cold.Cpu = 37; cold.Gpu = 64;
            cold.CpuTemp = null; cold.GpuTemp = null; cold.GpuHot = null;
            cold.MemUsed = s.MemUsed; cold.MemTotal = s.MemTotal;
            cold.TempSrc = "none";
            fake.Current = cold;
            fake.Version = 1000;
            fake.LastTicks = DateTime.UtcNow.Ticks;
            typeof(WidgetForm).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { null, EventArgs.Empty });
            typeof(WidgetForm).GetMethod("BuildFrame", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, null);
            ((Bitmap)typeof(WidgetForm)
                .GetField("_frame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form))
                .Save(Path.Combine(Dir, "selftest-notemp.png"), ImageFormat.Png);

            // back to the warm sample so the collapsed frame stays comparable
            fake.Current = s;
            fake.Version = 1001;
            fake.LastTicks = DateTime.UtcNow.Ticks;
            typeof(WidgetForm).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { null, EventArgs.Empty });

            cfg.ShowGraph = false;
            typeof(WidgetForm).GetMethod("ApplyLook", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, null);
            Bitmap collapsed = (Bitmap)typeof(WidgetForm)
                .GetField("_frame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
            collapsed.Save(Path.Combine(Dir, "selftest-collapsed.png"), ImageFormat.Png);

            Say(String.Format(CultureInfo.InvariantCulture,
                "SELFTEST OK {0}x{1} (collapsed {2}x{3})", sz.Width, sz.Height, collapsed.Width, collapsed.Height));
            form.Dispose();
            return 0;
        }
    }
}

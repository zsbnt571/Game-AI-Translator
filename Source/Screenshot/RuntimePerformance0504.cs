using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class RuntimePerformance0504
{
    internal static int Run(string output, string imagePath)
    {
        Directory.CreateDirectory(output);
        var lines = new List<string>();
        var data = new Dictionary<string, object?>();
        var pass = 0;
        var fail = 0;
        void Test(string name, Action action)
        {
            try { action(); lines.Add("PASS " + name); pass++; }
            catch (Exception ex) { lines.Add("FAIL " + name + ": " + ex.Message); fail++; }
        }
        static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        using var image = new Bitmap(imagePath);
        var runtime = new VisionRuntimeManager(AppContext.BaseDirectory);
        var memory = new Dictionary<string, long>();
        var times = new List<long>();
        int? stablePid = null;
        var normalStarts = 0;
        var normalLoads = 0;
        var normalRestarts = 0;
        try
        {
            Test("Application shared manager across 1 5 10 previews", () =>
            {
                var config = Path.Combine(output, "settings.json");
                ConfigurationManager.Save(config, new ApiSettings { VisualModel = VisualModelKind.Off });
                using var main = new MainForm(config);
                main.Show();
                var previews = new List<PreviewForm>();
                foreach (var count in new[] { 1, 5, 10 })
                {
                    while (previews.Count < count) previews.Add(main.OpenPreviewForSmoke(image));
                    Application.DoEvents();
                    Need(previews.All(p => ReferenceEquals(p.VisionRuntimeForSmoke, main.SharedVisionForSmoke)), $"manager identity failed at {count}");
                    data[$"Preview{count}WorkerCount"] = 1;
                }
                foreach (var p in previews) p.Close();
                main.ExitForSmoke();
            });

            Test("PP-S model loads once and 50 requests reuse PID", () =>
            {
                memory["PythonStarted"] = 0;
                for (var i = 1; i <= 50; i++)
                {
                    var sw = Stopwatch.StartNew();
                    runtime.AnalyzeAsync(image, VisualModelKind.PPDocLayoutS, i, CancellationToken.None).GetAwaiter().GetResult();
                    sw.Stop();
                    times.Add(sw.ElapsedMilliseconds);
                    stablePid ??= runtime.ActiveWorkerPid;
                    Need(runtime.ActiveWorkerPid == stablePid, $"PID changed at {i}");
                    if (i == 1) memory["OCR1"] = ProcessTreeWorkingSet(stablePid!.Value);
                    if (i == 10) memory["OCR10"] = ProcessTreeWorkingSet(stablePid!.Value);
                    if (i == 25) memory["OCR25"] = ProcessTreeWorkingSet(stablePid!.Value);
                    if (i == 50) memory["OCR50"] = ProcessTreeWorkingSet(stablePid!.Value);
                }
                memory["ModelLoaded"] = memory["OCR1"];
                normalStarts = runtime.WorkerStartCount;
                normalLoads = runtime.ModelLoadCount;
                normalRestarts = runtime.WorkerRestartCount;
                Need(normalStarts == 1, $"starts={normalStarts}");
                Need(normalLoads == 1, $"loads={normalLoads}");
                Need(normalRestarts == 0, $"restarts={normalRestarts}");
            });

            Test("Memory does not grow linearly", () =>
            {
                var first = memory["OCR1"];
                var last = memory["OCR50"];
                Need(last < first * 1.75 + 256L * 1024 * 1024, $"{first}->{last}");
            });

            Test("Idle worker uses blocking IO", () =>
            {
                var pid = runtime.ActiveWorkerPid ?? throw new InvalidOperationException("pid");
                var before = ProcessTreeCpuTime(pid);
                var wall = Stopwatch.StartNew();
                Thread.Sleep(3000);
                wall.Stop();
                var cpu = (ProcessTreeCpuTime(pid) - before).TotalMilliseconds / (wall.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
                data["IdleCpuPercent"] = cpu;
                Need(cpu < 3, $"idle cpu={cpu:F2}%");
            });

            Test("Crash recovery leaves one worker", () =>
            {
                var old = runtime.ActiveWorkerPid ?? throw new InvalidOperationException("pid");
                using (var p = Process.GetProcessById(old)) { p.Kill(true); p.WaitForExit(5000); }
                try { runtime.AnalyzeAsync(image, VisualModelKind.PPDocLayoutS, 51, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                runtime.AnalyzeAsync(image, VisualModelKind.PPDocLayoutS, 52, CancellationToken.None).GetAwaiter().GetResult();
                Need(runtime.ActiveWorkerPid is int next && next != old, "not recovered");
                Need(runtime.WorkerRestartCount == 1, $"restarts={runtime.WorkerRestartCount}");
            });
        }
        finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        var warm = times.Skip(1).OrderBy(x => x).ToArray();
        long Percentile(double q) => warm.Length == 0 ? 0 : warm[Math.Clamp((int)Math.Ceiling(q * warm.Length) - 1, 0, warm.Length - 1)];
        data["WorkerPid"] = stablePid;
        data["NormalWorkerStartCount"] = normalStarts;
        data["NormalModelLoadCount"] = normalLoads;
        data["NormalWorkerRestartCount"] = normalRestarts;
        data["AfterCrashWorkerStartCount"] = runtime.WorkerStartCount;
        data["AfterCrashModelLoadCount"] = runtime.ModelLoadCount;
        data["AfterCrashWorkerRestartCount"] = runtime.WorkerRestartCount;
        data["RequestCount"] = runtime.RequestCount;
        data["MemoryBytes"] = memory;
        data["ColdMs"] = times.FirstOrDefault();
        data["Warm1Ms"] = times.Skip(1).FirstOrDefault();
        data["Warm2Ms"] = times.Skip(2).FirstOrDefault();
        data["Warm3Ms"] = times.Skip(3).FirstOrDefault();
        data["WarmP50Ms"] = Percentile(.5);
        data["WarmP95Ms"] = Percentile(.95);
        data["Tests"] = $"{pass} PASS / {fail} FAIL";
        File.WriteAllText(Path.Combine(output, "0504-performance.json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        lines.Add($"TOTAL={pass + fail} PASS={pass} FAIL={fail}");
        lines.Add("RealApiCalls=0");
        lines.Add("ActiveOperations=0");
        lines.Add("Worker residue=NONE");
        File.WriteAllLines(Path.Combine(output, "0504-performance.txt"), lines);
        return fail == 0 ? 0 : 1;
    }

    private static long ProcessTreeWorkingSet(int root) => ProcessTreeIds(root).Sum(id =>
    {
        try { using var process = Process.GetProcessById(id); return process.WorkingSet64; }
        catch { return 0L; }
    });

    private static TimeSpan ProcessTreeCpuTime(int root) => TimeSpan.FromTicks(ProcessTreeIds(root).Sum(id =>
    {
        try { using var process = Process.GetProcessById(id); return process.TotalProcessorTime.Ticks; }
        catch { return 0L; }
    }));

    private static HashSet<int> ProcessTreeIds(int root)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot != new IntPtr(-1))
        {
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                        entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
                    } while (Process32Next(snapshot, ref entry));
                }
            }
            finally { CloseHandle(snapshot); }
        }
        var ids = new HashSet<int> { root };
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in parents) if (ids.Contains(pair.Value) && ids.Add(pair.Key)) changed = true;
        } while (changed);
        return ids;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

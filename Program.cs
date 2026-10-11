using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WukongBench;

public static class Program
{
    static async Task<int> Main(string[] args)
    {
        var cfg = AppConfig.Load(args);
        Directory.CreateDirectory(cfg.OutputDirectory);

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This runner requires Windows.");
            return 2;
        }

        var install = SteamLocator.Find(cfg.BenchmarkRoot);
        Console.WriteLine($"Benchmark root: {install.Root}");

        var settings = Path.Combine(install.Root, @"b1\Saved\Config\Windows\GameUserSettings.ini");
        var launcher = Path.Combine(install.Root, "b1_benchmark.exe");
        var history = cfg.HistoryDirectory;

        if (!File.Exists(settings))
            throw new FileNotFoundException("GameUserSettings.ini not found.", settings);
        if (!File.Exists(launcher))
            throw new FileNotFoundException("b1_benchmark.exe not found.", launcher);
        Directory.CreateDirectory(history);

        var original = File.ReadAllText(settings, Encoding.UTF8);
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var runDir = Path.Combine(cfg.OutputDirectory, runId);
        Directory.CreateDirectory(runDir);

        try
        {
            var native = IniDocument.Parse(original);
            var cpuProfile = Profiles.Cpu(native);
            var gpuProfile = Profiles.Gpu(native);

            Console.WriteLine("\nCPU profile:");
            Console.WriteLine(cpuProfile.Description);
            Console.WriteLine("\nGPU profile:");
            Console.WriteLine(gpuProfile.Description);

            if (cfg.PrepareOnly)
            {
                File.WriteAllText(Path.Combine(runDir, "cpu.ini"), cpuProfile.IniText);
                File.WriteAllText(Path.Combine(runDir, "gpu.ini"), gpuProfile.IniText);
                Console.WriteLine($"\nPrepared profiles: {runDir}");
                return 0;
            }

            var cpu = await RunPassAsync("CPU", cpuProfile, settings, launcher, history, runDir, cfg);
            var gpu = await RunPassAsync("GPU", gpuProfile, settings, launcher, history, runDir, cfg);

            var report = Report.Build(cpu, gpu, runDir);
            File.WriteAllText(Path.Combine(runDir, "report.txt"), report, Encoding.UTF8);
            Console.WriteLine("\n" + report);
            return 0;
        }
        finally
        {
            File.WriteAllText(settings, original, Encoding.UTF8);
            Console.WriteLine("\nOriginal GameUserSettings.ini restored.");
        }
    }

    static async Task<PassResult> RunPassAsync(
        string name, Profile profile, string settings, string launcher,
        string history, string runDir, AppConfig cfg)
    {
        Console.WriteLine($"\n=== {name} PASS ===");
        File.WriteAllText(settings, profile.IniText, Encoding.UTF8);

        var before = Directory.GetFiles(history, "*")
            .ToDictionary(x => x, x => File.GetLastWriteTimeUtc(x));

        var startUtc = DateTime.UtcNow;

        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = launcher,
            WorkingDirectory = Path.GetDirectoryName(launcher)!,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Could not start benchmark.");

        Console.WriteLine("Benchmark process started.");

        if (!cfg.SkipUiAutomation)
        {
            await UiAutomation.WaitAndStartBenchmarkAsync(p.Id, cfg);
        }
        else
        {
            Console.WriteLine("UI automation disabled. Start the benchmark manually.");
        }

        var resultFile = await ResultWatcher.WaitForNewJsonAsync(
            history, before, TimeSpan.FromMinutes(cfg.TimeoutMinutes), startUtc);

        var json = await File.ReadAllTextAsync(resultFile);
        var result = JsonSerializer.Deserialize<BenchmarkResult>(json)
                     ?? throw new InvalidDataException("Invalid benchmark JSON.");

        File.Copy(resultFile, Path.Combine(runDir, $"{name.ToLowerInvariant()}_raw.json"), true);

        Console.WriteLine($"{name}: {result.FPSAvg:F1} FPS avg, GPU {result.GPUAvg:F1}%, CPU {result.CPUAvg:F1}%");
        Console.WriteLine($"Result: {resultFile}");

        try
        {
            // Попытаемся корректно завершить запущенные процессы бенчмарка.
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            catch { }

            // Иногда сам лаунчер порождает отдельный процесс игры (b1-Win64-Shipping или b1-benchmark).
            // Убедимся, что они тоже завершены перед следующим прогоном.
            foreach (var procName in new[] { "b1-Win64-Shipping", "b1-benchmark" })
            {
                try
                {
                    var procs = Process.GetProcessesByName(procName);
                    foreach (var gp in procs)
                    {
                        try
                        {
                            if (!gp.HasExited)
                            {
                                gp.Kill();
                                gp.WaitForExit(5000);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

        }
        catch { }

        return new PassResult(name, profile, result, resultFile);
    }
}

public sealed record PassResult(string Name, Profile Profile, BenchmarkResult Result, string SourceFile);

public sealed record AppConfig
{
    public string? BenchmarkRootArg { get; init; }
    public string HistoryDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "b1", "BenchMarkHistory", "Tool");
    public string OutputDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "WukongBenchResults");
    public int TimeoutMinutes { get; init; } = 15;
    public bool PrepareOnly { get; init; }
    public bool SkipUiAutomation { get; init; }

    // Normalized coordinates in the benchmark window (0..1).
    // These are deliberately configurable because Unreal UI layout can differ.
    public double BenchmarkButtonX { get; init; } = 0.50;
    public double BenchmarkButtonY { get; init; } = 0.57;
    public double ConfirmButtonX { get; init; } = 0.50;
    public double ConfirmButtonY { get; init; } = 0.74;
    // Optional pixel coordinates (screen space). If present, these take precedence over normalized coords.
    public int? BenchmarkButtonXPx { get; init; }
    public int? BenchmarkButtonYPx { get; init; }
    public int? ConfirmButtonXPx { get; init; }
    public int? ConfirmButtonYPx { get; init; }
    public string? BenchmarkRoot { get; internal set; }

    public static AppConfig Load(string[] args)
    {
        string? root = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;
        var cfg = new AppConfig
        {
            BenchmarkRootArg = root,
            PrepareOnly = args.Contains("--prepare-only"),
            SkipUiAutomation = args.Contains("--manual")
        };

        // Parse optional pixel coordinates from args in the form --benchmark-button-px=X,Y and --confirm-button-px=X,Y
        foreach (var a in args)
        {
            if (a.StartsWith("--benchmark-button-px=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a.Split('=', 2)[1];
                var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out var px) && int.TryParse(parts[1], out var py))
                {
                    cfg = cfg with { BenchmarkButtonXPx = px, BenchmarkButtonYPx = py };
                }
            }
            if (a.StartsWith("--confirm-button-px=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a.Split('=', 2)[1];
                var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out var px) && int.TryParse(parts[1], out var py))
                {
                    cfg = cfg with { ConfirmButtonXPx = px, ConfirmButtonYPx = py };
                }
            }
        }

        return cfg;
    }
}

public sealed record InstallInfo(string Root);

public static class SteamLocator
{
    public static InstallInfo Find(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Validate(explicitRoot);

        var candidates = new List<string>();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steam = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(steam))
                candidates.Add(steam);
        }
        catch { }

        foreach (var steam in candidates.ToArray())
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            foreach (var line in File.ReadLines(vdf))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, "\"path\"\\s+\"([^\"]+)\"");
                if (m.Success) candidates.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
        }

        foreach (var library in candidates.Distinct())
        {
            var root = Path.Combine(library, "steamapps", "common", "Black Myth Wukong Benchmark Tool");
            if (File.Exists(Path.Combine(root, "b1_benchmark.exe")))
                return new InstallInfo(root);
        }

        throw new DirectoryNotFoundException(
            "Could not find Black Myth Wukong Benchmark Tool. Pass the install folder as the first argument.");
    }

    static InstallInfo Validate(string root)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "b1_benchmark.exe")))
            throw new FileNotFoundException("b1_benchmark.exe not found under supplied benchmark root.");
        return new InstallInfo(root);
    }
}

public sealed class Profile
{
    public string Description { get; init; } = "";
    public string IniText { get; init; } = "";
}

public static class Profiles
{
    static readonly string[] QualityKeys =
    {
        "ViewDistance", "AntiAliasing", "PostProcessing", "ShadowQuality",
        "TextureQuality", "FxQuality", "MaterialQuality", "VegetationQuality",
        "GlobalIllumination", "ReflectionQuality"
    };

    static readonly string[] ScaleKeys =
    {
        "ViewDistanceQuality", "AntiAliasingQuality", "PostProcessQuality",
        "ShadowQuality", "TextureQuality", "EffectsQuality", "ShadingQuality",
        "FoliageQuality", "GlobalIlluminationQuality", "ReflectionQuality"
    };

    public static Profile Cpu(IniDocument source)
    {
        var x = source.Clone();

        x.Set("ResolutionSizeX", "1280");
        x.Set("ResolutionSizeY", "720");
        x.Set("LastUserConfirmedResolutionSizeX", "1280");
        x.Set("LastUserConfirmedResolutionSizeY", "720");

        x.SetInTuple("UISettingData", "ScreenResolution", "0");
        x.SetInTuple("UISettingData", "ScreenMode", "1");
        x.SetInTuple("UISettingData", "Vsync", "0");
        x.SetInTuple("UISettingData", "LockFrameRate", "0");
        x.SetInTuple("UISettingData", "Dlss", "0");
        x.SetInTuple("UISettingData", "InsertFrame", "0");
        x.SetInTuple("UISettingData", "Rtx", "0");
        x.SetInTuple("UISettingData", "QualityLevel", "1");

        foreach (var k in QualityKeys) x.SetInTuple("UISettingData", k, "1");
        x.SetInSection("ScalabilityGroups", "sg.ResolutionQuality", "50");
        foreach (var k in ScaleKeys) x.SetInSection("ScalabilityGroups", "sg." + k, "0");

        x.SetInTuple("UISettingData", "FxQuality", "1");
        x.SetInTuple("UISettingData", "GlobalIllumination", "1");
        x.SetInTuple("UISettingData", "ReflectionQuality", "1");
        x.SetInSection("RayTracing", "r.RayTracing.EnableInGame", "False");

        return new Profile
        {
            Description = "1280x720, 50% render scale, Low quality, RT off, VSync off, FPS limit off, frame generation off. Goal: minimize GPU influence so CPU becomes the likely bottleneck.",
            IniText = x.Render()
        };
    }

    public static Profile Gpu(IniDocument source)
    {
        var x = source.Clone();

        // Use the user's current/native resolution as the heavy-test resolution.
        int w = source.Int("ResolutionSizeX", 1920);
        int h = source.Int("ResolutionSizeY", 1080);
        if (w < 1920 || h < 1080) { w = 1920; h = 1080; }

        x.Set("ResolutionSizeX", w.ToString());
        x.Set("ResolutionSizeY", h.ToString());
        x.Set("LastUserConfirmedResolutionSizeX", w.ToString());
        x.Set("LastUserConfirmedResolutionSizeY", h.ToString());

        x.SetInTuple("UISettingData", "ScreenMode", "1");
        x.SetInTuple("UISettingData", "Vsync", "0");
        x.SetInTuple("UISettingData", "LockFrameRate", "0");
        x.SetInTuple("UISettingData", "Dlss", "0");
        x.SetInTuple("UISettingData", "InsertFrame", "0");
        x.SetInTuple("UISettingData", "Rtx", "1");
        x.SetInTuple("UISettingData", "RtxLevel", "2");
        x.SetInTuple("UISettingData", "QualityLevel", "5");

        foreach (var k in QualityKeys) x.SetInTuple("UISettingData", k, "5");
        x.SetInSection("ScalabilityGroups", "sg.ResolutionQuality", "100");
        foreach (var k in ScaleKeys) x.SetInSection("ScalabilityGroups", "sg." + k, "4");

        x.SetInTuple("UISettingData", "FxQuality", "5");
        x.SetInTuple("UISettingData", "GlobalIllumination", "5");
        x.SetInTuple("UISettingData", "ReflectionQuality", "5");
        x.SetInSection("RayTracing", "r.RayTracing.EnableInGame", "True");

        return new Profile
        {
            Description = $"{w}x{h}, 100% render scale, Cinematic quality, RT on (level 2), VSync off, FPS limit off, frame generation off. Goal: maximize GPU load.",
            IniText = x.Render()
        };
    }
}

public sealed class IniDocument
{
    readonly List<string> Lines;
    public string CurrentSection { get; private set; } = "";

    IniDocument(List<string> lines) => Lines = lines;

    public static IniDocument Parse(string text) =>
        new(text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList());

    public IniDocument Clone() => new(Lines.ToList());

    public int Int(string key, int fallback)
    {
        var v = Get(key);
        return int.TryParse(v, out var n) ? n : fallback;
    }

    public string? Get(string key, string? section = null)
    {
        string current = "";
        foreach (var line in Lines)
        {
            if (line.StartsWith("[") && line.EndsWith("]")) current = line[1..^1];
            if (section != null && current != section) continue;
            if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return line[(key.Length + 1)..].Trim();
        }
        return null;
    }

    public void Set(string key, string value) => SetInSection(null, key, value);

    public void SetInSection(string section, string key, string value)
    {
        int start = 0, end = Lines.Count;
        if (section != null)
        {
            var header = "[" + section + "]";
            start = Lines.FindIndex(x => x.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (start < 0) { Lines.Add(header); start = Lines.Count - 1; }
            end = Lines.FindIndex(start + 1, x => x.StartsWith("[") && x.EndsWith("]"));
            if (end < 0) end = Lines.Count;
        }

        for (int i = start; i < end; i++)
        {
            if (Lines[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                Lines[i] = key + "=" + value;
                return;
            }
        }
        Lines.Insert(end, key + "=" + value);
    }

    public void SetInTuple(string tupleKey, string item, string value)
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            if (!Lines[i].StartsWith(tupleKey + "=", StringComparison.OrdinalIgnoreCase)) continue;

            var line = Lines[i];
            var pattern = new Regex(
                @"\(""" + Regex.Escape(item) + @""",\s*""[^""]*""\)",
                RegexOptions.IgnoreCase);

            if (pattern.IsMatch(line))
                Lines[i] = pattern.Replace(line, $@"(""{item}"", ""{value}"")", 1);
            else
                Lines[i] = line.TrimEnd(')') + $@",(""{item}"", ""{value}""))";
            return;
        }

        Lines.Add($"{tupleKey}=((\"{item}\", \"{value}\"))");
    }

    public string Render() => string.Join("\r\n", Lines);
}

public sealed class BenchmarkResult
{
    public long TimeStamp { get; set; }
    public int BenckMarkVersion { get; set; }
    public double FPSAvg { get; set; }
    public double FPSMax { get; set; }
    public double FPSMin { get; set; }
    public double FPS95 { get; set; }
    public double CPUAvg { get; set; }
    public double GPUAvg { get; set; }
    public double GPULeak { get; set; }
    public double VideoMem { get; set; }
    public string? GameVer { get; set; }
    public string? SysVer { get; set; }
    public string? CPUModel { get; set; }
    public string? GPUModel { get; set; }
    public string? GpuDriverVer { get; set; }
    public string? VideoMemSize { get; set; }
    public string? SysMem { get; set; }
    public int ScreenMode { get; set; }
    public string? ScreenResolution { get; set; }
    public int QualityLevel { get; set; }
    public double ImageQuality { get; set; }
    public int ViewDistance { get; set; }
    public int AntiAliasing { get; set; }
    public int PostProcessing { get; set; }
    public int ShadowQuality { get; set; }
    public int TextureQuality { get; set; }
    public int MaterialQuality { get; set; }
    public int VegetationQuality { get; set; }
    public int MotionBlur { get; set; }
    public int Rtx { get; set; }
    public int Dlss { get; set; }
    public int InsertFrame { get; set; }
    public int Dx12 { get; set; }
    public List<FrameRecord> Records { get; set; } = [];
}

public sealed class FrameRecord
{
    public long TimeStamp { get; set; }
    public double FrameRate { get; set; }
    public double CPUUsage { get; set; }
    public double GPUUsage { get; set; }
    public double CPUFrameTime { get; set; }
    public double GPUFrameTime { get; set; }
    public double VideoMemoryUsage { get; set; }
}

public static class ResultWatcher
{
    public static async Task<string> WaitForNewJsonAsync(
        string directory, Dictionary<string, DateTime> before, TimeSpan timeout, DateTime? startUtc = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var files = Directory.GetFiles(directory, "*")
                .Select(p => new FileInfo(p))
                .Where(f =>
                {
                    if (!before.TryGetValue(f.FullName, out var old)) return true;
                    if (f.LastWriteTimeUtc > old) return true;
                    if (startUtc.HasValue && f.LastWriteTimeUtc > startUtc.Value) return true;
                    return false;
                })
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var f in files)
            {
                try
                {
                    using var s = File.OpenRead(f.FullName);
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.TryGetProperty("FPSAvg", out _))
                        return f.FullName;
                }
                catch { }
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"No new benchmark JSON appeared in {directory} within {timeout}.");
    }
}

public static class UiAutomation
{
    const uint INPUT_MOUSE = 0;
    const uint INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const ushort VK_RETURN = 0x0D;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION U; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }


    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private static IntPtr FindVisibleWindow(int targetPid)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);

            if (pid != (uint)targetPid)
                return true;

            if (!IsWindowVisible(hWnd))
                return true;

            // Не берём окна без заголовка.
            if (GetWindowTextLength(hWnd) == 0)
                return true;

            found = hWnd;
            return false; // Нашли первое подходящее окно.
        }, IntPtr.Zero);

        return found;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }

    private static void DumpBenchmarkWindows()
    {
        Console.WriteLine("Видимые окна процессов Benchmark Tool:");

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);

            string processName;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                processName = p.ProcessName;
            }
            catch
            {
                processName = "?";
            }

            string title = GetWindowTitle(hWnd);

            if (processName.Contains("b1",
                    StringComparison.OrdinalIgnoreCase)
                || title.Contains("b1",
                    StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"PID={pid}, Process={processName}, " +
                    $"Title='{title}', HWND={hWnd}");
            }

            return true;
        }, IntPtr.Zero);
    }

    public static async Task WaitAndStartBenchmarkAsync(
        int launcherPid, AppConfig cfg)
    {
        Process? game = null;
        IntPtr hwnd = IntPtr.Zero;

        // Ищем процессы, пока у одного из них не появится видимое окно.
        for (int i = 0; i < 120; i++)
        {
            var processes = Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        return p.ProcessName.Equals(
                                   "b1-Win64-Shipping",
                                   StringComparison.OrdinalIgnoreCase)
                               || p.ProcessName.Equals(
                                   "b1-benchmark",
                                   StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();

            foreach (var process in processes)
            {
                hwnd = FindVisibleWindow(process.Id);

                if (hwnd != IntPtr.Zero)
                {
                    game = process;
                    break;
                }
            }

            if (hwnd != IntPtr.Zero)
                break;

            await Task.Delay(1000);
        }

        if (game == null || hwnd == IntPtr.Zero)
        {
            Console.WriteLine("Не удалось найти видимое окно.");
            DumpBenchmarkWindows();
            throw new InvalidOperationException(
                "Benchmark process found, but no visible top-level window was found.");
        }

        Console.WriteLine(
            $"Окно найдено: PID={game.Id}, " +
            $"Process={game.ProcessName}, " +
            $"Title='{GetWindowTitle(hwnd)}', HWND={hwnd}");

        SetForegroundWindow(hwnd);
        await Task.Delay(3000);

        // Поскольку GUI прогружается не сразу и обратной связи нет,
        // выполняем серию попыток (до 5) через каждую минуту, начиная сразу.
        const int maxAttempts = 7;
        var interval = TimeSpan.FromMinutes(0.1);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (game == null || game.HasExited)
                {
                    Console.WriteLine("Benchmark process exited before UI interaction.");
                    break;
                }

                Console.WriteLine($"UI attempt {attempt}/{maxAttempts}: sending wake key (Enter).");
                // Ставим окно на передний план и посылаем Enter, чтобы пробудить UI.
                SetForegroundWindow(hwnd);
                Key(0x0D);

                // Ждём, чтобы интерфейс отреагировал.
                await Task.Delay(5000);

                // Вычисляем координаты по формуле и кликаем на пиксель.
                int W = GetScreenWidth();
                int H = GetScreenHeight();
                double R = 16.0 / 9.0;
                double Wi = Math.Min(W, H * R);
                double Hi = Wi / R;
                double left = (W - Wi) / 2.0;
                double top = (H - Hi) / 2.0;
                double scale = Wi / 3120.0;

                int x1 = (int)Math.Round(left + 361 * scale);
                int y1 = (int)Math.Round(top + 794 * scale);
                int x2 = (int)Math.Round(left + 1243 * scale);
                int y2 = (int)Math.Round(top + 1040 * scale);

                Console.WriteLine($"UI attempt {attempt}/{maxAttempts}: clicking first button at ({x1},{y1}).");
                ClickAbsolute(x1, y1);

                await Task.Delay(1000);

                Console.WriteLine($"UI attempt {attempt}/{maxAttempts}: clicking second button at ({x2},{y2}).");
                ClickAbsolute(x2, y2);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UI attempt {attempt} failed: {ex.Message}");
            }

            if (attempt < maxAttempts)
            {
                Console.WriteLine($"Ожидание {interval.TotalMinutes} минуты перед следующей попыткой...");
                await Task.Delay(interval);
            }
        }

        Console.WriteLine("Отправлены команды запуска Benchmark.");
    }
    static void Key(ushort vk)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } }
        };
        var up = input;
        up.U.ki.dwFlags = 0x0002;
        SendInput(2, new[] { input, up }, Marshal.SizeOf<INPUT>());
    }

    static void ClickNormalized(IntPtr hwnd, double x, double y)
    {
        if (!GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("GetWindowRect failed.");

        int sx = Math.Clamp(r.Left + (int)((r.Right - r.Left) * x), r.Left, r.Right - 1);
        int sy = Math.Clamp(r.Top + (int)((r.Bottom - r.Top) * y), r.Top, r.Bottom - 1);

        int vx = (int)Math.Round(sx * 65535.0 / (GetScreenWidth() - 1));
        int vy = (int)Math.Round(sy * 65535.0 / (GetScreenHeight() - 1));

        var down = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION { mi = new MOUSEINPUT { dx = vx, dy = vy, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN } }
        };
        var up = down;
        up.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTUP;

        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    static void ClickAbsolute(int sx, int sy)
    {
        // sx, sy — экранные координаты в пикселях
        int vx = (int)Math.Round(sx * 65535.0 / (GetScreenWidth() - 1));
        int vy = (int)Math.Round(sy * 65535.0 / (GetScreenHeight() - 1));

        var down = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION { mi = new MOUSEINPUT { dx = vx, dy = vy, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN } }
        };
        var up = down;
        up.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTUP;

        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    static int GetScreenWidth() => GetSystemMetrics(0);
    static int GetScreenHeight() => GetSystemMetrics(1);
}
 
public static class Report
{
    public static string Build(PassResult cpu, PassResult gpu, string dir)
    {
        var r = new StringBuilder();
        r.AppendLine("Black Myth: Wukong Benchmark — automated report");
        r.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        r.AppendLine();
        r.AppendLine("SYSTEM");
        r.AppendLine($"CPU: {cpu.Result.CPUModel}");
        r.AppendLine($"GPU: {cpu.Result.GPUModel}");
        r.AppendLine($"GPU driver: {cpu.Result.GpuDriverVer}");
        r.AppendLine($"RAM: {cpu.Result.SysMem}");
        r.AppendLine($"OS: {cpu.Result.SysVer}");
        r.AppendLine();
        AppendPass(r, cpu);
        AppendPass(r, gpu);
        r.AppendLine($"Raw JSON/report directory: {dir}");
        return r.ToString();
    }

    static void AppendPass(StringBuilder r, PassResult p)
    {
        var x = p.Result;
        r.AppendLine(p.Name.ToUpperInvariant() + " TEST");
        r.AppendLine($"  FPS avg/min/max/5%: {x.FPSAvg:F1} / {x.FPSMin:F1} / {x.FPSMax:F1} / {x.FPS95:F1}");
        r.AppendLine($"  CPU avg usage: {x.CPUAvg:F1}%");
        r.AppendLine($"  GPU avg usage: {x.GPUAvg:F1}%");
        r.AppendLine($"  Resolution: {x.ScreenResolution}");
        r.AppendLine($"  QualityLevel: {x.QualityLevel}, ImageQuality: {x.ImageQuality}");
        r.AppendLine($"  ViewDistance/AA/Post/Shadow/Texture/Material/Vegetation: {x.ViewDistance}/{x.AntiAliasing}/{x.PostProcessing}/{x.ShadowQuality}/{x.TextureQuality}/{x.MaterialQuality}/{x.VegetationQuality}");
        r.AppendLine($"  RT/DLSS/FrameGen/DX12: {x.Rtx}/{x.Dlss}/{x.InsertFrame}/{x.Dx12}");

        if (x.Records.Count > 0)
        {
            double cpuBound = x.Records.Count(v => v.CPUFrameTime > v.GPUFrameTime) * 100.0 / x.Records.Count;
            double gpuBound = x.Records.Count(v => v.GPUFrameTime > v.CPUFrameTime) * 100.0 / x.Records.Count;
            r.AppendLine($"  Frame-time classification: CPU-bound {cpuBound:F1}%, GPU-bound {gpuBound:F1}%");
            r.AppendLine($"  Mean CPU/GPU frame time: {x.Records.Average(v => v.CPUFrameTime):F2} / {x.Records.Average(v => v.GPUFrameTime):F2} ms");
        }

        r.AppendLine($"  Profile: {p.Profile.Description}");
        r.AppendLine($"  Source JSON: {p.SourceFile}");
        r.AppendLine();
    }
}

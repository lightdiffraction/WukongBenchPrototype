using System.ComponentModel;
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

    static async Task EnsureProcessesExitedAsync(string[] procNames, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine("Ожидание завершения игровых процессов...");
        while (sw.Elapsed < timeout)
        {
            var any = false;
            foreach (var name in procNames)
            {
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    if (procs.Length > 0)
                    {
                        any = true;
                        foreach (var p in procs)
                        {
                            try { if (!p.HasExited) p.Kill(); } catch { }
                            finally { p.Dispose(); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Ошибка при проверке процессов '{name}': {ex.Message}");
                }
            }

            if (!any)
            {
                Console.WriteLine("Все игровые процессы завершены.");
                return;
            }

            await Task.Delay(500);
        }

        Console.Error.WriteLine("Внимание: некоторые игровые процессы не завершились в отведённое время.");
    }

        var install = SteamLocator.Find(cfg.BenchmarkRoot);
        Console.WriteLine($"Benchmark root: {install.Root}");

        var settings = Path.Combine(install.Root, @"b1\Saved\Config\Windows\GameUserSettings.ini");
        // Сначала обращаемся к shipping executable в b1\Binaries\Win64, иначе открываем b1_benchmark.exe если он существует.
        var shippingLauncher = Path.Combine(install.Root, Path.Combine("b1", "Binaries", "Win64", "b1-Win64-Shipping.exe"));
        var legacyLauncher = Path.Combine(install.Root, "b1_benchmark.exe");
        var launcher = File.Exists(shippingLauncher) ? shippingLauncher : legacyLauncher;
        var history = cfg.HistoryDirectory;

        if (!File.Exists(settings))
            throw new FileNotFoundException("GameUserSettings.ini not found.", settings);
        if (!File.Exists(shippingLauncher) && !File.Exists(legacyLauncher))
            throw new FileNotFoundException("Could not find the benchmark/game executable.");
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

            // Убедимся, что все процессы игры/лаунчера завершились перед вторым прогоном (GPU).
            // Иногда лаунчер или игра (b1-Win64-Shipping, b1-benchmark, BootstrapPackagedGame) остаются активными
            // и мешают запуску следующего прогона с ошибкой "game is already running".
            await EnsureProcessesExitedAsync(new[] { "b1-Win64-Shipping", "b1-benchmark", "BootstrapPackagedGame" }, TimeSpan.FromSeconds(30));

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
            catch (InvalidOperationException)
            {
                // Процесс уже завершился между HasExited и Kill.
            }
            catch (Win32Exception ex)
            {
                Console.Error.WriteLine($"Не удалось завершить процесс {p.Id}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Непредвиденная ошибка при завершении процесса {p.Id}: {ex}");
            }
            finally { p.Dispose(); }

            // Лаунчер порождает отдельный процесс игры (b1-Win64-Shipping или b1-benchmark).
            // Убедимся, что они тоже завершены перед следующим прогоном, иначе будет ошибка game is already running.
            foreach (var procName in new[] { "b1-Win64-Shipping", "b1-benchmark", "BootstrapPackagedGame" })
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
                        catch (InvalidOperationException)
                        {
                            // Процесс уже помер между HasExited и Kill.
                        }
                        catch (Win32Exception ex)
                        {
                            Console.Error.WriteLine($"Не удалось убить {gp.ProcessName} ({gp.Id}): {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Ошибка при убийстве {gp.ProcessName} ({gp.Id}): {ex}");
                        }
                        finally { gp.Dispose(); }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Не удалось получить список процессов '{procName}': {ex.Message}");
                    continue;
                }
            }

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка при завершении процессов бенчмарка: {ex}");
        }

        return new PassResult(name, profile, result, resultFile);
    }
}

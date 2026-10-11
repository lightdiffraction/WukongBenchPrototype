using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace WukongBench;
// Отображение результатов бенчмарка в виде JSON. Структура соответствует тому, что генерирует бенчмарк при запуске.
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
    public List<FrameRecord> Records { get; set; } = new();
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
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Ошибка при чтении JSON файла {f.FullName}: {ex.Message}");
                }
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"No new benchmark JSON appeared in {directory} within {timeout}.");
    }
}

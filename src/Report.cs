using System;
using System.Linq;
using System.Text;

namespace WukongBench;

public sealed record PassResult(string Name, Profile Profile, BenchmarkResult Result, string SourceFile);

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

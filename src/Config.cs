using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace WukongBench;

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

    // Нормализованные координаты кнопок в окне бенчмарка (0.0-1.0, относительно ширины/высоты окна).
    public double BenchmarkButtonX { get; init; } = 0.50;
    public double BenchmarkButtonY { get; init; } = 0.57;
    public double ConfirmButtonX { get; init; } = 0.50;
    public double ConfirmButtonY { get; init; } = 0.74;
    // Дополнительные координаты кнопок в пикселях, если указаны через аргументы командной строки.
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

        // Распарсить дополнительные координаты кнопок в пикселях из аргументов командной строки --benchmark-button-px=X,Y и --confirm-button-px=X,Y
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
            if (a.StartsWith("--history-dir=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a.Split('=', 2)[1];
                if (!string.IsNullOrWhiteSpace(v)) cfg = cfg with { HistoryDirectory = v };
            }
            if (a.StartsWith("--output-dir=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a.Split('=', 2)[1];
                if (!string.IsNullOrWhiteSpace(v)) cfg = cfg with { OutputDirectory = v };
            }
        }

        return cfg;
    }
}

public sealed record InstallInfo(string Root);

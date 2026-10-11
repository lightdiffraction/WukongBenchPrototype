using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WukongBench;

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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка при поиске установки Steam: {ex.Message}");
        }

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
            // Сначала обращаемся к shipping executable в b1\Binaries\Win64, иначе открываем b1_benchmark.exe если он существует.
            var shipping = Path.Combine(root, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe");
            var legacy = Path.Combine(root, "b1_benchmark.exe");
            if (File.Exists(shipping) || File.Exists(legacy))
                return new InstallInfo(root);
        }

        throw new DirectoryNotFoundException(
            "Could not find Black Myth Wukong Benchmark Tool. Pass the install folder as the first argument.");
    }

    static InstallInfo Validate(string root)
    {
        root = Path.GetFullPath(root);
        var shipping = Path.Combine(root, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe");
        var legacy = Path.Combine(root, "b1_benchmark.exe");
        if (!File.Exists(shipping) && !File.Exists(legacy))
            throw new FileNotFoundException("Could not find benchmark/game executable under supplied benchmark root.");
        return new InstallInfo(root);
    }
}

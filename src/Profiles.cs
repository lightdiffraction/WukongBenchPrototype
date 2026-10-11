using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WukongBench;

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

        // Для профиля gpu использовать разрешение 1920x1080 или большее.
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
                @"\(""" + Regex.Escape(item) + @""",\s*""[^"" ]*""\)",
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

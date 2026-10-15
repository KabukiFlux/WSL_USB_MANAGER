// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.IO;
using System.Text;

namespace WslUsbManager.Infrastructure;

/// <summary>Minimal INI reader/writer: [Section], key=value, ';' or '#' comments. Keeps section order.</summary>
public sealed class IniFile
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Load(string path)
    {
        var ini = new IniFile();
        if (!File.Exists(path)) return ini;

        string section = "";
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']') { section = line[1..^1].Trim(); ini.GetOrAdd(section); continue; }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            var comment = value.IndexOf(" ;", StringComparison.Ordinal);
            if (comment >= 0) value = value[..comment].TrimEnd();
            ini.Set(section, line[..eq].Trim(), value);
        }
        return ini;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        foreach (var name in _order)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append('[').Append(name).AppendLine("]");
            foreach (var (k, v) in _sections[name]) sb.Append(k).Append('=').AppendLine(v);
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public string Get(string section, string key, string defaultValue = "") =>
        _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : defaultValue;

    public bool GetBool(string section, string key, bool defaultValue) =>
        bool.TryParse(Get(section, key), out var b) ? b : defaultValue;

    public int GetInt(string section, string key, int defaultValue) =>
        int.TryParse(Get(section, key), out var i) ? i : defaultValue;

    public void Set(string section, string key, string? value) => GetOrAdd(section)[key] = value ?? "";
    public void Set(string section, string key, bool value) => Set(section, key, value ? "true" : "false");
    public void Set(string section, string key, int value) => Set(section, key, value.ToString());

    public void RemoveSections(Func<string, bool> predicate)
    {
        foreach (var name in _order.Where(predicate).ToList())
        {
            _order.Remove(name);
            _sections.Remove(name);
        }
    }

    private Dictionary<string, string> GetOrAdd(string section)
    {
        if (!_sections.TryGetValue(section, out var s))
        {
            s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = s;
            _order.Add(section);
        }
        return s;
    }
}

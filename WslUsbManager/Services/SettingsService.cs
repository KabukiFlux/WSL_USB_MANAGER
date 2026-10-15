// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Globalization;
using System.IO;
using WslUsbManager.Infrastructure;
using WslUsbManager.Models;

namespace WslUsbManager.Services;

public interface ISettingsService
{
    string FilePath { get; }
    string LastSelectedDistro { get; set; }
    bool AutoAttachEnabled { get; set; }
    string ArduinoCliPath { get; set; }
    string LastPort { get; set; }
    string LastFqbn { get; set; }
    string LastSketchPath { get; set; }
    int MonitorBaud { get; set; }
    string UploadMethod { get; set; }
    Dictionary<DetectedFamily, bool> Filters { get; }
    List<FavoriteDevice> Favorites { get; }
    FavoriteDevice? FindFavorite(UsbDevice device);
    void Load();
    void Save();
}

public sealed class SettingsService : ISettingsService
{
    private const string General = "General";
    private const string FiltersSection = "Filters";
    private const string FavoritesSection = "Favorites";
    private const string FavoritePrefix = "Favorite_";

    private static readonly (DetectedFamily Family, string Key)[] FilterKeys =
    {
        (DetectedFamily.Arduino, "Arduino"),
        (DetectedFamily.EspressifEsp32, "Esp32"),
        (DetectedFamily.Stm32, "Stm32"),
        (DetectedFamily.RaspberryPiPico, "Pico"),
        (DetectedFamily.Fpga, "Fpga"),
        (DetectedFamily.GenericSerialBridge, "Serial"),
        (DetectedFamily.Other, "Other"),
    };

    public string FilePath { get; } = Path.Combine(Logger.AppDataDir, "settings.ini");

    public string LastSelectedDistro { get; set; } = "";
    public bool AutoAttachEnabled { get; set; } = true;
    public string ArduinoCliPath { get; set; } = "arduino-cli";
    public string LastPort { get; set; } = "";
    public string LastFqbn { get; set; } = "";
    public string LastSketchPath { get; set; } = "";
    public int MonitorBaud { get; set; } = 115200;
    public string UploadMethod { get; set; } = "Auto";
    public Dictionary<DetectedFamily, bool> Filters { get; } = FilterKeys.ToDictionary(f => f.Family, _ => true);
    public List<FavoriteDevice> Favorites { get; } = new();

    public FavoriteDevice? FindFavorite(UsbDevice device) =>
        // Prefer serial-specific favorites over VID:PID-only ones.
        Favorites.Where(f => f.Matches(device)).OrderByDescending(f => !string.IsNullOrEmpty(f.SerialNumber)).FirstOrDefault();

    public void Load()
    {
        try
        {
            var ini = IniFile.Load(FilePath);
            LastSelectedDistro = ini.Get(General, "LastSelectedDistro");
            AutoAttachEnabled = ini.GetBool(General, "AutoAttachEnabled", true);
            ArduinoCliPath = NonEmpty(ini.Get(General, "ArduinoCliPath"), "arduino-cli");
            LastPort = ini.Get(General, "LastPort");
            LastFqbn = ini.Get(General, "LastFqbn");
            LastSketchPath = ini.Get(General, "LastSketchPath");
            MonitorBaud = ini.GetInt(General, "MonitorBaud", 115200);
            UploadMethod = NonEmpty(ini.Get(General, "UploadMethod"), "Auto");

            foreach (var (family, key) in FilterKeys)
                Filters[family] = ini.GetBool(FiltersSection, key, true);

            Favorites.Clear();
            var count = ini.GetInt(FavoritesSection, "Count", 0);
            for (int i = 0; i < count; i++)
            {
                var s = FavoritePrefix + i;
                if (!TryHex(ini.Get(s, "VID"), out var vid) || !TryHex(ini.Get(s, "PID"), out var pid)) continue;
                var serial = ini.Get(s, "SerialNumber");
                Favorites.Add(new FavoriteDevice
                {
                    Name = ini.Get(s, "Name"),
                    VendorId = vid,
                    ProductId = pid,
                    SerialNumber = serial.Length == 0 ? null : serial,
                    Distro = ini.Get(s, "Distro"),
                    AutoAttach = ini.GetBool(s, "AutoAttach", true),
                });
            }
            Logger.Info($"Settings loaded from {FilePath} ({Favorites.Count} favorites)");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load settings", ex);
        }
    }

    public void Save()
    {
        try
        {
            var ini = IniFile.Load(FilePath); // keep unknown keys the user may have added
            ini.Set(General, "LastSelectedDistro", LastSelectedDistro);
            ini.Set(General, "AutoAttachEnabled", AutoAttachEnabled);
            ini.Set(General, "ArduinoCliPath", ArduinoCliPath);
            ini.Set(General, "LastPort", LastPort);
            ini.Set(General, "LastFqbn", LastFqbn);
            ini.Set(General, "LastSketchPath", LastSketchPath);
            ini.Set(General, "MonitorBaud", MonitorBaud);
            ini.Set(General, "UploadMethod", UploadMethod);

            foreach (var (family, key) in FilterKeys)
                ini.Set(FiltersSection, key, Filters[family]);

            ini.RemoveSections(n => n.StartsWith(FavoritePrefix, StringComparison.OrdinalIgnoreCase));
            ini.Set(FavoritesSection, "Count", Favorites.Count);
            for (int i = 0; i < Favorites.Count; i++)
            {
                var f = Favorites[i];
                var s = FavoritePrefix + i;
                ini.Set(s, "Name", f.Name);
                ini.Set(s, "VID", f.VendorId.ToString("X4"));
                ini.Set(s, "PID", f.ProductId.ToString("X4"));
                ini.Set(s, "SerialNumber", f.SerialNumber ?? "");
                ini.Set(s, "Distro", f.Distro);
                ini.Set(s, "AutoAttach", f.AutoAttach);
            }
            ini.Save(FilePath);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save settings", ex);
        }
    }

    private static bool TryHex(string s, out ushort value) =>
        ushort.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static string NonEmpty(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}

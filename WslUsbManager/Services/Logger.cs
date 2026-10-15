// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.IO;

namespace WslUsbManager.Services;

/// <summary>Daily file log under %LocalAppData%\WslUsbManager\logs plus an event for the live log panel.</summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslUsbManager");

    public static string LogDir { get; } = Path.Combine(AppDataDir, "logs");

    public static event Action<string>? LineLogged;

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message) => Write("ERR", message);
    public static void Error(string message, Exception ex) => Write("ERR", $"{message}: {ex}");

    /// <summary>Raw output line (e.g. streamed process output) — shown verbatim in the panel.</summary>
    public static void Output(string line) => Write("OUT", line);

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss} {level} {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var file = Path.Combine(LogDir, $"log-{now:yyyyMMdd}.txt");
                using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.WriteLine(line);
            }
            catch
            {
                // Logging must never break the app (e.g. elevated child writing concurrently).
            }
        }
        LineLogged?.Invoke(level == "OUT" ? "    " + message : line);
    }
}

// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using WslUsbManager.Models;

namespace WslUsbManager.Services;

public interface IWslService
{
    Task<List<WslDistro>> ListDistrosAsync();
    Task<CommandResult> EnsureRunningAsync(string distro);
    Task<string> ToWslPathAsync(string distro, string windowsPath);
    Task<CommandResult> RunAsync(string distro, IEnumerable<string> command, Action<string>? onLine = null, CancellationToken ct = default);
}

public sealed class WslService : IWslService
{
    public const string WslExe = "wsl.exe";

    /// <summary>Makes wsl.exe write UTF-8 instead of UTF-16LE.</summary>
    public static readonly IReadOnlyDictionary<string, string> Utf8Env = new Dictionary<string, string> { ["WSL_UTF8"] = "1" };

    public async Task<List<WslDistro>> ListDistrosAsync()
    {
        var list = new List<WslDistro>();
        var r = await ProcessRunner.RunAsync(WslExe, new[] { "--list", "--verbose" }, environment: Utf8Env, logCommand: false)
            .ConfigureAwait(false);
        if (!r.Success) { Logger.Warn($"wsl --list failed: {r.Message}"); return list; }

        // STATE text is localized, so ask for the running set separately.
        var running = await ProcessRunner.RunAsync(WslExe, new[] { "--list", "--running", "--quiet" }, environment: Utf8Env, logCommand: false)
            .ConfigureAwait(false);
        var runningNames = running.Success
            ? running.Output.Split('\n').Select(Clean).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();

        foreach (var raw in r.Output.Split('\n').Skip(1)) // first line is the header
        {
            var line = Clean(raw);
            if (line.Length == 0) continue;
            var isDefault = line.StartsWith('*');
            var tokens = line.TrimStart('*').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            var name = tokens[0];
            if (name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new WslDistro
            {
                Name = name,
                IsDefault = isDefault,
                IsRunning = runningNames.Contains(name),
                Version = int.TryParse(tokens[^1], out var v) ? v : 0,
            });
        }
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    public Task<CommandResult> EnsureRunningAsync(string distro) =>
        ProcessRunner.RunAsync(WslExe, new[] { "-d", distro, "-e", "true" }, environment: Utf8Env);

    public async Task<string> ToWslPathAsync(string distro, string windowsPath)
    {
        var r = await ProcessRunner.RunAsync(WslExe, new[] { "-d", distro, "-e", "wslpath", "-a", windowsPath },
            environment: Utf8Env, logCommand: false).ConfigureAwait(false);
        if (r.Success && r.Output.Trim().StartsWith('/')) return r.Output.Trim();

        // Fallback assuming the default automount root.
        if (windowsPath.Length >= 2 && windowsPath[1] == ':')
            return $"/mnt/{char.ToLowerInvariant(windowsPath[0])}{windowsPath[2..].Replace('\\', '/')}";
        return windowsPath.Replace('\\', '/');
    }

    public Task<CommandResult> RunAsync(string distro, IEnumerable<string> command, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var args = new List<string> { "-d", distro, "-e" };
        args.AddRange(command);
        return ProcessRunner.RunAsync(WslExe, args, onLine, Utf8Env, ct: ct);
    }

    private static string Clean(string s) => s.Replace("\0", "").Trim();
}

// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using WslUsbManager.Models;

namespace WslUsbManager.Services;

/// <summary>
/// Runs usbipd elevated by relaunching this same executable with <c>--elevated &lt;resultFile&gt; &lt;usbipd args…&gt;</c>.
/// "runas" requires UseShellExecute, so stdout cannot be redirected; the child writes a JSON result file instead.
/// </summary>
public static class ElevatedRunner
{
    public const string ElevatedSwitch = "--elevated";
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: user dismissed the UAC prompt

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static async Task<CommandResult> RunUsbipdElevatedAsync(IReadOnlyList<string> usbipdArgs)
    {
        var usbipd = UsbipdService.Locate();
        if (usbipd is null) return CommandResult.Fail("usbipd-win is not installed.");

        if (IsAdministrator)
            return await ProcessRunner.RunAsync(usbipd, usbipdArgs, line => Logger.Output(line)).ConfigureAwait(false);

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine own executable path.");
        var resultFile = Path.Combine(Path.GetTempPath(), $"WslUsbManager-{Guid.NewGuid():N}.json");
        var args = new List<string> { ElevatedSwitch, resultFile };
        args.AddRange(usbipdArgs);

        Logger.Info($"> [elevated] usbipd {CommandLine.Join(usbipdArgs)}");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = CommandLine.Join(args),
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return CommandResult.Fail("Failed to start the elevated helper.");
            await p.WaitForExitAsync().ConfigureAwait(false);

            if (!File.Exists(resultFile))
                return CommandResult.Fail($"Elevated helper exited with code {p.ExitCode} and produced no result.", p.ExitCode);

            var json = await File.ReadAllTextAsync(resultFile).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<CommandResult>(json) ?? CommandResult.Fail("Invalid helper result.");
            foreach (var line in (result.Output + result.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Logger.Output(line.TrimEnd('\r'));
            if (!result.Success) Logger.Warn($"  exit {result.ExitCode}");
            return result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Logger.Warn("  UAC prompt was cancelled.");
            return new CommandResult { ExitCode = -1, Cancelled = true };
        }
        finally
        {
            try { File.Delete(resultFile); } catch { /* best effort */ }
        }
    }

    /// <summary>Entry point for the elevated child process. Only ever runs usbipd.</summary>
    public static int RunElevatedChild(string[] args)
    {
        if (args.Length < 3) return 2;
        var resultFile = args[1];
        var usbipdArgs = args.Skip(2).ToArray();

        CommandResult result;
        var usbipd = UsbipdService.Locate();
        if (usbipd is null)
            result = CommandResult.Fail("usbipd-win is not installed.");
        else
            // Task.Run avoids deadlocking on the WPF dispatcher synchronization context.
            result = Task.Run(() => ProcessRunner.RunAsync(usbipd, usbipdArgs, logCommand: false)).GetAwaiter().GetResult();

        try
        {
            File.WriteAllText(resultFile, JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            Logger.Error("Elevated helper could not write result file", ex);
        }
        return result.ExitCode;
    }
}

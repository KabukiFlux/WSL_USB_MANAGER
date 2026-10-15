// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WslUsbManager.Models;

namespace WslUsbManager.Services;

/// <summary>Runs external commands asynchronously, capturing stdout/stderr (UTF-8).</summary>
public static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool logCommand = true,
        CancellationToken ct = default)
    {
        var argList = args.ToList();
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argList) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        if (logCommand)
            Logger.Info($"> {System.IO.Path.GetFileName(fileName)} {CommandLine.Join(argList)}");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        try
        {
            p.Start();
        }
        catch (Win32Exception ex)
        {
            Logger.Error($"Cannot start '{fileName}': {ex.Message}");
            return CommandResult.Fail($"Cannot start '{fileName}': {ex.Message}");
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new CommandResult { ExitCode = -1, Cancelled = true };
        }
        p.WaitForExit(); // flush async readers

        var result = new CommandResult { ExitCode = p.ExitCode, Output = stdout.ToString(), Error = stderr.ToString() };
        if (logCommand && !result.Success)
            Logger.Warn($"  exit {result.ExitCode}: {Truncate(result.Message, 600)}");
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Windows command-line quoting (CommandLineToArgvW rules) and bash single-quote escaping.</summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteWindows));

    public static string QuoteWindows(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string QuoteBash(string arg) => "'" + arg.Replace("'", "'\\''") + "'";
}

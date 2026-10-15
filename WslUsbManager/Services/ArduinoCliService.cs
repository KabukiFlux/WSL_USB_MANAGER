// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Text.Json;
using WslUsbManager.Models;

namespace WslUsbManager.Services;

public interface IArduinoCliService
{
    string CliPath { get; set; }
    Task<CommandResult> VersionAsync(string distro);
    Task<(List<ArduinoBoardPort> Ports, CommandResult Result)> BoardListAsync(string distro);
    Task<CommandResult> CompileUploadAsync(string distro, string port, string fqbn, string wslSketchPath, CancellationToken ct = default);
    Task<CommandResult> CompileAsync(string distro, string fqbn, string wslSketchPath, string wslOutputDir, CancellationToken ct = default);
    Task<CommandResult> InstallCoreAsync(string distro, CorePreset core, CancellationToken ct = default);
    Task<CommandResult> TouchResetAsync(string distro, string port);
    Task<CommandResult> RunCustomAsync(string distro, string argumentLine, CancellationToken ct = default);
    void OpenMonitor(string distro, string port, int baud);
}

/// <summary>Runs arduino-cli inside WSL through a login shell so the user's PATH applies.</summary>
public sealed class ArduinoCliService : IArduinoCliService
{
    private readonly IWslService _wsl;

    public ArduinoCliService(IWslService wsl) => _wsl = wsl;

    public string CliPath { get; set; } = "arduino-cli";

    public Task<CommandResult> VersionAsync(string distro) => RunCliAsync(distro, new[] { "version" }, stream: false);

    public async Task<(List<ArduinoBoardPort>, CommandResult)> BoardListAsync(string distro)
    {
        var r = await RunCliAsync(distro, new[] { "board", "list", "--format", "json" }, stream: false).ConfigureAwait(false);
        return (r.Success ? ParseBoardList(r.Output) : new List<ArduinoBoardPort>(), r);
    }

    public Task<CommandResult> CompileUploadAsync(string distro, string port, string fqbn, string wslSketchPath, CancellationToken ct = default) =>
        RunCliAsync(distro, new[] { "compile", "--upload", "-p", port, "--fqbn", fqbn, wslSketchPath }, stream: true, ct);

    public Task<CommandResult> CompileAsync(string distro, string fqbn, string wslSketchPath, string wslOutputDir, CancellationToken ct = default) =>
        RunCliAsync(distro, new[] { "compile", "--fqbn", fqbn, "--output-dir", wslOutputDir, wslSketchPath }, stream: true, ct);

    /// <summary>Adds the board-manager URL (once), updates the index and installs/updates the core.</summary>
    public Task<CommandResult> InstallCoreAsync(string distro, CorePreset core, CancellationToken ct = default)
    {
        var cli = CommandLine.QuoteBash(CliPath);
        var lines = new List<string> { "set -e", $"{cli} config init >/dev/null 2>&1 || true" };
        if (core.IndexUrl is not null)
        {
            var url = CommandLine.QuoteBash(core.IndexUrl);
            lines.Add($"if ! {cli} config get board_manager.additional_urls | grep -qF {url}; then " +
                      $"echo 'Adding board manager URL {core.IndexUrl}'; {cli} config add board_manager.additional_urls {url}; fi");
        }
        lines.Add($"{cli} core update-index");
        lines.Add($"{cli} core install {CommandLine.QuoteBash(core.CoreId)}");
        lines.Add($"{cli} core upgrade {CommandLine.QuoteBash(core.CoreId)} || true");
        lines.Add($"{cli} core list");
        var script = string.Join('\n', lines);
        return RunScriptAsync(distro, script, stream: true, ct);
    }

    /// <summary>
    /// "1200-baud touch": opening the CDC port at 1200 bps and closing it makes RP2040/RP2350 (arduino-pico),
    /// 32U4 and SAMD boards reboot into their bootloader. Works for a WSL port (/dev/tty*) or a Windows COM port.
    /// </summary>
    public Task<CommandResult> TouchResetAsync(string distro, string port)
    {
        if (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            return ProcessRunner.RunAsync("cmd.exe", new[] { "/c", "mode", $"{port}:", "BAUD=1200", "DTR=OFF" });
        var script = $"stty -F {CommandLine.QuoteBash(port)} 1200 hupcl && sleep 0.2";
        return _wsl.RunAsync(distro, new[] { "bash", "-c", script });
    }

    /// <summary>Free-form arguments; passed to bash unquoted so the user can use normal shell syntax.</summary>
    public Task<CommandResult> RunCustomAsync(string distro, string argumentLine, CancellationToken ct = default) =>
        RunScriptAsync(distro, $"{CommandLine.QuoteBash(CliPath)} {argumentLine}", stream: true, ct);

    public void OpenMonitor(string distro, string port, int baud)
    {
        var script = $"{CommandLine.QuoteBash(CliPath)} monitor -p {CommandLine.QuoteBash(port)} --config baudrate={baud}; " +
                     "echo; read -n1 -r -p 'Monitor closed. Press any key...'";
        var args = new[] { "-d", distro, "-e", "bash", "-lc", script };
        Logger.Info($"> wsl {CommandLine.Join(args)}  (new window)");
        Process.Start(new ProcessStartInfo(WslService.WslExe)
        {
            UseShellExecute = true,
            Arguments = CommandLine.Join(args),
        });
    }

    private Task<CommandResult> RunCliAsync(string distro, IEnumerable<string> cliArgs, bool stream, CancellationToken ct = default) =>
        RunScriptAsync(distro, string.Join(" ", new[] { CliPath }.Concat(cliArgs).Select(CommandLine.QuoteBash)), stream, ct);

    private async Task<CommandResult> RunScriptAsync(string distro, string script, bool stream, CancellationToken ct)
    {
        var r = await _wsl.RunAsync(distro, new[] { "bash", "-lc", script }, stream ? Logger.Output : null, ct).ConfigureAwait(false);
        if (!r.Success && (r.ExitCode == 127 || r.Error.Contains("command not found")))
            r.Error += $"\n'{CliPath}' was not found in '{distro}'. Install arduino-cli, symlink it into /usr/local/bin, " +
                       "or set ArduinoCliPath in settings.ini.";
        else if (!r.Success && r.Message.Contains("Permission denied") && r.Message.Contains("/dev/tty"))
            r.Error += "\nAdd your WSL user to the dialout group: sudo usermod -aG dialout $USER  (then wsl --shutdown).";
        return r;
    }

    /// <summary>Parses arduino-cli 1.x (<c>detected_ports</c>) and the legacy 0.x (<c>ports</c>) JSON.</summary>
    internal static List<ArduinoBoardPort> ParseBoardList(string json)
    {
        var list = new List<ArduinoBoardPort>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        IEnumerable<JsonElement> entries =
            root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() :
            root.TryGetProperty("detected_ports", out var dp) ? dp.EnumerateArray() :
            root.TryGetProperty("ports", out var p) ? p.EnumerateArray() :
            Enumerable.Empty<JsonElement>();

        foreach (var entry in entries)
        {
            var port = entry.TryGetProperty("port", out var pe) ? pe : entry;
            var item = new ArduinoBoardPort
            {
                Address = Str(port, "address") ?? "",
                Protocol = Str(port, "protocol") ?? "",
                ProtocolLabel = Str(port, "protocol_label") ?? "",
            };
            if (port.TryGetProperty("properties", out var props))
            {
                item.Vid = Str(props, "vid");
                item.Pid = Str(props, "pid");
                item.SerialNumber = Str(props, "serialNumber");
            }

            var boardsKey = entry.TryGetProperty("matching_boards", out var mb) ? mb
                          : entry.TryGetProperty("boards", out var b) ? b : default;
            if (boardsKey.ValueKind == JsonValueKind.Array && boardsKey.GetArrayLength() > 0)
            {
                var first = boardsKey[0];
                item.BoardName = Str(first, "name");
                item.Fqbn = Str(first, "fqbn");
            }
            if (item.Address.Length > 0) list.Add(item);
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

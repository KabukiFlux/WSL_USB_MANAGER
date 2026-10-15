// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using WslUsbManager.Infrastructure;
using WslUsbManager.Models;

namespace WslUsbManager.Services;

public interface IUsbipdService
{
    string? ExecutablePath { get; }
    bool IsInstalled { get; }
    Task<string?> GetVersionAsync();
    Task<List<UsbDevice>> ListDevicesAsync();
    Task<CommandResult> BindAsync(string busId, bool force = false);
    Task<CommandResult> UnbindAsync(UsbDevice device);
    Task<CommandResult> AttachAsync(string busId, string? distro);
    Task<CommandResult> DetachAsync(string busId);
}

public sealed partial class UsbipdService : IUsbipdService
{
    private readonly IDeviceDetectionService _detection;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public UsbipdService(IDeviceDetectionService detection) => _detection = detection;

    public string? ExecutablePath => Locate();
    public bool IsInstalled => ExecutablePath is not null;

    /// <summary>PATH first, then the default MSI install location (PATH may be stale right after install).</summary>
    public static string? Locate()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "usbipd.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        var programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    public async Task<string?> GetVersionAsync()
    {
        var exe = ExecutablePath;
        if (exe is null) return null;
        var r = await ProcessRunner.RunAsync(exe, new[] { "--version" }, logCommand: false).ConfigureAwait(false);
        if (!r.Success) return null;
        var v = r.Output.Trim();
        var plus = v.IndexOfAny(new[] { '-', '+' });
        return plus > 0 ? v[..plus] : v;
    }

    public async Task<List<UsbDevice>> ListDevicesAsync()
    {
        var exe = ExecutablePath ?? throw new InvalidOperationException("usbipd-win is not installed.");
        var r = await ProcessRunner.RunAsync(exe, new[] { "state" }, logCommand: false).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException($"usbipd state failed: {r.Message}");

        var state = JsonSerializer.Deserialize<UsbipdStateModel>(r.Output, JsonOptions) ?? new UsbipdStateModel();
        var list = new List<UsbDevice>();
        foreach (var d in state.Devices)
        {
            var device = Map(d);
            _detection.Classify(device);
            list.Add(device);
        }
        return list;
    }

    public Task<CommandResult> BindAsync(string busId, bool force = false)
    {
        var args = new List<string> { "bind", "--busid", busId };
        if (force) args.Add("--force");
        return ElevatedRunner.RunUsbipdElevatedAsync(args);
    }

    public Task<CommandResult> UnbindAsync(UsbDevice device) =>
        ElevatedRunner.RunUsbipdElevatedAsync(device.BusId is not null
            ? new[] { "unbind", "--busid", device.BusId }
            : new[] { "unbind", "--guid", device.PersistedGuid ?? throw new InvalidOperationException("Device has no BusId or GUID.") });

    public Task<CommandResult> AttachAsync(string busId, string? distro)
    {
        var args = new List<string> { "attach", "--wsl" };
        if (!string.IsNullOrWhiteSpace(distro)) args.Add(distro);
        args.AddRange(new[] { "--busid", busId });
        return RunAsync(args);
    }

    public Task<CommandResult> DetachAsync(string busId) => RunAsync(new[] { "detach", "--busid", busId });

    private Task<CommandResult> RunAsync(IEnumerable<string> args)
    {
        var exe = ExecutablePath;
        return exe is null
            ? Task.FromResult(CommandResult.Fail("usbipd-win is not installed."))
            : ProcessRunner.RunAsync(exe, args, line => Logger.Output(line));
    }

    internal static UsbDevice Map(UsbipdDeviceModel d)
    {
        var device = new UsbDevice
        {
            BusId = string.IsNullOrWhiteSpace(d.BusId) ? null : d.BusId,
            InstanceId = d.InstanceId ?? "",
            Description = d.Description ?? "",
            IsForced = d.IsForced,
            PersistedGuid = d.PersistedGuid,
            ClientIpAddress = d.ClientIPAddress,
            State = d.ClientIPAddress is not null ? DeviceState.Attached
                  : d.PersistedGuid is not null ? DeviceState.Shared
                  : DeviceState.NotShared,
        };

        var m = InstanceIdRegex().Match(device.InstanceId);
        if (m.Success)
        {
            device.VendorId = Convert.ToUInt16(m.Groups[1].Value, 16);
            device.ProductId = Convert.ToUInt16(m.Groups[2].Value, 16);
            device.SerialNumber = m.Groups[3].Value;
            device.HasRealSerial = !device.SerialNumber.Contains('&');
        }

        device.ProductString = DevicePropertyReader.GetProductString(device.InstanceId);

        var com = ComPortRegex().Match(device.Description);
        if (com.Success) device.ComPort = com.Groups[1].Value;
        return device;
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})[^\\]*\\(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstanceIdRegex();

    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComPortRegex();
}

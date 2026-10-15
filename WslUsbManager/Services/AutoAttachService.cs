// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using WslUsbManager.Models;

namespace WslUsbManager.Services;

public interface IAutoAttachService
{
    /// <summary>Attaches connected favorites flagged AutoAttach. Returns InstanceId → distro for each successful attach.</summary>
    Task<Dictionary<string, string>> ProcessAsync(IReadOnlyList<UsbDevice> devices, string fallbackDistro);

    /// <summary>Called on manual detach: don't re-attach this device until it is unplugged.</summary>
    void Suppress(string instanceId);

    /// <summary>True while a flashing flow needs bootloader drives to stay in Windows.</summary>
    bool Paused { get; set; }
}

public sealed class AutoAttachService : IAutoAttachService
{
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;
    private readonly IUsbipdService _usbipd;
    private readonly IWslService _wsl;
    private readonly HashSet<string> _suppressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastFailure = new(StringComparer.OrdinalIgnoreCase);

    public AutoAttachService(ISettingsService settings, IUsbipdService usbipd, IWslService wsl)
    {
        _settings = settings;
        _usbipd = usbipd;
        _wsl = wsl;
    }

    public void Suppress(string instanceId) => _suppressed.Add(instanceId);

    public bool Paused { get; set; }

    public async Task<Dictionary<string, string>> ProcessAsync(IReadOnlyList<UsbDevice> devices, string fallbackDistro)
    {
        var attached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Unplugged (or vanished) devices lose their suppression.
        var connected = devices.Where(d => d.IsConnected).Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _suppressed.RemoveWhere(id => !connected.Contains(id));

        if (!_settings.AutoAttachEnabled || Paused) return attached;

        foreach (var device in devices)
        {
            if (!device.IsConnected || device.State == DeviceState.Attached) continue;
            if (_suppressed.Contains(device.InstanceId)) continue;
            if (_lastFailure.TryGetValue(device.InstanceId, out var t) && DateTime.UtcNow - t < RetryBackoff) continue;

            var fav = _settings.FindFavorite(device);
            if (fav is null || !fav.AutoAttach) continue;

            var distro = string.IsNullOrWhiteSpace(fav.Distro) ? fallbackDistro : fav.Distro;
            Logger.Info($"Auto-attach: {device.DisplayName} ({device.BusId}) → {distro}");

            if (device.State == DeviceState.NotShared)
            {
                var bind = await _usbipd.BindAsync(device.BusId!).ConfigureAwait(false);
                if (!bind.Success) { Fail(device, bind); continue; }
            }

            if (!string.IsNullOrWhiteSpace(distro))
                await _wsl.EnsureRunningAsync(distro).ConfigureAwait(false);

            var r = await _usbipd.AttachAsync(device.BusId!, distro).ConfigureAwait(false);
            if (r.Success)
            {
                _lastFailure.Remove(device.InstanceId);
                attached[device.InstanceId] = distro;
            }
            else Fail(device, r);
        }
        return attached;
    }

    private void Fail(UsbDevice device, CommandResult r)
    {
        _lastFailure[device.InstanceId] = DateTime.UtcNow;
        Logger.Warn($"Auto-attach failed for {device.DisplayName}: {r.Message} (retry in {RetryBackoff.TotalSeconds:0}s)");
    }
}

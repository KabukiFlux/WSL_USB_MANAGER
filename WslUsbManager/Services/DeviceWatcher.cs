// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Management;

namespace WslUsbManager.Services;

/// <summary>WMI device arrival/removal watcher, debounced (attach/detach and plug-in produce bursts of events).</summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly TimeSpan _debounce;
    private ManagementEventWatcher? _watcher;
    private Timer? _timer;

    public DeviceWatcher(TimeSpan? debounce = null) => _debounce = debounce ?? TimeSpan.FromMilliseconds(1500);

    /// <summary>Raised on a thread-pool thread after the burst settles.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_watcher is not null) return;
        try
        {
            _timer = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3"));
            _watcher.EventArrived += (_, _) => _timer?.Change(_debounce, Timeout.InfiniteTimeSpan);
            _watcher.Start();
            Logger.Info("Device watcher started.");
        }
        catch (Exception ex)
        {
            Logger.Error("Could not start WMI device watcher (auto-refresh disabled)", ex);
            _watcher = null;
        }
    }

    public void Stop()
    {
        try { _watcher?.Stop(); } catch { /* ignore */ }
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();
}

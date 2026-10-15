// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using WslUsbManager.Models;
using WslUsbManager.Services;

namespace WslUsbManager.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IUsbipdService _usbipd;
    private readonly IWslService _wsl;
    private readonly IArduinoCliService _arduino;
    private readonly IAutoAttachService _autoAttach;
    private readonly DeviceWatcher _watcher = new();
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly Dictionary<string, string> _attachedDistros = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _arduinoCts;
    private int _changeQueued;
    private bool _initialized;

    public MainViewModel()
    {
        _settings = new SettingsService();
        _usbipd = new UsbipdService(new DeviceDetectionService());
        _wsl = new WslService();
        _arduino = new ArduinoCliService(_wsl);
        _autoAttach = new AutoAttachService(_settings, _usbipd, _wsl);

        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = o => o is DeviceViewModel d && IsFamilyVisible(d.Family);
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceViewModel.SortKey), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(() => RunOpAsync("Refreshing…", async () =>
        {
            await LoadDistrosCoreAsync();
            await RefreshDevicesCoreAsync();
            StatusText = "Ready";
        }));
        BindCommand = new AsyncRelayCommand(() => BindAsync(force: false),
            () => Selected is { IsConnected: true, State: DeviceState.NotShared });
        BindForceCommand = new AsyncRelayCommand(() => BindAsync(force: true),
            () => Selected is { IsConnected: true } s && s.State != DeviceState.Attached);
        AttachCommand = new AsyncRelayCommand(AttachAsync,
            () => Selected is { IsConnected: true } s && s.State != DeviceState.Attached && SelectedDistro is not null);
        DetachCommand = new AsyncRelayCommand(DetachAsync, () => Selected is { State: DeviceState.Attached });
        UnbindCommand = new AsyncRelayCommand(UnbindAsync,
            () => Selected is { } s && (s.State != DeviceState.NotShared || !s.IsConnected));
        ToggleAttachCommand = new AsyncRelayCommand(
            () => Selected?.State == DeviceState.Attached ? DetachAsync() : AttachAsync(),
            () => Selected is { IsConnected: true } && SelectedDistro is not null);
        ToggleFavoriteCommand = new RelayCommand(() => { if (Selected is { } s) s.IsFavorite = !s.IsFavorite; }, () => Selected is not null);
        CopyVidPidCommand = new RelayCommand(() => CopyText(Selected?.VidPid), () => Selected is not null);
        CopyInstanceIdCommand = new RelayCommand(() => CopyText(Selected?.InstanceId), () => Selected is not null);
        StartDistroCommand = new AsyncRelayCommand(StartDistroAsync, () => SelectedDistro is { IsRunning: false });
        InstallUsbipdCommand = new RelayCommand(InstallUsbipd);
        OpenLogFolderCommand = new RelayCommand(() => OpenPath(Logger.LogDir));
        OpenSettingsCommand = new RelayCommand(() => { _settings.Save(); OpenPath(_settings.FilePath); });

        DetectBoardsCommand = new AsyncRelayCommand(DetectBoardsAsync, () => CanRunArduino);
        BrowseSketchCommand = new RelayCommand(BrowseSketch);
        UploadCommand = new AsyncRelayCommand(UploadAsync, () => CanRunArduino);
        MonitorCommand = new RelayCommand(OpenMonitor, () => SelectedDistro is not null && !string.IsNullOrWhiteSpace(Port));
        RunCustomCommand = new AsyncRelayCommand(RunCustomAsync, () => CanRunArduino && !string.IsNullOrWhiteSpace(CustomArgs));
        CancelArduinoCommand = new RelayCommand(() => _arduinoCts?.Cancel(), () => IsArduinoBusy);
        InstallCoreCommand = new AsyncRelayCommand(InstallCoreAsync, () => CanRunArduino && SelectedCore is not null);
        InstallUdevRulesCommand = new AsyncRelayCommand(InstallUdevRulesAsync, () => SelectedDistro is not null);

        _watcher.Changed += OnDevicesChanged;
    }

    #region Collections & selection

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ICollectionView DevicesView { get; }
    public ObservableCollection<WslDistro> Distros { get; } = new();
    public ObservableCollection<ArduinoBoardPort> BoardPorts { get; } = new();

    public IReadOnlyList<string> CommonFqbns => BoardSupport.CommonFqbns;
    public IReadOnlyList<CorePreset> CorePresets => BoardSupport.Cores;
    public IReadOnlyList<UploadMethodOption> UploadMethods => BoardSupport.UploadMethods;

    public IReadOnlyList<int> BaudRates { get; } = new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    private DeviceViewModel? _selected;
    public DeviceViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value) || value is null) return;
            // Pre-fill the FQBN for a recognized board (only when the user hasn't chosen one).
            if (string.IsNullOrWhiteSpace(Fqbn) && BoardSupport.SuggestFqbn(value.Device) is { } suggestion)
                Fqbn = suggestion;
        }
    }

    private WslDistro? _selectedDistro;
    public WslDistro? SelectedDistro
    {
        get => _selectedDistro;
        set
        {
            if (!SetField(ref _selectedDistro, value) || value is null) return;
            OnPropertyChanged(nameof(DistroStateText));
            _settings.LastSelectedDistro = value.Name;
            if (_initialized) _settings.Save();
        }
    }

    public string DistroStateText => SelectedDistro is null ? "" : SelectedDistro.IsRunning ? "Running" : "Stopped";

    #endregion

    #region Status

    private string _statusText = "Starting…";
    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }

    private bool _isArduinoBusy;
    public bool IsArduinoBusy { get => _isArduinoBusy; private set => SetField(ref _isArduinoBusy, value); }

    private string _usbipdVersionText = "";
    public string UsbipdVersionText { get => _usbipdVersionText; private set => SetField(ref _usbipdVersionText, value); }

    private string _deviceCountText = "";
    public string DeviceCountText { get => _deviceCountText; private set => SetField(ref _deviceCountText, value); }

    #endregion

    #region Filters & options

    private bool IsFamilyVisible(DetectedFamily f) =>
        _settings.Filters.TryGetValue(f is DetectedFamily.Unknown ? DetectedFamily.Other : f, out var v) && v;

    private bool GetFilter(DetectedFamily f) => _settings.Filters[f];

    private void SetFilter(DetectedFamily f, bool value, string name)
    {
        if (_settings.Filters[f] == value) return;
        _settings.Filters[f] = value;
        OnPropertyChanged(name);
        DevicesView.Refresh();
        UpdateCountText();
        _settings.Save();
    }

    public bool ShowArduino { get => GetFilter(DetectedFamily.Arduino); set => SetFilter(DetectedFamily.Arduino, value, nameof(ShowArduino)); }
    public bool ShowEsp32 { get => GetFilter(DetectedFamily.EspressifEsp32); set => SetFilter(DetectedFamily.EspressifEsp32, value, nameof(ShowEsp32)); }
    public bool ShowStm32 { get => GetFilter(DetectedFamily.Stm32); set => SetFilter(DetectedFamily.Stm32, value, nameof(ShowStm32)); }
    public bool ShowPico { get => GetFilter(DetectedFamily.RaspberryPiPico); set => SetFilter(DetectedFamily.RaspberryPiPico, value, nameof(ShowPico)); }
    public bool ShowFpga { get => GetFilter(DetectedFamily.Fpga); set => SetFilter(DetectedFamily.Fpga, value, nameof(ShowFpga)); }
    public bool ShowSerial { get => GetFilter(DetectedFamily.GenericSerialBridge); set => SetFilter(DetectedFamily.GenericSerialBridge, value, nameof(ShowSerial)); }
    public bool ShowOther { get => GetFilter(DetectedFamily.Other); set => SetFilter(DetectedFamily.Other, value, nameof(ShowOther)); }

    public bool AutoAttachEnabled
    {
        get => _settings.AutoAttachEnabled;
        set
        {
            if (_settings.AutoAttachEnabled == value) return;
            _settings.AutoAttachEnabled = value;
            OnPropertyChanged();
            _settings.Save();
            if (value) _ = RunOpAsync("Auto-attaching favorites…", RunAutoAttachCoreAsync, quiet: true);
        }
    }

    #endregion

    #region Arduino properties

    private string _port = "";
    public string Port { get => _port; set => SetField(ref _port, value?.Trim() ?? ""); }

    private string _fqbn = "";
    public string Fqbn { get => _fqbn; set => SetField(ref _fqbn, value?.Trim() ?? ""); }

    private string _sketchPath = "";
    public string SketchPath { get => _sketchPath; set => SetField(ref _sketchPath, value?.Trim() ?? ""); }

    private int _monitorBaud = 115200;
    public int MonitorBaud { get => _monitorBaud; set => SetField(ref _monitorBaud, value); }

    private string _customArgs = "core list";
    public string CustomArgs { get => _customArgs; set => SetField(ref _customArgs, value); }

    private ArduinoBoardPort? _selectedBoardPort;
    public ArduinoBoardPort? SelectedBoardPort
    {
        get => _selectedBoardPort;
        set
        {
            if (!SetField(ref _selectedBoardPort, value) || value is null) return;
            Port = value.Address;
            if (!string.IsNullOrEmpty(value.Fqbn)) Fqbn = value.Fqbn;
        }
    }

    private CorePreset? _selectedCore = BoardSupport.Cores[1];
    public CorePreset? SelectedCore
    {
        get => _selectedCore;
        set { if (SetField(ref _selectedCore, value)) OnPropertyChanged(nameof(SelectedCoreNote)); }
    }

    public string SelectedCoreNote => SelectedCore?.Note ?? "";

    private UploadMethodOption _selectedUploadMethod = BoardSupport.UploadMethods[0];
    public UploadMethodOption SelectedUploadMethod
    {
        get => _selectedUploadMethod;
        set
        {
            if (!SetField(ref _selectedUploadMethod, value)) return;
            _settings.UploadMethod = value.Method.ToString();
            if (_initialized) _settings.Save();
        }
    }

    private bool CanRunArduino => SelectedDistro is not null && !IsArduinoBusy;

    #endregion

    #region Commands

    public ICommand RefreshCommand { get; }
    public ICommand BindCommand { get; }
    public ICommand BindForceCommand { get; }
    public ICommand AttachCommand { get; }
    public ICommand DetachCommand { get; }
    public ICommand UnbindCommand { get; }
    public ICommand ToggleAttachCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand CopyVidPidCommand { get; }
    public ICommand CopyInstanceIdCommand { get; }
    public ICommand StartDistroCommand { get; }
    public ICommand InstallUsbipdCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand DetectBoardsCommand { get; }
    public ICommand BrowseSketchCommand { get; }
    public ICommand UploadCommand { get; }
    public ICommand MonitorCommand { get; }
    public ICommand RunCustomCommand { get; }
    public ICommand CancelArduinoCommand { get; }
    public ICommand InstallCoreCommand { get; }
    public ICommand InstallUdevRulesCommand { get; }

    #endregion

    #region Lifecycle

    public async Task InitializeAsync()
    {
        _settings.Load();
        _arduino.CliPath = _settings.ArduinoCliPath;
        Port = _settings.LastPort;
        Fqbn = _settings.LastFqbn;
        SketchPath = _settings.LastSketchPath;
        MonitorBaud = _settings.MonitorBaud;
        SelectedUploadMethod = BoardSupport.UploadMethods.FirstOrDefault(m => m.Method.ToString() == _settings.UploadMethod)
                               ?? BoardSupport.UploadMethods[0];
        foreach (var n in new[] { nameof(ShowArduino), nameof(ShowEsp32), nameof(ShowStm32), nameof(ShowPico),
                                  nameof(ShowFpga), nameof(ShowSerial), nameof(ShowOther), nameof(AutoAttachEnabled) })
            OnPropertyChanged(n);

        await RunOpAsync("Loading WSL distributions…", LoadDistrosCoreAsync);
        _initialized = true;

        if (!_usbipd.IsInstalled)
        {
            StatusText = "usbipd-win is not installed.";
            UsbipdVersionText = "usbipd-win: not installed";
            PromptInstallUsbipd();
            return;
        }

        var version = await _usbipd.GetVersionAsync();
        UsbipdVersionText = $"usbipd-win {version ?? "?"}";
        if (version is not null && Version.TryParse(version, out var v) && v.Major < 4)
            Logger.Warn($"usbipd-win {version} is older than 4.0; 'usbipd state' may not be available. Please update.");

        await RunOpAsync("Reading USB devices…", async () =>
        {
            await RefreshDevicesCoreAsync();
            await RunAutoAttachCoreAsync();
            StatusText = "Ready";
        });
        _watcher.Start();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _arduinoCts?.Cancel();
        _settings.LastPort = Port;
        _settings.LastFqbn = Fqbn;
        _settings.LastSketchPath = SketchPath;
        _settings.MonitorBaud = MonitorBaud;
        _settings.Save();
    }

    private void OnDevicesChanged()
    {
        // Coalesce bursts: at most one queued refresh.
        if (Interlocked.Exchange(ref _changeQueued, 1) == 1) return;
        Application.Current?.Dispatcher.InvokeAsync(() => RunOpAsync("Device change detected…", async () =>
        {
            Interlocked.Exchange(ref _changeQueued, 0);
            await RefreshDevicesCoreAsync();
            await RunAutoAttachCoreAsync();
            StatusText = "Ready";
        }, quiet: true));
    }

    #endregion

    #region Core operations

    /// <summary>Serializes all usbipd operations and maps exceptions to the status bar.</summary>
    private async Task RunOpAsync(string status, Func<Task> op, bool quiet = false)
    {
        await _opLock.WaitAsync();
        IsBusy = true;
        if (!quiet) StatusText = status;
        try
        {
            await op();
        }
        catch (Exception ex)
        {
            Logger.Error(status.TrimEnd('…'), ex);
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _opLock.Release();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task LoadDistrosCoreAsync()
    {
        var distros = await _wsl.ListDistrosAsync();
        var keep = SelectedDistro?.Name ?? _settings.LastSelectedDistro;
        Distros.Clear();
        foreach (var d in distros) Distros.Add(d);
        SelectedDistro = Distros.FirstOrDefault(d => d.Name.Equals(keep, StringComparison.OrdinalIgnoreCase))
                         ?? Distros.FirstOrDefault(d => d.IsDefault) ?? Distros.FirstOrDefault();
        OnPropertyChanged(nameof(DistroStateText));
        if (Distros.Count == 0) Logger.Warn("No WSL distributions found. Install one with: wsl --install -d Ubuntu");
    }

    private async Task RefreshDevicesCoreAsync()
    {
        if (!_usbipd.IsInstalled) return;
        var devices = await _usbipd.ListDevicesAsync();
        var byId = Devices.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            seen.Add(device.InstanceId);
            var fav = _settings.FindFavorite(device);
            if (device.State != DeviceState.Attached) _attachedDistros.Remove(device.InstanceId);

            if (byId.TryGetValue(device.InstanceId, out var vm))
                vm.Update(device, fav is not null, fav?.AutoAttach ?? false);
            else
                Devices.Add(vm = new DeviceViewModel(device, fav is not null, fav?.AutoAttach ?? false,
                    OnFavoriteChanged, OnAutoAttachChanged));

            vm.AttachedDistro = _attachedDistros.GetValueOrDefault(device.InstanceId);
        }

        foreach (var gone in Devices.Where(d => !seen.Contains(d.InstanceId)).ToList())
            Devices.Remove(gone);

        DevicesView.Refresh();
        UpdateCountText();
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task RunAutoAttachCoreAsync()
    {
        var attached = await _autoAttach.ProcessAsync(Devices.Select(d => d.Device).ToList(), SelectedDistro?.Name ?? "");
        if (attached.Count == 0) return;
        foreach (var (id, distro) in attached) _attachedDistros[id] = distro;
        await RefreshDevicesCoreAsync();
        StatusText = $"Auto-attached {attached.Count} device(s).";
    }

    private void UpdateCountText()
    {
        var visible = DevicesView.Cast<object>().Count();
        DeviceCountText = $"{visible} of {Devices.Count} devices shown";
    }

    #endregion

    #region usbipd actions

    private Task BindAsync(bool force) => Selected is not { } dev ? Task.CompletedTask :
        RunOpAsync($"Binding {dev.Name}…", async () =>
        {
            var r = await _usbipd.BindAsync(dev.Device.BusId!, force);
            await RefreshDevicesCoreAsync();
            Report(r, $"{dev.Name} is now shared{(force ? " (forced)" : "")}.");
        });

    private Task AttachAsync() => Selected is not { } dev || SelectedDistro is not { } distro ? Task.CompletedTask :
        RunOpAsync($"Attaching {dev.Name} to {distro.Name}…", async () =>
        {
            var busId = dev.Device.BusId!;
            if (!distro.IsRunning)
            {
                StatusText = $"Starting {distro.Name}…";
                await _wsl.EnsureRunningAsync(distro.Name);
            }
            if (dev.State == DeviceState.NotShared)
            {
                StatusText = $"Binding {dev.Name} (administrator approval needed)…";
                var bind = await _usbipd.BindAsync(busId);
                if (!bind.Success) { Report(bind, ""); return; }
            }
            StatusText = $"Attaching {dev.Name} to {distro.Name}…";
            var r = await _usbipd.AttachAsync(busId, distro.Name);
            if (r.Success) _attachedDistros[dev.InstanceId] = distro.Name;
            await LoadDistrosCoreAsync();
            await RefreshDevicesCoreAsync();
            Report(r, $"{dev.Name} attached to {distro.Name}. Use 'Detect boards' to find its /dev/tty* port.");
        });

    private Task DetachAsync() => Selected is not { } dev ? Task.CompletedTask :
        RunOpAsync($"Detaching {dev.Name}…", async () =>
        {
            var r = await _usbipd.DetachAsync(dev.Device.BusId!);
            if (r.Success)
            {
                _autoAttach.Suppress(dev.InstanceId);
                _attachedDistros.Remove(dev.InstanceId);
            }
            await RefreshDevicesCoreAsync();
            Report(r, $"{dev.Name} detached (auto-attach paused until it is replugged).");
        });

    private Task UnbindAsync() => Selected is not { } dev ? Task.CompletedTask :
        RunOpAsync($"Unbinding {dev.Name}…", async () =>
        {
            var r = await _usbipd.UnbindAsync(dev.Device);
            if (r.Success) _autoAttach.Suppress(dev.InstanceId);
            await RefreshDevicesCoreAsync();
            Report(r, $"{dev.Name} is no longer shared.");
        });

    private Task StartDistroAsync() => SelectedDistro is not { } distro ? Task.CompletedTask :
        RunOpAsync($"Starting {distro.Name}…", async () =>
        {
            var r = await _wsl.EnsureRunningAsync(distro.Name);
            await LoadDistrosCoreAsync();
            Report(r, $"{distro.Name} is running.");
        });

    private void Report(CommandResult r, string successText)
    {
        if (r.Success) { StatusText = successText; Logger.Info(successText); return; }
        var msg = r.Message;
        if (msg.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || msg.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            msg += "  (this operation needs administrator approval)";
        StatusText = r.Cancelled ? "Operation cancelled." : "Failed: " + FirstLine(msg);
    }

    #endregion

    #region Favorites

    private void OnFavoriteChanged(DeviceViewModel vm)
    {
        if (vm.IsFavorite)
        {
            if (_settings.FindFavorite(vm.Device) is null)
                _settings.Favorites.Add(FavoriteDevice.FromDevice(vm.Device, SelectedDistro?.Name ?? ""));
        }
        else
        {
            _settings.Favorites.RemoveAll(f => f.Matches(vm.Device));
        }
        SaveFavoritesAndSync();
    }

    private void OnAutoAttachChanged(DeviceViewModel vm)
    {
        var fav = _settings.FindFavorite(vm.Device);
        if (fav is null && vm.AutoAttach)
            _settings.Favorites.Add(fav = FavoriteDevice.FromDevice(vm.Device, SelectedDistro?.Name ?? ""));
        if (fav is not null)
        {
            fav.AutoAttach = vm.AutoAttach;
            if (vm.AutoAttach && SelectedDistro is not null) fav.Distro = SelectedDistro.Name;
        }
        SaveFavoritesAndSync();
        if (vm.AutoAttach && AutoAttachEnabled)
            _ = RunOpAsync("Auto-attaching favorites…", RunAutoAttachCoreAsync, quiet: true);
    }

    private void SaveFavoritesAndSync()
    {
        _settings.Save();
        // A VID:PID-only favorite may apply to several rows; re-sync after the current binding update completes.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var d in Devices)
            {
                var fav = _settings.FindFavorite(d.Device);
                if (d.IsFavorite != (fav is not null) || d.AutoAttach != (fav?.AutoAttach ?? false))
                    d.Update(d.Device, fav is not null, fav?.AutoAttach ?? false);
            }
        });
    }

    #endregion

    #region Arduino actions

    private async Task RunArduinoAsync(string status, Func<string, CancellationToken, Task> op)
    {
        if (SelectedDistro is not { } distro) return;
        _arduinoCts = new CancellationTokenSource();
        IsArduinoBusy = true;
        StatusText = status;
        _arduino.CliPath = _settings.ArduinoCliPath;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await op(distro.Name, _arduinoCts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error(status.TrimEnd('…'), ex);
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            IsArduinoBusy = false;
            _arduinoCts.Dispose();
            _arduinoCts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private Task DetectBoardsAsync() => RunArduinoAsync("Detecting boards inside WSL…", async (distro, _) =>
    {
        var (ports, r) = await _arduino.BoardListAsync(distro);
        if (!r.Success) { Report(r, ""); return; }

        var keepPort = Port;
        BoardPorts.Clear();
        foreach (var p in ports.Where(p => p.Protocol == "serial").Concat(ports.Where(p => p.Protocol != "serial")))
        {
            BoardPorts.Add(p);
            Logger.Info($"  found {p.Display}");
        }
        var best = BoardPorts.FirstOrDefault(p => p.Address == keepPort)
                   ?? BoardPorts.FirstOrDefault(p => p.Fqbn is not null)
                   ?? BoardPorts.FirstOrDefault(p => p.Protocol == "serial");
        if (best is not null) SelectedBoardPort = best;
        StatusText = BoardPorts.Count == 0
            ? $"No ports found in {distro}. Is the device attached?"
            : $"{BoardPorts.Count} port(s) found in {distro}.";
    });

    private void BrowseSketch()
    {
        var dlg = new OpenFolderDialog { Title = "Select the sketch folder (contains <folder>.ino)" };
        if (Directory.Exists(SketchPath)) dlg.InitialDirectory = SketchPath;
        if (dlg.ShowDialog() == true) SketchPath = dlg.FolderName;
    }

    private Task UploadAsync() => RunArduinoAsync("Compiling and uploading…", async (distro, ct) =>
    {
        if (string.IsNullOrWhiteSpace(Fqbn) || string.IsNullOrWhiteSpace(SketchPath))
        {
            StatusText = "Select an FQBN and a sketch folder first.";
            return;
        }
        var method = BoardSupport.Resolve(SelectedUploadMethod.Method, Fqbn);
        if (method == UploadMethod.ArduinoCli && string.IsNullOrWhiteSpace(Port))
        {
            StatusText = "Select the board's port first (Detect boards).";
            return;
        }
        var sketch = SketchPath.StartsWith('/') ? SketchPath : await _wsl.ToWslPathAsync(distro, SketchPath);
        _settings.LastPort = Port;
        _settings.LastFqbn = Fqbn;
        _settings.LastSketchPath = SketchPath;
        _settings.Save();
        Logger.Info($"Upload method: {BoardSupport.UploadMethods.First(m => m.Method == method).Name}");

        switch (method)
        {
            case UploadMethod.Uf2Copy:
                await UploadUf2Async(distro, sketch, ct);
                break;
            case UploadMethod.StMassStorage:
                await UploadStMassStorageAsync(distro, sketch, ct);
                break;
            default:
                StatusText = $"Compiling and uploading to {Port} ({Fqbn})…";
                var r = await _arduino.CompileUploadAsync(distro, Port, Fqbn, sketch, ct);
                if (!r.Success && Fqbn.StartsWith("STMicroelectronics:", StringComparison.OrdinalIgnoreCase))
                    Logger.Warn("STM32 uploads inside WSL need the ST-LINK attached plus USB permissions ('USB permissions' button). " +
                                "upload_method=OpenOCDSTLink works with the bundled OpenOCD; swd/dfu/serialMethod need " +
                                "STM32CubeProgrammer installed in WSL. Nucleo/Discovery boards can use 'Mass storage via Windows'.");
                Report(r, $"Upload to {Port} finished successfully.");
                break;
        }
    });

    /// <summary>Compiles inside WSL into a Windows folder and returns the produced firmware file (.uf2 / .bin).</summary>
    private async Task<string?> CompileToWindowsAsync(string distro, string wslSketch, string extension, CancellationToken ct)
    {
        var name = Path.GetFileName(SketchPath.TrimEnd('\\', '/'));
        var outDir = Path.Combine(Logger.AppDataDir, "build", string.Concat(name.Split(Path.GetInvalidFileNameChars())));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        StatusText = $"Compiling {name} for {Fqbn}…";
        var r = await _arduino.CompileAsync(distro, Fqbn, wslSketch, await _wsl.ToWslPathAsync(distro, outDir), ct);
        if (!r.Success) { Report(r, ""); return null; }

        var file = Directory.GetFiles(outDir, "*" + extension)
            .Where(f => !f.Contains("with_bootloader", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Length).FirstOrDefault();
        if (file is null) StatusText = $"Compiled, but no {extension} file was produced in {outDir}.";
        return file;
    }

    /// <summary>
    /// RP2040/RP2350: compile in WSL → 1200-baud touch → the board reboots into BOOTSEL and Windows mounts its drive →
    /// copy the .uf2 → the board reboots into the new firmware → re-attach it to WSL.
    /// </summary>
    private async Task UploadUf2Async(string distro, string wslSketch, CancellationToken ct)
    {
        var uf2 = await CompileToWindowsAsync(distro, wslSketch, ".uf2", ct);
        if (uf2 is null) return;

        _autoAttach.Paused = true; // the BOOTSEL drive must stay in Windows
        try
        {
            var attachedBefore = await GetAttachedIdsAsync();
            var drive = WindowsFlasher.FindUf2Drive();
            if (drive is null)
            {
                if (string.IsNullOrWhiteSpace(Port))
                {
                    StatusText = "No BOOTSEL drive and no port: hold BOOTSEL while plugging the board in, or select its port, then Upload again.";
                    return;
                }
                StatusText = $"Resetting {Port} into BOOTSEL…";
                var touch = await _arduino.TouchResetAsync(distro, Port);
                if (!touch.Success) Logger.Warn($"1200-baud reset on {Port} failed: {touch.Message}");
                drive = await WindowsFlasher.WaitForDriveAsync(WindowsFlasher.FindUf2Drive, TimeSpan.FromSeconds(20), ct);
                if (drive is null)
                {
                    StatusText = "The BOOTSEL drive (RPI-RP2 / RP2350) didn't appear in Windows. Hold BOOTSEL while plugging in, then Upload again.";
                    return;
                }
            }

            LogUf2BoardId(drive);
            var root = drive.RootDirectory.FullName;
            StatusText = $"Flashing {Path.GetFileName(uf2)} to {WindowsFlasher.Describe(drive)}…";
            await WindowsFlasher.CopyAsync(uf2, drive, ct);
            await WindowsFlasher.WaitForDriveGoneAsync(root, TimeSpan.FromSeconds(15), ct);

            StatusText = "Flashed. Waiting for the board to restart…";
            var reattached = await ReattachAsync(attachedBefore, distro, CancellationToken.None);
            StatusText = "Upload finished (UF2 via Windows)" + (reattached > 0 ? $"; board re-attached to {distro}." : ".");
            Logger.Info(StatusText);
        }
        finally
        {
            _autoAttach.Paused = false;
        }
    }

    /// <summary>
    /// STM32 Nucleo/Discovery: compile in WSL → (detach the ST-LINK if attached, so Windows mounts its drive) →
    /// copy the .bin → the ST-LINK programs the target and remounts → re-attach for the serial monitor.
    /// </summary>
    private async Task UploadStMassStorageAsync(string distro, string wslSketch, CancellationToken ct)
    {
        var bin = await CompileToWindowsAsync(distro, wslSketch, ".bin", ct);
        if (bin is null) return;

        _autoAttach.Paused = true;
        var detached = new List<string>();
        try
        {
            var drive = WindowsFlasher.FindStLinkDrive();
            if (drive is null)
            {
                var stlinks = await WithUsbLockAsync(async () =>
                {
                    await RefreshDevicesCoreAsync();
                    return Devices.Where(d => d.State == DeviceState.Attached && IsStLink(d.Device)).ToList();
                });
                foreach (var d in stlinks)
                {
                    StatusText = $"Detaching {d.Name} so Windows can mount its drive…";
                    var r = await WithUsbLockAsync(() => _usbipd.DetachAsync(d.Device.BusId!));
                    if (r.Success) detached.Add(d.InstanceId);
                }
                StatusText = "Waiting for the ST-LINK drive (NOD_… / DIS_…)…";
                drive = await WindowsFlasher.WaitForDriveAsync(WindowsFlasher.FindStLinkDrive, TimeSpan.FromSeconds(20), ct);
                if (drive is null)
                {
                    StatusText = "No ST-LINK drive found. Is a Nucleo/Discovery board (ST-LINK V2-1/V3) plugged in?";
                    return;
                }
            }

            var root = drive.RootDirectory.FullName;
            StatusText = $"Flashing {Path.GetFileName(bin)} to {WindowsFlasher.Describe(drive)}…";
            await WindowsFlasher.CopyAsync(bin, drive, ct);

            // The ST-LINK programs the MCU, then re-mounts the drive (FAIL.TXT appears on error).
            StatusText = "ST-LINK is programming the board…";
            await WindowsFlasher.WaitForDriveGoneAsync(root, TimeSpan.FromSeconds(15), ct);
            var after = await WindowsFlasher.WaitForDriveAsync(WindowsFlasher.FindStLinkDrive, TimeSpan.FromSeconds(15), ct);
            var fail = after is null ? null : Path.Combine(after.RootDirectory.FullName, "FAIL.TXT");
            if (fail is not null && File.Exists(fail))
            {
                var msg = (await File.ReadAllTextAsync(fail, ct)).Trim();
                Logger.Error($"ST-LINK reported: {msg}");
                StatusText = "ST-LINK flashing failed: " + msg;
                return;
            }
            StatusText = "Upload finished (ST-LINK mass storage).";
            Logger.Info(StatusText);
        }
        finally
        {
            if (detached.Count > 0)
            {
                var n = await ReattachAsync(detached, distro, CancellationToken.None);
                if (n > 0) StatusText += $" ST-LINK re-attached to {distro}.";
            }
            _autoAttach.Paused = false;
        }
    }

    private static bool IsStLink(UsbDevice d) =>
        d.VendorId == 0x0483 && (d.KnownName ?? d.ProductString ?? "").Contains("LINK", StringComparison.OrdinalIgnoreCase);

    private static void LogUf2BoardId(DriveInfo drive)
    {
        try
        {
            var info = File.ReadAllLines(Path.Combine(drive.RootDirectory.FullName, "INFO_UF2.TXT"));
            var id = info.FirstOrDefault(l => l.StartsWith("Board-ID", StringComparison.OrdinalIgnoreCase));
            if (id is not null) Logger.Info($"BOOTSEL drive {WindowsFlasher.Describe(drive)}: {id}");
        }
        catch (IOException) { }
    }

    private Task<HashSet<string>> GetAttachedIdsAsync() => WithUsbLockAsync(async () =>
    {
        await RefreshDevicesCoreAsync();
        return Devices.Where(d => d.State == DeviceState.Attached).Select(d => d.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>Waits (≤ 25 s) for the given devices to re-enumerate and attaches them again. Returns how many were attached.</summary>
    private async Task<int> ReattachAsync(IEnumerable<string> instanceIds, string distro, CancellationToken ct)
    {
        var pending = new HashSet<string>(instanceIds, StringComparer.OrdinalIgnoreCase);
        var attached = 0;
        var until = DateTime.UtcNow.AddSeconds(25);
        while (pending.Count > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(1000, ct);
            await WithUsbLockAsync(async () =>
            {
                await RefreshDevicesCoreAsync();
                foreach (var d in Devices.Where(d => pending.Contains(d.InstanceId)).ToList())
                {
                    if (d.State == DeviceState.Attached) { pending.Remove(d.InstanceId); continue; }
                    if (!d.IsConnected) continue;
                    var r = await _usbipd.AttachAsync(d.Device.BusId!, distro);
                    if (!r.Success) continue;
                    _attachedDistros[d.InstanceId] = distro;
                    pending.Remove(d.InstanceId);
                    attached++;
                }
                if (attached > 0) await RefreshDevicesCoreAsync();
                return true;
            });
        }
        if (pending.Count > 0) Logger.Warn($"{pending.Count} device(s) did not come back in time to be re-attached; use Attach.");
        return attached;
    }

    private async Task<T> WithUsbLockAsync<T>(Func<Task<T>> op)
    {
        await _opLock.WaitAsync();
        try { return await op(); }
        finally { _opLock.Release(); }
    }

    private Task InstallCoreAsync() => RunArduinoAsync($"Installing {SelectedCore?.Name}…", async (distro, ct) =>
    {
        if (SelectedCore is not { } core) return;
        StatusText = $"Installing {core.CoreId} in {distro} (this can take several minutes)…";
        var r = await _arduino.InstallCoreAsync(distro, core, ct);
        Report(r, $"{core.CoreId} is installed in {distro}. {core.Note}");
    });

    /// <summary>
    /// Installs udev rules inside WSL so the user can reach debug probes/bootloaders through libusb
    /// (ST-LINK + OpenOCD, RP2040/RP2350 picotool, Black Magic, ESP32 USB-JTAG, FPGA JTAG cables). Needs sudo → opens a console.
    /// </summary>
    private async Task InstallUdevRulesAsync()
    {
        if (SelectedDistro is not { } distro) return;
        Directory.CreateDirectory(Logger.AppDataDir);
        var rulesFile = Path.Combine(Logger.AppDataDir, "99-wsl-usb-manager.rules");
        await File.WriteAllTextAsync(rulesFile, UdevRules);
        var wslRules = await _wsl.ToWslPathAsync(distro.Name, rulesFile);
        var script =
            "echo 'Installing /etc/udev/rules.d/99-wsl-usb-manager.rules (sudo password required)'; " +
            $"sudo install -m 644 {CommandLine.QuoteBash(wslRules)} /etc/udev/rules.d/99-wsl-usb-manager.rules && " +
            "sudo udevadm control --reload-rules && sudo udevadm trigger && " +
            "echo 'Done. Detach and re-attach the device for the new permissions to apply.'; " +
            "read -n1 -r -p 'Press any key to close...'";
        var args = new[] { "-d", distro.Name, "-e", "bash", "-c", script };
        Logger.Info("> wsl (new window): install udev rules for MCU probes/bootloaders");
        Process.Start(new ProcessStartInfo(WslService.WslExe) { UseShellExecute = true, Arguments = CommandLine.Join(args) });
        StatusText = "Enter your WSL sudo password in the console window to install the USB permission rules.";
    }

    private const string UdevRules = """
        # Installed by WSL USB Manager: user access (plugdev) to MCU probes and bootloaders
        # STMicroelectronics ST-LINK V1/V2/V2-1/V3 and STM32 DFU bootloader
        SUBSYSTEM=="usb", ATTR{idVendor}=="0483", ATTR{idProduct}=="3744|3748|374b|3752|374d|374e|374f|3753|3754|3757|df11", MODE="0660", GROUP="plugdev", TAG+="uaccess"
        # Raspberry Pi RP2040/RP2350 BOOTSEL (picotool) and Debug Probe
        SUBSYSTEM=="usb", ATTR{idVendor}=="2e8a", MODE="0660", GROUP="plugdev", TAG+="uaccess"
        # Black Magic Probe
        SUBSYSTEM=="usb", ATTR{idVendor}=="1d50", ATTR{idProduct}=="6017|6018", MODE="0660", GROUP="plugdev", TAG+="uaccess"
        # Espressif USB-Serial/JTAG (OpenOCD)
        SUBSYSTEM=="usb", ATTR{idVendor}=="303a", MODE="0660", GROUP="plugdev", TAG+="uaccess"
        # Maple / Blue Pill bootloader
        SUBSYSTEM=="usb", ATTR{idVendor}=="1eaf", MODE="0660", GROUP="plugdev", TAG+="uaccess"
        # FPGA: Intel/Altera USB-Blaster (I/II), Xilinx Platform Cable, Digilent Adept, Lattice, Microchip FlashPro
        SUBSYSTEM=="usb", ATTR{idVendor}=="09fb|03fd|1443|1134|1514", MODE="0660", GROUP="plugdev", TAG+="uaccess"
        # FPGA: FTDI-based blasters (Arrow USB Blaster on MAX1000/CYC1000/DECA, Digilent, iCEstick, Tang Nano, ULX3S)
        SUBSYSTEM=="usb", ATTR{idVendor}=="0403", ATTR{idProduct}=="6010|6014|6015", MODE="0660", GROUP="plugdev", TAG+="uaccess"
        # FPGA: TinyFPGA BX, Fomu, OrangeCrab bootloaders
        SUBSYSTEM=="usb", ATTR{idVendor}=="1d50", ATTR{idProduct}=="6130", MODE="0660", GROUP="plugdev", TAG+="uaccess"
        SUBSYSTEM=="usb", ATTR{idVendor}=="1209", ATTR{idProduct}=="5bf0|5af0", MODE="0660", GROUP="plugdev", TAG+="uaccess"

        """;

    private void OpenMonitor()
    {
        if (SelectedDistro is null) return;
        _settings.MonitorBaud = MonitorBaud;
        _settings.LastPort = Port;
        _settings.Save();
        _arduino.CliPath = _settings.ArduinoCliPath;
        try
        {
            _arduino.OpenMonitor(SelectedDistro.Name, Port, MonitorBaud);
            StatusText = $"Serial monitor opened for {Port} @ {MonitorBaud}.";
        }
        catch (Exception ex)
        {
            Logger.Error("Open monitor", ex);
            StatusText = "Error: " + ex.Message;
        }
    }

    private Task RunCustomAsync() => RunArduinoAsync($"arduino-cli {CustomArgs}…", async (distro, ct) =>
    {
        var r = await _arduino.RunCustomAsync(distro, CustomArgs, ct);
        Report(r, $"arduino-cli {CustomArgs}: done.");
    });

    #endregion

    #region Helpers

    private void PromptInstallUsbipd()
    {
        var answer = MessageBox.Show(
            "usbipd-win was not found.\n\nIt is required to share USB devices with WSL and must be installed system-wide " +
            "(service + drivers).\n\nYes = install now with winget\nNo = open the download page",
            "usbipd-win missing", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) InstallUsbipd();
        else if (answer == MessageBoxResult.No) OpenPath("https://github.com/dorssel/usbipd-win/releases");
    }

    private void InstallUsbipd()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/k winget install --exact --id dorssel.usbipd-win") { UseShellExecute = true });
            StatusText = "Installing usbipd-win in a console window — press Refresh when it finishes.";
        }
        catch (Exception ex)
        {
            Logger.Error("winget", ex);
            OpenPath("https://github.com/dorssel/usbipd-win/releases");
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (!path.StartsWith("http") && !Path.HasExtension(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error($"Cannot open {path}", ex); }
    }

    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); StatusText = $"Copied {text}"; }
        catch (Exception ex) { Logger.Warn("Clipboard: " + ex.Message); }
    }

    private static string FirstLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault() ?? s;
    }

    #endregion
}

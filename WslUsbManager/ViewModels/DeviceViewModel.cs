// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using WslUsbManager.Models;

namespace WslUsbManager.ViewModels;

public sealed class DeviceViewModel : ViewModelBase
{
    private readonly Action<DeviceViewModel> _favoriteChanged;
    private readonly Action<DeviceViewModel> _autoAttachChanged;
    private UsbDevice _device;
    private bool _isFavorite;
    private bool _autoAttach;
    private string? _attachedDistro;

    public DeviceViewModel(UsbDevice device, bool isFavorite, bool autoAttach,
        Action<DeviceViewModel> favoriteChanged, Action<DeviceViewModel> autoAttachChanged)
    {
        _device = device;
        _isFavorite = isFavorite;
        _autoAttach = autoAttach;
        _favoriteChanged = favoriteChanged;
        _autoAttachChanged = autoAttachChanged;
    }

    public UsbDevice Device => _device;

    /// <summary>Refresh in place (keeps DataGrid selection and scroll position).</summary>
    public void Update(UsbDevice device, bool isFavorite, bool autoAttach)
    {
        _device = device;
        _isFavorite = isFavorite;
        _autoAttach = autoAttach;
        OnPropertyChanged(string.Empty); // all properties
    }

    public string InstanceId => _device.InstanceId;
    public string Name => _device.DisplayName;
    public string Description => _device.Description;
    /// <summary>Second line in the grid: product string / Windows description / hint, when they add information.</summary>
    public string Subtitle => string.Join("  ·  ",
        new[] { _device.ProductString, _device.Description, _device.Hint }
            .Where(s => !string.IsNullOrWhiteSpace(s) && !s.Equals(Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    public string BusIdText => _device.BusId ?? "—";
    public string VidPid => _device.VidPid;
    public string ComPort => _device.ComPort ?? "";
    public DetectedFamily Family => _device.Family;
    public bool IsConnected => _device.IsConnected;
    public DeviceState State => _device.State;
    public string Tooltip => string.Join("\n", new[]
    {
        $"Windows: {_device.Description}",
        _device.ProductString is null ? null : $"USB product: {_device.ProductString}",
        _device.Chip is null ? null : $"Chip: {_device.Chip}",
        $"Detected by: {_device.DetectionSource}",
        $"Instance: {_device.InstanceId}",
        _device.ClientIpAddress is null ? null : $"Client: {_device.ClientIpAddress}",
    }.Where(s => s is not null));

    public string? Chip => _device.Chip;

    /// <summary>Connected first, then microcontrollers, serial bridges, others; then by bus id.</summary>
    public string SortKey =>
        $"{(_device.IsConnected ? 0 : 1)}" +
        $"{(_device.Family is DetectedFamily.Other or DetectedFamily.Unknown ? 2 : _device.Family == DetectedFamily.GenericSerialBridge ? 1 : 0)}" +
        $"{_device.BusId?.PadLeft(8) ?? _device.InstanceId}";

    public string FamilyText => _device.Family switch
    {
        DetectedFamily.Arduino => "Arduino",
        DetectedFamily.EspressifEsp32 => _device.Chip ?? "ESP32",
        DetectedFamily.Stm32 => _device.Chip ?? "STM32",
        DetectedFamily.RaspberryPiPico => _device.Chip ?? "RP2040/2350",
        DetectedFamily.Fpga => _device.Chip ?? "FPGA",
        DetectedFamily.GenericSerialBridge => "Serial",
        _ => "Other",
    };

    /// <summary>Key used by the view to pick the state color.</summary>
    public string StateKey => !_device.IsConnected ? "Offline" : _device.State.ToString();

    public string StateText => !_device.IsConnected
        ? "Shared – not connected"
        : _device.State switch
        {
            DeviceState.Attached => _attachedDistro is null ? "Attached to WSL" : $"Attached to {_attachedDistro}",
            DeviceState.Shared => _device.IsForced ? "Shared (forced)" : "Shared",
            _ => "Not shared",
        };

    public string? AttachedDistro
    {
        get => _attachedDistro;
        set { if (SetField(ref _attachedDistro, value)) OnPropertyChanged(nameof(StateText)); }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (SetField(ref _isFavorite, value)) _favoriteChanged(this); }
    }

    public bool AutoAttach
    {
        get => _autoAttach;
        set { if (SetField(ref _autoAttach, value)) _autoAttachChanged(this); }
    }
}

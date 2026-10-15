// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

namespace WslUsbManager.Models;

public enum DetectedFamily
{
    Unknown,
    Arduino,
    EspressifEsp32,
    Stm32,
    RaspberryPiPico,
    Fpga,                  // USB-Blaster, Arrow USB Blaster (MAX1000/CYC1000), Digilent, Xilinx, Lattice, Gowin...
    GenericSerialBridge,   // CH34x, CP210x, FTDI, PL2303...
    Other
}

public enum DeviceState
{
    NotShared,
    Shared,     // bound with usbipd ("persisted")
    Attached
}

public class UsbDevice
{
    /// <summary>Null when the device is bound (persisted) but not plugged in.</summary>
    public string? BusId { get; set; }
    public string InstanceId { get; set; } = "";
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public string? SerialNumber { get; set; }
    /// <summary>False when the serial segment is a Windows-generated path (contains '&amp;').</summary>
    public bool HasRealSerial { get; set; }
    public string Description { get; set; } = "";
    public string? ComPort { get; set; }
    public DeviceState State { get; set; }
    public bool IsForced { get; set; }
    public string? PersistedGuid { get; set; }
    public string? ClientIpAddress { get; set; }
    /// <summary>USB iProduct string reported by the device itself (not localized).</summary>
    public string? ProductString { get; set; }
    public DetectedFamily Family { get; set; }
    public string? KnownName { get; set; }
    /// <summary>Microcontroller/chip when known, e.g. "RP2350", "ESP32-S3", "STM32F103".</summary>
    public string? Chip { get; set; }
    public string? Hint { get; set; }
    /// <summary>How the family was determined (shown in the tooltip).</summary>
    public string DetectionSource { get; set; } = "";

    public bool IsConnected => BusId is not null;
    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";
    public string DisplayName => KnownName ?? ProductString ?? Description;
}

public class WslDistro
{
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsRunning { get; set; }
    public int Version { get; set; }

    public override string ToString() => Name;
}

public class CommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Cancelled { get; set; }
    public bool Success => ExitCode == 0 && !Cancelled;

    /// <summary>Best human-readable message (stderr first, then stdout).</summary>
    public string Message =>
        Cancelled ? "Operation cancelled." :
        !string.IsNullOrWhiteSpace(Error) ? Error.Trim() : Output.Trim();

    public static CommandResult Fail(string error, int code = -1) => new() { ExitCode = code, Error = error };
}

public class FavoriteDevice
{
    public string Name { get; set; } = "";
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    /// <summary>Empty => matches any device with this VID:PID.</summary>
    public string? SerialNumber { get; set; }
    public string Distro { get; set; } = "";
    public bool AutoAttach { get; set; }

    public bool Matches(UsbDevice d) =>
        d.VendorId == VendorId && d.ProductId == ProductId &&
        (string.IsNullOrEmpty(SerialNumber) ||
         string.Equals(SerialNumber, d.SerialNumber, StringComparison.OrdinalIgnoreCase));

    public static FavoriteDevice FromDevice(UsbDevice d, string distro) => new()
    {
        Name = d.DisplayName,
        VendorId = d.VendorId,
        ProductId = d.ProductId,
        SerialNumber = d.HasRealSerial ? d.SerialNumber : null,
        Distro = distro,
        AutoAttach = true
    };
}

public class ArduinoBoardPort
{
    public string Address { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string ProtocolLabel { get; set; } = "";
    public string? BoardName { get; set; }
    public string? Fqbn { get; set; }
    public string? Vid { get; set; }
    public string? Pid { get; set; }
    public string? SerialNumber { get; set; }

    public string Display =>
        BoardName is null ? $"{Address}  ({ProtocolLabel})" : $"{Address}  —  {BoardName}  [{Fqbn}]";
}

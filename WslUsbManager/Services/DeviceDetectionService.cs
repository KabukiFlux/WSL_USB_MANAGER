// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Text.RegularExpressions;
using WslUsbManager.Infrastructure;
using WslUsbManager.Models;

namespace WslUsbManager.Services;

public interface IDeviceDetectionService
{
    void Classify(UsbDevice device);
}

/// <summary>
/// Detection order:
///   1. exact VID:PID in the catalog (authoritative when it pins down the chip);
///   2. the device's USB product string / Windows description, matched against MCU patterns
///      (refines vendor wildcards and bridge chips, e.g. an Adafruit "Feather RP2040" → RP2040);
///   3. vendor wildcard / bridge chip from the catalog;
///   4. generic serial keywords or a COM port → serial bridge; otherwise Other.
/// </summary>
public sealed partial class DeviceDetectionService : IDeviceDetectionService
{
    private sealed record Rule(Regex Pattern, DetectedFamily Family, Func<Match, string?> Chip, string? Hint = null);

    private const string ArrowHint = "Arrow USB Blaster: on-board on MAX1000, CYC1000 and DECA (needs the Arrow USB Blaster driver for Quartus)";

    // Order matters: FPGA boards first (pico-ice is an RP2040 + iCE40 board), then RP and ESP before Arduino,
    // so "Arduino Nano ESP32" → ESP32.
    private static readonly Rule[] McuRules =
    {
        new(Rx(@"\bMAX\s?1000\b"), DetectedFamily.Fpga, _ => "MAX 10", ArrowHint),
        new(Rx(@"\bCYC\s?1000\b"), DetectedFamily.Fpga, _ => "Cyclone 10 LP", ArrowHint),
        new(Rx(@"\bDECA\b"), DetectedFamily.Fpga, _ => "MAX 10", ArrowHint),
        new(Rx(@"Arrow\s*USB"), DetectedFamily.Fpga, _ => null, ArrowHint),
        new(Rx(@"Vidor"), DetectedFamily.Fpga, _ => "Cyclone 10 LP + SAMD21"),
        new(Rx(@"USB[\s-]?Blaster|Altera|Intel\s*FPGA"), DetectedFamily.Fpga, _ => null),
        new(Rx(@"iCE\s?Breaker|iCEstick|Lattice\s*FTUSB|UPduino|TinyFPGA|\bFomu\b|pico2?-ice|\biCE40"), DetectedFamily.Fpga, _ => "iCE40"),
        new(Rx(@"ULX3S|OrangeCrab|ButterStick|\bECP5"), DetectedFamily.Fpga, _ => "ECP5"),
        new(Rx(@"Tang\s*(Nano|Primer|Mega|Console)|Gowin|Sipeed[\s-]*(USB[\s-]*)?Debug"), DetectedFamily.Fpga, _ => "Gowin"),
        new(Rx(@"Digilent|Alchitry|\b(Arty|Basys|Nexys|Cmod|Zybo|ZedBoard)\b"), DetectedFamily.Fpga, _ => null),
        new(Rx(@"Efinix|\bTrion\b"), DetectedFamily.Fpga, _ => "Efinix"),
        new(Rx(@"Xilinx|Platform\s*Cable|FlashPro|\bFPGA\b|\bCPLD\b"), DetectedFamily.Fpga, _ => null),

        new(Rx(@"RP2350|\bPico\s*2\b"), DetectedFamily.RaspberryPiPico, _ => "RP2350"),
        new(Rx(@"RP2040|\bRP2 Boot\b"), DetectedFamily.RaspberryPiPico, _ => "RP2040"),
        new(Rx(@"\bPico\b|Raspberry\s*Pi"), DetectedFamily.RaspberryPiPico, _ => null),

        new(Rx(@"ESP32-?(S2|S3|C2|C3|C5|C6|C61|H2|P4)\b"), DetectedFamily.EspressifEsp32, m => "ESP32-" + m.Groups[1].Value.ToUpperInvariant()),
        new(Rx(@"ESP8266"), DetectedFamily.EspressifEsp32, _ => "ESP8266"),
        new(Rx(@"ESP32"), DetectedFamily.EspressifEsp32, _ => "ESP32"),
        new(Rx(@"Espressif|USB JTAG/serial debug unit"), DetectedFamily.EspressifEsp32, _ => null),

        new(Rx(@"\b(STM32[A-Z]\d+[A-Z0-9]*)"), DetectedFamily.Stm32, m => m.Groups[1].Value.ToUpperInvariant()),
        new(Rx(@"STM32|ST-?LINK|STMicroelectronics|\bMaple\b|Black Magic"), DetectedFamily.Stm32, _ => null),

        new(Rx(@"Arduino|Genuino"), DetectedFamily.Arduino, _ => null),
    };

    private static readonly Regex SerialKeywords =
        Rx(@"Serial|\bserie\b|UART|CH34\d|CH910\d|CP210|FTDI|FT232|PL2303");

    public void Classify(UsbDevice device)
    {
        var known = KnownDeviceCatalog.Lookup(device.VendorId, device.ProductId);
        var vidPid = $"VID:PID {device.VidPid}";

        if (known is not null)
        {
            device.KnownName = known.Name;
            device.Family = known.Family;
            device.Chip = known.Chip;
            device.Hint = known.Hint;
            device.DetectionSource = known.ProductId is null ? $"vendor ID {device.VendorId:X4}" : vidPid;
            if (!known.Refinable) return;

            // Vendor wildcard: the device's own product string is a better name than "Adafruit board".
            if (known.ProductId is null && device.ProductString is not null)
            {
                device.Hint = known.Name;
                device.KnownName = device.ProductString;
            }
        }

        var matched = MatchMcu(device.ProductString, out var family, out var chip, out var hint) ||
                      MatchMcu(device.Description, out family, out chip, out hint);

        // "Arduino" in a product string only says which core/IDE is used (e.g. "PicoArduino" on an RP2040):
        // it must not override a chip-specific vendor such as Raspberry Pi, Espressif or ST.
        if (matched && family == DetectedFamily.Arduino && known is not null &&
            known.Family != DetectedFamily.GenericSerialBridge)
            matched = false;

        if (matched)
        {
            if (known?.Family == DetectedFamily.GenericSerialBridge)
            {
                device.Hint = $"{known.Name.Replace(" USB-Serial", "")} USB-serial bridge";
                if (device.ProductString is not null) device.KnownName = device.ProductString;
            }
            if (hint is not null)
                device.Hint = device.Hint is null || known?.Family != DetectedFamily.GenericSerialBridge
                    ? hint : $"{hint}; {device.Hint}";
            device.Family = family;
            device.Chip ??= chip;
            device.DetectionSource = known is null
                ? "USB product string"
                : $"{device.DetectionSource} + product string";
            return;
        }

        if (known is not null) return;

        if (SerialKeywords.IsMatch(device.ProductString ?? "") || SerialKeywords.IsMatch(device.Description) || device.ComPort is not null)
        {
            device.Family = DetectedFamily.GenericSerialBridge;
            device.DetectionSource = device.ComPort is not null ? "has a COM port" : "description";
            return;
        }

        device.Family = DetectedFamily.Other;
        device.DetectionSource = "no match";
    }

    private static bool MatchMcu(string? text, out DetectedFamily family, out string? chip, out string? hint)
    {
        family = DetectedFamily.Unknown;
        chip = null;
        hint = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var rule in McuRules)
        {
            var m = rule.Pattern.Match(text);
            if (!m.Success) continue;
            family = rule.Family;
            chip = rule.Chip(m);
            hint = rule.Hint;
            return true;
        }
        return false;
    }

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

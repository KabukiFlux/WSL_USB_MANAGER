// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using WslUsbManager.Models;

namespace WslUsbManager.Infrastructure;

/// <param name="ProductId">Null = vendor wildcard (any PID from this VID).</param>
/// <param name="Chip">Microcontroller / chip, when the VID:PID pins it down.</param>
/// <param name="Hint">Extra info shown for ambiguous devices (e.g. which boards use a bridge chip).</param>
public sealed record KnownDevice(
    ushort VendorId, ushort? ProductId, string Name, DetectedFamily Family, string? Chip = null, string? Hint = null)
{
    /// <summary>
    /// Exact board/chip IDs are authoritative. Vendor wildcards, multi-chip vendors and bridge chips can be
    /// refined by the device's product string.
    /// </summary>
    public bool Refinable => ProductId is null || Family == DetectedFamily.GenericSerialBridge || Chip is null;
}

public static class KnownDeviceCatalog
{
    private const string EspBridgeHint = "common on ESP32 / ESP8266 dev boards and Arduino clones";
    private const string StmVendor = "STMicroelectronics";

    public static readonly IReadOnlyList<KnownDevice> Devices = new List<KnownDevice>
    {
        // ── Arduino (arduino.cc / arduino.org) ─────────────────────────────────────────────
        new(0x2341, 0x0043, "Arduino Uno", DetectedFamily.Arduino, "ATmega328P"),
        new(0x2341, 0x0001, "Arduino Uno", DetectedFamily.Arduino, "ATmega328P"),
        new(0x2341, 0x0243, "Genuino Uno", DetectedFamily.Arduino, "ATmega328P"),
        new(0x2341, 0x0042, "Arduino Mega 2560", DetectedFamily.Arduino, "ATmega2560"),
        new(0x2341, 0x0010, "Arduino Mega 2560", DetectedFamily.Arduino, "ATmega2560"),
        new(0x2341, 0x8036, "Arduino Leonardo", DetectedFamily.Arduino, "ATmega32U4"),
        new(0x2341, 0x8037, "Arduino Micro", DetectedFamily.Arduino, "ATmega32U4"),
        new(0x2341, 0x0058, "Arduino Nano Every", DetectedFamily.Arduino, "ATmega4809"),
        new(0x2341, 0x0069, "Arduino Uno R4 Minima", DetectedFamily.Arduino, "RA4M1"),
        new(0x2341, 0x1002, "Arduino Uno R4 WiFi", DetectedFamily.Arduino, "RA4M1"),
        new(0x2341, 0x0070, "Arduino Nano ESP32", DetectedFamily.EspressifEsp32, "ESP32-S3"),
        new(0x2341, 0x005E, "Arduino Nano RP2040 Connect", DetectedFamily.RaspberryPiPico, "RP2040"),
        new(0x2341, 0x025E, "Arduino Nano RP2040 Connect", DetectedFamily.RaspberryPiPico, "RP2040"),
        new(0x2341, null, "Arduino board", DetectedFamily.Arduino),
        new(0x2A03, 0x0043, "Arduino Uno (.org)", DetectedFamily.Arduino, "ATmega328P"),
        new(0x2A03, 0x0042, "Arduino Mega 2560 (.org)", DetectedFamily.Arduino, "ATmega2560"),
        new(0x2A03, null, "Arduino board (.org)", DetectedFamily.Arduino),

        // ── Multi-chip board vendors: family comes from the product string when possible ──
        new(0x1B4F, 0x9205, "SparkFun Pro Micro", DetectedFamily.Arduino, "ATmega32U4"),
        new(0x1B4F, 0x9206, "SparkFun Pro Micro", DetectedFamily.Arduino, "ATmega32U4"),
        new(0x1B4F, null, "SparkFun board", DetectedFamily.Arduino),
        new(0x239A, null, "Adafruit board", DetectedFamily.Arduino),
        new(0x2886, null, "Seeed Studio board", DetectedFamily.Arduino),
        new(0x16C0, 0x0483, "Teensy (USB Serial)", DetectedFamily.Arduino),

        // ── Espressif native USB (ESP32-S2/S3/C3/C5/C6/H2/P4) ───────────────────────────────
        new(0x303A, 0x1001, "ESP32 USB-Serial/JTAG", DetectedFamily.EspressifEsp32,
            Hint: "built-in USB of ESP32-S3 / C3 / C5 / C6 / H2 / P4"),
        new(0x303A, 0x0002, "ESP32-S2 (ROM / USB-OTG)", DetectedFamily.EspressifEsp32, "ESP32-S2"),
        new(0x303A, null, "Espressif ESP32 (native USB)", DetectedFamily.EspressifEsp32),

        // ── STMicroelectronics: ST-LINK probes, DFU bootloader, native USB CDC ─────────────
        new(0x0483, 0x3744, "ST-LINK/V1", DetectedFamily.Stm32, Hint: "debug probe for STM32"),
        new(0x0483, 0x3748, "ST-LINK/V2", DetectedFamily.Stm32, Hint: "debug probe for STM32"),
        new(0x0483, 0x374B, "ST-LINK/V2-1 (Nucleo / Discovery)", DetectedFamily.Stm32, Hint: "on-board probe + virtual COM"),
        new(0x0483, 0x3752, "ST-LINK/V2-1", DetectedFamily.Stm32, Hint: "on-board probe + virtual COM"),
        new(0x0483, 0x374D, "STLINK-V3 (loader)", DetectedFamily.Stm32),
        new(0x0483, 0x374E, "STLINK-V3", DetectedFamily.Stm32, Hint: "on-board probe + virtual COM"),
        new(0x0483, 0x374F, "STLINK-V3", DetectedFamily.Stm32, Hint: "on-board probe + virtual COM"),
        new(0x0483, 0x3753, "STLINK-V3 (2 VCP)", DetectedFamily.Stm32),
        new(0x0483, 0x3754, "STLINK-V3", DetectedFamily.Stm32),
        new(0x0483, 0x3757, "STLINK-V3PWR", DetectedFamily.Stm32),
        new(0x0483, 0xDF11, "STM32 DFU bootloader", DetectedFamily.Stm32, Hint: "BOOT0 / system-memory USB DFU"),
        new(0x0483, 0x5740, "STM32 Virtual COM Port", DetectedFamily.Stm32, Hint: "native USB CDC (e.g. Black Pill, STM32duino)"),
        new(0x0483, null, StmVendor + " device", DetectedFamily.Stm32),
        new(0x1EAF, 0x0003, "Maple DFU bootloader (Blue Pill)", DetectedFamily.Stm32, "STM32F103"),
        new(0x1EAF, 0x0004, "Maple serial (Blue Pill)", DetectedFamily.Stm32, "STM32F103"),
        new(0x1D50, 0x6018, "Black Magic Probe", DetectedFamily.Stm32, Hint: "debug probe (often for STM32)"),
        new(0x1D50, 0x6017, "Black Magic Probe (DFU)", DetectedFamily.Stm32),

        // ── Raspberry Pi RP2040 / RP2350 ──────────────────────────────────────────────────
        new(0x2E8A, 0x0003, "RP2040 BOOTSEL (RPI-RP2)", DetectedFamily.RaspberryPiPico, "RP2040", "UF2 mass-storage bootloader"),
        new(0x2E8A, 0x000F, "RP2350 BOOTSEL (RP2350)", DetectedFamily.RaspberryPiPico, "RP2350", "UF2 mass-storage bootloader"),
        new(0x2E8A, 0x000A, "Raspberry Pi Pico (SDK serial)", DetectedFamily.RaspberryPiPico, "RP2040"),
        new(0x2E8A, 0x0009, "Raspberry Pi Pico 2 (SDK serial)", DetectedFamily.RaspberryPiPico, "RP2350"),
        new(0x2E8A, 0x0005, "Raspberry Pi Pico (MicroPython)", DetectedFamily.RaspberryPiPico),
        new(0x2E8A, 0x000C, "Raspberry Pi Debug Probe", DetectedFamily.RaspberryPiPico, "RP2040", "CMSIS-DAP probe + UART"),
        new(0x2E8A, null, "Raspberry Pi RP2040/RP2350 board", DetectedFamily.RaspberryPiPico),

        // ── FPGA: JTAG cables, on-board blasters and FPGA boards with their own VID:PID ───
        // FTDI-based blasters (Arrow USB Blaster on MAX1000 / CYC1000 / DECA, Digilent, iCEstick,
        // Tang Nano, ULX3S...) reuse the FTDI IDs below and are recognized by their product string.
        new(0x09FB, 0x6001, "Altera USB-Blaster", DetectedFamily.Fpga,
            Hint: "JTAG cable for Intel/Altera FPGAs & CPLDs (also on-board on Terasic DE0 / DE1 / DE2)"),
        new(0x09FB, 0x6010, "Intel USB-Blaster II (loader)", DetectedFamily.Fpga,
            Hint: "loads its firmware on first use, then re-enumerates as 09FB:6810"),
        new(0x09FB, 0x6810, "Intel USB-Blaster II", DetectedFamily.Fpga,
            Hint: "JTAG cable / on-board on DE10-Nano, DE10-Standard and Cyclone V SoC kits"),
        new(0x09FB, null, "Intel/Altera FPGA device", DetectedFamily.Fpga),
        new(0x03FD, 0x0008, "Xilinx Platform Cable USB II", DetectedFamily.Fpga, Hint: "JTAG cable for AMD/Xilinx FPGAs"),
        new(0x03FD, 0x000D, "Xilinx Platform Cable USB II (loader)", DetectedFamily.Fpga,
            Hint: "loads its firmware on first use, then re-enumerates as 03FD:0008"),
        new(0x03FD, null, "Xilinx device", DetectedFamily.Fpga),
        new(0x1443, null, "Digilent Adept device", DetectedFamily.Fpga, Hint: "older Digilent boards / cables (Basys2, Nexys2, JTAG-USB)"),
        new(0x1134, null, "Lattice programming cable", DetectedFamily.Fpga),
        new(0x1514, null, "Microchip FlashPro programmer", DetectedFamily.Fpga, Hint: "PolarFire / IGLOO / SmartFusion"),
        new(0x2341, 0x8056, "Arduino MKR Vidor 4000", DetectedFamily.Fpga, "Cyclone 10 LP + SAMD21"),
        new(0x2341, 0x0056, "Arduino MKR Vidor 4000 (bootloader)", DetectedFamily.Fpga, "Cyclone 10 LP + SAMD21"),
        new(0x1D50, 0x6130, "TinyFPGA BX (bootloader)", DetectedFamily.Fpga, "iCE40LP8K"),
        new(0x1209, 0x5BF0, "Fomu", DetectedFamily.Fpga, "iCE40UP5K"),
        new(0x1209, 0x5AF0, "OrangeCrab (DFU bootloader)", DetectedFamily.Fpga, "ECP5"),

        // ── USB-serial bridge chips (the MCU behind them can't be told from USB) ──────────
        new(0x1A86, 0x7523, "CH340 USB-Serial", DetectedFamily.GenericSerialBridge, Hint: EspBridgeHint),
        new(0x1A86, 0x5523, "CH341 USB-Serial", DetectedFamily.GenericSerialBridge, Hint: EspBridgeHint),
        new(0x1A86, 0x55D3, "CH343 USB-Serial", DetectedFamily.GenericSerialBridge, Hint: EspBridgeHint),
        new(0x1A86, 0x55D4, "CH9102 USB-Serial", DetectedFamily.GenericSerialBridge, Hint: EspBridgeHint),
        new(0x10C4, 0xEA60, "CP210x USB-Serial", DetectedFamily.GenericSerialBridge, Hint: EspBridgeHint),
        new(0x10C4, 0xEA70, "CP2105 USB-Serial", DetectedFamily.GenericSerialBridge),
        new(0x10C4, 0xEA71, "CP2108 USB-Serial", DetectedFamily.GenericSerialBridge),
        new(0x0403, 0x6001, "FT232R USB-Serial", DetectedFamily.GenericSerialBridge),
        new(0x0403, 0x6010, "FT2232 USB-Serial", DetectedFamily.GenericSerialBridge,
            Hint: "also used by ESP-Prog JTAG and FPGA boards (Arrow USB Blaster, Digilent, iCEstick, Tang Nano)"),
        new(0x0403, 0x6011, "FT4232 USB-Serial", DetectedFamily.GenericSerialBridge),
        new(0x0403, 0x6014, "FT232H USB-Serial", DetectedFamily.GenericSerialBridge),
        new(0x0403, 0x6015, "FT231X USB-Serial", DetectedFamily.GenericSerialBridge),
        new(0x067B, 0x2303, "PL2303 USB-Serial", DetectedFamily.GenericSerialBridge),
    };

    private static readonly Dictionary<(ushort, ushort?), KnownDevice> Index =
        Devices.ToDictionary(d => (d.VendorId, d.ProductId));

    /// <summary>Exact VID:PID match first, then vendor wildcard.</summary>
    public static KnownDevice? Lookup(ushort vid, ushort pid) =>
        Index.TryGetValue((vid, pid), out var exact) ? exact :
        Index.TryGetValue((vid, null), out var vendor) ? vendor : null;
}

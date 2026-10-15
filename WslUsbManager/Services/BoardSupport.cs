// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using WslUsbManager.Models;

namespace WslUsbManager.Services;

/// <summary>An Arduino core that can be installed inside WSL with one click.</summary>
public sealed record CorePreset(string Name, string CoreId, string? IndexUrl, string Note)
{
    public override string ToString() => Name;
}

public enum UploadMethod
{
    /// <summary>Pick from the FQBN (see <see cref="BoardSupport.Resolve"/>).</summary>
    Auto,
    /// <summary><c>arduino-cli compile --upload</c> entirely inside WSL (device must be attached).</summary>
    ArduinoCli,
    /// <summary>RP2040/RP2350: compile in WSL, reset to BOOTSEL, copy the .uf2 to the drive Windows mounts.</summary>
    Uf2Copy,
    /// <summary>STM32 Nucleo/Discovery: compile in WSL, copy the .bin to the ST-LINK drive Windows mounts.</summary>
    StMassStorage,
}

public sealed record UploadMethodOption(UploadMethod Method, string Name)
{
    public override string ToString() => Name;
}

public static class BoardSupport
{
    public static readonly IReadOnlyList<CorePreset> Cores = new[]
    {
        new CorePreset("Arduino AVR (Uno, Nano, Mega…)", "arduino:avr", null, ""),
        new CorePreset("RP2040 / RP2350 (arduino-pico, Earle Philhower)", "rp2040:rp2040",
            "https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json",
            "Upload with 'UF2 copy via Windows' (default for rp2040:* boards)."),
        new CorePreset("STM32 (STM32duino, STMicroelectronics)", "STMicroelectronics:stm32",
            "https://github.com/stm32duino/BoardManagerFiles/raw/main/package_stmicroelectronics_index.json",
            "Nucleo/Discovery: 'Mass storage via Windows' needs nothing else. SWD/DFU/serial uploads need " +
            "STM32CubeProgrammer installed inside WSL (STM32_Programmer_CLI)."),
        new CorePreset("ESP32 (Espressif)", "esp32:esp32",
            "https://espressif.github.io/arduino-esp32/package_esp32_index.json",
            "Uploads over serial with the normal arduino-cli upload."),
    };

    public static readonly IReadOnlyList<UploadMethodOption> UploadMethods = new[]
    {
        new UploadMethodOption(UploadMethod.Auto, "Auto (from FQBN)"),
        new UploadMethodOption(UploadMethod.ArduinoCli, "arduino-cli upload (in WSL)"),
        new UploadMethodOption(UploadMethod.Uf2Copy, "UF2 copy via Windows (RP2040/RP2350)"),
        new UploadMethodOption(UploadMethod.StMassStorage, "Mass storage via Windows (ST-LINK)"),
    };

    public static readonly IReadOnlyList<string> CommonFqbns = new[]
    {
        "arduino:avr:uno", "arduino:avr:nano", "arduino:avr:mega", "arduino:avr:leonardo", "arduino:avr:micro",
        "arduino:renesas_uno:minima", "arduino:renesas_uno:unor4wifi",
        "esp32:esp32:esp32", "esp32:esp32:esp32s3", "esp32:esp32:esp32c3", "esp32:esp32:esp32c6",
        "rp2040:rp2040:rpipico", "rp2040:rp2040:rpipicow", "rp2040:rp2040:rpipico2", "rp2040:rp2040:rpipico2w",
        "STMicroelectronics:stm32:Nucleo_64:pnum=NUCLEO_F411RE",
        "STMicroelectronics:stm32:Nucleo_64:pnum=NUCLEO_F401RE",
        "STMicroelectronics:stm32:Nucleo_64:pnum=NUCLEO_L476RG",
        "STMicroelectronics:stm32:Nucleo_144:pnum=NUCLEO_F767ZI",
        "STMicroelectronics:stm32:GenF1:pnum=BLUEPILL_F103C8,upload_method=swdMethod",
        "STMicroelectronics:stm32:GenF4:pnum=BLACKPILL_F411CE,upload_method=dfuMethod",
    };

    /// <summary>Resolves Auto to a concrete method from the FQBN.</summary>
    public static UploadMethod Resolve(UploadMethod method, string fqbn)
    {
        if (method != UploadMethod.Auto) return method;

        if (fqbn.StartsWith("rp2040:rp2040:", StringComparison.OrdinalIgnoreCase))
            return UploadMethod.Uf2Copy;

        if (fqbn.StartsWith("STMicroelectronics:stm32:", StringComparison.OrdinalIgnoreCase))
        {
            var board = fqbn.Split(':').ElementAtOrDefault(2) ?? "";
            var options = fqbn.Split(':').ElementAtOrDefault(3) ?? "";
            var explicitMethod = options.Split(',').FirstOrDefault(o => o.StartsWith("upload_method=", StringComparison.OrdinalIgnoreCase));
            if (explicitMethod is not null)
                return explicitMethod.EndsWith("MassStorage", StringComparison.OrdinalIgnoreCase)
                    ? UploadMethod.StMassStorage : UploadMethod.ArduinoCli;
            // Nucleo / Discovery boards default to mass storage through the on-board ST-LINK.
            return board.StartsWith("Nucleo", StringComparison.OrdinalIgnoreCase) ||
                   board.StartsWith("Disco", StringComparison.OrdinalIgnoreCase)
                ? UploadMethod.StMassStorage : UploadMethod.ArduinoCli;
        }
        return UploadMethod.ArduinoCli;
    }

    /// <summary>FQBN to suggest when a device is selected and no FQBN is set yet.</summary>
    public static string? SuggestFqbn(UsbDevice d) => d.Family switch
    {
        DetectedFamily.RaspberryPiPico => d.Chip == "RP2350" ? "rp2040:rp2040:rpipico2" : "rp2040:rp2040:rpipico",
        DetectedFamily.EspressifEsp32 => d.Chip switch
        {
            "ESP32-S3" => "esp32:esp32:esp32s3",
            "ESP32-S2" => "esp32:esp32:esp32s2",
            "ESP32-C3" => "esp32:esp32:esp32c3",
            "ESP32-C6" => "esp32:esp32:esp32c6",
            _ => "esp32:esp32:esp32",
        },
        DetectedFamily.Stm32 when d.Chip == "STM32F103" => "STMicroelectronics:stm32:GenF1:pnum=BLUEPILL_F103C8",
        DetectedFamily.Stm32 => "STMicroelectronics:stm32:Nucleo_64:pnum=NUCLEO_F411RE",
        DetectedFamily.Fpga when d.VendorId == 0x2341 => "arduino:samd:mkrvidor4000",
        _ => null,
    };
}

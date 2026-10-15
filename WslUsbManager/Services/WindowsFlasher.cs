// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.IO;

namespace WslUsbManager.Services;

/// <summary>
/// Finds the USB drives that boards expose for drag-and-drop flashing and copies firmware onto them:
/// RP2040/RP2350 BOOTSEL (INFO_UF2.TXT) and ST-LINK V2-1/V3 mass storage on Nucleo/Discovery (DETAILS.TXT / MBED.HTM).
/// </summary>
public static class WindowsFlasher
{
    public static DriveInfo? FindUf2Drive() => FindDrive(root => File.Exists(Path.Combine(root, "INFO_UF2.TXT")));

    public static DriveInfo? FindStLinkDrive() => FindDrive(root =>
        File.Exists(Path.Combine(root, "DETAILS.TXT")) || File.Exists(Path.Combine(root, "MBED.HTM")));

    public static string Describe(DriveInfo d)
    {
        string label;
        try { label = d.VolumeLabel; } catch { label = ""; }
        return $"{d.Name.TrimEnd('\\')} \"{label}\"";
    }

    public static async Task<DriveInfo?> WaitForDriveAsync(Func<DriveInfo?> finder, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (finder() is { } d) return d;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return null;
    }

    public static async Task<bool> WaitForDriveGoneAsync(string root, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!IsReady(root)) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Copies the firmware and flushes it to the device. The board reboots/flashes once the write completes.</summary>
    public static async Task CopyAsync(string source, DriveInfo drive, CancellationToken ct)
    {
        var target = Path.Combine(drive.RootDirectory.FullName, Path.GetFileName(source));
        Logger.Info($"Copying {Path.GetFileName(source)} ({new FileInfo(source).Length / 1024} KB) to {Describe(drive)}");
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        try
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            dst.Flush(flushToDisk: true);
        }
        catch (IOException) when (!IsReady(drive.RootDirectory.FullName))
        {
            // The RP bootloader may reboot as soon as the last block is written; that's a success.
        }
    }

    private static DriveInfo? FindDrive(Func<string, bool> probe)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Removable or DriveType.Fixed) || !d.IsReady) continue;
                if (d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) continue;
                if (probe(d.RootDirectory.FullName)) return d;
            }
            catch (IOException) { /* drive vanished */ }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static bool IsReady(string root)
    {
        try { return new DriveInfo(root).IsReady; }
        catch { return false; }
    }
}

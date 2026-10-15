// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;

namespace WslUsbManager.Services;

/// <summary>
/// Reads device properties that usbipd doesn't expose, via CfgMgr32.
/// The bus-reported description is the device's own USB iProduct string (e.g. "RP2 Boot",
/// "USB JTAG/serial debug unit", "STM32 STLink"), which identifies boards far better than the
/// localized Windows driver description.
/// </summary>
public static class DevicePropertyReader
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint LocatePhantom = 0x1; // CM_LOCATE_DEVNODE_PHANTOM: also unplugged devices
    private const uint DevpropTypeString = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    private static DevPropKey _busReportedDeviceDesc = new()
    {
        FmtId = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), Pid = 4,
    };

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type,
        byte[]? buffer, ref uint size, uint flags);

    /// <summary>The USB product string, or null if unavailable.</summary>
    public static string? GetProductString(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        try
        {
            if (CM_Locate_DevNodeW(out var devInst, instanceId, LocatePhantom) != CrSuccess) return null;

            uint size = 0;
            var cr = CM_Get_DevNode_PropertyW(devInst, ref _busReportedDeviceDesc, out _, null, ref size, 0);
            if (cr != CrBufferSmall || size == 0) return null;

            var buffer = new byte[size];
            if (CM_Get_DevNode_PropertyW(devInst, ref _busReportedDeviceDesc, out var type, buffer, ref size, 0) != CrSuccess ||
                type != DevpropTypeString)
                return null;

            var s = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
            return s.Length == 0 ? null : s;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}

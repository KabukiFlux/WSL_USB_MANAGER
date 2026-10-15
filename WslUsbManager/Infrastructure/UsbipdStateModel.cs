// -----------------------------------------------------------------------------
//  WSL USB Manager
//  Copyright (c) 2026 KabukiFlux
//  Licensed under the MIT License. See LICENSE in the project root.
// -----------------------------------------------------------------------------

namespace WslUsbManager.Infrastructure;

/// <summary>JSON shape of <c>usbipd state</c> (usbipd-win ≥ 4.0).</summary>
public sealed class UsbipdStateModel
{
    public List<UsbipdDeviceModel> Devices { get; set; } = new();
}

public sealed class UsbipdDeviceModel
{
    public string? BusId { get; set; }
    public string? ClientIPAddress { get; set; }
    public string? Description { get; set; }
    public string? InstanceId { get; set; }
    public bool IsForced { get; set; }
    public string? PersistedGuid { get; set; }
    public string? StubInstanceId { get; set; }
}

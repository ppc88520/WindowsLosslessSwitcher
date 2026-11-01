namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// Describes a Windows audio render device exposed to the user.
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string FriendlyName,
    bool IsDefault)
{
    /// <summary>Nearest present, started physical USB ancestor read from the PnP tree.</summary>
    public string? PhysicalUsbDeviceId { get; init; }

    /// <summary>Windows EndpointFormFactor value. Missing/unknown values are not trusted.</summary>
    public int? EndpointFormFactor { get; init; }
}

using System.Runtime.InteropServices;
using System.Text;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Conservative hardware allow-list. USB alone, a friendly name and a Speakers form factor
/// do not establish that an endpoint is an external DAC.
/// </summary>
public static class DacDeviceIdentity
{
    public static bool IsSupportedDac(AudioDeviceInfo? device) =>
        device is not null &&
        device.EndpointFormFactor is 1 or 2 or 3 or 5 &&
        IsSupportedPhysicalUsbId(device.PhysicalUsbDeviceId);

    public static bool IsSupportedPhysicalUsbId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        var segments = instanceId.Split('\\');
        if (segments.Length != 3 || !segments[0].Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[2])) return false;
        // TOPPING DX1 II, verified against its physical USB ancestor. This intentionally does
        // not approve all TOPPING devices or every USB audio adapter.
        return segments[1].Equals("VID_152A&PID_8750", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read-only PnP traversal. Never opens an audio client or probes a format.</summary>
    public static string? ReadPhysicalUsbAncestor(string endpointId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var node, "SWD\\MMDEVAPI\\" + endpointId, 0) != 0) return null;
            for (var depth = 0; depth < 16; depth++)
            {
                var instanceId = new StringBuilder(1024);
                if (CM_Get_Device_IDW(node, instanceId, instanceId.Capacity, 0) != 0) return null;
                var value = instanceId.ToString();
                if (value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                {
                    const uint Started = 0x00000008;
                    return CM_Get_DevNode_Status(out var status, out var problem, node, 0) == 0 &&
                        (status & Started) != 0 && problem == 0 ? value : null;
                }
                if (CM_Get_Parent(out node, node, 0) != 0) return null;
            }
        }
        catch { }
        return null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int bufferLength, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
}

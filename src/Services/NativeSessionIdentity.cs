using System.Runtime.InteropServices;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Compares the canonical IUnknown identity, rather than the managed WinRT projection or
/// application id. Different media sessions from one application must remain distinct.
/// </summary>
internal static class NativeSessionIdentity
{
    private static readonly Guid IUnknownId = new("00000000-0000-0000-C000-000000000046");

    internal static bool AreSame(object? left, object? right) => AreSame(left, right, GetIdentity);

    internal static bool AreSame(object? left, object? right, Func<object, nint> identityReader)
    {
        if (left is null || right is null) return false;
        if (ReferenceEquals(left, right)) return true;
        var leftIdentity = identityReader(left);
        return leftIdentity != 0 && leftIdentity == identityReader(right);
    }

    internal static nint GetIdentity(object value)
    {
        if (value is not WinRT.IWinRTObject winrt) return 0;
        nint identity = 0;
        try
        {
            var iid = IUnknownId;
            var result = Marshal.QueryInterface(winrt.NativeObject.ThisPtr, ref iid, out identity);
            return result >= 0 ? identity : 0;
        }
        catch { return 0; }
        finally
        {
            // Both input wrappers remain alive during comparisons. QueryInterface adds a
            // temporary reference; never release the reference owned by the projection.
            if (identity != 0) Marshal.Release(identity);
            GC.KeepAlive(value);
        }
    }
}

using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class NativeSessionIdentityTests
{
    private sealed record Wrapper(string Application, nint NativeIdentity);

    [Fact]
    public void ReenumeratedWrapperOfSameNativeSessionIsAccepted()
    {
        var attached = new Wrapper("AppleMusic", 101);
        var enumerated = new Wrapper("AppleMusic", 101);
        Assert.NotSame(attached, enumerated);
        Assert.True(NativeSessionIdentity.AreSame(attached, enumerated, ReadIdentity));
    }

    [Fact]
    public void ReplacedSessionFromSameApplicationIsRejected()
    {
        var beforeIdle = new Wrapper("AppleMusic", 101);
        var afterIdle = new Wrapper("AppleMusic", 202);
        Assert.False(NativeSessionIdentity.AreSame(beforeIdle, afterIdle, ReadIdentity));
    }

    [Fact]
    public void MissingNativeIdentityNeverMatchesTwoWrappers()
    {
        Assert.False(NativeSessionIdentity.AreSame(new Wrapper("AppleMusic", 0), new Wrapper("AppleMusic", 0), ReadIdentity));
    }

    [Fact]
    public void MissingSessionIsRejectedEvenWhenBothAreMissing()
    {
        Assert.False(NativeSessionIdentity.AreSame(null, null, ReadIdentity));
        Assert.False(NativeSessionIdentity.AreSame(new Wrapper("AppleMusic", 101), null, ReadIdentity));
    }

    [Fact]
    public void SameManagedWrapperRemainsValidWithoutNativeComparison()
    {
        var attached = new Wrapper("AppleMusic", 101);
        Assert.True(NativeSessionIdentity.AreSame(attached, attached, _ => throw new InvalidOperationException()));
    }

    private static nint ReadIdentity(object value) => ((Wrapper)value).NativeIdentity;
}

namespace WindowsLosslessSwitcher.Models;

public enum RecoveryCategory
{
    None,
    WaitingForPlayback,
    PreviousUnavailable,
    AudioRecoveryFailed,
    UnsafeState,
    DeviceUnavailable,
    UnsupportedFormat,
    FormatApplyFailed,
    FormatNotStable,
    MetadataUnavailable,
}

namespace WindowsLosslessSwitcher.Models;

/// <summary>The actual lock policy remains paused even when safe cleanup could not be confirmed.</summary>
public sealed record DacLockChangeResult(bool Succeeded, bool IsPaused, string Message);

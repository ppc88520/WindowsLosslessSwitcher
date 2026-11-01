namespace WindowsLosslessSwitcher.Services;

public sealed class StartupRegistrationService
{
    public void SetEnabled(bool enabled, string executablePath)
    {
        // Disabled for the isolated test. Do not even open the installed app's Run key.
    }
}

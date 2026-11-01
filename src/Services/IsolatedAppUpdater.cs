using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>No network, installation, registry, or update side effects in the acceptance build.</summary>
public sealed class IsolatedAppUpdater : IAppUpdater
{
    public event EventHandler<UpdateStatusSnapshot>? StatusChanged { add { } remove { } }
    public UpdateStatusSnapshot CurrentStatus { get; } = new(TestBuildProfile.Version,
        "Updates are disabled in this isolated test build.", UpdateActionKind.None, null,
        false, false, false, false, true);
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CheckForUpdatesAsync(bool userInitiated, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RunPrimaryActionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void OpenReleasesPage() { }
}

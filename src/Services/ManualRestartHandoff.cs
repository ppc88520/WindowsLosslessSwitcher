using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WindowsLosslessSwitcher.Services;

public enum ManualRestartMode { ToolOnly, AppleMusicAndTool }

public sealed record ManualRestartRequest(string Nonce, int ParentPid, long ParentStartTicks,
    string Executable, ManualRestartMode Mode, DateTimeOffset CreatedUtc, string Profile);

/// <summary>Same-executable handoff. No parent kill, shell commands, navigation, or Play.</summary>
public static class ManualRestartHandoff
{
    public const string ApplicationName = "Windows Lossless Switcher";
    internal static string ProfileIdentity => Path.GetFileName(AppDataPaths.RootDirectory);
    internal static bool IsSupportedExecutable(string path) => string.Equals(Path.GetFileName(path),
        typeof(Program).Assembly.GetName().Name + ".exe", StringComparison.OrdinalIgnoreCase);

    public const string HelperArgument = "--manual-restart-helper";
    public const string StartupArgument = "--manual-restart-startup";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string RequestsDirectory => Path.Combine(AppDataPaths.RootDirectory, "restart");

    public static async Task<string> StartHelperAsync(ManualRestartMode mode, CancellationToken token)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        if (!IsSupportedExecutable(executable))
            throw new InvalidOperationException("Restart requires launching the application executable rather than the dotnet host.");
        Directory.CreateDirectory(RequestsDirectory);
        var nonce = Guid.NewGuid().ToString("N");
        var path = Path.Combine(RequestsDirectory, nonce + ".json");
        using var parent = Process.GetCurrentProcess();
        var request = new ManualRestartRequest(nonce, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
            executable, mode, DateTimeOffset.UtcNow, ProfileIdentity);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, JsonOptions), token);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(path);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Restart helper could not start.");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(path + ".ready") && await File.ReadAllTextAsync(path + ".ready", token) == nonce)
                    return path;
                if (helper.HasExited) break;
                await Task.Delay(50, token);
            }
            throw new InvalidOperationException("Restart helper did not confirm readiness; the tool stays open.");
        }
        catch
        {
            Cancel(path);
            throw;
        }
    }

    public static bool IsAppleMusicRunning()
    {
        var processes = Process.GetProcessesByName("AppleMusic");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static void Cancel(string path) => File.WriteAllText(path + ".cancel", "cancel");

    internal static bool Validate(ManualRestartRequest request, string path, string executable, DateTimeOffset now)
    {
        var expected = Path.Combine(RequestsDirectory, request.Nonce + ".json");
        return Guid.TryParseExact(request.Nonce, "N", out _) && request.ParentPid > 0 &&
            request.ParentStartTicks > 0 && request.Profile == ProfileIdentity &&
            Enum.IsDefined(request.Mode) && request.CreatedUtc <= now.AddSeconds(5) &&
            now - request.CreatedUtc < TimeSpan.FromMinutes(2) &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(request.Executable), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task SignalReadyAsync(string path, string nonce)
    {
        var temporary = path + ".ready.tmp";
        await File.WriteAllTextAsync(temporary, nonce);
        // Publish only after the writer is closed: the parent never reads a partial file.
        File.Move(temporary, path + ".ready");
    }

    internal static async Task<bool> CompleteAsync(ManualRestartMode mode, Func<Task<bool>> waitForParent,
        Func<Task<bool>> restartPlayer, Action<bool> launchTool)
    {
        if (!await waitForParent()) return false;
        var playerSucceeded = true;
        if (mode == ManualRestartMode.AppleMusicAndTool)
        {
            try { playerSucceeded = await restartPlayer(); }
            catch (TimeoutException) { throw; }
            catch { playerSucceeded = false; }
        }
        launchTool(playerSucceeded);
        return true;
    }

    public static async Task RunHelperAsync(string requestPath)
    {
        try
        {
            var executable = Environment.ProcessPath ?? "";
            var request = JsonSerializer.Deserialize<ManualRestartRequest>(await File.ReadAllTextAsync(requestPath));
            if (request is null || !Validate(request, requestPath, executable, DateTimeOffset.UtcNow)) return;
            // Atomic claim prevents a second helper from performing the same request.
            using (new FileStream(requestPath + ".claim", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using var parent = Process.GetProcessById(request.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks || parent.Id == Environment.ProcessId ||
                !string.Equals(parent.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return;
            await SignalReadyAsync(requestPath, request.Nonce);
            var logger = new DiagnosticsLogger();
            logger.Info($"Manual restart helper ready: mode={request.Mode}, request={request.Nonce}.");
            var completed = await CompleteAsync(request.Mode, async () =>
            {
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (File.Exists(requestPath + ".cancel")) return false;
                    if (parent.HasExited) return true;
                    await Task.Delay(100);
                }
                logger.Warn("Manual restart aborted: old process did not exit. It was not killed or relaunched.");
                return false;
            }, async () => await Task.Run(() => new AppleMusicProcessController(logger).TryRestartAsync(CancellationToken.None))
                    .WaitAsync(TimeSpan.FromSeconds(45)), playerSucceeded =>
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
                start.ArgumentList.Add(StartupArgument);
                if (!playerSucceeded) start.ArgumentList.Add("--manual-player-restart-failed");
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Tool relaunch failed.");
                logger.Info($"Manual restart launched the same application executable; playerRestartConfirmed={playerSucceeded}.");
            });
            if (!completed && !File.Exists(requestPath + ".cancel"))
                LocalizedDialog.Show("The old application did not exit in time. It was not killed and no second copy was started. Pause Apple Music, then quit the tool manually.", ApplicationName);
        }
        catch (Exception ex)
        {
            LocalizedDialog.Show(LocalizationService.Current.Format("Restart did not complete: {0}\nPause Apple Music, then launch Windows Lossless Switcher manually.", ex.Message), ApplicationName);
        }
    }
}

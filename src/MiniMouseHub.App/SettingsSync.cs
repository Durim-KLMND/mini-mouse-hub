using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace MiniMouseHub.App;

/// <summary>
/// Keeps the settings in sync with the signed-in GitHub account without the user doing anything:
/// every change is uploaded a few seconds after it's saved, and at startup the copy on GitHub is
/// loaded unless this PC has changes that haven't been uploaded yet.
/// </summary>
public sealed class SettingsSync
{
    private static readonly TimeSpan UploadDelay = TimeSpan.FromSeconds(3);

    private readonly AppSettings _settings;
    private readonly GitHubSync _github = new();
    private readonly DispatcherTimer _upload = new() { Interval = UploadDelay };
    private bool _busy;

    /// <summary>The settings were replaced with the copy from GitHub.</summary>
    public event Action? Pulled;

    /// <summary>A sync finished or failed; read <see cref="Error"/>.</summary>
    public event Action? StatusChanged;

    /// <summary>Why the last sync failed, or null if it worked.</summary>
    public string? Error { get; private set; }

    public GitHubSync GitHub => _github;

    public SettingsSync(AppSettings settings)
    {
        _settings = settings;
        _upload.Tick += async (_, _) =>
        {
            _upload.Stop();
            await Run(Push);
        };
        AppSettings.Saved += () =>
        {
            if (_settings.GitHub != null && Hash(_settings.ToBackupJson()) != _settings.GitHub.SyncedHash)
                _upload.Start(); // restarting the timer groups quick changes into one upload
        };
    }

    /// <summary>
    /// Brings this PC and GitHub in line: uploads local changes, otherwise loads the copy on GitHub.
    /// Run at startup, after signing in, and now and then while the app is open.
    /// </summary>
    public Task SyncAsync() => Run(async account =>
    {
        var local = _settings.ToBackupJson();
        if (account.SyncedHash != null && Hash(local) != account.SyncedHash)
        {
            await Push(account); // changed on this PC since the last sync
            return;
        }

        var remote = await _github.DownloadAsync(account);
        if (remote == null)
        {
            await Push(account); // nothing on GitHub yet
            return;
        }
        if (Hash(remote) != Hash(local))
        {
            _settings.RestoreBackup(remote);
            Pulled?.Invoke();
        }
        MarkSynced(account);
    });

    private async Task Push(GitHubAccount account)
    {
        var json = _settings.ToBackupJson();
        if (Hash(json) == account.SyncedHash) return;
        await _github.BackUpAsync(account, json);
        MarkSynced(account);
    }

    private void MarkSynced(GitHubAccount account)
    {
        account.SyncedHash = Hash(_settings.ToBackupJson());
        account.LastSync = DateTimeOffset.Now;
        _settings.Save();
    }

    private async Task Run(Func<GitHubAccount, Task> action)
    {
        if (_settings.GitHub is not { } account) return;
        if (_busy)
        {
            _upload.Start(); // try again once the current sync is done
            return;
        }
        _busy = true;
        try
        {
            await action(account);
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
        StatusChanged?.Invoke();
    }

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}

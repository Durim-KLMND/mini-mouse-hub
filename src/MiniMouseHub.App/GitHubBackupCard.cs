using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace MiniMouseHub.App;

/// <summary>Settings card for signing in to GitHub and backing up or restoring the settings file.</summary>
internal sealed class GitHubBackupCard
{
    private readonly AppSettings _settings;
    private readonly Action _restored;
    private readonly GitHubSync _sync = new();
    private readonly StackPanel _content = new();
    private CancellationTokenSource? _signIn;

    // A restore rebuilds the settings page, so the new card shows the result.
    private static string? _statusAfterRestore;

    public Border Root { get; }

    /// <param name="restored">Called after a backup replaced the settings, so the page can show the new values.</param>
    public GitHubBackupCard(AppSettings settings, Action restored)
    {
        _settings = settings;
        _restored = restored;
        Root = Ui.Card(_content);
        Root.Unloaded += (_, _) => _signIn?.Cancel();
        Render(_statusAfterRestore);
        _statusAfterRestore = null;
    }

    private void Render(string? status = null)
    {
        _content.Children.Clear();
        _content.Children.Add(Ui.Label("BACKUP TO GITHUB"));

        if (!GitHubSync.IsConfigured)
        {
            _content.Children.Add(Ui.Text("GitHub backup isn't set up in this version of the app yet.", 13, "Muted"));
        }
        else if (_settings.GitHub is not { } account)
        {
            _content.Children.Add(Ui.Text("Sign in to keep a copy of your settings in a secret gist on your GitHub account, and restore it on another PC.", 13, "Muted"));
            var signIn = PrimaryButton("Sign in with GitHub");
            signIn.Click += async (_, _) => await SignIn();
            _content.Children.Add(Row(signIn));
        }
        else
        {
            var last = account.LastBackup is { } time ? $"Last backup {time.LocalDateTime:g}." : "No backup from this PC yet.";
            _content.Children.Add(Ui.Text($"Signed in as {account.Login}. {last}", 13, "Muted"));
            var backUp = PrimaryButton("Back up now");
            var restore = new Button { Content = "Restore", Margin = new Thickness(8, 0, 0, 0) };
            var signOut = new Button { Content = "Sign out", Margin = new Thickness(8, 0, 0, 0) };
            backUp.Click += async (_, _) => await Run(BackUp);
            restore.Click += async (_, _) => await Run(Restore);
            signOut.Click += (_, _) =>
            {
                _settings.GitHub = null;
                _settings.Save();
                Render("Signed out. You can also revoke the app's access in your GitHub settings under Applications.");
            };
            _content.Children.Add(Row(backUp, restore, signOut));
        }

        if (status != null)
        {
            var text = Ui.Text(status, 12, "Muted");
            text.Margin = new Thickness(0, 8, 0, 0);
            _content.Children.Add(text);
        }

        var path = Ui.Text($"Settings file: {AppSettings.FilePath}", 12, "Muted");
        path.Margin = new Thickness(0, 10, 0, 0);
        _content.Children.Add(path);
    }

    private async Task SignIn()
    {
        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        var cancel = _signIn.Token;
        GitHubSync.DeviceCode device;
        try { device = await _sync.StartSignInAsync(); }
        catch (Exception ex)
        {
            Render($"Couldn't reach GitHub: {ex.Message}");
            return;
        }

        ShowCode(device);
        try
        {
            _settings.GitHub = await _sync.FinishSignInAsync(device, cancel);
            _settings.Save();
            Render($"Signed in as {_settings.GitHub.Login}.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (Root.IsLoaded) Render();
        }
        catch (Exception ex)
        {
            Render($"Sign-in failed: {ex.Message}");
        }
    }

    private void ShowCode(GitHubSync.DeviceCode device)
    {
        _content.Children.Clear();
        _content.Children.Add(Ui.Label("BACKUP TO GITHUB"));
        _content.Children.Add(Ui.Text($"Enter this code at {device.VerificationUri} to let Mini Mouse Hub save gists for you:", 13, "Muted"));
        var code = new TextBox
        {
            Text = device.UserCode,
            IsReadOnly = true,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Background = null,
            Foreground = MainWindow.Res("Text"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        _content.Children.Add(code);

        var open = PrimaryButton("Copy code and open GitHub");
        open.Click += (_, _) =>
        {
            try { Clipboard.SetText(device.UserCode); } catch (Exception) { } // clipboard busy; the code is on screen
            Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
        };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => _signIn?.Cancel();
        _content.Children.Add(Row(open, cancel));
        var waiting = Ui.Text("Waiting for GitHub…", 12, "Muted");
        waiting.Margin = new Thickness(0, 8, 0, 0);
        _content.Children.Add(waiting);
    }

    private async Task<string> BackUp()
    {
        await _sync.BackUpAsync(_settings.GitHub!, _settings.ToBackupJson());
        _settings.Save(); // remembers the gist
        return "Settings saved to GitHub.";
    }

    private async Task<string> Restore()
    {
        var json = await _sync.DownloadAsync(_settings.GitHub!);
        if (json == null)
            return "There's no backup on GitHub yet. Use Back up now first.";
        var answer = MessageBox.Show("Replace the settings on this PC with your backup from GitHub?", "Mini Mouse Hub",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return "Restore cancelled.";
        _settings.RestoreBackup(json);
        _statusAfterRestore = "Settings restored from GitHub.";
        Theme.Set(_settings.Theme);
        _restored();
        return "Settings restored from GitHub.";
    }

    /// <summary>Runs a GitHub action with the buttons disabled and shows how it went.</summary>
    private async Task Run(Func<Task<string>> action)
    {
        _content.IsEnabled = false;
        string status;
        try { status = await action(); }
        catch (Exception ex) { status = $"Something went wrong: {ex.Message}"; }
        finally { _content.IsEnabled = true; }
        Render(status);
    }

    private static Button PrimaryButton(string text) => new()
    {
        Content = text,
        Style = (Style)Application.Current.Resources["PrimaryButton"],
    };

    private static StackPanel Row(params UIElement[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace MiniMouseHub.App;

/// <summary>Settings card for signing in to GitHub, after which the settings sync on their own.</summary>
internal sealed class GitHubSyncCard
{
    private readonly AppSettings _settings;
    private readonly SettingsSync _sync;
    private readonly StackPanel _content = new();
    private CancellationTokenSource? _signIn;

    public Border Root { get; }

    public GitHubSyncCard(AppSettings settings, SettingsSync sync)
    {
        _settings = settings;
        _sync = sync;
        Root = Ui.Card(_content);
        Root.Loaded += (_, _) => _sync.StatusChanged += OnSynced;
        Root.Unloaded += (_, _) =>
        {
            _sync.StatusChanged -= OnSynced;
            _signIn?.Cancel();
        };
        Render();
    }

    private void OnSynced()
    {
        if (_signIn == null) Render();
    }

    private void Render(string? status = null)
    {
        _content.Children.Clear();
        _content.Children.Add(Ui.Label("SYNC WITH GITHUB"));

        if (!GitHubSync.IsConfigured)
        {
            _content.Children.Add(Ui.Text("GitHub sync isn't set up in this version of the app.", 13, "Muted"));
        }
        else if (_settings.GitHub is not { } account)
        {
            _content.Children.Add(Ui.Text("Sign in with GitHub and your settings follow you to any PC. They're saved to your account automatically whenever you change something.", 13, "Muted"));
            var signIn = PrimaryButton("Sign in with GitHub");
            signIn.Click += async (_, _) => await SignIn();
            _content.Children.Add(Row(signIn));
        }
        else
        {
            var last = account.LastSync is { } time ? $"Last synced {time.LocalDateTime:g}." : "Syncing…";
            _content.Children.Add(Ui.Text($"Signed in as {account.Login}. Your settings sync automatically. {last}", 13, "Muted"));
            var signOut = new Button { Content = "Sign out" };
            signOut.Click += (_, _) =>
            {
                _settings.GitHub = null;
                _settings.Save();
                Render("Signed out. Your settings stay on this PC. To remove the app's access, go to GitHub settings → Applications.");
            };
            _content.Children.Add(Row(signOut));
            status ??= _sync.Error is { } error ? $"Couldn't sync: {error}" : null;
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
        var signIn = _signIn = new CancellationTokenSource();
        GitHubSync.DeviceCode device;
        try { device = await _sync.GitHub.StartSignInAsync(); }
        catch (Exception ex)
        {
            _signIn = null;
            Render($"Couldn't reach GitHub: {ex.Message}");
            return;
        }

        CopyCodeAndOpenGitHub(device);
        ShowCode(device);
        try
        {
            _settings.GitHub = await _sync.GitHub.FinishSignInAsync(device, signIn.Token);
            _settings.Save();
            _signIn = null;
            Render();
            await _sync.SyncAsync();
        }
        catch (OperationCanceledException) when (signIn.IsCancellationRequested)
        {
            if (_signIn == signIn) _signIn = null;
            if (Root.IsLoaded) Render();
        }
        catch (Exception ex)
        {
            _signIn = null;
            Render($"Sign-in failed: {ex.Message}");
        }
    }

    private static void CopyCodeAndOpenGitHub(GitHubSync.DeviceCode device)
    {
        try { Clipboard.SetText(device.UserCode); } catch (Exception) { } // clipboard busy; the code is on screen
        Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
    }

    private void ShowCode(GitHubSync.DeviceCode device)
    {
        _content.Children.Clear();
        _content.Children.Add(Ui.Label("SYNC WITH GITHUB"));
        _content.Children.Add(Ui.Text("GitHub is open in your browser. Paste this code there (it's already copied) and approve Mini mouse hub:", 13, "Muted"));
        _content.Children.Add(new TextBox
        {
            Text = device.UserCode,
            IsReadOnly = true,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Background = null,
            Foreground = MainWindow.Res("Text"),
            Margin = new Thickness(0, 8, 0, 0),
        });

        var reopen = new Button { Content = "Open GitHub again" };
        reopen.Click += (_, _) => CopyCodeAndOpenGitHub(device);
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => _signIn?.Cancel();
        _content.Children.Add(Row(reopen, cancel));
        var waiting = Ui.Text("Waiting for GitHub…", 12, "Muted");
        waiting.Margin = new Thickness(0, 8, 0, 0);
        _content.Children.Add(waiting);
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

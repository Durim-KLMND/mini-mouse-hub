using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MiniMouseHub.App;

/// <summary>
/// Checks the project's GitHub releases for a newer build and installs it. GitHub Actions publishes
/// a release with MiniMouseHub.exe on every push to main.
/// </summary>
public sealed class Updater
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Durim-KLMND/mini-mouse-hub/releases/latest";
    private const string AssetName = "MiniMouseHub.exe";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    public record Release(Version Version, string DownloadUrl, string Tag);

    /// <summary>Returns the latest release if it's newer than this build, or null.</summary>
    public async Task<Release?> CheckAsync()
    {
        using var response = await Http.GetAsync(LatestReleaseUrl);
        if (!response.IsSuccessStatusCode)
            return null; // no releases yet, or GitHub is unreachable
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || version <= CurrentVersion)
            return null;

        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == AssetName)
                return new Release(version, asset.GetProperty("browser_download_url").GetString()!, tag);
        }
        return null;
    }

    /// <summary>
    /// Downloads the release next to the running exe, swaps it in, and starts it. Windows lets a running
    /// exe be renamed, so the current one becomes MiniMouseHub.old.exe and is deleted on the next start.
    /// Returns once the new copy has been started; the caller should then exit.
    /// </summary>
    public async Task InstallAsync(Release release, bool startMinimized)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the running app.");
        var folder = Path.GetDirectoryName(exe)!;
        var downloaded = Path.Combine(folder, "MiniMouseHub.new.exe");
        var old = Path.Combine(folder, "MiniMouseHub.old.exe");

        await using (var source = await Http.GetStreamAsync(release.DownloadUrl))
        await using (var target = File.Create(downloaded))
            await source.CopyToAsync(target);

        if (new FileInfo(downloaded).Length < 100_000)
            throw new IOException("The downloaded update looks incomplete.");

        File.Delete(old);
        File.Move(exe, old);
        File.Move(downloaded, exe);

        var args = "--updated" + (startMinimized ? " --minimized" : "");
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
    }

    /// <summary>Deletes the previous version left behind by an update.</summary>
    public static void CleanUpOldVersion()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var old = Path.Combine(Path.GetDirectoryName(exe)!, "MiniMouseHub.old.exe");
        for (var attempt = 0; attempt < 10 && File.Exists(old); attempt++)
        {
            try { File.Delete(old); }
            catch (IOException) { Thread.Sleep(500); } // the old process may still be exiting
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    private static Version ReadCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniMouseHub-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

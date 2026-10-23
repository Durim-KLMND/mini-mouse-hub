using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniMouseHub.App;

/// <summary>
/// Signs in to GitHub with the OAuth device flow and keeps a backup of the settings in a secret gist.
/// The app only asks for the "gist" scope, so it can't see the user's repositories.
/// </summary>
public sealed class GitHubSync
{
    /// <summary>Client ID of the Mini Mouse Hub OAuth app (with Device Flow enabled). Client IDs are public.</summary>
    public const string ClientId = "";

    public const string BackupFileName = "mini-mouse-hub-settings.json";
    private const string GistDescription = "Mini Mouse Hub settings backup";

    private static readonly HttpClient Http = CreateClient();

    public static bool IsConfigured => ClientId.Length > 0;

    public record DeviceCode(string Code, string UserCode, string VerificationUri, TimeSpan Interval, DateTimeOffset Expires);

    /// <summary>Starts a sign-in. Show the user code and send the user to the verification page.</summary>
    public async Task<DeviceCode> StartSignInAsync()
    {
        using var json = await PostFormAsync("https://github.com/login/device/code", new()
        {
            ["client_id"] = ClientId,
            ["scope"] = "gist",
        });
        var root = json.RootElement;
        return new DeviceCode(
            root.GetProperty("device_code").GetString()!,
            root.GetProperty("user_code").GetString()!,
            root.GetProperty("verification_uri").GetString()!,
            TimeSpan.FromSeconds(root.GetProperty("interval").GetInt32()),
            DateTimeOffset.Now.AddSeconds(root.GetProperty("expires_in").GetInt32()));
    }

    /// <summary>Waits until the user has entered the code on GitHub, then returns the signed-in account.</summary>
    public async Task<GitHubAccount> FinishSignInAsync(DeviceCode device, CancellationToken cancel)
    {
        var interval = device.Interval;
        while (DateTimeOffset.Now < device.Expires)
        {
            await Task.Delay(interval, cancel);
            using var json = await PostFormAsync("https://github.com/login/oauth/access_token", new()
            {
                ["client_id"] = ClientId,
                ["device_code"] = device.Code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            });
            var root = json.RootElement;
            if (root.TryGetProperty("access_token", out var token))
            {
                var login = await GetLoginAsync(token.GetString()!);
                return GitHubAccount.Create(login, token.GetString()!);
            }

            switch (root.GetProperty("error").GetString())
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "access_denied":
                    throw new InvalidOperationException("The sign-in was cancelled on GitHub.");
                case "expired_token":
                    throw new TimeoutException("The code expired. Try signing in again.");
                default:
                    throw new InvalidOperationException(root.TryGetProperty("error_description", out var d) ? d.GetString() : "GitHub refused the sign-in.");
            }
        }
        throw new TimeoutException("The code expired. Try signing in again.");
    }

    /// <summary>Uploads the settings to the backup gist, creating the gist the first time.</summary>
    public async Task BackUpAsync(GitHubAccount account, string settingsJson)
    {
        var token = account.Token;
        account.GistId ??= await FindGistAsync(token);
        var files = new JsonObject { [BackupFileName] = new JsonObject { ["content"] = settingsJson } };

        if (account.GistId != null)
        {
            using var update = await SendAsync(HttpMethod.Patch, $"https://api.github.com/gists/{account.GistId}", token,
                new JsonObject { ["files"] = files });
            if (update.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccess(update);
                account.LastBackup = DateTimeOffset.Now;
                return;
            }
            // The gist was deleted on GitHub; make a new one.
        }

        using var create = await SendAsync(HttpMethod.Post, "https://api.github.com/gists", token,
            new JsonObject { ["description"] = GistDescription, ["public"] = false, ["files"] = files });
        await EnsureSuccess(create);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        account.GistId = created.RootElement.GetProperty("id").GetString();
        account.LastBackup = DateTimeOffset.Now;
    }

    /// <summary>Downloads the settings backup, or returns null if there isn't one.</summary>
    public async Task<string?> DownloadAsync(GitHubAccount account)
    {
        var token = account.Token;
        account.GistId ??= await FindGistAsync(token);
        if (account.GistId == null) return null;

        using var response = await SendAsync(HttpMethod.Get, $"https://api.github.com/gists/{account.GistId}", token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            account.GistId = null;
            return null;
        }
        await EnsureSuccess(response);
        using var gist = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return gist.RootElement.GetProperty("files").TryGetProperty(BackupFileName, out var file)
            ? file.GetProperty("content").GetString()
            : null;
    }

    /// <summary>Looks through the user's gists for an earlier backup, for example one made on another PC.</summary>
    private static async Task<string?> FindGistAsync(string token)
    {
        for (var page = 1; page <= 10; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"https://api.github.com/gists?per_page=100&page={page}", token);
            await EnsureSuccess(response);
            using var gists = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (gists.RootElement.GetArrayLength() == 0) return null;
            foreach (var gist in gists.RootElement.EnumerateArray())
            {
                if (gist.GetProperty("files").TryGetProperty(BackupFileName, out _))
                    return gist.GetProperty("id").GetString();
            }
        }
        return null;
    }

    private static async Task<string> GetLoginAsync(string token)
    {
        using var response = await SendAsync(HttpMethod.Get, "https://api.github.com/user", token);
        await EnsureSuccess(response);
        using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return user.RootElement.GetProperty("login").GetString()!;
    }

    private static async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> form)
    {
        using var response = await Http.PostAsync(url, new FormUrlEncodedContent(form));
        await EnsureSuccess(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            request.Content = JsonContent.Create(body);
        return Http.SendAsync(request);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("GitHub no longer accepts this sign-in. Sign out and sign in again.");
        throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniMouseHub");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}

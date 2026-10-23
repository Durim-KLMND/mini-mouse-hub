using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace MiniMouseHub.App;

/// <summary>
/// Signs in to GitHub and stores the settings in a secret gist. Release builds sign in through the
/// browser and get the answer back on a local port; builds without the client secret fall back to
/// the device flow, where the user pastes a code.
/// The app only asks for the "gist" scope, so it can't see the user's repositories.
/// </summary>
public sealed class GitHubSync
{
    /// <summary>Client ID of the Mini mouse hub OAuth app (with Device Flow enabled). Client IDs are public.</summary>
    public const string ClientId = "Ov23liP8WLQzxcRmd1uq";

    public const string BackupFileName = "mini-mouse-hub-settings.json";
    private const string GistDescription = "Mini mouse hub settings";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// The OAuth app's client secret, added at build time from the MMH_GITHUB_CLIENT_SECRET Actions
    /// secret so it never sits in the repository (GitHub Desktop does the same).
    /// </summary>
    private static readonly string ClientSecret = typeof(GitHubSync).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "GitHubClientSecret")?.Value ?? "";

    public static bool IsConfigured => ClientId.Length > 0;

    /// <summary>True when sign-in can go straight through the browser, without a code.</summary>
    public static bool CanSignInWithBrowser => ClientSecret.Length > 0;

    /// <summary>
    /// Opens GitHub in the browser and waits for the user to approve. GitHub then sends the browser back
    /// to http://127.0.0.1:{port}/callback, where this listens for the one request carrying the code.
    /// </summary>
    public async Task<GitHubAccount> SignInWithBrowserAsync(CancellationToken cancel)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback";
            var state = RandomToken();
            var verifier = RandomToken();
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            Process.Start(new ProcessStartInfo(
                "https://github.com/login/oauth/authorize" +
                $"?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(redirect)}&scope=gist&state={state}" +
                $"&code_challenge={challenge}&code_challenge_method=S256") { UseShellExecute = true });

            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancel);
                var stream = client.GetStream();
                var requestLine = await new StreamReader(stream, Encoding.ASCII).ReadLineAsync(cancel) ?? "";
                var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "";
                if (!target.StartsWith("/callback?"))
                {
                    await Respond(stream, "404 Not Found", "Not found.");
                    continue; // e.g. the browser asking for a favicon
                }

                var query = HttpUtility.ParseQueryString(target["/callback".Length..]);
                if (query["state"] != state)
                {
                    await Respond(stream, "400 Bad Request", "This sign-in link doesn't match. Start again from Mini mouse hub.");
                    continue;
                }
                if (query["code"] is not { } code)
                {
                    await Respond(stream, "200 OK", "Sign-in was cancelled. You can close this tab.");
                    throw new InvalidOperationException("The sign-in was cancelled on GitHub.");
                }

                await Respond(stream, "200 OK", "You're signed in. You can close this tab and go back to Mini mouse hub.");
                using var json = await PostFormAsync("https://github.com/login/oauth/access_token", new()
                {
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["code"] = code,
                    ["redirect_uri"] = redirect,
                    ["code_verifier"] = verifier,
                });
                if (!json.RootElement.TryGetProperty("access_token", out var token))
                    throw new InvalidOperationException(json.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : "GitHub refused the sign-in.");
                var login = await GetLoginAsync(token.GetString()!);
                return GitHubAccount.Create(login, token.GetString()!);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task Respond(NetworkStream stream, string status, string message)
    {
        var html = "<!doctype html><meta charset=utf-8><title>Mini mouse hub</title>" +
            "<body style=\"font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:90vh;color:#1F2120\">" +
            $"<p style=\"font-size:18px\">{WebUtility.HtmlEncode(message)}</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
                return;
            }
            // The gist was deleted on GitHub; make a new one.
        }

        using var create = await SendAsync(HttpMethod.Post, "https://api.github.com/gists", token,
            new JsonObject { ["description"] = GistDescription, ["public"] = false, ["files"] = files });
        await EnsureSuccess(create);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        account.GistId = created.RootElement.GetProperty("id").GetString();
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

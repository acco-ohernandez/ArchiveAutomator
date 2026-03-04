using ArchiveAutomator.Interfaces;
using Box.Sdk.Gen;
using Box.Sdk.Gen.Managers;
using System.Diagnostics;
using System.IO;
using Task = System.Threading.Tasks.Task;

namespace ArchiveAutomator.Providers;

/// <summary>
/// Box.com storage provider using OAuth2 (Authorization Code flow via system browser).
///
/// Path format: OrchestratorService.BuildPath produces "parentFolderId\folderName" on Windows
/// (e.g. "368694270455\10001"). Box API requires each folder's own unique ID, not a name-based
/// path. <see cref="ResolveSourceIdAsync"/> lists the parent folder's children and looks up the
/// matching subfolder ID before every Move/Copy/Delete call.
/// </summary>
public class BoxApiProvider : IStorageProvider
{
    public static string ClientId { get; set; } = string.Empty;
    public static string ClientSecret { get; set; } = string.Empty;

    // Static so the authenticated BoxClient is shared across all BoxApiProvider instances.
    // AuthenticateBoxAsync() creates one instance for sign-in; StartAsync() creates a separate
    // instance for operations. Without static, the operations instance always has _client=null
    // → "BoxApiProvider is not authenticated" error on every job.
    private static BoxClient? _client;

    // Candidate ports tried in order when starting the local OAuth callback listener.
    // The first port that is free for HttpListener is chosen at runtime; its full callback
    // URI is passed explicitly to oauth.GetAuthorizeUrl() so Box knows where to redirect.
    //
    // Register ALL five in Box Developer Console → App → Configuration → Redirect URIs:
    //   http://localhost:49200/callback
    //   http://localhost:49201/callback
    //   http://localhost:49202/callback
    //   http://localhost:49203/callback
    //   http://localhost:49204/callback
    //
    // These ports are in the IANA dynamic/ephemeral range (49152–65535) and are extremely
    // unlikely to have pre-existing http.sys URL ACLs registered by other applications.
    private static readonly int[] OAuthPorts = { 49200, 49201, 49202, 49203, 49204 };
    private const int MaxRetries = 4;
    private const int BaseDelayMs = 500;

    // ── Auth ───────────────────────────────────────────────────────────────

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(ClientSecret))
            throw new InvalidOperationException("Box ClientId and ClientSecret must be set before authenticating.");

        var config = new OAuthConfig(clientId: ClientId, clientSecret: ClientSecret);
        var oauth  = new BoxOAuth(config: config);

        // Find a free local port and pass its callback URI to Box so the authorize URL
        // includes redirect_uri — required when Box has multiple URIs registered, and
        // it prevents the "redirect_uri_missing" error when no default has been set.
        (int port, string callbackUri) = FindFreeOAuthPort();
        string authUrl = oauth.GetAuthorizeUrl(new GetAuthorizeUrlOptions { RedirectUri = callbackUri });
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        string? authCode = await CaptureAuthCodeAsync(port, ct);
        if (string.IsNullOrEmpty(authCode))
            return false;

        await oauth.GetTokensAuthorizationCodeGrantAsync(authCode);
        _client = new BoxClient(auth: oauth);
        return true;
    }

    private static BoxClient GetClient() =>
        _client ?? throw new InvalidOperationException(
            "BoxApiProvider is not authenticated. Call AuthenticateAsync first.");

    // ── IStorageProvider ───────────────────────────────────────────────────

    public async Task<bool> CanMoveAsync(string folderId, CancellationToken ct = default)
    {
        try
        {
            await RetryAsync(() => GetClient().Folders.GetFolderByIdAsync(folderId, cancellationToken: ct), ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Moves a Box subfolder to a new parent folder.
    /// <paramref name="sourcePath"/>      = "sourceParentId\subfolderName" — subfolder name is resolved to its Box ID.
    /// <paramref name="destinationPath"/> = "destParentId\subfolderName"  — only the parent ID is used.
    /// </summary>
    public async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        string sourceFolderId = await ResolveSourceIdAsync(sourcePath, ct);
        string destParentId   = ParseParentId(destinationPath);

        var body = new UpdateFolderByIdRequestBody
        {
            Parent = new UpdateFolderByIdRequestBodyParentField { Id = destParentId }
        };

        await RetryAsync(
            () => GetClient().Folders.UpdateFolderByIdAsync(sourceFolderId, body, cancellationToken: ct), ct);
    }

    /// <summary>
    /// Copies a Box subfolder to a new parent folder.
    /// <paramref name="sourcePath"/>      = "sourceParentId\subfolderName" — subfolder name is resolved to its Box ID.
    /// <paramref name="destinationPath"/> = "destParentId\subfolderName"  — only the parent ID is used.
    /// </summary>
    public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        string sourceFolderId = await ResolveSourceIdAsync(sourcePath, ct);
        string destParentId   = ParseParentId(destinationPath);

        var body = new CopyFolderRequestBody(
            parent: new CopyFolderRequestBodyParentField(id: destParentId));

        await RetryAsync(
            () => GetClient().Folders.CopyFolderAsync(sourceFolderId, body, cancellationToken: ct), ct);
    }

    /// <summary>
    /// Deletes a Box subfolder recursively.
    /// <paramref name="sourcePath"/> = "sourceParentId\subfolderName" — subfolder name is resolved to its Box ID.
    /// </summary>
    public async Task DeleteAsync(string sourcePath, CancellationToken ct = default)
    {
        string sourceFolderId = await ResolveSourceIdAsync(sourcePath, ct);

        var queryParams = new DeleteFolderByIdQueryParams { Recursive = true };
        await RetryAsync(
            () => GetClient().Folders.DeleteFolderByIdAsync(
                sourceFolderId, queryParams: queryParams, cancellationToken: ct), ct);
    }

    // ── Box path resolution ────────────────────────────────────────────────

    /// <summary>
    /// Parses "parentId\folderName" and resolves the subfolder name to its Box folder ID
    /// by listing the parent folder's children. If <paramref name="path"/> contains no
    /// backslash it is treated as a bare Box folder ID and returned directly.
    /// </summary>
    private static async System.Threading.Tasks.Task<string> ResolveSourceIdAsync(
        string path, CancellationToken ct)
    {
        int sep = path.IndexOf('\\');
        if (sep < 0) return path;               // already a bare Box folder ID

        string parentId   = path[..sep];
        string folderName = path[(sep + 1)..];

        return await FindFolderByNameAsync(parentId, folderName, ct);
    }

    /// <summary>
    /// For destination paths ("destParentId\subfolderName"), only the parent folder ID
    /// is needed — Box moves/copies change a folder's parent, not its name.
    /// </summary>
    private static string ParseParentId(string path)
    {
        int sep = path.IndexOf('\\');
        return sep < 0 ? path : path[..sep];
    }

    /// <summary>
    /// Lists up to 1 000 items in <paramref name="parentFolderId"/> and returns the ID
    /// of the first entry whose name matches <paramref name="folderName"/> (case-insensitive).
    /// Throws <see cref="DirectoryNotFoundException"/> if no match is found.
    /// </summary>
    private static async System.Threading.Tasks.Task<string> FindFolderByNameAsync(
        string parentFolderId, string folderName, CancellationToken ct)
    {
        var queryParams = new GetFolderItemsQueryParams
        {
            Fields = new List<string> { "id", "name" },
            Limit  = 1000L
        };

        var page = await RetryAsync(
            () => GetClient().Folders.GetFolderItemsAsync(
                parentFolderId, queryParams: queryParams, cancellationToken: ct),
            ct);

        // page.Entries is IReadOnlyList<FileFullOrFolderMiniOrWebLink>.
        // FolderMini is non-null only for folder entries; null for files and web links.
        var match = page.Entries?
            .Select(e => e.FolderMini)
            .FirstOrDefault(f => f is not null &&
                string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new DirectoryNotFoundException(
                $"Box folder '{folderName}' was not found inside parent folder {parentFolderId}. " +
                $"Verify the Source Folder ID is correct and the subfolder exists in Box.");

        return match.Id
            ?? throw new InvalidOperationException(
                $"Box folder '{folderName}' returned a null ID from the API.");
    }

    // ── Retry with exponential back-off ───────────────────────────────────

    /// <summary>
    /// Retries <paramref name="action"/> up to <see cref="MaxRetries"/> times with exponential
    /// back-off on rate-limit (HTTP 429) or transient server errors (5xx).
    /// Any non-retryable exception — or a failure on the final attempt — propagates immediately.
    /// </summary>
    private static async System.Threading.Tasks.Task<T> RetryAsync<T>(
        Func<System.Threading.Tasks.Task<T>> action, CancellationToken ct)
    {
        BoxApiException? lastTransient = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await action();
            }
            catch (BoxApiException ex) when (attempt < MaxRetries && IsRateLimitOrTransient(ex))
            {
                lastTransient = ex;
                int delay = BaseDelayMs * (int)Math.Pow(2, attempt); // 500ms, 1s, 2s, 4s
                await System.Threading.Tasks.Task.Delay(delay, ct);
            }
            // Non-retryable BoxApiException or final-attempt failure propagates naturally
        }

        // Only reached when every attempt was a retryable failure — rethrow the last one.
        throw lastTransient!;
    }

    private static async Task RetryAsync(Func<System.Threading.Tasks.Task> action, CancellationToken ct)
    {
        await RetryAsync<object?>(async () => { await action(); return null; }, ct);
    }

    private static bool IsRateLimitOrTransient(BoxApiException ex)
    {
        int code = ex.ResponseInfo.StatusCode;
        return code == 429 || code is >= 500 and <= 503;
    }

    // ── Local HTTP listener for OAuth redirect ─────────────────────────────

    /// <summary>
    /// Probes each port in <see cref="OAuthPorts"/> until one is free for an HttpListener.
    /// Returns the chosen port and the full callback URI to embed in the Box authorize URL.
    /// Throws <see cref="InvalidOperationException"/> if every candidate port is in use.
    /// </summary>
    private static (int port, string callbackUri) FindFreeOAuthPort()
    {
        foreach (int port in OAuthPorts)
        {
            using var probe = new System.Net.HttpListener();
            probe.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                probe.Start();
                probe.Stop();
                return (port, $"http://localhost:{port}/callback");
            }
            catch (System.Net.HttpListenerException)
            {
                // Port already registered with http.sys — try the next candidate.
            }
        }

        throw new InvalidOperationException(
            $"No local port is available for the Box sign-in callback " +
            $"(tried: {string.Join(", ", OAuthPorts)}).\n" +
            "Close other applications that may be using these ports and try again.");
    }

    private static async System.Threading.Tasks.Task<string?> CaptureAuthCodeAsync(
        int port, CancellationToken ct)
    {
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        try
        {
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(2), ct);
            string? code = System.Web.HttpUtility.ParseQueryString(
                context.Request.Url?.Query ?? string.Empty)["code"];

            using var response = context.Response;
            byte[] body = System.Text.Encoding.UTF8.GetBytes(
                "<html><body><h2>Authentication complete.</h2><p>You can close this tab and return to Archive Automator.</p></body></html>");
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, ct);

            return code;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            // WaitAsync(2 min) expired — user didn't complete sign-in in time.
            // Return null so AuthenticateAsync returns false instead of crashing.
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }
}

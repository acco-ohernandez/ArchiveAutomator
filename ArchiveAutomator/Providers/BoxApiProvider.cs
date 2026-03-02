using ArchiveAutomator.Interfaces;
using Box.Sdk.Gen;
using Box.Sdk.Gen.Managers;
using System.Diagnostics;
using Task = System.Threading.Tasks.Task;

namespace ArchiveAutomator.Providers;

/// <summary>
/// Box.com storage provider using OAuth2 (Authorization Code flow via system browser).
/// All operations use Folder IDs — never search by folder name inside the loop.
/// </summary>
public class BoxApiProvider : IStorageProvider
{
    public static string ClientId { get; set; } = string.Empty;
    public static string ClientSecret { get; set; } = string.Empty;

    private const string RedirectUri = "http://localhost:4545/";
    private const int MaxRetries = 4;
    private const int BaseDelayMs = 500;

    private BoxClient? _client;

    // ── Auth ───────────────────────────────────────────────────────────────

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(ClientSecret))
            throw new InvalidOperationException("Box ClientId and ClientSecret must be set before authenticating.");

        var config = new OAuthConfig(clientId: ClientId, clientSecret: ClientSecret);
        var oauth = new BoxOAuth(config: config);

        string authUrl = oauth.GetAuthorizeUrl();
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        string? authCode = await CaptureAuthCodeAsync(ct);
        if (string.IsNullOrEmpty(authCode))
            return false;

        await oauth.GetTokensAuthorizationCodeGrantAsync(authCode);
        _client = new BoxClient(auth: oauth);
        return true;
    }

    private BoxClient GetClient() =>
        _client ?? throw new InvalidOperationException("BoxApiProvider is not authenticated. Call AuthenticateAsync first.");

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
    /// Moves a Box folder by updating its parent ID.
    /// sourcePath = source folder ID, destinationPath = destination parent folder ID.
    /// </summary>
    public async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        var body = new UpdateFolderByIdRequestBody
        {
            Parent = new UpdateFolderByIdRequestBodyParentField { Id = destinationPath }
        };

        await RetryAsync(() => GetClient().Folders.UpdateFolderByIdAsync(sourcePath, body, cancellationToken: ct), ct);
    }

    /// <summary>
    /// Copies a Box folder to the destination parent ID.
    /// </summary>
    public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        var body = new CopyFolderRequestBody(
            parent: new CopyFolderRequestBodyParentField(id: destinationPath));

        await RetryAsync(() => GetClient().Folders.CopyFolderAsync(sourcePath, body, cancellationToken: ct), ct);
    }

    /// <summary>
    /// Deletes a Box folder recursively.
    /// </summary>
    public async Task DeleteAsync(string sourcePath, CancellationToken ct = default)
    {
        var queryParams = new DeleteFolderByIdQueryParams { Recursive = true };
        await RetryAsync(
            () => GetClient().Folders.DeleteFolderByIdAsync(sourcePath, queryParams: queryParams, cancellationToken: ct),
            ct);
    }

    // ── Retry with exponential back-off ───────────────────────────────────

    private static async System.Threading.Tasks.Task<T> RetryAsync<T>(
        Func<System.Threading.Tasks.Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await action();
            }
            catch (BoxApiException ex) when (attempt < MaxRetries && IsRateLimitOrTransient(ex))
            {
                int delay = BaseDelayMs * (int)Math.Pow(2, attempt);
                await System.Threading.Tasks.Task.Delay(delay, ct);
            }
        }
        throw new InvalidOperationException("RetryAsync: unreachable");
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

    private static async System.Threading.Tasks.Task<string?> CaptureAuthCodeAsync(CancellationToken ct)
    {
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        try
        {
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(2), ct);
            string? code = System.Web.HttpUtility.ParseQueryString(
                context.Request.Url?.Query ?? string.Empty)["code"];

            using var response = context.Response;
            byte[] body = System.Text.Encoding.UTF8.GetBytes(
                "<html><body>Authentication complete. You can close this tab.</body></html>");
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, ct);

            return code;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }
}

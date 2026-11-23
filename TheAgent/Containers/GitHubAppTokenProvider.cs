using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xianix.Containers;

/// <summary>A GitHub App's ID (or Client ID) and private key, from the <c>GITHUB-TOKEN</c> secret.</summary>
public sealed record GitHubAppCredentials(string AppId, string PrivateKeyPem);

/// <summary>A 1-hour App token for one repository.</summary>
public sealed record GitHubInstallationToken(string Token, DateTimeOffset ExpiresAt, long InstallationId)
{
    // Hides the token so logging this record can't leak it.
    public override string ToString() =>
        $"GitHubInstallationToken {{ InstallationId = {InstallationId}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>
/// A GitHub App token could not be created. The message holds no secrets.
/// <c>IsTransient</c> is true when a retry may succeed (network, 5xx, rate limit).
/// </summary>
public sealed class GitHubAppTokenException(string message, Exception? inner = null, bool isTransient = false)
    : Exception(message, inner)
{
    public bool IsTransient { get; } = isTransient;
}

/// <summary>
/// Creates 1-hour GitHub App tokens limited to one repository, so runs act as the App
/// instead of a user's PAT. Steps: sign a JWT with the App key, find the App's installation
/// on the repository, then request a token for that repository only.
/// </summary>
public sealed class GitHubAppTokenProvider
{
    /// <summary>Secret and container variable: a PAT, or <c>&lt;app-id&gt;,&lt;private-key&gt;</c> for an App.</summary>
    public const string TokenEnvName = "GITHUB-TOKEN";

    private const string AppIdPart = "the App ID (before the comma) in " + TokenEnvName;
    private const string KeyPart   = "the private key (after the comma) in " + TokenEnvName;
    private const string ExpectedFormat =
        "Expected '<app-id>,<private-key>': the numeric App ID or Client ID (Iv…), a comma, then the App's .pem private key.";

    private static readonly Regex AppIdPattern = new(@"^(\d+|Iv[0-9A-Za-z._-]+)$", RegexOptions.Compiled);

    /// <summary>How long GitHub keeps an installation token valid.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    private static readonly Uri ApiBase = new("https://api.github.com/");

    // GitHub allows at most 10 minutes; start 60s early to allow for clock drift.
    private static readonly TimeSpan JwtClockSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JwtLifetime  = TimeSpan.FromMinutes(9);

    private static readonly Regex ScpLikeUrl = new(
        @"^[^@\s]+@(?<host>[^:\s]+):(?<path>.+)$", RegexOptions.Compiled);

    private static readonly Lazy<GitHubAppTokenProvider> SharedInstance = new(() =>
        new GitHubAppTokenProvider(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));

    /// <summary>Shared instance with one reused <see cref="HttpClient"/>.</summary>
    public static GitHubAppTokenProvider Shared => SharedInstance.Value;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public GitHubAppTokenProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Creates a token for the one repository at <paramref name="repositoryUrl"/>.</summary>
    public async Task<GitHubInstallationToken> MintForRepositoryAsync(
        GitHubAppCredentials credentials,
        string repositoryUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (!TryParseGitHubRepository(repositoryUrl, out var owner, out var repo))
            throw new GitHubAppTokenException(
                $"'{repositoryUrl}' is not a github.com repository URL; GitHub App tokens can only be minted for github.com repositories.");

        var jwt = CreateJwt(credentials.AppId, credentials.PrivateKeyPem, _time.GetUtcNow());

        var installationId = await GetInstallationIdAsync(jwt, owner, repo, cancellationToken)
            .ConfigureAwait(false);

        return await CreateAccessTokenAsync(jwt, installationId, owner, repo, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<long> GetInstallationIdAsync(
        string jwt, string owner, string repo, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/installation", jwt);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubAppTokenException(
                $"The GitHub App is not installed on '{owner}/{repo}' (or the repository does not exist). " +
                "Install the App on the repository's owner and grant it access to this repository.");

        await EnsureSuccessAsync(response, $"look up the App installation for '{owner}/{repo}'", ct)
            .ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var installationId))
            return installationId;

        throw new GitHubAppTokenException(
            $"GitHub returned an installation for '{owner}/{repo}' without an id.");
    }

    private async Task<GitHubInstallationToken> CreateAccessTokenAsync(
        string jwt, long installationId, string owner, string repo, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt);
        // Limit the token to this repository; permissions are the App's own.
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { repositories = new[] { repo } }),
            Encoding.UTF8,
            "application/json");

        using var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            throw new GitHubAppTokenException(
                $"The GitHub App installation for '{owner}' does not include repository '{repo}'. " +
                "Grant the App access to this repository (Installation → Repository access).");

        await EnsureSuccessAsync(response, $"create an installation token for '{owner}/{repo}'", ct)
            .ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
            throw new GitHubAppTokenException(
                $"GitHub returned no token for the installation covering '{owner}/{repo}'.");

        var expiresAt = root.TryGetProperty("expires_at", out var e)
                        && e.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(e.GetString(), out var parsed)
            ? parsed
            : _time.GetUtcNow() + TokenLifetime;

        return new GitHubInstallationToken(token, expiresAt, installationId);
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string relativePath, string jwt)
    {
        var request = new HttpRequestMessage(method, new Uri(ApiBase, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("xianix-agent", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new GitHubAppTokenException($"Could not reach the GitHub API: {ex.Message}", ex, isTransient: true);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await ReadGitHubMessageAsync(response, ct).ConfigureAwait(false);
        var hint = response.StatusCode == HttpStatusCode.Unauthorized
            ? $" Check the App ID and private key in {TokenEnvName} in the tenant Secret Vault, and that the agent host clock is correct."
            : string.Empty;

        var status = (int)response.StatusCode;
        var transient = status >= 500 || status == 429
            || (status == 403 && detail?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true);

        throw new GitHubAppTokenException(
            $"GitHub API refused to {action} (HTTP {status}{(detail is null ? "" : $": {detail}")}).{hint}",
            isTransient: transient);
    }

    private static async Task<string?> ReadGitHubMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Builds the signed App JWT that GitHub expects.</summary>
    internal static string CreateJwt(string appId, string privateKeyPem, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new GitHubAppTokenException($"{AppIdPart} is empty. {ExpectedFormat}");

        using var rsa = ImportPrivateKey(privateKeyPem);

        var header  = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = (now - JwtClockSkew).ToUnixTimeSeconds(),
            exp = (now + JwtLifetime).ToUnixTimeSeconds(),
            iss = appId.Trim(),
        }));

        var signingInput = $"{header}.{payload}";
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static RSA ImportPrivateKey(string privateKeyPem)
    {
        var pem = NormalizePrivateKey(privateKeyPem);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new GitHubAppTokenException(
                $"{KeyPart} is not a valid PEM-encoded RSA private key.", ex);
        }
    }

    /// <summary>True when the value is App credentials: PATs never contain a comma.</summary>
    internal static bool IsAppCredentials(string? value) =>
        value is not null && value.Contains(',', StringComparison.Ordinal);

    /// <summary>
    /// Splits <c>&lt;app-id&gt;,&lt;private-key&gt;</c> at the first comma and checks both parts.
    /// A key never contains a comma.
    /// </summary>
    internal static GitHubAppCredentials ParseAppCredentials(string value)
    {
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
            throw new GitHubAppTokenException($"{TokenEnvName} does not contain GitHub App credentials. {ExpectedFormat}");

        var appId = value[..comma].Trim();
        var key   = value[(comma + 1)..].Trim();

        if (appId.Length == 0)
            throw new GitHubAppTokenException($"{AppIdPart} is empty. {ExpectedFormat}");
        if (!AppIdPattern.IsMatch(appId))
            throw new GitHubAppTokenException(
                $"{AppIdPart} must be the numeric App ID or the Client ID (Iv…). {ExpectedFormat}");

        // Fail on a bad key before calling GitHub.
        NormalizePrivateKey(key);
        return new GitHubAppCredentials(appId, key);
    }

    /// <summary>Accepts the key as PEM, PEM with literal <c>\n</c>, or the PEM file base64-encoded.</summary>
    internal static string NormalizePrivateKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new GitHubAppTokenException($"{KeyPart} is empty. {ExpectedFormat}");

        var value = raw.Trim();

        if (!value.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            try
            {
                value = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Trim();
            }
            catch (FormatException)
            {
                throw new GitHubAppTokenException(
                    $"{KeyPart} must be the PEM private key downloaded from GitHub (or that file base64-encoded).");
            }
        }

        return value.Replace("\\r\\n", "\n").Replace("\\n", "\n");
    }

    /// <summary>Gets owner and repo from a github.com HTTPS or SSH URL.</summary>
    internal static bool TryParseGitHubRepository(string? url, out string owner, out string repo)
    {
        owner = repo = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmed = url.Trim();
        string host, path;

        var scp = ScpLikeUrl.Match(trimmed);
        if (!trimmed.Contains("://", StringComparison.Ordinal) && scp.Success)
        {
            host = scp.Groups["host"].Value;
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                 && uri.Scheme is "https" or "http" or "ssh")
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            return false;
        }

        if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        if (segments[0].Length == 0 || name.Length == 0)
            return false;

        owner = Uri.UnescapeDataString(segments[0]);
        repo  = Uri.UnescapeDataString(name);
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

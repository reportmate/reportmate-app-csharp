// Explicit usings: this file is also compiled into the test project, which does not
// enable implicit usings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReportMate.App.Services;

/// <summary>Where the API address in use came from.</summary>
public enum ApiUrlSource
{
    None,

    /// <summary>An app that embeds the dashboard supplied it.</summary>
    Host,

    /// <summary>The runner's own configuration, <c>HKLM\SOFTWARE\ReportMate</c>.</summary>
    Client,

    /// <summary>Typed into this app's Settings.</summary>
    Settings,

    /// <summary><c>REPORTMATE_URL</c>.</summary>
    Environment,

    /// <summary>Intune CSP or Group Policy, <c>HKLM\SOFTWARE\Policies\ReportMate</c>.</summary>
    Policy,
}

/// <summary>What <c>/api/v1/auth/config</c> says a deployment accepts. Public, no secrets.</summary>
public sealed record AuthConfig(bool OidcEnabled, string? Audience, IReadOnlyList<string> Issuers)
{
    /// <summary>The API's description, or null when the body is not one.</summary>
    public static AuthConfig? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("oidc", out var oidc)
                || oidc.ValueKind != JsonValueKind.Object) return null;

            var enabled = oidc.TryGetProperty("enabled", out var e)
                && (e.ValueKind == JsonValueKind.True
                    || (e.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            var audience = oidc.TryGetProperty("audience", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()?.Trim()
                : null;
            var issuers = oidc.TryGetProperty("issuers", out var i) && i.ValueKind == JsonValueKind.Array
                ? i.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();
            return new AuthConfig(enabled, string.IsNullOrEmpty(audience) ? null : audience, issuers);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The rules for signing in to the API with Entra: a token minted from the operator's
/// own <c>az login</c> and held in memory, so no secret is ever stored on the device.
/// A port of the Mac app's <c>AppConfiguration.resolvingEntra</c>.
/// </summary>
public static class EntraSignIn
{
    /// <summary>
    /// Only an address an administrator set may hand the app an audience silently:
    /// the runner's configuration and policy both live under HKLM, and the
    /// environment belongs to whoever launched the process. An address typed into
    /// Settings or supplied by a host does not.
    /// </summary>
    public static bool IsAdministratorSet(ApiUrlSource source) =>
        source is ApiUrlSource.Client or ApiUrlSource.Policy or ApiUrlSource.Environment;

    /// <summary>
    /// Discovery hands the app an audience it then mints a token for, so it runs
    /// over https only (plain http only to this machine, for development).
    /// </summary>
    public static bool IsTrustedDiscoveryUrl(string? apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl) || !Uri.TryCreate(apiUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.ToLowerInvariant() switch
        {
            "https" => true,
            "http" => uri.IsLoopback,
            _ => false,
        };
    }

    /// <summary>Whether the API at this address may be asked for its audience.</summary>
    public static bool MayDiscover(ApiUrlSource source, string? apiUrl) =>
        IsAdministratorSet(source) && IsTrustedDiscoveryUrl(apiUrl);

    // An application ID URI is api:// and then a host, a GUID or a path. The
    // character set is kept narrow on purpose: the value ends up on a command line.
    private static readonly Regex ApiUri = new(@"^api://[A-Za-z0-9][A-Za-z0-9._\-/:]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Guid = new(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// An advertised audience must be an <c>api://</c> application ID URI. A bare
    /// application ID could name Microsoft Graph or another first-party app, and a
    /// URL (graph.microsoft.com, management.azure.com, a vault) would have the app
    /// mint a token for that resource and send it to the server that asked for it.
    /// </summary>
    public static bool IsAcceptableAudience(string? audience) =>
        !string.IsNullOrWhiteSpace(audience) && ApiUri.IsMatch(audience.Trim());

    /// <summary>
    /// An audience an administrator configured explicitly: an <c>api://</c> URI or a
    /// bare application ID. Anything else is refused rather than passed to <c>az</c>.
    /// </summary>
    public static bool IsSafeResource(string? resource) =>
        !string.IsNullOrWhiteSpace(resource) && (ApiUri.IsMatch(resource.Trim()) || Guid.IsMatch(resource.Trim()));

    /// <summary>The discovery endpoint for an API address, with or without its /api/v1.</summary>
    public static string AuthConfigUrl(string apiUrl)
    {
        var trimmed = apiUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) return trimmed + "/auth/config";
        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) return trimmed + "/v1/auth/config";
        return trimmed + "/api/v1/auth/config";
    }

    /// <summary>
    /// The audience to sign in for: one an administrator configured, or, for an
    /// administrator-set https address, the <c>api://</c> audience the API advertises
    /// when it says OIDC is on. Null when Entra is not available here.
    /// </summary>
    public static string? ChooseAudience(string? configured, ApiUrlSource source, string? apiUrl, AuthConfig? advertised)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return IsSafeResource(configured) ? configured.Trim() : null;
        if (!MayDiscover(source, apiUrl) || advertised is not { OidcEnabled: true, Audience: { } audience }) return null;
        return IsAcceptableAudience(audience) ? audience.Trim() : null;
    }
}

/// <summary>
/// The Entra bearer for the fleet reads: works out the audience once per API address
/// and keeps the token <c>az</c> mints in memory only. Nothing is written anywhere.
/// </summary>
public sealed class EntraSession
{
    public static EntraSession Shared { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // A token lives about an hour; refreshing at fifty minutes keeps a request from
    // going out with one that expires in flight.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50);

    // After a failure the app falls back to the stored credential; trying az again
    // on every request would add seconds to each one.
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (AuthConfig? Config, DateTime At)> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Token, DateTime Expires)> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _failedAt = new(StringComparer.Ordinal);

    /// <summary>Mints a token for a resource; <c>az account get-access-token</c> by default.</summary>
    public Func<string, CancellationToken, Task<string?>> Mint { get; set; } = AzCli.GetAccessTokenAsync;

    /// <summary>Reads an API's auth description; <c>GET /api/v1/auth/config</c> by default.</summary>
    public Func<string, CancellationToken, Task<AuthConfig?>> Discover { get; set; } = DiscoverAsync;

    /// <summary>The audience the last successful sign-in was for, for Settings to show.</summary>
    public string? SignedInAudience { get; private set; }

    private readonly object _sync = new();

    /// <summary>The audience in play for this connection, or null when Entra is not available.</summary>
    public async Task<string?> AudienceAsync(string? configured, ApiUrlSource source, string? apiUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configured) || !EntraSignIn.MayDiscover(source, apiUrl))
            return EntraSignIn.ChooseAudience(configured, source, apiUrl, null);

        var key = apiUrl!.Trim().TrimEnd('/');
        if (CachedDiscovery(key, out var advertised))
            return EntraSignIn.ChooseAudience(null, source, apiUrl, advertised);

        await _gate.WaitAsync(ct);
        try
        {
            if (!CachedDiscovery(key, out advertised))
            {
                advertised = await SafeAsync(() => Discover(EntraSignIn.AuthConfigUrl(key), ct));
                lock (_sync) _discovered[key] = (advertised, DateTime.UtcNow);
            }
        }
        finally
        {
            _gate.Release();
        }
        return EntraSignIn.ChooseAudience(null, source, apiUrl, advertised);
    }

    private bool CachedDiscovery(string key, out AuthConfig? advertised)
    {
        lock (_sync)
        {
            if (_discovered.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < DiscoveryLifetime)
            {
                advertised = hit.Config;
                return true;
            }
        }
        advertised = null;
        return false;
    }

    /// <summary>
    /// A bearer token for this connection, or null when Entra is not available or
    /// <c>az</c> cannot mint one (not installed, not signed in, no access).
    /// </summary>
    public async Task<string?> TokenAsync(string? configured, ApiUrlSource source, string? apiUrl, CancellationToken ct)
    {
        var audience = await AudienceAsync(configured, source, apiUrl, ct);
        if (audience is null) return null;
        if (Cached(audience, out var hit)) return hit;

        await _gate.WaitAsync(ct);
        try
        {
            if (Cached(audience, out hit)) return hit;
            lock (_sync)
                if (_failedAt.TryGetValue(audience, out var failed) && DateTime.UtcNow - failed < RetryAfterFailure) return null;

            var token = (await SafeAsync(() => Mint(audience, ct)))?.Trim();
            lock (_sync)
            {
                if (string.IsNullOrEmpty(token))
                {
                    _failedAt[audience] = DateTime.UtcNow;
                    return null;
                }
                _failedAt.Remove(audience);
                _tokens[audience] = (token, DateTime.UtcNow + TokenLifetime);
                SignedInAudience = audience;
                return token;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool Cached(string audience, out string? token)
    {
        lock (_sync)
        {
            if (_tokens.TryGetValue(audience, out var cached) && DateTime.UtcNow < cached.Expires)
            {
                token = cached.Token;
                return true;
            }
        }
        token = null;
        return false;
    }

    /// <summary>Drop every token, after the API refused one, so the next read mints afresh.</summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _tokens.Clear();
            SignedInAudience = null;
        }
    }

    /// <summary>Forget everything, after the connection settings change.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _tokens.Clear();
            _discovered.Clear();
            _failedAt.Clear();
            SignedInAudience = null;
        }
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T?>> action) where T : class
    {
        try { return await action(); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<AuthConfig?> DiscoverAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return AuthConfig.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>The Azure CLI, run as a child process with no window.</summary>
public static class AzCli
{
    /// <summary>
    /// <c>az account get-access-token --resource &lt;resource&gt;</c>, or null when az is
    /// missing, not signed in, or refuses. The resource is checked against
    /// <see cref="EntraSignIn.IsSafeResource"/> first, because on Windows az is a
    /// batch file and runs through cmd.
    /// </summary>
    public static async Task<string?> GetAccessTokenAsync(string resource, CancellationToken ct)
    {
        if (!EntraSignIn.IsSafeResource(resource)) return null;
        resource = resource.Trim();

        var args = $"account get-access-token --resource {resource} --query accessToken -o tsv";
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{Locate()} {args}\"")
            : new ProcessStartInfo(Locate(), args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi);
        if (process is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return null;
        }

        if (process.ExitCode != 0) return null;
        var token = (await stdout).Trim();
        _ = await stderr;
        return token.Length == 0 || token.Contains(' ') ? null : token;
    }

    /// <summary>
    /// The az the MSI installs, quoted for cmd, when it is where the MSI puts it;
    /// otherwise whatever az is on the path.
    /// </summary>
    private static string Locate()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var candidate in new[] { "/opt/homebrew/bin/az", "/usr/local/bin/az", "/usr/bin/az" })
                if (File.Exists(candidate)) return candidate;
            return "az";
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd");
            if (File.Exists(path)) return $"\"{path}\"";
        }
        return "az";
    }
}

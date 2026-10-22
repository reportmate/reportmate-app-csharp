namespace ReportMate.App.Services;

/// <summary>
/// All configuration settings for the ReportMate client.
/// Mirrors ReportMateClientConfiguration from the CLI project.
/// </summary>
public sealed class ReportMateConfig
{
    // Connection
    public string ApiUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? Passphrase { get; set; }

    /// <summary>
    /// A read-scoped API key for the fleet pages. Separate from ApiKey, which is the
    /// endpoint's ingest credential and is rejected by the read endpoints.
    /// </summary>
    public string? ReadApiKey { get; set; }

    /// <summary>
    /// The Entra audience (an <c>api://</c> URI or application ID) to sign in for. Left
    /// empty, the app asks the API at an administrator-set address for the one it
    /// advertises. Not a secret: it names the API, and the token is minted by az.
    /// </summary>
    public string? OidcAudience { get; set; }

    /// <summary>Which layer the API address came from; decides whether its audience is trusted.</summary>
    public ApiUrlSource ApiUrlSource { get; set; } = ApiUrlSource.None;

    /// <summary>
    /// The web dashboard's base URL. The only thing a shareable link needs that the app
    /// cannot work out for itself, so it is configured rather than hardcoded per
    /// deployment. Falls back to the API host with its api. prefix dropped.
    /// </summary>
    public string? WebUrl { get; set; }
    public string? DeviceId { get; set; }

    // Collection
    public int CollectionIntervalSeconds { get; set; } = ReportMateConstants.DefaultCollectionInterval;
    public int MaxDataAgeMinutes { get; set; } = ReportMateConstants.DefaultMaxDataAge;
    public int ApiTimeoutSeconds { get; set; } = ReportMateConstants.DefaultApiTimeout;
    public string OsQueryPath { get; set; } = ReportMateConstants.DefaultOsQueryPath;
    public string StorageMode { get; set; } = ReportMateConstants.DefaultStorageMode;

    // Behavior
    public bool DebugLogging { get; set; }
    public bool CimianIntegrationEnabled { get; set; } = true;
    public bool SkipCertificateValidation { get; set; }
    public int MaxRetryAttempts { get; set; } = ReportMateConstants.DefaultMaxRetryAttempts;

    // Advanced
    public string UserAgent { get; set; } = ReportMateConstants.DefaultUserAgent;
    public string? ProxyUrl { get; set; }

    public ReportMateConfig Clone() => (ReportMateConfig)MemberwiseClone();
}

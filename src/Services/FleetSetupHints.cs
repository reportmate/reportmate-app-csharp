namespace ReportMate.App.Services;

/// <summary>What a fleet page tells the reader to set when it cannot read for want of configuration.</summary>
public static class FleetSetupHints
{
    /// <summary>
    /// Set by an app that embeds the dashboard and supplies its connection. Given
    /// whether an API URL is known, it returns the setting to fill in that app, or
    /// null to fall back to this app's own guidance.
    /// </summary>
    public static Func<bool, string?>? HostHint { get; set; }

    /// <summary>
    /// The missing setting, by the name it has in Settings. <paramref name="hostSignIn"/>
    /// is true when an embedding app offers sign-in tokens but returned none.
    /// </summary>
    /// <param name="entraAudience">
    /// The audience this API signs in with, when it has one: then the missing piece is
    /// usually an <c>az login</c> on this machine rather than a stored credential.
    /// </param>
    public static string MissingSetting(bool hasApiUrl, bool hostSignIn, string? entraAudience = null)
    {
        // An embedding app that supplies the connection knows which of its own
        // settings is missing; its words beat a pointer at settings it overrides.
        if (HostHint?.Invoke(hasApiUrl) is { Length: > 0 } hostHint)
            return hostHint;
        if (!hasApiUrl)
            return "Set the API URL in Settings › General › Connection.";
        if (entraAudience is not null && !hostSignIn)
            return "This API signs in with Entra, and az could not get a token for it. "
                + "Run `az login` with an account that can read the fleet, then refresh, "
                + "or set the Read passphrase in Settings › General › Connection.";
        return hostSignIn
            ? "The app hosting this dashboard returned no sign-in token, and no read passphrase is set. "
              + "Configure the host app's sign-in for this API, or set the Read passphrase in Settings › General › Connection."
            : "Set the Read passphrase in Settings › General › Connection. "
              + "The runner's API key can report data in but cannot read the fleet back out.";
    }
}

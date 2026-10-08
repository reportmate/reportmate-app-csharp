namespace ReportMate.App.Services;

/// <summary>What a fleet page tells the reader to set when it cannot read for want of configuration.</summary>
public static class FleetSetupHints
{
    /// <summary>
    /// The missing setting, by the name it has in Settings. <paramref name="hostSignIn"/>
    /// is true when an embedding app offers sign-in tokens but returned none.
    /// </summary>
    public static string MissingSetting(bool hasApiUrl, bool hostSignIn)
    {
        if (!hasApiUrl)
            return "Set the API URL in Settings › General › Connection.";
        return hostSignIn
            ? "The app hosting this dashboard returned no sign-in token, and no read passphrase is set. "
              + "Configure the host app's sign-in for this API, or set the Read passphrase in Settings › General › Connection."
            : "Set the Read passphrase in Settings › General › Connection. "
              + "The runner's API key can report data in but cannot read the fleet back out.";
    }
}

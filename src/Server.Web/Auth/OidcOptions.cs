namespace JMW.Discovery.Server.Auth;

/// <summary>
/// OIDC SSO configuration, read from environment variables (JMW_OIDC_AUTHORITY,
/// JMW_OIDC_CLIENT_ID, JMW_OIDC_CLIENT_SECRET, JMW_OIDC_CALLBACK_PATH) — mirrors the pattern
/// used for other deploy-time-only config (e.g. ReleaseManager/JMW_RELEASES_DIR). SSO is either
/// fully configured at deploy time or fully absent; there's no partial/DB-editable state.
/// </summary>
public sealed class OidcOptions
{
    public string? Authority { get; }
    public string? ClientId { get; }
    public string? ClientSecret { get; }
    public string CallbackPath { get; }

    /// <summary>True only when Authority, ClientId, and ClientSecret are all set.</summary>
    public bool Enabled => !string.IsNullOrEmpty(Authority)
     && !string.IsNullOrEmpty(ClientId)
     && !string.IsNullOrEmpty(ClientSecret);

    public OidcOptions(string? authority, string? clientId, string? clientSecret, string? callbackPath)
    {
        Authority = authority;
        ClientId = clientId;
        ClientSecret = clientSecret;
        CallbackPath = string.IsNullOrEmpty(callbackPath) ? "/signin-oidc" : callbackPath;
    }
}
using System.Security.Cryptography;

namespace JMW.Discovery.Server.Auth;

/// <summary>
/// A one-time, process-lifetime secret an operator must copy from the server console/logs into
/// the bootstrap form. Without this, whoever reaches <c>/bootstrap</c> first — any host on the
/// LAN, since the server is reachable before setup completes — could claim the admin account.
/// Generated eagerly (not gated on whether an admin already exists) so validation never races
/// <see cref="BootstrapService" />, which only decides whether to print it.
/// </summary>
public sealed class BootstrapSetupToken
{
    public string Value { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
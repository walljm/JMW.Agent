using JMW.Discovery.Server.Auth;

namespace JMW.Discovery.UnitTests.Server;

public sealed class OidcOptionsTests
{
    [Fact]
    public void Enabled_TrueOnlyWhenAllThreeRequiredValuesArePresent()
    {
        Assert.True(new OidcOptions("https://idp.example.com", "client-id", "secret", null).Enabled);
        Assert.False(new OidcOptions(null, "client-id", "secret", null).Enabled);
        Assert.False(new OidcOptions("https://idp.example.com", null, "secret", null).Enabled);
        Assert.False(new OidcOptions("https://idp.example.com", "client-id", null, null).Enabled);
        Assert.False(new OidcOptions(null, null, null, null).Enabled);
    }

    [Fact]
    public void CallbackPath_DefaultsToSigninOidc_WhenNotProvided()
    {
        OidcOptions options = new("https://idp.example.com", "client-id", "secret", null);
        Assert.Equal("/signin-oidc", options.CallbackPath);
    }

    [Fact]
    public void CallbackPath_UsesProvidedValue_WhenSet()
    {
        OidcOptions options = new("https://idp.example.com", "client-id", "secret", "/custom-callback");
        Assert.Equal("/custom-callback", options.CallbackPath);
    }
}
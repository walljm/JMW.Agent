using JMW.Discovery.Server.Auth;

namespace JMW.Discovery.Tests;

public sealed class BootstrapSetupTokenTests
{
    [Fact]
    public void Value_IsNonEmptyLowercaseHex()
    {
        string value = new BootstrapSetupToken().Value;
        Assert.NotEmpty(value);
        Assert.Matches("^[0-9a-f]+$", value);
    }

    [Fact]
    public void Value_DiffersAcrossInstances()
    {
        string a = new BootstrapSetupToken().Value;
        string b = new BootstrapSetupToken().Value;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Value_IsStableForTheLifetimeOfTheInstance()
    {
        BootstrapSetupToken token = new();
        Assert.Equal(token.Value, token.Value);
    }
}

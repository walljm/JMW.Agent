using JMW.Discovery.Server.Agents;

namespace JMW.Discovery.Tests;

public sealed class ApiKeyServiceTests
{
    private readonly ApiKeyService _svc;

    public ApiKeyServiceTests()
    {
        Environment.SetEnvironmentVariable("JMW_API_KEY_SECRET", "test-hmac-secret-key-that-is-long-enough");
        _svc = new ApiKeyService();
    }

    [Fact]
    public void Generate_ProducesUniqueKeys()
    {
        string k1 = _svc.Generate();
        string k2 = _svc.Generate();
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void Generate_ProducesLowercaseHex()
    {
        string key = _svc.Generate();
        Assert.Matches("^[0-9a-f]+$", key);
    }

    [Fact]
    public void Hash_IsDeterministic()
    {
        string h1 = _svc.Hash("somekey");
        string h2 = _svc.Hash("somekey");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void Verify_ReturnsTrueForMatchingKey()
    {
        string key = _svc.Generate();
        string hash = _svc.Hash(key);
        Assert.True(_svc.Verify(key, hash));
    }

    [Fact]
    public void Verify_ReturnsFalseForWrongKey()
    {
        string key = _svc.Generate();
        string otherKey = _svc.Generate();
        string hash = _svc.Hash(key);
        Assert.False(_svc.Verify(otherKey, hash));
    }

    [Fact]
    public void Verify_ReturnsFalseForEmptyKey()
    {
        string hash = _svc.Hash("realkey");
        Assert.False(_svc.Verify("", hash));
    }

    // security-03: docker-compose.yml ships this exact value; anyone who has read the repo knows
    // it, so it must never be accepted as real HMAC keying material.
    [Fact]
    public void Constructor_ThrowsForPublishedExampleSecret()
    {
        Environment.SetEnvironmentVariable("JMW_API_KEY_SECRET", "dev-api-key-secret-change-in-production");
        Assert.Throws<InvalidOperationException>(() => new ApiKeyService());
    }

    [Fact]
    public void Constructor_ThrowsForShortSecret()
    {
        Environment.SetEnvironmentVariable("JMW_API_KEY_SECRET", "short");
        Assert.Throws<InvalidOperationException>(() => new ApiKeyService());
    }

    [Fact]
    public void Constructor_ThrowsForSecretOneByteUnder32()
    {
        Environment.SetEnvironmentVariable("JMW_API_KEY_SECRET", new string('a', 31));
        Assert.Throws<InvalidOperationException>(() => new ApiKeyService());
    }

    [Fact]
    public void Constructor_AcceptsSecretAtExactly32Bytes()
    {
        Environment.SetEnvironmentVariable("JMW_API_KEY_SECRET", new string('a', 32));
        ApiKeyService svc = new();
        Assert.NotEmpty(svc.Generate());
    }
}
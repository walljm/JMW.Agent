using JMW.Discovery.Server.Auth;

namespace JMW.Discovery.Tests;

public sealed class PasswordServiceTests
{
    private readonly PasswordService _svc = new();

    [Fact]
    public void Hash_ProducesDifferentHashesForSamePassword()
    {
        string hash1 = _svc.Hash("hunter2");
        string hash2 = _svc.Hash("hunter2");
        Assert.NotEqual(hash1, hash2); // different salts
    }

    [Fact]
    public void Verify_ReturnsTrueForCorrectPassword()
    {
        string hash = _svc.Hash("correct-horse-battery-staple");
        Assert.True(_svc.Verify("correct-horse-battery-staple", hash));
    }

    [Fact]
    public void Verify_ReturnsFalseForWrongPassword()
    {
        string hash = _svc.Hash("correct-horse");
        Assert.False(_svc.Verify("wrong-horse", hash));
    }

    [Fact]
    public void Verify_ReturnsFalseForEmptyPassword()
    {
        string hash = _svc.Hash("non-empty");
        Assert.False(_svc.Verify("", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("notahash")]
    [InlineData("onlyone:part")]
    [InlineData("bad===base64:also===bad")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$0$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$600000$onlythreeparts")]
    public void Verify_ReturnsFalseForMalformedStoredHash(string storedHash)
    {
        Assert.False(_svc.Verify("any-password", storedHash));
    }

    [Fact]
    public void Hash_OutputUsesPrefixedFourPartFormat()
    {
        string hash = _svc.Hash("test");
        string[] parts = hash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal(600_000, int.Parse(parts[1]));
        Assert.NotEmpty(parts[2]);
        Assert.NotEmpty(parts[3]);
    }

    [Fact]
    public void Verify_AcceptsLegacyColonFormat()
    {
        // Pre-existing accounts stored "base64(salt):base64(hash)" at 100k iterations before the
        // work factor was raised; those hashes must keep verifying without a forced reset.
        byte[] salt = Convert.FromBase64String("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        byte[] hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes("legacy-password"),
            salt,
            100_000,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            32
        );
        string legacyStoredHash = Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);

        Assert.True(_svc.Verify("legacy-password", legacyStoredHash));
        Assert.False(_svc.Verify("wrong-password", legacyStoredHash));
    }

    [Fact]
    public void NeedsRehash_TrueForLegacyFormat()
    {
        Assert.True(_svc.NeedsRehash("c2FsdHNhbHRzYWx0c2FsdHNhbHRzYWx0c2FsdHNhbHQ=:aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g="));
    }

    [Fact]
    public void NeedsRehash_FalseForCurrentFormat()
    {
        Assert.False(_svc.NeedsRehash(_svc.Hash("test")));
    }

    [Fact]
    public void NeedsRehash_TrueForLowerIterationCurrentFormat()
    {
        Assert.True(_svc.NeedsRehash("pbkdf2-sha256$100000$c2FsdA==$aGFzaA=="));
    }
}
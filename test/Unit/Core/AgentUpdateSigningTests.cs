using System.Security.Cryptography;

using JMW.Discovery.Core;

namespace JMW.Discovery.Tests;

public sealed class AgentUpdateSigningTests
{
    private const string Version = "v1.2.3";
    private const string Filename = "jmw-agent-linux-x64";
    private const string Sha256 = "1665c5dfcfcaf0c357a021f0d53e8f76e0edacb354b7c9af795136c7bc80a0a5";
    private const long Size = 21;

    [Fact]
    public void Verify_AcceptsASignatureFromTheMatchingPrivateKey()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = AgentUpdateSigning.Sign(key, Version, Filename, Sha256, Size);

        Assert.True(AgentUpdateSigning.Verify(key, Version, Filename, Sha256, Size, signature));
    }

    [Fact]
    public void Verify_RejectsASignatureFromADifferentKey()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = AgentUpdateSigning.Sign(signingKey, Version, Filename, Sha256, Size);

        Assert.False(AgentUpdateSigning.Verify(otherKey, Version, Filename, Sha256, Size, signature));
    }

    [Theory]
    [InlineData("v9.9.9", Filename, Sha256, Size)] // tampered version
    [InlineData(Version, "jmw-agent-linux-arm64", Sha256, Size)] // tampered filename
    [InlineData(Version, Filename, "0000000000000000000000000000000000000000000000000000000000000", Size)] // tampered hash
    [InlineData(Version, Filename, Sha256, 999L)] // tampered size
    public void Verify_RejectsAnyTamperedField(string version, string filename, string sha256, long size)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = AgentUpdateSigning.Sign(key, Version, Filename, Sha256, Size);

        Assert.False(AgentUpdateSigning.Verify(key, version, filename, sha256, size, signature));
    }

    [Fact]
    public void BuildCanonicalString_MatchesTheDocumentedFormat()
    {
        string canonical = AgentUpdateSigning.BuildCanonicalString(Version, Filename, Sha256, Size);

        Assert.Equal($"version={Version}\nfilename={Filename}\nsha256={Sha256}\nsize={Size}\n", canonical);
    }
}
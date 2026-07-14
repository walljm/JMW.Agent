using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using JMW.Discovery.Agent;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// CaTrust decides whether a validating collector accepts a TLS certificate. The rules that
/// matter for security: system-trusted certs pass; certs chaining to a configured private CA
/// pass; hostname mismatches stay fatal even for configured CAs; and with no CA configured
/// the agent falls back to system trust only. These build throwaway CA + leaf certs to pin
/// that behavior. CaTrust holds process-wide state, so each test sets it explicitly.
/// </summary>
public sealed class CaTrustTests
{
    private static (X509Certificate2 ca, X509Certificate2 leaf) MakeCaAndLeaf(string leafCn = "CN=ha.home")
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest caReq = new("CN=Test Home CA", caKey, HashAlgorithmName.SHA256);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true)
        );
        X509Certificate2 ca = caReq.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        using ECDsa leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest leafReq = new(leafCn, leafKey, HashAlgorithmName.SHA256);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        X509Certificate2 leaf = leafReq.Create(
            ca,
            now.AddDays(-1),
            now.AddYears(1),
            [1, 2, 3, 4, 5, 6, 7, 8]
        );

        return (ca, leaf);
    }

    [Fact]
    public void Validate_SystemTrustSucceeds_AcceptsRegardlessOfConfiguredCas()
    {
        CaTrust.Update(null);
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeCaAndLeaf();
        using (ca)
        using (leaf)
        {
            Assert.True(CaTrust.Validate(this, leaf, chain: null, SslPolicyErrors.None));
        }
    }

    [Fact]
    public void Validate_LeafChainsToConfiguredCa_Accepts()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeCaAndLeaf();
        using (ca)
        using (leaf)
        {
            CaTrust.Update([ca.ExportCertificatePem()]);

            Assert.True(CaTrust.Validate(this, leaf, chain: null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
    }

    [Fact]
    public void Validate_NoConfiguredCa_RejectsUntrustedChain()
    {
        CaTrust.Update(null);
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeCaAndLeaf();
        using (ca)
        using (leaf)
        {
            Assert.False(CaTrust.Validate(this, leaf, chain: null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
    }

    [Fact]
    public void Validate_NameMismatch_StaysFatalEvenForConfiguredCa()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeCaAndLeaf();
        using (ca)
        using (leaf)
        {
            CaTrust.Update([ca.ExportCertificatePem()]);

            // A hostname mismatch must never be overridden by CA trust.
            Assert.False(
                CaTrust.Validate(
                    this,
                    leaf,
                    chain: null,
                    SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch
                )
            );
        }
    }

    [Fact]
    public void Validate_LeafSignedByDifferentCa_Rejects()
    {
        (X509Certificate2 configuredCa, X509Certificate2 _) = MakeCaAndLeaf();
        (X509Certificate2 otherCa, X509Certificate2 otherLeaf) = MakeCaAndLeaf();
        using (configuredCa)
        using (otherCa)
        using (otherLeaf)
        {
            CaTrust.Update([configuredCa.ExportCertificatePem()]);

            // otherLeaf chains to otherCa, which is NOT the configured trust anchor.
            Assert.False(
                CaTrust.Validate(
                    this,
                    otherLeaf,
                    chain: null,
                    SslPolicyErrors.RemoteCertificateChainErrors
                )
            );
        }
    }

    [Fact]
    public void Update_MalformedPem_IsSkippedAndDoesNotThrow()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeCaAndLeaf();
        using (ca)
        using (leaf)
        {
            // A malformed entry must not blow up Update, and the valid one still takes effect.
            CaTrust.Update(["not a pem", "", ca.ExportCertificatePem()]);

            Assert.True(CaTrust.Validate(this, leaf, chain: null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
    }
}
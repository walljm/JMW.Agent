namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// The ECDSA P-256 public key used to verify binary self-updates.
/// Value is a base64-encoded DER SubjectPublicKeyInfo blob.
/// This is the trust anchor for updates — it must be baked into the binary,
/// not loaded from a config file or environment variable. A config-file key
/// can be replaced by an attacker with filesystem access; a baked-in key
/// cannot be changed without shipping a new signed binary.
/// Key rotation: generate a new keypair, set Value to the new public key,
/// sign all future release artifacts with the new private key, and ship the
/// binary. The new binary will only accept artifacts signed by the new key.
/// Generating a key (OpenSSL):
/// openssl ecparam -name prime256v1 -genkey -noout -out update-signing.pem
/// openssl ec -in update-signing.pem -pubout -outform DER | base64 -w 0 > update-public.b64
/// # update-signing.pem = private key for your signing tool
/// # update-public.b64  = paste below
/// </summary>
public static class UpdatePublicKey
{
    // PLACEHOLDER — replace with your real key before deploying.
    // Build will succeed; updates will fail at runtime until this is set.
    public const string Value = "";
}
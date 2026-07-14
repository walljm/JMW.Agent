using System.Security.Cryptography;

using JMW.Discovery.Core;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    try
    {
        return args[0] switch
        {
            "generate-key" => GenerateKey(args[1..]),
            "public-key" => PrintPublicKey(args[1..]),
            "sign" => Sign(args[1..]),
            "-h" or "--help" => Usage(),
            _ => Unknown(args[0]),
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

static int Usage()
{
    PrintUsage();
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        """
        Signs agent release binaries for the self-update mechanism (see
        src/Agent/Collection/Updater.cs / UpdatePublicKey.cs).

        Usage:
          updatesign generate-key --out <private-key.pem>
              Generates a new ECDSA P-256 keypair, writes the private key PEM to
              --out, and prints the public key (base64 DER SubjectPublicKeyInfo —
              paste into UpdatePublicKey.Value) to stdout.

          updatesign public-key --key <private-key.pem>
              Prints the base64 SubjectPublicKeyInfo for an existing private key,
              without generating a new one.

          updatesign sign --key <private-key.pem> --version vX.Y.Z <binary...>
              Signs one or more release binaries. For each <binary>, writes
              <binary>.sig containing the base64 ECDSA signature the agent's
              Updater verifies. Filenames must already follow the
              jmw-agent-<os>-<arch>[.exe] convention ReleaseManager expects.
        """
    );
}

static int GenerateKey(string[] args)
{
    string? outPath = GetOption(args, "--out");
    if (outPath is null)
    {
        Console.Error.WriteLine("error: --out <private-key.pem> is required");
        return 1;
    }

    using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string privatePem = key.ExportECPrivateKeyPem();
    File.WriteAllText(outPath, privatePem + Environment.NewLine);

    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(outPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    Console.Error.WriteLine($"Private key written to {outPath} — keep it secret; it signs every future release.");
    Console.WriteLine(PublicKeyBase64(key));
    return 0;
}

static int PrintPublicKey(string[] args)
{
    string? keyPath = GetOption(args, "--key");
    if (keyPath is null)
    {
        Console.Error.WriteLine("error: --key <private-key.pem> is required");
        return 1;
    }

    using ECDsa key = LoadPrivateKey(keyPath);
    Console.WriteLine(PublicKeyBase64(key));
    return 0;
}

static int Sign(string[] args)
{
    string? keyPath = GetOption(args, "--key");
    string? version = GetOption(args, "--version");
    List<string> binaries = GetPositional(args, "--key", "--version");

    if (keyPath is null || version is null || binaries.Count == 0)
    {
        Console.Error.WriteLine("error: --key <private-key.pem> --version vX.Y.Z <binary...> are all required");
        return 1;
    }

    using ECDsa key = LoadPrivateKey(keyPath);

    foreach (string binaryPath in binaries)
    {
        if (!File.Exists(binaryPath))
        {
            Console.Error.WriteLine($"error: {binaryPath} does not exist");
            return 1;
        }

        string filename = Path.GetFileName(binaryPath);
        long size;
        string sha256;
        using (FileStream stream = File.OpenRead(binaryPath))
        {
            size = stream.Length;
            sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        byte[] signature = AgentUpdateSigning.Sign(key, version, filename, sha256, size);
        string sigPath = binaryPath + ".sig";
        File.WriteAllText(sigPath, Convert.ToBase64String(signature));

        Console.WriteLine($"{filename}: sha256={sha256} size={size} -> {Path.GetFileName(sigPath)}");
    }

    return 0;
}

static ECDsa LoadPrivateKey(string path)
{
    ECDsa key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(path));
    return key;
}

static string PublicKeyBase64(ECDsa key) =>
    Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

static string? GetOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}

static List<string> GetPositional(string[] args, params string[] optionNames)
{
    List<string> positional = new();
    for (int i = 0; i < args.Length; i++)
    {
        if (optionNames.Contains(args[i]))
        {
            i++; // skip the option's value
            continue;
        }

        positional.Add(args[i]);
    }

    return positional;
}
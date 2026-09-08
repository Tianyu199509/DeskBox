using System.Text.Json;
using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// The C# verifier must agree with the Node tooling: every committed spike
/// package (built, integrity-listed, and Ed25519-signed by scripts/spike)
/// must verify here byte-for-byte, and the tamper cases must be caught -
/// cross-implementation conformance for the whole chain.
/// </summary>
public sealed class PluginPackageVerifierTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N")))
        .FullName;

    public static TheoryData<string> CommittedPackages => new()
    {
        "spikes/github-stats",
        "spikes/github-stats-live",
        "spikes/github-stats-process",
        "spikes/github-stats-wasm"
    };

    [Theory]
    [MemberData(nameof(CommittedPackages))]
    public void CommittedPackages_VerifyWithTheFullChain(string relativePackagePath)
    {
        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(
            TestPaths.FromRepository(relativePackagePath));

        Assert.True(result.IsValid, string.Join("; ", result.Failures));
        Assert.False(result.UnsignedPackage);
        Assert.NotNull(result.ManifestJson);
    }

    [Fact]
    public void TamperedPayloadFile_FailsIntegrity()
    {
        string copy = CopyPackage("spikes/github-stats");
        File.AppendAllText(Path.Combine(copy, "files", "icon.svg"), "<!--tamper-->");

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(copy);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, f => f.Contains("integrity mismatch for files/icon.svg", StringComparison.Ordinal));
    }

    [Fact]
    public void EditedManifestWithoutRebuild_FailsItsOwnIntegrityLine()
    {
        string copy = CopyPackage("spikes/github-stats-live");
        string manifestPath = Path.Combine(copy, "manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("300", "301"));

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(copy);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, f =>
            f.Contains("manifest.json integrity line does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void TraversalPathInIntegrityList_ViolatesThePackagePathGrammar()
    {
        string copy = CopyPackage("spikes/github-stats");
        File.AppendAllText(
            Path.Combine(copy, "package.integrity"),
            $"{new string('a', 64)}  ../../evil.txt\n");

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(copy);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, f =>
            f.Contains("path violates the package path grammar", StringComparison.Ordinal) &&
            f.Contains("../../evil.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatePermissionIds_AreRejected()
    {
        string copy = CopyPackage("spikes/github-stats-process");
        string manifestPath = Path.Combine(copy, "manifest.json");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace("\"id\": \"shell.open\"", "\"id\": \"network.fetch\""));

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(copy);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, f => f.Contains("duplicate id 'network.fetch'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsignedPackage_SkipsSignatureButEnforcesIntegrity()
    {
        // Build a minimal unsigned package whose integrity line is computed
        // with the C# canonicalizer itself (internal seam): steps 1-2 must
        // pass, signature steps are skipped, UnsignedPackage = true.
        string package = Path.Combine(_tempRoot, "unsigned-pkg");
        Directory.CreateDirectory(package);
        string manifest = """
        {
          "schemaVersion": 0,
          "id": "com.example.unsigned",
          "version": "0.1.0",
          "publisher": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "publisherPublicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==",
          "runtime": "none",
          "hostApi": { "min": "1.0.0", "max": "1.0.0" },
          "contributions": [
            {
              "type": "widget",
              "id": "hello",
              "displayName": "Hello",
              "template": "metric",
              "payload": { "version": 1, "label": "Hello", "value": "1" }
            }
          ],
          "signature": null
        }
        """;
        File.WriteAllText(Path.Combine(package, "manifest.json"), manifest);

        using JsonDocument document = JsonDocument.Parse(manifest);
        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(PluginPackageVerifier.CanonicalizeManifest(document.RootElement)))
            .ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(package, "package.integrity"),
            $"{digest}  manifest.json\n");

        PluginPackageVerifier.VerificationResult devResult = PluginPackageVerifier.Verify(
            package, PluginPackageVerificationPolicy.Development);

        Assert.True(devResult.IsValid, string.Join("; ", devResult.Failures));
        Assert.True(devResult.UnsignedPackage);

        PluginPackageVerifier.VerificationResult storeResult = PluginPackageVerifier.Verify(
            package, PluginPackageVerificationPolicy.Store);

        Assert.False(storeResult.IsValid);
        Assert.Contains(storeResult.Failures, f =>
            f.Contains("signature required for store packages", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedPackage_NeverThrows_AllFailuresAreResults()
    {
        // Fail-closed total function (round 9): wrong types, invalid
        // base64, garbage - every byte input yields a result, never an
        // unhandled exception, and structural failures stop the phases.
        string package = Path.Combine(_tempRoot, "malformed-pkg");
        Directory.CreateDirectory(package);
        File.WriteAllText(
            Path.Combine(package, "manifest.json"),
            """
            {
              "schemaVersion": "zero",
              "id": [],
              "version": 1.5,
              "publisher": {},
              "publisherPublicKey": "!!!!not-base64!!!!",
              "runtime": 7,
              "hostApi": { "min": 3 },
              "contributions": "nope",
              "permissions": [ { "id": "bad", "required": "yes", "scope": { "allow": [ 42 ] } } ],
              "dataSources": { "UPPER": { "type": "http-json", "url": "http://x/", "refreshSeconds": 1.5 } },
              "entry": { "main": 5 }
            }
            """);
        File.WriteAllText(Path.Combine(package, "package.integrity"), "garbage\n");

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(package);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Failures);
        // Structural phase must report the type violations it is designed
        // to catch (wrong types now FAIL instead of silently skipping).
        Assert.Contains(result.Failures, f => f.Contains("schemaVersion must be an integer", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("id must be a string", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("runtime must be a string", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("hostApi.min must be a non-empty string", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("manifest numbers must be integers", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("map key 'UPPER' must use the local id pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void FloatNumbers_AreRejectedInSignedPackagesToo()
    {
        // Floats would break cross-platform canonicalization: even a
        // structurally-plausible float must fail (integer-only manifests).
        string copy = CopyPackage("spikes/github-stats");
        string manifestPath = Path.Combine(copy, "manifest.json");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace("\"version\": 1", "\"version\": 1", StringComparison.Ordinal));

        // Inject a float into a payload field (payload allows extra fields).
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace("\"value\": \"1284\"", "\"value\": \"1284\", \"rating\": 4.5"));

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(
            copy, PluginPackageVerificationPolicy.Development);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, f => f.Contains("manifest numbers must be integers", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidBase64ButWrongLengthKey_IsRejectedWithoutThrowing()
    {
        string package = Path.Combine(_tempRoot, "badkey-pkg");
        Directory.CreateDirectory(package);
        string manifest = """
        {
          "schemaVersion": 0,
          "id": "com.example.bad",
          "version": "0.1.0",
          "publisher": "PUB",
          "publisherPublicKey": "QUJD",
          "runtime": "none",
          "hostApi": { "min": "1.0.0", "max": "1.0.0" },
          "contributions": [
            { "type": "widget", "id": "x", "displayName": "X", "template": "metric", "payload": { "version": 1 } }
          ],
          "signature": { "contentHash": "PLACEHOLDER", "publisherSignature": "!!!" }
        }
        """
        .Replace("\"publisher\": \"PUB\"", "\"publisher\": \"" + new string('a', 64) + "\"");
        string manifestPath = Path.Combine(package, "manifest.json");
        File.WriteAllText(manifestPath, manifest);

        // Integrity line covers the manifest's canonical form (signature
        // forced null, so the placeholder contentHash is excluded); the
        // real contentHash is then back-filled into the manifest without
        // changing the canonical hash.
        using (JsonDocument document = JsonDocument.Parse(manifest))
        {
            string digest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(PluginPackageVerifier.CanonicalizeManifest(document.RootElement)))
                .ToLowerInvariant();
            File.WriteAllText(
                Path.Combine(package, "package.integrity"),
                $"{digest}  manifest.json\n");
        }
        string integrityHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(package, "package.integrity")))).ToLowerInvariant();
        File.WriteAllText(manifestPath, manifest.Replace("\"PLACEHOLDER\"", $"\"{integrityHash}\""));

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(package);

        Assert.False(result.IsValid);
        // Short key ("QUJB" = 3 bytes) / invalid signature base64 are
        // reported as failures, never thrown.
        Assert.Contains(result.Failures, f =>
            f.Contains("must decode to 32 raw bytes", StringComparison.Ordinal) ||
            f.Contains("not valid base64", StringComparison.Ordinal));
    }

    private string CopyPackage(string relativePackagePath)
    {
        string source = TestPaths.FromRepository(relativePackagePath);
        string destination = Path.Combine(_tempRoot, Path.GetFileName(relativePackagePath));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for files briefly held by antivirus.
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal interface ISyncExecutionRuntimeArtifactVerifier
{
    IReadOnlyList<SurfaceError> Validate(SyncExecutionReceipt receipt);
}

internal static class SyncExecutionRuntimeArtifactVerifierLoader
{
    public static ISyncExecutionRuntimeArtifactVerifier Load()
    {
        Type? type = typeof(SyncExecutionRuntimeArtifactVerifierLoader).Assembly.GetType(
            "ONI_Together.HeadlessTests.SyncExecutionRuntimeArtifactVerifier",
            throwOnError: false,
            ignoreCase: false);
        if (type is null)
            throw new InvalidOperationException(
                "missing production SyncExecutionRuntimeArtifactVerifier");
        if (Activator.CreateInstance(type) is not ISyncExecutionRuntimeArtifactVerifier verifier)
            throw new InvalidOperationException(
                "artifact verifier must implement its runtime contract");
        return verifier;
    }
}

internal static class SyncExecutionArtifactContractTests
{
    private const string InventoryDigest =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string CoverageDigest =
        "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string EntryId = "sync:artifact-contract-entry";

    public static void IngameAndRealArtifactsRequireAuthenticLogAndResultHashes()
    {
        string root = CreateArtifactDirectory();
        try
        {
            foreach ((string tier, string kind) in new[]
                     {
                         ("ingame", "ingame-result"),
                         ("real", "real-run"),
                     })
            {
                SyncExecutionReceipt receipt = AuthenticReceipt(root, tier, kind);
                IReadOnlyList<SurfaceError> errors = Verifier().Validate(receipt);
                Equal(0, errors.Count);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void ArtifactMutationsFailClosed()
    {
        string root = CreateArtifactDirectory();
        try
        {
            ResetPayloads(root);
            SyncExecutionReceipt manifestMutation =
                AuthenticReceipt(root, "real", "real-run");
            File.AppendAllText(manifestMutation.Artifact!.Path, "tampered");
            HasError(Verifier().Validate(manifestMutation),
                "runtime_artifact_manifest_hash_mismatch");

            ResetPayloads(root);
            SyncExecutionReceipt logMutation = AuthenticReceipt(root, "real", "real-run");
            File.AppendAllText(Path.Combine(root, "runtime.log"), "tampered");
            HasError(Verifier().Validate(logMutation), "runtime_log_hash_mismatch");

            ResetPayloads(root);
            SyncExecutionReceipt resultMutation =
                AuthenticReceipt(root, "real", "real-run");
            File.AppendAllText(Path.Combine(root, "result.json"), "tampered");
            HasError(Verifier().Validate(resultMutation), "runtime_result_hash_mismatch");

            ResetPayloads(root);
            SyncExecutionReceipt incomplete = IncompleteReceipt(root);
            HasError(Verifier().Validate(incomplete), "runtime_artifact_missing_result");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ISyncExecutionRuntimeArtifactVerifier Verifier()
    {
        return SyncExecutionRuntimeArtifactVerifierLoader.Load();
    }

    private static string CreateArtifactDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"oni-execution-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        ResetPayloads(root);
        return root;
    }

    private static void ResetPayloads(string root)
    {
        File.WriteAllText(Path.Combine(root, "runtime.log"), "runtime observation\n");
        File.WriteAllText(Path.Combine(root, "result.json"), "{\"passed\":true}\n");
    }

    private static SyncExecutionReceipt AuthenticReceipt(
        string root,
        string tier,
        string kind)
    {
        string manifest = Path.Combine(root, $"{tier}-artifact.json");
        WriteArtifactManifest(manifest, root, includeResult: true);
        return ParseReceipt(tier, kind, manifest);
    }

    private static SyncExecutionReceipt IncompleteReceipt(string root)
    {
        string manifest = Path.Combine(root, "incomplete-artifact.json");
        WriteArtifactManifest(manifest, root, includeResult: false);
        return ParseReceipt("real", "real-run", manifest);
    }

    private static void WriteArtifactManifest(
        string manifest,
        string root,
        bool includeResult)
    {
        var values = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["log"] = HashedFile(root, "runtime.log"),
        };
        if (includeResult)
            values["result"] = HashedFile(root, "result.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(values));
    }

    private static object HashedFile(string root, string name)
    {
        return new
        {
            path = name,
            sha256 = Digest(File.ReadAllBytes(Path.Combine(root, name))),
        };
    }

    private static SyncExecutionReceipt ParseReceipt(
        string tier,
        string kind,
        string manifest)
    {
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = $"run-artifact-{tier}",
            inventoryDigest = InventoryDigest,
            coverageDigest = CoverageDigest,
            dllHash = new string('d', 64),
            pdbHash = new string('e', 64),
            testId = $"{tier}:artifact-contract",
            tier,
            scenarioId = "door",
            polarity = "positive",
            executedEntryIds = new[] { EntryId },
            absentEntryIds = Array.Empty<string>(),
            registrationWitnesses = Array.Empty<object>(),
            artifact = new
            {
                kind,
                path = manifest,
                sha256 = Digest(File.ReadAllBytes(manifest)),
            },
        });
        return SyncExecutionReceipt.Parse(json);
    }

    private static string Digest(byte[] content)
    {
        return $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";
    }

    private static void HasError(IEnumerable<SurfaceError> errors, string code)
    {
        if (!errors.Any(error => error.Code == code))
            throw new InvalidOperationException($"missing {code}");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}, got {actual}");
    }
}

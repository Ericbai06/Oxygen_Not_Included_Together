using System.Security.Cryptography;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal sealed class SyncExecutionRuntimeArtifactVerifier :
    ISyncExecutionRuntimeArtifactVerifier
{
    public IReadOnlyList<SurfaceError> Validate(SyncExecutionReceipt receipt)
    {
        return Validate(receipt, null);
    }

    public IReadOnlyList<SurfaceError> Validate(
        SyncExecutionReceipt receipt,
        string? evidenceRoot)
    {
        var errors = new List<SurfaceError>();
        if (receipt.Tier is not (SyncExecutionTier.Ingame or SyncExecutionTier.Real))
            return errors;
        SyncExecutionArtifact? artifact = receipt.Artifact;
        if (artifact is null)
        {
            errors.Add(new SurfaceError("runtime_artifact_missing", receipt.TestId));
            return errors;
        }
        string? path = ResolvePath(artifact.Path, evidenceRoot, receipt.TestId, errors);
        if (path is null || !File.Exists(path))
        {
            errors.Add(new SurfaceError("runtime_artifact_missing", receipt.TestId));
            return errors.Distinct().ToArray();
        }
        if (!DigestMatches(path, artifact.Sha256))
        {
            errors.Add(new SurfaceError(
                evidenceRoot is null
                    ? "runtime_artifact_manifest_hash_mismatch"
                    : "runtime_artifact_hash_mismatch",
                receipt.TestId));
            return errors;
        }
        ValidateManifest(path, receipt, evidenceRoot is not null, errors);
        return errors.Distinct().ToArray();
    }

    private static string? ResolvePath(
        string path,
        string? evidenceRoot,
        string testId,
        ICollection<SurfaceError> errors)
    {
        if (evidenceRoot is null)
            return path;
        string root = Path.GetFullPath(evidenceRoot);
        string candidate = Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(root, path));
        if (!IsInsideRoot(root, candidate))
        {
            errors.Add(new SurfaceError("runtime_artifact_outside_root", testId));
            return null;
        }
        return candidate;
    }

    private static void ValidateManifest(
        string manifestPath,
        SyncExecutionReceipt receipt,
        bool requireControlPath,
        ICollection<SurfaceError> errors)
    {
        string testId = receipt.TestId;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            {
                errors.Add(new SurfaceError("runtime_artifact_manifest_invalid", testId));
                return;
            }
            ValidateControlPath(root, testId, requireControlPath, errors);
            if (requireControlPath && !IdentityMatches(root, receipt))
                errors.Add(new SurfaceError(
                    "runtime_artifact_identity_mismatch", testId));
            ValidateMember(root, "log", manifestPath, testId,
                "runtime_artifact_missing_log", "runtime_log_hash_mismatch", errors);
            ValidateMember(root, "result", manifestPath, testId,
                "runtime_artifact_missing_result", "runtime_result_hash_mismatch", errors);
            if (requireControlPath && !ResultPassed(root, manifestPath))
                errors.Add(new SurfaceError("runtime_result_failed", testId));
        }
        catch (JsonException)
        {
            errors.Add(new SurfaceError("runtime_artifact_manifest_invalid", testId));
        }
        catch (IOException)
        {
            errors.Add(new SurfaceError("runtime_artifact_missing", testId));
        }
    }

    private static bool IdentityMatches(
        JsonElement root,
        SyncExecutionReceipt receipt)
    {
        return StringMember(root, "runId") == receipt.RunId &&
            StringMember(root, "testId") == receipt.TestId &&
            NullableStringMember(root, "scenarioId") == receipt.ScenarioId &&
            StringMember(root, "tier") == SyncExecutionText.Tier(receipt.Tier) &&
            EntryIdsMatch(root, receipt.ExecutedEntryIds);
    }

    private static bool EntryIdsMatch(
        JsonElement root,
        IReadOnlyList<string> expected)
    {
        if (!root.TryGetProperty("executedEntryIds", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            return false;
        string?[] actual = value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
        return actual.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static string? StringMember(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? NullableStringMember(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool ResultPassed(JsonElement root, string manifestPath)
    {
        if (!root.TryGetProperty("result", out JsonElement result) ||
            !TryReadHashedFile(result, out string relativePath, out _))
            return false;
        string rootPath = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string path = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        if (!IsInsideRoot(rootPath, path) || !File.Exists(path))
            return false;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("passed", out JsonElement passed) &&
            passed.ValueKind is JsonValueKind.True;
    }

    private static void ValidateControlPath(
        JsonElement root,
        string testId,
        bool required,
        ICollection<SurfaceError> errors)
    {
        if (!root.TryGetProperty("controlPath", out JsonElement control))
        {
            if (required)
                errors.Add(new SurfaceError("runtime_control_path_mismatch", testId));
            return;
        }
        if (control.ValueKind != JsonValueKind.Object ||
            !control.TryGetProperty("driver", out JsonElement driver) ||
            driver.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(driver.GetString()) ||
            driver.GetString() == "manual-observe")
            errors.Add(new SurfaceError("runtime_control_path_mismatch", testId));
    }

    private static void ValidateMember(
        JsonElement root,
        string property,
        string manifestPath,
        string testId,
        string missingCode,
        string mismatchCode,
        ICollection<SurfaceError> errors)
    {
        if (!root.TryGetProperty(property, out JsonElement value) ||
            !TryReadHashedFile(value, out string? relativePath, out string? digest))
        {
            errors.Add(new SurfaceError(missingCode, testId));
            return;
        }
        string rootPath = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string candidate = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        if (!IsInsideRoot(rootPath, candidate) || !File.Exists(candidate))
        {
            errors.Add(new SurfaceError(missingCode, testId));
            return;
        }
        if (!DigestMatches(candidate, digest))
            errors.Add(new SurfaceError(mismatchCode, testId));
    }

    private static bool TryReadHashedFile(
        JsonElement value,
        out string path,
        out string digest)
    {
        path = "";
        digest = "";
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("path", out JsonElement pathValue) ||
            pathValue.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("sha256", out JsonElement digestValue) ||
            digestValue.ValueKind != JsonValueKind.String)
            return false;
        path = pathValue.GetString()!;
        digest = digestValue.GetString()!;
        return !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
            !string.IsNullOrWhiteSpace(digest);
    }

    private static bool IsInsideRoot(string root, string candidate)
    {
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool DigestMatches(string path, string expected)
    {
        byte[] content = File.ReadAllBytes(path);
        string actual = "sha256:" + Convert.ToHexString(
            SHA256.HashData(content)).ToLowerInvariant();
        return StringComparer.Ordinal.Equals(expected, actual);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal sealed class ActualDebugUnitTestInstrumentationCache :
    IActualDebugUnitTestInstrumentationCache
{
    public ActualDebugUnitTestInstrumentationCacheResult GetOrCreate(
        ActualDebugUnitTestInstrumentationCacheInput input)
    {
        ActualDebugUnitTestInstrumentationCacheKey key = Key(input);
        string root = Path.GetFullPath(input.CacheDirectory);
        Directory.CreateDirectory(root);
        string cacheRoot = Path.Combine(root,
            ActualDebugUnitTestInstrumentationCacheSchema.Namespace);
        Directory.CreateDirectory(cacheRoot);
        string target = Path.Combine(cacheRoot, key.KeyDigest);
        if (TryLoad(target, key, out SyncExecutionInstrumentedAssembly? cached))
            return new ActualDebugUnitTestInstrumentationCacheResult
            {
                Key = key,
                Assembly = cached,
                InstrumentationCount = 0,
                CacheHit = true
            };

        SyncExecutionInstrumentedAssembly instrumented =
            SyncExecutionIlInstrumenter.Instrument(
                input.Catalog, input.Assembly, input.GameLibsDirectory);
        Publish(target, key, instrumented);
        return new ActualDebugUnitTestInstrumentationCacheResult
        {
            Key = key,
            Assembly = instrumented,
            InstrumentationCount = 1,
            CacheHit = false
        };
    }

    private static ActualDebugUnitTestInstrumentationCacheKey Key(
        ActualDebugUnitTestInstrumentationCacheInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Catalog.Errors.Count != 0)
            throw new InvalidOperationException("catalog contains scan errors");
        string dllHash = Digest(input.Assembly.PeImage);
        string pdbHash = Digest(input.Assembly.PdbImage);
        string inventoryDigest = InventoryDigest(input.Catalog);
        if (inventoryDigest != input.InventoryDigest)
            throw new InvalidOperationException(
                "instrumentation cache inventory digest drift");
        string identity = string.Join("\n",
            ActualDebugUnitTestInstrumentationCacheSchema.Namespace,
            ActualDebugUnitTestInstrumentationCacheSchema.Version,
            dllHash, pdbHash, inventoryDigest);
        return new ActualDebugUnitTestInstrumentationCacheKey
        {
            SchemaVersion =
                ActualDebugUnitTestInstrumentationCacheSchema.Version,
            DllHash = dllHash,
            PdbHash = pdbHash,
            InventoryDigest = inventoryDigest,
            KeyDigest = Digest(Encoding.UTF8.GetBytes(identity))
        };
    }

    private static bool TryLoad(
        string directory,
        ActualDebugUnitTestInstrumentationCacheKey key,
        [NotNullWhen(true)] out SyncExecutionInstrumentedAssembly? assembly)
    {
        assembly = null;
        if (!Directory.Exists(directory) ||
            !FilesAreExact(directory))
            return false;
        try
        {
            string metadataPath = Path.Combine(directory,
                ActualDebugUnitTestInstrumentationCacheSchema.MetadataFileName);
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(metadataPath));
            JsonElement metadata = document.RootElement;
            if (!MetadataIsExact(metadata, key))
                return false;
            byte[] pe = File.ReadAllBytes(Path.Combine(directory,
                ActualDebugUnitTestInstrumentationCacheSchema.PeFileName));
            byte[] pdb = File.ReadAllBytes(Path.Combine(directory,
                ActualDebugUnitTestInstrumentationCacheSchema.PdbFileName));
            string peHash = Digest(pe);
            string pdbHash = Digest(pdb);
            if (peHash != String(metadata, "instrumentedDllHash") ||
                pdbHash != String(metadata, "instrumentedPdbHash"))
                return false;
            assembly = new SyncExecutionInstrumentedAssembly(
                pe, pdb, key.DllHash, key.PdbHash);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FilesAreExact(string directory)
    {
        string[] expected =
        [
            ActualDebugUnitTestInstrumentationCacheSchema.MetadataFileName,
            ActualDebugUnitTestInstrumentationCacheSchema.PeFileName,
            ActualDebugUnitTestInstrumentationCacheSchema.PdbFileName
        ];
        return !Directory.EnumerateDirectories(directory).Any() &&
            Directory.EnumerateFiles(directory).Select(Path.GetFileName)
                .ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool MetadataIsExact(
        JsonElement metadata,
        ActualDebugUnitTestInstrumentationCacheKey key)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.EnumerateObject().Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(
                    ActualDebugUnitTestInstrumentationCacheSchema
                        .MetadataFields))
            return false;
        return Integer(metadata, "schemaVersion") == key.SchemaVersion &&
            String(metadata, "keyDigest") == key.KeyDigest &&
            String(metadata, "dllHash") == key.DllHash &&
            String(metadata, "pdbHash") == key.PdbHash &&
            String(metadata, "inventoryDigest") == key.InventoryDigest &&
            IsDigest(String(metadata, "instrumentedDllHash")) &&
            IsDigest(String(metadata, "instrumentedPdbHash"));
    }

    private static void Publish(
        string target,
        ActualDebugUnitTestInstrumentationCacheKey key,
        SyncExecutionInstrumentedAssembly assembly)
    {
        string parent = Path.GetDirectoryName(target)!;
        string staging = Path.Combine(parent,
            $".{key.KeyDigest}.{Guid.NewGuid():N}.tmp");
        string stale = Path.Combine(parent,
            $".{key.KeyDigest}.{Guid.NewGuid():N}.stale");
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Combine(staging,
                ActualDebugUnitTestInstrumentationCacheSchema.PeFileName),
                assembly.PeImage);
            File.WriteAllBytes(Path.Combine(staging,
                ActualDebugUnitTestInstrumentationCacheSchema.PdbFileName),
                assembly.PdbImage);
            File.WriteAllText(Path.Combine(staging,
                ActualDebugUnitTestInstrumentationCacheSchema.MetadataFileName),
                Metadata(key, assembly));
            if (Directory.Exists(target))
                Directory.Move(target, stale);
            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (Directory.Exists(stale))
                    Directory.Move(stale, target);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            if (Directory.Exists(stale))
                Directory.Delete(stale, recursive: true);
        }
    }

    private static string Metadata(
        ActualDebugUnitTestInstrumentationCacheKey key,
        SyncExecutionInstrumentedAssembly assembly) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = key.SchemaVersion,
            keyDigest = key.KeyDigest,
            dllHash = key.DllHash,
            pdbHash = key.PdbHash,
            inventoryDigest = key.InventoryDigest,
            instrumentedDllHash = Digest(assembly.PeImage),
            instrumentedPdbHash = Digest(assembly.PdbImage)
        });

    private static string String(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out JsonElement item) ||
            item.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(item.GetString()))
            throw new FormatException($"invalid cache metadata {property}");
        return item.GetString()!;
    }

    private static int Integer(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out JsonElement item) ||
            !item.TryGetInt32(out int result))
            throw new FormatException($"invalid cache metadata {property}");
        return result;
    }

    private static string InventoryDigest(SyncCatalogScan catalog)
    {
        using JsonDocument inventory = JsonDocument.Parse(
            SyncInventoryJson.Serialize(catalog));
        return inventory.RootElement.GetProperty("digest").GetString()!;
    }

    private static bool IsDigest(string value) =>
        value.Length == 64 && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string Digest(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

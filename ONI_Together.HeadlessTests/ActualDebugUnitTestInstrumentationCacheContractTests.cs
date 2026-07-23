using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestInstrumentationCacheContractTests
{
    internal static void ReusesExactBytesAndRejectsInvalidCacheState()
    {
        IActualDebugUnitTestInstrumentationCache cache =
            ActualDebugUnitTestInstrumentationCacheLoader.Load();
        ActualDebugUnitTestPreflightInput fixture =
            ActualDebugUnitTestPreflightFixture.CreateValid();
        string temporary = Path.Combine(Path.GetTempPath(),
            "oni-actual-unit-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            V2EntryIsNotReusedByV3(
                cache,
                fixture,
                Path.Combine(temporary, "schema-migration"));
            ActualDebugUnitTestInstrumentationCacheInput input =
                Input(fixture, temporary);
            ActualDebugUnitTestInstrumentationCacheResult first =
                cache.GetOrCreate(input);
            ActualDebugUnitTestInstrumentationCacheResult second =
                cache.GetOrCreate(input);
            ValidateFirst(first, fixture);
            ValidateHit(first, second);
            string metadataPath = ValidateCacheFiles(temporary, first.Key);

            File.WriteAllText(metadataPath, "{}");
            ActualDebugUnitTestInstrumentationCacheResult corrupt =
                cache.GetOrCreate(input);
            ValidateRebuild(first, corrupt);

            ForgeMetadata(metadataPath);
            ActualDebugUnitTestInstrumentationCacheResult forged =
                cache.GetOrCreate(input);
            ValidateRebuild(first, forged);

            ActualDebugUnitTestPreflightInput changedAssembly =
                fixture with { Assembly = Reemit(fixture.Assembly) };
            changedAssembly = changedAssembly with
            {
                DllHash = ActualDebugUnitTestPreflightFixture.Digest(
                    changedAssembly.Assembly.PeImage),
                PdbHash = ActualDebugUnitTestPreflightFixture.Digest(
                    changedAssembly.Assembly.PdbImage)
            };
            ActualDebugUnitTestInstrumentationCacheResult binaryChanged =
                cache.GetOrCreate(Input(changedAssembly, temporary));
            Equal(1, binaryChanged.InstrumentationCount);
            True(!binaryChanged.CacheHit,
                "changed binary key reused cached instrumentation");
            True(binaryChanged.Key.KeyDigest != first.Key.KeyDigest,
                "changed binary produced the same cache key");

            ActualDebugUnitTestInstrumentationCacheInput inventoryChanged =
                ChangedInventory(input);
            ActualDebugUnitTestInstrumentationCacheResult catalogChanged =
                cache.GetOrCreate(inventoryChanged);
            Equal(1, catalogChanged.InstrumentationCount);
            True(!catalogChanged.CacheHit,
                "changed inventory key reused cached instrumentation");
            True(catalogChanged.Key.KeyDigest != first.Key.KeyDigest,
                "changed inventory produced the same cache key");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void V2EntryIsNotReusedByV3(
        IActualDebugUnitTestInstrumentationCache cache,
        ActualDebugUnitTestPreflightInput fixture,
        string root)
    {
        ActualDebugUnitTestInstrumentationCacheInput input =
            Input(fixture, root);
        string v2Target = WriteV2Entry(input);
        string[] v2Snapshot = Snapshot(v2Target);

        ActualDebugUnitTestInstrumentationCacheResult first =
            cache.GetOrCreate(input);
        Equal(3, first.Key.SchemaVersion);
        Equal(1, first.InstrumentationCount);
        True(!first.CacheHit, "v3 cache reused a valid v2 entry");
        string v3Root = Path.Combine(
            root,
            ActualDebugUnitTestInstrumentationCacheSchema.Namespace);
        EqualSet([first.Key.KeyDigest],
            Directory.EnumerateDirectories(v3Root).Select(Path.GetFileName)!);
        ValidateCacheFiles(root, first.Key);
        True(v2Snapshot.SequenceEqual(Snapshot(v2Target)),
            "v3 cache publication changed the valid v2 target");

        ActualDebugUnitTestInstrumentationCacheResult second =
            cache.GetOrCreate(input);
        ValidateHit(first, second);
    }

    private static string WriteV2Entry(
        ActualDebugUnitTestInstrumentationCacheInput input)
    {
        const string oldNamespace = "actual-unit-instrumentation-v2";
        string dllHash = Digest(input.Assembly.PeImage);
        string pdbHash = Digest(input.Assembly.PdbImage);
        string keyDigest = Digest(Encoding.UTF8.GetBytes(string.Join("\n",
            oldNamespace,
            2,
            dllHash,
            pdbHash,
            input.InventoryDigest)));
        string target = Path.Combine(
            input.CacheDirectory, oldNamespace, keyDigest);
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target,
            ActualDebugUnitTestInstrumentationCacheSchema.PeFileName),
            input.Assembly.PeImage);
        File.WriteAllBytes(Path.Combine(target,
            ActualDebugUnitTestInstrumentationCacheSchema.PdbFileName),
            input.Assembly.PdbImage);
        File.WriteAllText(Path.Combine(target,
            ActualDebugUnitTestInstrumentationCacheSchema.MetadataFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                keyDigest,
                dllHash,
                pdbHash,
                inventoryDigest = input.InventoryDigest,
                instrumentedDllHash = dllHash,
                instrumentedPdbHash = pdbHash
            }));
        return target;
    }

    private static string[] Snapshot(string directory) =>
        Directory.EnumerateFiles(directory)
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" +
                Digest(File.ReadAllBytes(path)))
            .ToArray();

    private static ActualDebugUnitTestInstrumentationCacheInput Input(
        ActualDebugUnitTestPreflightInput fixture,
        string cacheDirectory) => new()
    {
        Catalog = fixture.Catalog,
        Assembly = fixture.Assembly,
        InventoryDigest = fixture.InventoryDigest,
        CacheDirectory = cacheDirectory
    };

    private static ActualDebugUnitTestInstrumentationCacheInput
        ChangedInventory(ActualDebugUnitTestInstrumentationCacheInput input)
    {
        SyncEntry first = input.Catalog.Entries[0];
        SyncEntry changed = first with
        {
            Id = "sync:fffffffffffffffffffffff2"
        };
        var catalog = new SyncCatalogScan(
            [changed, .. input.Catalog.Entries.Skip(1)], []);
        return input with
        {
            Catalog = catalog,
            InventoryDigest =
                ActualDebugUnitTestPreflightFixture.InventoryDigest(catalog)
        };
    }

    private static void ValidateFirst(
        ActualDebugUnitTestInstrumentationCacheResult result,
        ActualDebugUnitTestPreflightInput fixture)
    {
        Equal(ActualDebugUnitTestInstrumentationCacheSchema.Version,
            result.Key.SchemaVersion);
        Equal(fixture.DllHash, result.Key.DllHash);
        Equal(fixture.PdbHash, result.Key.PdbHash);
        Equal(fixture.InventoryDigest, result.Key.InventoryDigest);
        Equal(1, result.InstrumentationCount);
        True(!result.CacheHit, "first cache request was reported as a hit");
    }

    private static void ValidateHit(
        ActualDebugUnitTestInstrumentationCacheResult first,
        ActualDebugUnitTestInstrumentationCacheResult second)
    {
        Equal(first.Key, second.Key);
        Equal(0, second.InstrumentationCount);
        True(second.CacheHit, "second same-key request missed cache");
        True(first.Assembly.PeImage.SequenceEqual(second.Assembly.PeImage),
            "cache hit changed instrumented PE bytes");
        True(first.Assembly.PdbImage.SequenceEqual(second.Assembly.PdbImage),
            "cache hit changed instrumented PDB bytes");
    }

    private static void ValidateRebuild(
        ActualDebugUnitTestInstrumentationCacheResult first,
        ActualDebugUnitTestInstrumentationCacheResult rebuilt)
    {
        Equal(first.Key, rebuilt.Key);
        Equal(1, rebuilt.InstrumentationCount);
        True(!rebuilt.CacheHit, "invalid cache metadata was accepted");
        True(first.Assembly.PeImage.SequenceEqual(rebuilt.Assembly.PeImage),
            "cache rebuild changed deterministic PE bytes");
        True(first.Assembly.PdbImage.SequenceEqual(rebuilt.Assembly.PdbImage),
            "cache rebuild changed deterministic PDB bytes");
    }

    private static string ValidateCacheFiles(
        string root,
        ActualDebugUnitTestInstrumentationCacheKey key)
    {
        string directory = Path.Combine(root,
            ActualDebugUnitTestInstrumentationCacheSchema.Namespace,
            key.KeyDigest);
        EqualSet([
            ActualDebugUnitTestInstrumentationCacheSchema.MetadataFileName,
            ActualDebugUnitTestInstrumentationCacheSchema.PeFileName,
            ActualDebugUnitTestInstrumentationCacheSchema.PdbFileName
        ], Directory.EnumerateFiles(directory).Select(Path.GetFileName)!);
        string metadata = Path.Combine(directory,
            ActualDebugUnitTestInstrumentationCacheSchema.MetadataFileName);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(metadata));
        EqualSet(ActualDebugUnitTestInstrumentationCacheSchema.MetadataFields,
            json.RootElement.EnumerateObject().Select(item => item.Name));
        return metadata;
    }

    private static void ForgeMetadata(string path)
    {
        JsonObject metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        metadata["dllHash"] = new string('0', 64);
        File.WriteAllText(path, metadata.ToJsonString());
    }

    private static SyncExecutionFixtureAssembly Reemit(
        SyncExecutionFixtureAssembly fixture)
    {
        using var pe = new MemoryStream(fixture.PeImage, writable: false);
        using var pdb = new MemoryStream(fixture.PdbImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            pe, new ReaderParameters
            {
                ReadSymbols = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                SymbolStream = pdb
            });
        assembly.MainModule.Mvid = Guid.NewGuid();
        using var outputPe = new MemoryStream();
        using var outputPdb = new MemoryStream();
        assembly.Write(outputPe, new WriterParameters
        {
            WriteSymbols = true,
            SymbolWriterProvider = new PortablePdbWriterProvider(),
            SymbolStream = outputPdb
        });
        return new SyncExecutionFixtureAssembly(
            outputPe.ToArray(), outputPdb.ToArray());
    }

    private static void EqualSet<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual)
    {
        if (!expected.ToHashSet().SetEquals(actual))
            throw new InvalidOperationException("sets differ");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"expected {expected}, actual {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Digest(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal sealed partial class ActualDebugUnitTestBatchRunner
{
    public string Serialize(ActualDebugUnitTestBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = result.SchemaVersion,
            runId = result.RunId,
            dllHash = result.DllHash,
            pdbHash = result.PdbHash,
            inventoryDigest = result.InventoryDigest,
            instrumentationCount = result.InstrumentationCount,
            assemblyLoadCount = result.AssemblyLoadCount,
            bootstrap = SerializeBootstrap(result.Bootstrap),
            accessBypassBootstrap =
                SerializeAccessBypassBootstrap(result.AccessBypassBootstrap),
            success = result.Success,
            results = result.Results.Select(SerializeResult).ToArray()
        });
    }

    public ActualDebugUnitTestBatchResult Parse(
        string json,
        ActualDebugUnitTestBatchInput expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Exact(root, RootFields, "batch");
        var result = new ActualDebugUnitTestBatchResult(
            Integer(root, "schemaVersion"),
            String(root, "runId"),
            String(root, "dllHash"),
            String(root, "pdbHash"),
            String(root, "inventoryDigest"),
            Integer(root, "instrumentationCount"),
            Integer(root, "assemblyLoadCount"),
            Boolean(root, "success"),
            Array(root, "results").Select(ParseResult).ToArray())
        {
            Bootstrap = ParseBootstrap(root.GetProperty("bootstrap")),
            AccessBypassBootstrap = ParseAccessBypassBootstrap(
                root.GetProperty("accessBypassBootstrap"))
        };
        ValidateCore(expected, result, requireLiveProvenance: true);
        return result;
    }

    private object SerializeResult(ActualDebugUnitTestResult result)
    {
        object? receipt = result.Receipt is null
            ? null : SerializeReceipt(result.Receipt);
        return new
        {
            testId = result.TestId,
            methodSymbol = result.MethodSymbol,
            outcome = result.Outcome.ToString(),
            message = result.Message,
            durationMs = result.DurationMs,
            dllHash = result.DllHash,
            pdbHash = result.PdbHash,
            inventoryDigest = result.InventoryDigest,
            observationEpoch = result.ObservationEpoch,
            observedEntryIds = result.ObservedEntryIds,
            runtimeReferenceEvidence = result.RuntimeReferenceEvidence,
            receipt
        };
    }

    private static object SerializeBootstrap(
        ActualDebugUnitTestBootstrapEvidence? bootstrap)
    {
        ActualDebugUnitTestBootstrapEvidence evidence = bootstrap ??
            throw new InvalidOperationException(
                "batch omitted PacketRegistry bootstrap evidence");
        return new
        {
            methodSymbol = evidence.MethodSymbol,
            invocationCount = evidence.InvocationCount,
            registeredPacketCountBefore =
                evidence.RegisteredPacketCountBefore,
            registeredPacketCountAfter =
                evidence.RegisteredPacketCountAfter,
            registryWasInitiallyEmpty =
                evidence.RegistryWasInitiallyEmpty
        };
    }

    private static ActualDebugUnitTestBootstrapEvidence ParseBootstrap(
        JsonElement element)
    {
        Exact(element, BootstrapFields, "batch bootstrap");
        return new ActualDebugUnitTestBootstrapEvidence(
            String(element, "methodSymbol"),
            Integer(element, "invocationCount"),
            Integer(element, "registeredPacketCountBefore"),
            Integer(element, "registeredPacketCountAfter"),
            Boolean(element, "registryWasInitiallyEmpty"));
    }

    private static object SerializeAccessBypassBootstrap(
        ActualDebugAccessBypassBootstrapEvidence? bootstrap)
    {
        ActualDebugAccessBypassBootstrapEvidence evidence = bootstrap ??
            throw new InvalidOperationException(
                "batch omitted access-bypass bootstrap evidence");
        return new
        {
            accessBypassTargets = evidence.AccessBypassTargets,
            instrumentedDllHash = evidence.InstrumentedDllHash,
            instrumentedPdbHash = evidence.InstrumentedPdbHash,
            runtimeAssemblies = evidence.RuntimeAssemblies.Select(item => new
            {
                path = item.Path,
                sha256 = item.Sha256,
                assemblyIdentity = item.AssemblyIdentity
            }).ToArray(),
            instrumentedTypeCount = evidence.InstrumentedTypeCount,
            cannotReduceAccessTypeLoadExceptionCount =
                evidence.CannotReduceAccessTypeLoadExceptionCount
        };
    }

    private static ActualDebugAccessBypassBootstrapEvidence
        ParseAccessBypassBootstrap(JsonElement element)
    {
        Exact(element, AccessBypassBootstrapFields, "access-bypass bootstrap");
        ActualDebugRuntimeAssemblyEvidence[] assemblies =
            Array(element, "runtimeAssemblies").Select(item =>
            {
                Exact(item, RuntimeAssemblyFields, "runtime assembly");
                return new ActualDebugRuntimeAssemblyEvidence(
                    String(item, "path"),
                    String(item, "sha256"),
                    String(item, "assemblyIdentity"));
            }).ToArray();
        return new ActualDebugAccessBypassBootstrapEvidence(
            Strings(element, "accessBypassTargets"),
            String(element, "instrumentedDllHash"),
            String(element, "instrumentedPdbHash"),
            assemblies,
            Integer(element, "instrumentedTypeCount"),
            Integer(element, "cannotReduceAccessTypeLoadExceptionCount"));
    }

    private object SerializeReceipt(SyncExecutionReceipt receipt)
    {
        object value = new
        {
            schemaVersion = receipt.SchemaVersion,
            runId = receipt.RunId,
            inventoryDigest = receipt.InventoryDigest,
            coverageDigest = receipt.CoverageDigest,
            testId = receipt.TestId,
            tier = SyncExecutionText.Tier(receipt.Tier),
            scenarioId = receipt.ScenarioId,
            polarity = receipt.Polarity == SyncExecutionPolarity.Positive
                ? "positive" : "negative",
            executedEntryIds = receipt.ExecutedEntryIds,
            absentEntryIds = receipt.AbsentEntryIds,
            registrationWitnesses = receipt.RegistrationWitnesses.Select(
                witness => new
                {
                    entryId = witness.EntryId,
                    registrationEntryId = witness.RegistrationEntryId
                }).ToArray(),
            artifact = receipt.Artifact,
            dllHash = receipt.DllHash,
            pdbHash = receipt.PdbHash
        };
        string json = JsonSerializer.Serialize(value);
        using JsonDocument document = JsonDocument.Parse(json);
        authenticReceipts[SyncCanonicalJson.Sha256(
            document.RootElement)] = receipt;
        return value;
    }

    private ActualDebugUnitTestResult ParseResult(JsonElement element)
    {
        Exact(element, ResultFields, "batch result");
        return new ActualDebugUnitTestResult(
            String(element, "testId"),
            String(element, "methodSymbol"),
            EnumValue<ActualDebugUnitTestOutcome>(element, "outcome"),
            NullableString(element, "message"),
            Number(element, "durationMs"),
            String(element, "dllHash"),
            String(element, "pdbHash"),
            String(element, "inventoryDigest"),
            Integer(element, "observationEpoch"),
            Strings(element, "observedEntryIds"),
            Strings(element, "runtimeReferenceEvidence"),
            ParseReceipt(element.GetProperty("receipt")));
    }

    private SyncExecutionReceipt? ParseReceipt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("receipt must be an object or null");
        string key = SyncCanonicalJson.Sha256(element);
        if (!authenticReceipts.TryGetValue(
                key, out SyncExecutionReceipt? receipt))
            throw new FormatException(
                "batch receipt has no live observed provenance");
        _ = SyncExecutionReceipt.Parse(element.GetRawText());
        return receipt;
    }

    private static void Exact(
        JsonElement element,
        IReadOnlySet<string> fields,
        string subject)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new FormatException($"{subject} fields are not exact");
    }

    private static string String(JsonElement value, string property)
    {
        JsonElement item = value.GetProperty(property);
        return item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw new FormatException($"{property} must be a string");
    }

    private static string? NullableString(
        JsonElement value,
        string property)
    {
        JsonElement item = value.GetProperty(property);
        return item.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => item.GetString(),
            _ => throw new FormatException(
                $"{property} must be a string or null")
        };
    }

    private static int Integer(JsonElement value, string property) =>
        value.GetProperty(property).TryGetInt32(out int result)
            ? result
            : throw new FormatException($"{property} must be an integer");

    private static double Number(JsonElement value, string property) =>
        value.GetProperty(property).TryGetDouble(out double result)
            ? result
            : throw new FormatException($"{property} must be a number");

    private static bool Boolean(JsonElement value, string property)
    {
        JsonElement item = value.GetProperty(property);
        return item.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"{property} must be a boolean")
        };
    }

    private static JsonElement.ArrayEnumerator Array(
        JsonElement value,
        string property)
    {
        JsonElement item = value.GetProperty(property);
        return item.ValueKind == JsonValueKind.Array
            ? item.EnumerateArray()
            : throw new FormatException($"{property} must be an array");
    }

    private static string[] Strings(
        JsonElement value,
        string property) => Array(value, property).Select(item =>
        item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw new FormatException(
                $"{property} values must be strings")).ToArray();

    private static T EnumValue<T>(JsonElement value, string property)
        where T : struct, Enum =>
        Enum.TryParse(String(value, property), false, out T result)
            ? result
            : throw new FormatException($"{property} has an invalid value");

    private static readonly IReadOnlySet<string> RootFields =
        new HashSet<string>([
            "schemaVersion", "runId", "dllHash", "pdbHash",
            "inventoryDigest", "instrumentationCount", "assemblyLoadCount",
            "bootstrap", "accessBypassBootstrap", "success", "results"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> BootstrapFields =
        new HashSet<string>([
            "methodSymbol", "invocationCount", "registeredPacketCountBefore",
            "registeredPacketCountAfter", "registryWasInitiallyEmpty"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> AccessBypassBootstrapFields =
        new HashSet<string>([
            "accessBypassTargets", "instrumentedDllHash",
            "instrumentedPdbHash", "runtimeAssemblies",
            "instrumentedTypeCount",
            "cannotReduceAccessTypeLoadExceptionCount"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> RuntimeAssemblyFields =
        new HashSet<string>([
            "path", "sha256", "assemblyIdentity"
        ], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ResultFields =
        new HashSet<string>([
            "testId", "methodSymbol", "outcome", "message", "durationMs",
            "dllHash", "pdbHash", "inventoryDigest", "observationEpoch",
            "observedEntryIds", "runtimeReferenceEvidence", "receipt"
        ], StringComparer.Ordinal);
}

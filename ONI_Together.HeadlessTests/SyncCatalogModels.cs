using Microsoft.CodeAnalysis;

namespace ONI_Together.HeadlessTests;

internal enum SyncEntryKind
{
    PacketRegistration,
    PacketSend,
    PacketRelay,
    PacketDeserialize,
    PacketDispatch,
    HarmonyPatch,
    EventSubscribe,
    EventPublish,
    Coroutine,
    StateMachine
}

internal enum SyncEntryStatus
{
    Active,
    RegisteredDisabled,
    Vendor,
    TestOnly
}

internal sealed record SyncBuildVariant(
    string Configuration,
    string Platform,
    IReadOnlySet<string> Symbols)
{
    public string Key => $"{Configuration}/{Platform}";
}

internal sealed record SyncVariantInput(
    SyncBuildVariant Variant,
    IReadOnlyDictionary<string, string> Sources,
    IReadOnlyList<string> MetadataReferences);

internal sealed record SyncEntry(
    string Id,
    SyncEntryKind Kind,
    string FullyQualifiedSymbol,
    string ResolvedTargetSignature,
    string Bootstrap,
    IReadOnlyList<SyncBuildVariant> Variants,
    SyncEntryStatus Status);

internal sealed record SyncCatalogScan(
    IReadOnlyList<SyncEntry> Entries,
    IReadOnlyList<SurfaceError> Errors);

internal sealed record SyncEntryCandidate(
    SyncEntryKind Kind,
    string FullyQualifiedSymbol,
    string ResolvedTargetSignature,
    string Bootstrap,
    SyncBuildVariant Variant,
    SyncEntryStatus Status,
    string Callsite = "");

internal sealed record CatalogExtractionContext(
    SemanticModel Model,
    SyncBuildVariant Variant,
    ICollection<SyncEntryCandidate> Candidates,
    IDictionary<string, int> CallsiteOccurrences,
    SyncEntryStatus SourceStatus);

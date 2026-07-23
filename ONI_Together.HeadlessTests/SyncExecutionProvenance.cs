using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionProvenance
{
    private static readonly ConditionalWeakTable<SyncExecutionReceipt, Proof> Proofs = new();

    public static void Attach(
        SyncExecutionReceipt receipt,
        IEnumerable<string> observed,
        IEnumerable<string> absent)
    {
        Proofs.Add(receipt, new Proof(
            observed.ToHashSet(StringComparer.Ordinal),
            absent.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    public static void AttachFromCatalog(
        SyncExecutionReceipt receipt,
        SyncCatalogScan catalog,
        IEnumerable<string> observed,
        IEnumerable<string> absent)
    {
        _ = catalog;
        Attach(receipt, observed, absent);
    }

    public static Recorder CreateRecorder(SyncCatalogScan catalog)
    {
        return new Recorder(catalog.Entries.ToDictionary(
            entry => entry.Id, EntryOrigin, StringComparer.Ordinal));
    }

    public static bool IsObserved(SyncExecutionReceipt receipt, string entryId)
    {
        return Proofs.TryGetValue(receipt, out Proof? proof) &&
            proof.Observed.Contains(entryId);
    }

    public static bool IsAbsent(SyncExecutionReceipt receipt, string entryId)
    {
        return Proofs.TryGetValue(receipt, out Proof? proof) &&
            proof.Absent.Contains(entryId);
    }

    public static bool MatchesOrigin(
        SyncExecutionReceipt receipt,
        SyncEntry entry)
    {
        return Proofs.TryGetValue(receipt, out Proof? proof) &&
            proof.Origins.TryGetValue(entry.Id, out string? origin) &&
            origin == EntryOrigin(entry);
    }

    private static string EntryOrigin(SyncEntry entry)
    {
        string variants = string.Join(",", entry.Variants
            .Select(item => item.Key).Order(StringComparer.Ordinal));
        string identity = string.Join("\n", entry.Id, entry.Kind,
            entry.FullyQualifiedSymbol, entry.ResolvedTargetSignature,
            entry.Bootstrap, entry.Status, variants);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private sealed record Proof(
        IReadOnlySet<string> Observed,
        IReadOnlySet<string> Absent,
        IReadOnlyDictionary<string, string> Origins);

    internal sealed class Recorder
    {
        private readonly IReadOnlyDictionary<string, string> catalogOrigins;
        private readonly HashSet<string> observed = new(StringComparer.Ordinal);

        internal Recorder(IReadOnlyDictionary<string, string> catalogOrigins)
        {
            this.catalogOrigins = catalogOrigins;
        }

        internal void Observe(string entryId)
        {
            if (catalogOrigins.ContainsKey(entryId))
                observed.Add(entryId);
        }

        internal void Attach(
            SyncExecutionReceipt receipt,
            IEnumerable<string> claimedObserved,
            IEnumerable<string> absent)
        {
            IReadOnlySet<string> observedIds =
                claimedObserved.ToHashSet(StringComparer.Ordinal);
            IReadOnlySet<string> absentIds = absent.ToHashSet(StringComparer.Ordinal);
            IReadOnlyDictionary<string, string> origins = catalogOrigins
                .Where(item => observedIds.Contains(item.Key) &&
                    observed.Contains(item.Key) || absentIds.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value,
                    StringComparer.Ordinal);
            Proofs.Add(receipt, new Proof(observedIds, absentIds, origins));
        }
    }
}

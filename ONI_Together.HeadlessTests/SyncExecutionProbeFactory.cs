using System.Reflection;

namespace ONI_Together.HeadlessTests;

internal sealed class SyncExecutionProbeFactory : ISyncExecutionProbeFactory
{
    public ISyncExecutionProbeSession Start(
        SyncExecutionProbeBinding binding,
        SyncCatalogScan catalog,
        SyncExecutionFixtureAssembly fixture)
    {
        SyncExecutionInstrumentedAssembly instrumented =
            SyncExecutionIlInstrumenter.Instrument(catalog, fixture);
        Assembly assembly = Assembly.Load(
            instrumented.PeImage, instrumented.PdbImage);
        var session = new SyncExecutionProbeSession(
            binding, catalog, assembly, instrumented.DllHash, instrumented.PdbHash);
        Type observer = assembly.GetType(
            SyncExecutionIlInstrumenter.ObserverTypeName, throwOnError: true)!;
        observer.GetField("Observer", BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, new Action<string, string>(session.Observe));
        return session;
    }
}

internal sealed class SyncExecutionProbeSession : ISyncExecutionProbeSession
{
    private readonly SyncExecutionProbeBinding binding;
    private readonly SyncCatalogScan catalog;
    private readonly IReadOnlySet<string> catalogEntryIds;
    private readonly Dictionary<string, HashSet<string>> observations =
        new(StringComparer.Ordinal);
    private readonly SyncExecutionProvenance.Recorder provenance;
    private readonly string dllHash;
    private readonly string pdbHash;

    public SyncExecutionProbeSession(
        SyncExecutionProbeBinding binding,
        SyncCatalogScan catalog,
        Assembly runtimeAssembly,
        string dllHash,
        string pdbHash)
    {
        this.binding = binding;
        this.catalog = catalog;
        catalogEntryIds = catalog.Entries.Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        provenance = SyncExecutionProvenance.CreateRecorder(catalog);
        this.dllHash = dllHash;
        this.pdbHash = pdbHash;
        RuntimeAssembly = runtimeAssembly;
    }

    public Assembly RuntimeAssembly { get; }

    public void Observe(string entryId, string phase)
    {
        _ = TryObserve(entryId, phase);
    }

    public bool TryObserve(string entryId, string phase)
    {
        if (!catalogEntryIds.Contains(entryId))
            return false;
        if (!observations.TryGetValue(entryId, out HashSet<string>? phases))
        {
            phases = new HashSet<string>(StringComparer.Ordinal);
            observations.Add(entryId, phases);
        }
        phases.Add(phase);
        provenance.Observe(entryId);
        return true;
    }

    public SyncExecutionReceipt Complete(SyncExecutionArtifact? artifact = null)
    {
        string[] observed = catalog.Entries.Where(IsCompleteObservation)
            .Select(entry => entry.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (observed.Length == 0)
            throw new InvalidOperationException("execution probe observed no entries");
        (SyncEntry Entry, SyncEntry Registration)[] absences = catalog.Entries
            .Where(entry => entry.Status == SyncEntryStatus.RegisteredDisabled &&
                !observations.ContainsKey(entry.Id))
            .Select(entry => (Entry: entry, Registration: FindRegistration(entry)))
            .Where(item => item.Registration is not null)
            .Select(item => (item.Entry, item.Registration!))
            .ToArray();
        var receipt = new SyncExecutionReceipt(
            1, binding.RunId, binding.InventoryDigest, binding.CoverageDigest,
            binding.TestId, binding.Tier, binding.ScenarioId, binding.Polarity,
            observed,
            absences.Select(item => item.Entry.Id).ToArray(),
            absences.Select(item => new SyncRegistrationWitness(
                item.Entry.Id, item.Registration.Id)).ToArray(),
            artifact);
        receipt.BindBinaryHashes(dllHash, pdbHash);
        provenance.Attach(receipt, observed, receipt.AbsentEntryIds);
        return receipt;
    }

    private bool IsCompleteObservation(SyncEntry entry)
    {
        if (!observations.TryGetValue(entry.Id, out HashSet<string>? phases))
            return false;
        if (entry.Kind != SyncEntryKind.Coroutine)
            return phases.Contains("hit");
        return phases.Contains("start") &&
            (phases.Contains("complete") || phases.Contains("cancel"));
    }

    private SyncEntry? FindRegistration(SyncEntry disabled)
    {
        if (binding.Polarity != SyncExecutionPolarity.Negative)
            return null;
        string owner = DeclaringType(disabled.FullyQualifiedSymbol);
        return catalog.Entries.FirstOrDefault(entry =>
            entry.Kind == SyncEntryKind.PacketRegistration &&
            DeclaringType(entry.FullyQualifiedSymbol) == owner &&
            observations.TryGetValue(entry.Id, out HashSet<string>? phases) &&
            phases.Contains("hit"));
    }

    private static string DeclaringType(string symbol)
    {
        if (!symbol.Contains('('))
            return symbol;
        int open = symbol.IndexOf('(');
        string member = open < 0 ? symbol : symbol[..open];
        int dot = member.LastIndexOf('.');
        return dot < 0 ? member : member[..dot];
    }
}

internal sealed record SyncExecutionInstrumentedAssembly(
    byte[] PeImage,
    byte[] PdbImage,
    string DllHash,
    string PdbHash);

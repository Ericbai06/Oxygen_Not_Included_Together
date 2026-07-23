namespace ONI_Together.HeadlessTests;

internal sealed class SyncCoverageExecutionInput
{
    public required SyncCatalogScan Catalog { get; init; }
    public required SyncCoverageManifest Manifest { get; init; }
    public required SyncTestRegistry Registry { get; init; }
    public required IReadOnlyList<SyncExecutionReceipt> Receipts { get; init; }
    public SyncExecutionEnvelope? Envelope { get; set; }
    public string? EvidenceRoot { get; set; }
}

internal sealed class SyncExecutionEnvelope
{
    public string RunId { get; set; } = "";
    public string InventoryDigest { get; set; } = "";
    public string CoverageDigest { get; set; } = "";
}

internal static class SyncCoverageExecutionValidator
{
    public static IReadOnlyList<SurfaceError> Validate(
        SyncCoverageExecutionInput input)
    {
        var errors = new List<SurfaceError>();
        foreach (SurfaceError error in SyncCoverageValidator.Validate(
                     input.Catalog, input.Manifest, input.Registry.Ids,
                     input.Registry.ScenarioIds))
            errors.Add(error);
        IReadOnlyDictionary<string, SyncEntry> catalog = input.Catalog.Entries
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(),
                StringComparer.Ordinal);
        ValidateReceipts(input, catalog, errors);
        foreach (SyncCoverageEntry entry in input.Manifest.Entries)
            ValidateEntry(input, entry, errors);
        return errors.Distinct().ToArray();
    }

    private static void ValidateReceipts(
        SyncCoverageExecutionInput input,
        IReadOnlyDictionary<string, SyncEntry> catalog,
        ICollection<SurfaceError> errors)
    {
        foreach (IGrouping<string, SyncExecutionReceipt> group in input.Receipts
                     .GroupBy(item => item.TestId, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
                errors.Add(new SurfaceError("execution_duplicate_receipt", group.Key));
        }
        foreach (SyncExecutionReceipt receipt in input.Receipts)
        {
            ValidateReceiptContract(input, receipt, errors);
            AddProvenanceErrors(receipt, catalog, errors);
            AddArtifactErrors(input, receipt, errors);
            foreach (string entryId in receipt.ExecutedEntryIds)
            {
                if (!catalog.ContainsKey(entryId))
                    errors.Add(new SurfaceError("execution_unknown_entry_receipt", entryId));
                else if (!ReceiptOwnsEntry(input.Manifest, receipt, entryId))
                    errors.Add(new SurfaceError(
                        "execution_unmapped_entry_receipt", entryId));
            }
        }
    }

    private static void AddProvenanceErrors(
        SyncExecutionReceipt receipt,
        IReadOnlyDictionary<string, SyncEntry> catalog,
        ICollection<SurfaceError> errors)
    {
        foreach (string entryId in receipt.ExecutedEntryIds)
        {
            if (!SyncExecutionProvenance.IsObserved(receipt, entryId))
                errors.Add(new SurfaceError("execution_unproven_entry_receipt", entryId));
            else if (catalog.TryGetValue(entryId, out SyncEntry? entry) &&
                !SyncExecutionProvenance.MatchesOrigin(receipt, entry))
                errors.Add(new SurfaceError(
                    "execution_provenance_origin_mismatch", entryId));
        }
    }

    private static void AddArtifactErrors(
        SyncCoverageExecutionInput input,
        SyncExecutionReceipt receipt,
        ICollection<SurfaceError> errors)
    {
        if (receipt.Tier is SyncExecutionTier.Ingame or SyncExecutionTier.Real &&
            input.EvidenceRoot is null)
            errors.Add(new SurfaceError(
                "runtime_artifact_root_missing", receipt.TestId));
        foreach (SurfaceError error in
                 new SyncExecutionRuntimeArtifactVerifier().Validate(
                     receipt, input.EvidenceRoot))
            errors.Add(error);
    }

    private static void ValidateReceiptContract(
        SyncCoverageExecutionInput input,
        SyncExecutionReceipt receipt,
        ICollection<SurfaceError> errors)
    {
        if (!input.Registry.TryGet(receipt.TestId, out SyncTestDefinition? definition))
        {
            errors.Add(new SurfaceError("execution_unknown_test_receipt", receipt.TestId));
            return;
        }
        if (definition.Tier != receipt.Tier)
            errors.Add(new SurfaceError("execution_tier_mismatch", receipt.TestId));
        if (!StringComparer.Ordinal.Equals(definition.ScenarioId, receipt.ScenarioId))
            errors.Add(new SurfaceError("execution_scenario_mismatch", receipt.TestId));
        if (!StringComparer.Ordinal.Equals(
                input.Manifest.InventoryDigest, receipt.InventoryDigest))
            errors.Add(new SurfaceError(
                "execution_inventory_digest_mismatch", receipt.TestId));
        if (!StringComparer.Ordinal.Equals(
                input.Manifest.CoverageDigest, receipt.CoverageDigest))
            errors.Add(new SurfaceError(
                "execution_coverage_digest_mismatch", receipt.TestId));
        if (!IsDigest(receipt.DllHash) || !IsDigest(receipt.PdbHash))
            errors.Add(new SurfaceError(
                "execution_binary_hash_invalid", receipt.TestId));
        ValidateEnvelope(input, receipt, errors);
    }

    private static bool IsDigest(string value)
    {
        return value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void ValidateEntry(
        SyncCoverageExecutionInput input,
        SyncCoverageEntry entry,
        ICollection<SurfaceError> errors)
    {
        SyncExecutionReceipt[] mapped = input.Receipts.Where(receipt =>
            IsMappedReceipt(entry, receipt) &&
            receipt.ExecutedEntryIds.Contains(entry.Id, StringComparer.Ordinal)).ToArray();
        if (entry.Status == SyncEntryStatus.RegisteredDisabled)
        {
            bool provenAbsent = input.Receipts.Any(receipt =>
                receipt.Polarity == SyncExecutionPolarity.Negative &&
                entry.NegativeTestIds.Contains(receipt.TestId, StringComparer.Ordinal) &&
                receipt.AbsentEntryIds.Contains(entry.Id, StringComparer.Ordinal) &&
                HasRegistrationWitness(receipt, entry.Id, input.Catalog.Entries) &&
                SyncExecutionProvenance.IsAbsent(receipt, entry.Id) &&
                input.Catalog.Entries.Any(catalogEntry =>
                    catalogEntry.Id == entry.Id &&
                    SyncExecutionProvenance.MatchesOrigin(receipt, catalogEntry)));
            if (!provenAbsent)
                errors.Add(new SurfaceError(
                    "registered_disabled_missing_negative_receipt", entry.Id));
        }
        else if (!mapped.Any(receipt => receipt.Polarity == SyncExecutionPolarity.Positive))
        {
            errors.Add(new SurfaceError("execution_missing_entry_receipt", entry.Id));
        }
        if (entry.HeadlessUnsupportedReason is not null &&
            !mapped.Any(receipt => IsValidRuntimeReceipt(input, receipt)))
            errors.Add(new SurfaceError("unity_only_missing_runtime_artifact", entry.Id));
    }

    private static bool HasRegistrationWitness(
        SyncExecutionReceipt receipt,
        string disabledId,
        IReadOnlyList<SyncEntry> catalog)
    {
        SyncEntry? disabled = catalog.SingleOrDefault(item => item.Id == disabledId);
        if (disabled is null)
            return false;
        return receipt.RegistrationWitnesses.Any(witness =>
            witness.EntryId == disabledId &&
            receipt.ExecutedEntryIds.Contains(
                witness.RegistrationEntryId, StringComparer.Ordinal) &&
            catalog.Any(registration =>
                registration.Id == witness.RegistrationEntryId &&
                registration.Kind == SyncEntryKind.PacketRegistration &&
                Owner(registration.FullyQualifiedSymbol) ==
                    Owner(disabled.FullyQualifiedSymbol)));
    }

    private static string Owner(string symbol)
    {
        if (!symbol.Contains('('))
            return symbol;
        string member = symbol[..symbol.IndexOf('(')];
        int dot = member.LastIndexOf('.');
        return dot < 0 ? member : member[..dot];
    }

    private static bool IsMappedReceipt(
        SyncCoverageEntry entry,
        SyncExecutionReceipt receipt)
    {
        return receipt.Polarity == SyncExecutionPolarity.Positive
            ? entry.TestIds.Contains(receipt.TestId, StringComparer.Ordinal)
            : entry.NegativeTestIds.Contains(receipt.TestId, StringComparer.Ordinal);
    }

    private static bool IsValidRuntimeReceipt(
        SyncCoverageExecutionInput input,
        SyncExecutionReceipt receipt)
    {
        if (receipt.Artifact is null)
            return false;
        if (input.EvidenceRoot is null)
            return false;
        bool kindMatches = receipt.Tier switch
        {
            SyncExecutionTier.Ingame => receipt.Artifact.Kind == "ingame-result",
            SyncExecutionTier.Real => receipt.Artifact.Kind == "real-run",
            _ => false
        };
        return kindMatches &&
            new SyncExecutionRuntimeArtifactVerifier().Validate(
                receipt, input.EvidenceRoot).Count == 0;
    }

    private static bool ReceiptOwnsEntry(
        SyncCoverageManifest manifest,
        SyncExecutionReceipt receipt,
        string entryId)
    {
        return manifest.Entries.Any(entry => entry.Id == entryId &&
            IsMappedReceipt(entry, receipt));
    }

    private static void ValidateEnvelope(
        SyncCoverageExecutionInput input,
        SyncExecutionReceipt receipt,
        ICollection<SurfaceError> errors)
    {
        SyncExecutionEnvelope? envelope = input.Envelope;
        if (envelope is null)
        {
            errors.Add(new SurfaceError(
                "execution_envelope_missing", receipt.TestId));
            return;
        }
        if (receipt.RunId != envelope.RunId)
            errors.Add(new SurfaceError("execution_run_id_mismatch", receipt.TestId));
        if (envelope.InventoryDigest != input.Manifest.InventoryDigest)
            errors.Add(new SurfaceError(
                "execution_envelope_inventory_digest_mismatch", receipt.TestId));
        if (envelope.CoverageDigest != input.Manifest.CoverageDigest)
            errors.Add(new SurfaceError(
                "execution_envelope_coverage_digest_mismatch", receipt.TestId));
    }
}

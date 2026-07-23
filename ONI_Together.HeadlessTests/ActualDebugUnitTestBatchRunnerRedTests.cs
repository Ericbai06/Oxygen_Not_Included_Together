using System.Text.Json.Nodes;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestBatchRunnerRedTests
{
    public static void CurrentDebugAssemblyRunsAsOneFailClosedBatch()
    {
        IActualDebugUnitTestBatchRunner runner =
            ActualDebugUnitTestBatchRunnerLoader.Load();
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load();
        True(input.ExpectedTests.Count > 0,
            "current Debug DLL has no UnitTestAttribute methods");
        ActualDebugUnitTestEnvironmentContract
            .ValidateStaticUnsupportedClassification(input);
        ActualDebugUnitTestEnvironmentContract.RequirePermissionsAssembly();

        ActualDebugUnitTestBatchResult first = runner.Run(input);
        runner.Validate(input, first);
        ValidateBatch(input, first);
        JsonContract(runner, input, first);
        MutationContract(runner, input, first);

        ActualDebugUnitTestBatchResult cleanControl = runner.Run(input);
        runner.Validate(input, cleanControl);
        ValidateBatch(input, cleanControl);
        EqualOutcomesAndEntrySets(first, cleanControl);
        MigrationRemainsFailClosed(input, first);

        Console.WriteLine("ACTUAL_UNIT_BATCH " +
            $"tests={first.Results.Count} passed={Count(first, ActualDebugUnitTestOutcome.Passed)} " +
            $"failed={Count(first, ActualDebugUnitTestOutcome.Failed)} " +
            $"notRun={Count(first, ActualDebugUnitTestOutcome.NotRun)} " +
            $"mapped={first.Results.Sum(result => result.ObservedEntryIds.Count)} " +
            $"success={first.Success} dll={first.DllHash} pdb={first.PdbHash} " +
            $"inventory={first.InventoryDigest}");
    }

    private static void ValidateBatch(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult batch)
    {
        Equal(1, batch.SchemaVersion);
        Equal(input.RunId, batch.RunId);
        Equal(input.DllHash, batch.DllHash);
        Equal(input.PdbHash, batch.PdbHash);
        Equal(input.InventoryDigest, batch.InventoryDigest);
        Equal(1, batch.InstrumentationCount);
        Equal(1, batch.AssemblyLoadCount);
        ActualDebugAccessBypassContractTests.ValidateBootstrap(
            input, batch.AccessBypassBootstrap);
        ValidateBootstrap(batch.Bootstrap);
        Equal(input.ExpectedTests.Count, batch.Results.Count);
        EqualSet(input.ExpectedTests.Select(test => test.TestId),
            batch.Results.Select(result => result.TestId));
        Equal(batch.Results.Count, batch.Results
            .Select(result => result.TestId)
            .Distinct(StringComparer.Ordinal).Count());
        Equal(!batch.Results.Any(result =>
            result.Outcome == ActualDebugUnitTestOutcome.Failed), batch.Success);

        IReadOnlyDictionary<string, ActualDebugUnitTestDescriptor> expected =
            input.ExpectedTests.ToDictionary(test => test.TestId,
                StringComparer.Ordinal);
        var epochs = new HashSet<int>();
        foreach (ActualDebugUnitTestResult result in batch.Results)
        {
            ActualDebugUnitTestDescriptor descriptor = expected[result.TestId];
            Equal(descriptor.MethodSymbol, result.MethodSymbol);
            Equal(input.DllHash, result.DllHash);
            Equal(input.PdbHash, result.PdbHash);
            Equal(input.InventoryDigest, result.InventoryDigest);
            True(result.DurationMs >= 0, "negative UnitTest duration");
            if (IsUnsupported(descriptor))
                ValidateNotRun(descriptor, result);
            else
                ValidateExecuted(input, result, epochs);
        }
        int executed = batch.Results.Count(result =>
            result.Outcome != ActualDebugUnitTestOutcome.NotRun);
        Equal(executed, epochs.Count);
        EqualSet(Enumerable.Range(1, executed), epochs);
    }

    private static void ValidateNotRun(
        ActualDebugUnitTestDescriptor descriptor,
        ActualDebugUnitTestResult result)
    {
        Equal(ActualDebugUnitTestOutcome.NotRun, result.Outcome);
        Equal(0, result.ObservationEpoch);
        EqualSequence([], result.ObservedEntryIds);
        EqualSequence(ExpectedEvidence(descriptor),
            result.RuntimeReferenceEvidence);
        Equal(NotRunReason(descriptor), result.Message);
        True(result.Receipt is null, "NotRun result contains a receipt");
    }

    private static void ValidateBootstrap(
        ActualDebugUnitTestBootstrapEvidence? bootstrap)
    {
        ActualDebugUnitTestBootstrapEvidence evidence = bootstrap ??
            throw new InvalidOperationException(
                "batch omitted PacketRegistry bootstrap evidence");
        Equal("ONI_Together.Networking.Packets.Architecture." +
            "PacketRegistry.RegisterDefaults()", evidence.MethodSymbol);
        Equal(1, evidence.InvocationCount);
        Equal(0, evidence.RegisteredPacketCountBefore);
        True(evidence.RegisteredPacketCountAfter > 0,
            "PacketRegistry bootstrap registered no packets");
        True(evidence.RegistryWasInitiallyEmpty,
            "fresh batch inherited packet registrations");
    }

    private static void ValidateExecuted(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestResult result,
        ISet<int> epochs)
    {
        True(result.Outcome != ActualDebugUnitTestOutcome.NotRun,
            "headless-safe UnitTest was not executed");
        True(result.ObservationEpoch > 0,
            "executed UnitTest lacks an observer epoch");
        True(epochs.Add(result.ObservationEpoch),
            "observer epoch was reused across UnitTests");
        EqualSequence([], result.RuntimeReferenceEvidence);
        if (result.Outcome == ActualDebugUnitTestOutcome.Failed)
        {
            EqualSequence([], result.ObservedEntryIds);
            True(result.Receipt is null,
                "failed UnitTest produced an execution mapping");
            return;
        }
        if (result.ObservedEntryIds.Count == 0)
        {
            True(result.Receipt is null,
                "entry-free passing UnitTest produced an empty receipt");
            return;
        }
        SyncExecutionReceipt receipt = result.Receipt ??
            throw new InvalidOperationException(
                "passing UnitTest with observed entries lacks a receipt");
        Equal(input.RunId + ":" + result.TestId, receipt.RunId);
        Equal(result.TestId, receipt.TestId);
        Equal(input.InventoryDigest, receipt.InventoryDigest);
        Equal(input.DllHash, receipt.DllHash);
        Equal(input.PdbHash, receipt.PdbHash);
        EqualSet(result.ObservedEntryIds, receipt.ExecutedEntryIds);
        foreach (string id in receipt.ExecutedEntryIds)
        {
            SyncEntry entry = input.Catalog.Entries.Single(item => item.Id == id);
            True(SyncExecutionProvenance.IsObserved(receipt, id) &&
                SyncExecutionProvenance.MatchesOrigin(receipt, entry),
                $"receipt lacks independent observed provenance: {id}");
        }
    }

    private static void JsonContract(
        IActualDebugUnitTestBatchRunner runner,
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult batch)
    {
        string json = runner.Serialize(batch);
        ActualDebugUnitTestBatchResult parsed = runner.Parse(json, input);
        EqualOutcomesAndEntrySets(batch, parsed);
        JsonObject unknown = JsonNode.Parse(json)!.AsObject();
        unknown["unknown"] = true;
        Throws<FormatException>(() => runner.Parse(unknown.ToJsonString(), input),
            "batch JSON accepted an unknown root field");
        JsonObject unknownResult = JsonNode.Parse(json)!.AsObject();
        unknownResult["results"]!.AsArray()[0]!.AsObject()["unknown"] = true;
        Throws<FormatException>(() =>
                runner.Parse(unknownResult.ToJsonString(), input),
            "batch JSON accepted an unknown result field");
    }

    private static void MutationContract(
        IActualDebugUnitTestBatchRunner runner,
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult batch)
    {
        ActualDebugUnitTestResult first = batch.Results[0];
        Reject(runner, input, batch with {
            Results = batch.Results.Append(first).ToArray()
        }, "duplicate UnitTest result was accepted");
        Reject(runner, input, batch with {
            Results = Replace(batch, first, first with {
                TestId = "headless:unit:unknown"
            })
        }, "unknown UnitTest result was accepted");
        Reject(runner, input, batch with { DllHash = Changed(batch.DllHash) },
            "batch DLL hash drift was accepted");
        Reject(runner, input, batch with { PdbHash = Changed(batch.PdbHash) },
            "batch PDB hash drift was accepted");
        Reject(runner, input, batch with {
            InventoryDigest = Changed(batch.InventoryDigest)
        }, "batch inventory digest drift was accepted");
        Reject(runner, input, batch with { Bootstrap = null },
            "missing PacketRegistry bootstrap evidence was accepted");
        Reject(runner, input, batch with { AccessBypassBootstrap = null },
            "missing access-bypass bootstrap evidence was accepted");
        ActualDebugAccessBypassContractTests.MutationContract(
            runner, input, batch);
        Reject(runner, input, batch with { Success = !batch.Success },
            "batch success did not derive from executed failures");

        ActualDebugUnitTestResult? mapped = batch.Results.FirstOrDefault(result =>
            result.Receipt is not null);
        True(mapped is not null,
            "current Debug batch produced no observed sync entry receipt");
        Reject(runner, input, batch with {
            Results = Replace(batch, mapped!, mapped! with {
                Outcome = ActualDebugUnitTestOutcome.NotRun,
                RuntimeReferenceEvidence = ["manual"],
                Message = "manual"
            })
        }, "NotRun result with receipt was accepted");
        Reject(runner, input, batch with {
            Results = Replace(batch, mapped!, mapped! with {
                Outcome = ActualDebugUnitTestOutcome.Failed
            })
        }, "Failed result with mapping was accepted");
        SyncExecutionReceipt manual = mapped!.Receipt! with {
            RunId = mapped.Receipt!.RunId + "-manual"
        };
        Reject(runner, input, batch with {
            Results = Replace(batch, mapped, mapped with { Receipt = manual })
        }, "manual entry claim without provenance was accepted");
    }

    private static void MigrationRemainsFailClosed(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult batch)
    {
        SyncExecutionReceipt[] receipts = batch.Results
            .Where(result =>
                result.Outcome == ActualDebugUnitTestOutcome.Passed &&
                result.Receipt is not null)
            .Select(result => result.Receipt!)
            .ToArray();
        True(receipts.Length > 0,
            "migration fixture has no real passing receipt");
        SyncCoverageManifest stale = CurrentCoverageTemporaryCopy(input);
        SyncTestRegistry registry = Registry(input.ExpectedTests);
        SyncCoverageMigrationResult migration =
            SyncCoverageManifestMigratorLoader.Load().Migrate(new(
                input.Catalog, input.InventoryDigest, stale, registry,
                receipts, input.DllHash, input.PdbHash));
        True(migration.AddedPositiveMappingCount > 0,
            "migrator added no real UnitTest mappings");
        True(migration.RemainingUnmappedCount > 0,
            "migrator incorrectly reported complete coverage");
        SyncCoverageManifest output =
            SyncCoverageManifest.Parse(migration.CoverageJson);
        IReadOnlyList<SurfaceError> errors = SyncCoverageValidator.Validate(
            input.Catalog, output, registry.Ids, registry.ScenarioIds);
        True(errors.Any(error =>
                error.Code == "manifest_missing_execution_test"),
            "migrated current coverage did not remain fail closed");
    }

    private static SyncCoverageManifest CurrentCoverageTemporaryCopy(
        ActualDebugUnitTestBatchInput input)
    {
        string source = Path.Combine(
            ActualDebugUnitTestBatchFixture.RepositoryRoot(),
            "sync-entry-coverage.json");
        string temporary = Path.Combine(Path.GetTempPath(),
            "oni-current-coverage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.Copy(source, temporary);
            SyncCoverageManifest current =
                SyncCoverageManifest.Parse(File.ReadAllText(temporary));
            IReadOnlyDictionary<string, SyncCoverageEntry> byId =
                current.Entries.GroupBy(entry => entry.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(),
                        StringComparer.Ordinal);
            return new SyncCoverageManifest(input.InventoryDigest, null,
                input.Catalog.Entries.Select(entry =>
                {
                    SyncCoverageEntry basis = byId.TryGetValue(
                        entry.Id, out SyncCoverageEntry? row)
                        ? row : new SyncCoverageEntry(
                            entry.Id, "unassigned", [], [], [], [],
                            entry.Status, null);
                    return basis with {
                        TestIds = [],
                        NegativeTestIds = [],
                        ScenarioIds = [],
                        Variants = entry.Variants.Select(variant => variant.Key)
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal).ToArray(),
                        Status = entry.Status
                    };
                }).ToArray());
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static SyncTestRegistry Registry(
        IReadOnlyList<ActualDebugUnitTestDescriptor> tests) =>
        SyncTestRegistry.Create(tests.Select(test =>
            new SyncTestDefinition(test.TestId, SyncExecutionTier.Headless, null))
            .Concat([
                new SyncTestDefinition("ingame:batch-placeholder",
                    SyncExecutionTier.Ingame, null),
                new SyncTestDefinition("python:batch-placeholder",
                    SyncExecutionTier.Python, null),
                new SyncTestDefinition("real:batch-placeholder",
                    SyncExecutionTier.Real, null)
            ]));

    private static IReadOnlyList<ActualDebugUnitTestResult> Replace(
        ActualDebugUnitTestBatchResult batch,
        ActualDebugUnitTestResult old,
        ActualDebugUnitTestResult replacement) =>
        batch.Results.Select(result =>
            ReferenceEquals(result, old) ? replacement : result).ToArray();

    private static void EqualOutcomesAndEntrySets(
        ActualDebugUnitTestBatchResult expected,
        ActualDebugUnitTestBatchResult actual)
    {
        IReadOnlyDictionary<string, ActualDebugUnitTestResult> byId =
            actual.Results.ToDictionary(result => result.TestId,
                StringComparer.Ordinal);
        EqualSet(expected.Results.Select(result => result.TestId), byId.Keys);
        foreach (ActualDebugUnitTestResult result in expected.Results)
        {
            ActualDebugUnitTestResult other = byId[result.TestId];
            Equal(result.Outcome, other.Outcome);
            EqualSet(result.ObservedEntryIds, other.ObservedEntryIds);
        }
    }

    private static string NotRunReason(
        ActualDebugUnitTestDescriptor descriptor) =>
        descriptor.HeadlessUnsupportedReason ??
        "not-headless: direct native terminal: " +
        string.Join(",", descriptor.DirectRuntimeReferences);

    private static bool IsUnsupported(
        ActualDebugUnitTestDescriptor descriptor) =>
        descriptor.HeadlessUnsupportedReason is not null ||
        descriptor.DirectRuntimeReferences.Count != 0;

    private static IReadOnlyList<string> ExpectedEvidence(
        ActualDebugUnitTestDescriptor descriptor) =>
        descriptor.HeadlessUnsupportedReason is not null
            ? [descriptor.HeadlessUnsupportedReason]
            : descriptor.DirectRuntimeReferences;

    private static int Count(
        ActualDebugUnitTestBatchResult batch,
        ActualDebugUnitTestOutcome outcome) =>
        batch.Results.Count(result => result.Outcome == outcome);

    private static string Changed(string value) =>
        (value[0] == '0' ? "1" : "0") + value[1..];

    private static void Reject(
        IActualDebugUnitTestBatchRunner runner,
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult result,
        string message) =>
        Throws<InvalidOperationException>(() => runner.Validate(input, result),
            message);

    private static void Throws<T>(Action action, string message)
        where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void EqualSet<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual)
    {
        if (!expected.ToHashSet().SetEquals(actual))
            throw new InvalidOperationException("sets differ");
    }

    private static void EqualSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("sequences differ");
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
}

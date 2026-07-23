using System.Reflection;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestCoverageDigestContractTests
{
    internal static void Validate()
    {
        ConstructorRemainsCompatible();
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load();
        string expected = RequiredCoverageDigest(input);
        Equal(CurrentManifestDigest(), expected);
        Console.WriteLine($"ACTUAL_UNIT_COVERAGE_DIGEST expected={expected}");
        SyncExecutionReceipt schemaFixture = ReceiptFixture(input);
        ValidateReceipt(input, schemaFixture);
        RejectSchemaMutations(input, schemaFixture);

        IActualDebugUnitTestBatchRunner runner =
            ActualDebugUnitTestBatchRunnerLoader.Load();
        ActualDebugUnitTestBatchResult first = runner.Run(input);
        SyncExecutionReceipt receipt = MappedReceipt(first);
        ValidateReceipt(input, receipt);
        RejectRunner(runner, input, first, receipt with {
            CoverageDigest = new string('c', 64)
        });
        ValidateJsonRoundtrip(runner, input, first);

        ActualDebugUnitTestBatchResult cleanControl = runner.Run(input);
        SyncExecutionReceipt cleanReceipt = MappedReceipt(cleanControl);
        ValidateReceipt(input, cleanReceipt);
        Equal(receipt.CoverageDigest, cleanReceipt.CoverageDigest);
        ValidateJsonRoundtrip(runner, input, cleanControl);
        Console.WriteLine(
            $"ACTUAL_UNIT_COVERAGE_DIGEST_PASS digest={expected}");
    }

    private static void ConstructorRemainsCompatible()
    {
        ParameterInfo parameter = typeof(ActualDebugUnitTestBatchInput)
            .GetConstructors().Single().GetParameters().Single(item =>
                item.Name == nameof(ActualDebugUnitTestBatchInput.CoverageDigest));
        True(parameter.HasDefaultValue && parameter.DefaultValue is null,
            "CoverageDigest broke existing batch input constructors");
    }

    private static string CurrentManifestDigest()
    {
        string path = Path.Combine(
            ActualDebugUnitTestBatchFixture.RepositoryRoot(),
            "sync-entry-coverage.json");
        using JsonDocument document =
            JsonDocument.Parse(File.ReadAllText(path));
        return SyncCanonicalJson.Sha256(document.RootElement);
    }

    private static string RequiredCoverageDigest(
        ActualDebugUnitTestBatchInput input)
    {
        if (string.IsNullOrWhiteSpace(input.CoverageDigest))
            throw new InvalidOperationException(
                "batch input omitted coverageDigest");
        return input.CoverageDigest;
    }

    private static SyncExecutionReceipt MappedReceipt(
        ActualDebugUnitTestBatchResult batch) =>
        batch.Results.Select(result => result.Receipt)
            .FirstOrDefault(receipt => receipt is not null) ??
        throw new InvalidOperationException(
            "actual Debug execution produced no mapped receipt");

    private static void ValidateReceipt(
        ActualDebugUnitTestBatchInput input,
        SyncExecutionReceipt receipt)
    {
        SyncExecutionReceipt parsed =
            SyncExecutionReceipt.Parse(ReceiptJson(receipt));
        Equal(RequiredCoverageDigest(input), receipt.CoverageDigest);
        Equal(receipt.CoverageDigest, parsed.CoverageDigest);
    }

    private static SyncExecutionReceipt ReceiptFixture(
        ActualDebugUnitTestBatchInput input)
    {
        var receipt = new SyncExecutionReceipt(
            1,
            input.RunId + ":coverage-contract",
            input.InventoryDigest,
            RequiredCoverageDigest(input),
            "headless:coverage-contract",
            SyncExecutionTier.Headless,
            null,
            SyncExecutionPolarity.Positive,
            [input.Catalog.Entries.First().Id],
            [],
            [],
            null);
        receipt.BindBinaryHashes(input.DllHash, input.PdbHash);
        return receipt;
    }

    private static void RejectSchemaMutations(
        ActualDebugUnitTestBatchInput input,
        SyncExecutionReceipt receipt)
    {
        RejectParser(receipt with { CoverageDigest = "" });
        RejectParser(receipt with { CoverageDigest = new string('c', 64) });
        string wrong = "sha256:" + new string('0', 64);
        Throws<InvalidOperationException>(() =>
            ValidateReceipt(input, receipt with { CoverageDigest = wrong }),
            "mapped receipt accepted the wrong canonical coverageDigest");
    }

    private static void ValidateJsonRoundtrip(
        IActualDebugUnitTestBatchRunner runner,
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult batch)
    {
        string json = runner.Serialize(batch);
        ActualDebugUnitTestBatchResult parsed = runner.Parse(json, input);
        Equal(RequiredCoverageDigest(input),
            MappedReceipt(parsed).CoverageDigest);
    }

    private static void RejectParser(SyncExecutionReceipt receipt) =>
        ThrowsExact<FormatException>(
            () => SyncExecutionReceipt.Parse(ReceiptJson(receipt)),
            "execution receipt has invalid coverageDigest");

    private static void RejectRunner(
        IActualDebugUnitTestBatchRunner runner,
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult batch,
        SyncExecutionReceipt receipt)
    {
        ActualDebugUnitTestResult mapped = batch.Results.Single(result =>
            result.Receipt is not null &&
            result.TestId == receipt.TestId);
        ActualDebugUnitTestBatchResult mutated = batch with {
            Results = batch.Results.Select(result =>
                ReferenceEquals(result, mapped)
                    ? mapped with { Receipt = receipt }
                    : result).ToArray()
        };
        Throws<InvalidOperationException>(
            () => runner.Validate(input, mutated),
            "runner accepted a synthetic unprefixed coverageDigest");
    }

    private static string ReceiptJson(SyncExecutionReceipt receipt) =>
        JsonSerializer.Serialize(new
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
        });

    private static void Throws<T>(Action action, string message)
        where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void ThrowsExact<T>(Action action, string message)
        where T : Exception
    {
        try { action(); }
        catch (T error) when (error.Message == message) { return; }
        throw new InvalidOperationException(
            $"expected exact failure: {message}");
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

using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionProbeContractTests
{
    private const string InventoryDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CoverageDigest =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PositiveTestId = "headless:execution-probe-fixture";
    private const string NegativeTestId = "headless:registered-disabled-probe";

    public static void RuntimeFixtureRecordsObservedEntryKinds()
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        ISyncExecutionProbeSession session = Start(catalog, PositiveRun());
        SyncEntry[] entries = ExecutableEntries(catalog);

        Invoke(session, "PacketRuntime", "RunBoth");
        Invoke(session, "HarmonyRuntime", "Run");
        Invoke(session, "EventRuntime", "Attach");
        Invoke(session, "EventRuntime", "Publish");
        Complete(StartCoroutine(session));
        Invoke(session, "StateRuntime", "Apply");
        SyncExecutionReceipt receipt = session.Complete();

        EqualSet(entries.Select(entry => entry.Id), receipt.ExecutedEntryIds);
        AssertFixtureSideEffects(session);
        RequireKinds(entries,
        [
            SyncEntryKind.PacketRegistration, SyncEntryKind.PacketSend,
            SyncEntryKind.PacketDeserialize, SyncEntryKind.PacketDispatch,
            SyncEntryKind.HarmonyPatch, SyncEntryKind.EventSubscribe,
            SyncEntryKind.EventPublish, SyncEntryKind.Coroutine,
            SyncEntryKind.StateMachine,
        ]);
    }

    public static void DuplicateCallsitesRemainDistinctAndUntriggeredStayAbsent()
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry[] repeated = RepeatedSends(catalog);
        SyncExecutionReceipt first = RunSendCase(
            catalog, "RunFirstOnly", "run-first-send");
        SyncExecutionReceipt second = RunSendCase(
            catalog, "RunSecondOnly", "run-second-send");
        string[] firstIds = SendIds(first, repeated);
        string[] secondIds = SendIds(second, repeated);
        Equal(1, firstIds.Length);
        Equal(1, secondIds.Length);
        False(firstIds[0] == secondIds[0], "repeated callsites collapsed to one ID");

        SyncExecutionReceipt joint = RunSendCase(catalog, "RunBoth", "run-both-sends");
        string[] jointIds = SendIds(joint, repeated);
        Equal(2, jointIds.Length);
        Equal(2, jointIds.Distinct(StringComparer.Ordinal).Count());
        EqualSet(firstIds.Concat(secondIds), jointIds);

        ISyncExecutionProbeSession neither = Start(catalog, PositiveRun("run-no-send"));
        Throws<InvalidOperationException>(() => neither.Complete());

        ISyncExecutionProbeSession stateSession = Start(catalog, PositiveRun("run-state"));
        Invoke(stateSession, "StateRuntime", "Apply");
        SyncExecutionReceipt state = stateSession.Complete();
        SyncEntry stateEntry = catalog.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.StateMachine);
        EqualSet([stateEntry.Id], state.ExecutedEntryIds);
    }

    public static void PdbIdentityIsRequiredForExecutionProvenance()
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncExecutionFixtureAssembly empty =
            SyncExecutionProbeFixtureCatalog.CompileWithoutPdb();
        SyncExecutionFixtureAssembly mismatched =
            SyncExecutionProbeFixtureCatalog.CompileWithMismatchedPdb();
        ISyncExecutionProbeFactory factory = SyncExecutionProbeFactoryLoader.Load();

        Throws<FormatException>(() => factory.Start(
            Binding(PositiveRun("run-empty-pdb")), catalog, empty));
        Throws<FormatException>(() => factory.Start(
            Binding(PositiveRun("run-mismatched-pdb")), catalog, mismatched));
    }

    public static void CoroutineRequiresStartAndTerminalObservation()
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry coroutine = catalog.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.Coroutine);
        ISyncExecutionProbeSession incomplete = Start(catalog,
            PositiveRun("run-coroutine-start-only"));
        _ = StartCoroutine(incomplete).MoveNext();
        Throws<InvalidOperationException>(() => incomplete.Complete());

        ISyncExecutionProbeSession completed = Start(catalog,
            PositiveRun("run-coroutine-complete"));
        Complete(StartCoroutine(completed));
        EqualSet([coroutine.Id], completed.Complete().ExecutedEntryIds);

        ISyncExecutionProbeSession cancelled = Start(catalog,
            PositiveRun("run-coroutine-cancel"));
        IEnumerator cancelledRoutine = StartCoroutine(cancelled);
        _ = cancelledRoutine.MoveNext();
        (cancelledRoutine as IDisposable)?.Dispose();
        EqualSet([coroutine.Id], cancelled.Complete().ExecutedEntryIds);
    }

    public static void ManualIdsAndStaticClaimsCannotForgeReceipt()
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry entry = ExecutableEntries(catalog).First();
        SyncExecutionReceipt forged = ParseForgedReceipt(entry.Id);
        IReadOnlyList<SurfaceError> errors = Validate(catalog,
            new ValidationFixture(entry, forged, false));

        HasError(errors, "execution_unproven_entry_receipt");
        Throws<InvalidOperationException>(() =>
            Start(catalog, PositiveRun()).Complete());
        AssertStaticClaimsRejected(entry.Id);
    }

    public static void RegisteredDisabledProvesRegistrationWithoutSend()
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry registration = catalog.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.PacketRegistration &&
            entry.FullyQualifiedSymbol.Contains(
                "DisabledPacketRuntime.Register", StringComparison.Ordinal));
        SyncEntry disabledSend = catalog.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.PacketSend &&
            entry.Status == SyncEntryStatus.RegisteredDisabled);
        ISyncExecutionProbeSession session = Start(catalog, new ProbeRun(
            NegativeTestId, "run-disabled-probe", SyncExecutionPolarity.Negative));

        Invoke(session, "DisabledPacketRuntime", "Register");
        Invoke(session, "DisabledPacketRuntime", "Send");
        SyncExecutionReceipt receipt = session.Complete();
        IReadOnlyList<SurfaceError> errors = Validate(catalog,
            new ValidationFixture(disabledSend, receipt, true));

        True(receipt.ExecutedEntryIds.Contains(registration.Id, StringComparer.Ordinal),
            "registration observation missing");
        False(receipt.ExecutedEntryIds.Contains(disabledSend.Id, StringComparer.Ordinal),
            "registered-disabled send was recorded as executed");
        EqualSet([disabledSend.Id], ReceiptIds(receipt, "AbsentEntryIds"));
        AssertRegistrationWitness(receipt, disabledSend.Id, registration.Id);
        NoError(errors, "registered_disabled_missing_negative_receipt");
    }

    public static void RegisteredDisabledAbsenceRequiresSameOwnerRegistration()
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry disabledSend = catalog.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.PacketSend &&
            entry.Status == SyncEntryStatus.RegisteredDisabled);
        ISyncExecutionProbeSession session = Start(catalog, new ProbeRun(
            NegativeTestId, "run-wrong-owner-registration", SyncExecutionPolarity.Negative));

        Invoke(session, "PacketRuntime", "RunBoth");
        Invoke(session, "DisabledPacketRuntime", "Send");
        SyncExecutionReceipt receipt = session.Complete();
        IReadOnlyList<SurfaceError> errors = Validate(catalog,
            new ValidationFixture(disabledSend, receipt, true));

        HasError(errors, "registered_disabled_missing_negative_receipt");
    }

    private static ISyncExecutionProbeSession Start(
        SyncCatalogScan catalog,
        ProbeRun run)
    {
        SyncExecutionFixtureAssembly fixture = SyncExecutionProbeFixtureCatalog.Compile();
        return SyncExecutionProbeFactoryLoader.Load().Start(
            Binding(run), catalog, fixture);
    }

    private static SyncExecutionProbeBinding Binding(ProbeRun run)
    {
        return new SyncExecutionProbeBinding
        {
            RunId = run.RunId,
            TestId = run.TestId,
            Tier = SyncExecutionTier.Headless,
            ScenarioId = null,
            Polarity = run.Polarity,
            InventoryDigest = InventoryDigest,
            CoverageDigest = CoverageDigest,
        };
    }

    private static ProbeRun PositiveRun(string runId = "run-probe-fixture")
    {
        return new ProbeRun(PositiveTestId, runId, SyncExecutionPolarity.Positive);
    }

    private static SyncEntry[] ExecutableEntries(SyncCatalogScan catalog)
    {
        string[] owners =
        [
            "PacketRuntime.Run", "DoorPatch.Postfix", "EventRuntime.Attach",
            "EventRuntime.Publish", "CoroutineRuntime.Start", "StateRuntime.Apply",
        ];
        return catalog.Entries.Where(entry => entry.Status == SyncEntryStatus.Active &&
            owners.Any(owner => entry.FullyQualifiedSymbol.Contains(
                owner, StringComparison.Ordinal))).ToArray();
    }

    private static SyncEntry[] RepeatedSends(SyncCatalogScan catalog)
    {
        return catalog.Entries.Where(entry =>
                entry.Kind == SyncEntryKind.PacketSend &&
                entry.FullyQualifiedSymbol.Contains("PacketRuntime.Run", StringComparison.Ordinal))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray() switch
        {
            { Length: 2 } entries => entries,
            var entries => throw new InvalidOperationException(
                $"expected two repeated send callsites, got {entries.Length}"),
        };
    }

    private static SyncExecutionReceipt RunSendCase(
        SyncCatalogScan catalog,
        string method,
        string runId)
    {
        ISyncExecutionProbeSession session = Start(catalog, PositiveRun(runId));
        Invoke(session, "PacketRuntime", method);
        return session.Complete();
    }

    private static string[] SendIds(
        SyncExecutionReceipt receipt,
        IReadOnlyList<SyncEntry> sends)
    {
        IReadOnlySet<string> expected = sends.Select(entry => entry.Id).ToHashSet(
            StringComparer.Ordinal);
        return receipt.ExecutedEntryIds.Where(expected.Contains).ToArray();
    }

    private static void Invoke(
        ISyncExecutionProbeSession session,
        string typeName,
        string methodName)
    {
        Type type = session.RuntimeAssembly.GetType(typeName, throwOnError: true)!;
        MethodInfo method = type.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Static)!;
        _ = method.Invoke(null, method.GetParameters().Length == 0
            ? null
            : [Activator.CreateInstance(method.GetParameters()[0].ParameterType)]);
    }

    private static IEnumerator StartCoroutine(ISyncExecutionProbeSession session)
    {
        Type type = session.RuntimeAssembly.GetType(
            "CoroutineRuntime", throwOnError: true)!;
        object instance = Activator.CreateInstance(type)!;
        return (IEnumerator)type.GetMethod("Start")!.Invoke(instance, null)!;
    }

    private static void Complete(IEnumerator routine)
    {
        while (routine.MoveNext()) { }
    }

    private static string[] ReceiptIds(object receipt, string property)
    {
        object? value = receipt.GetType().GetProperty(property)?.GetValue(receipt);
        return value is IEnumerable<string> ids
            ? ids.ToArray()
            : throw new InvalidOperationException($"receipt lacks typed {property}");
    }

    private static void AssertRegistrationWitness(
        object receipt,
        string entryId,
        string registrationId)
    {
        object? raw = receipt.GetType().GetProperty(
            "RegistrationWitnesses")?.GetValue(receipt);
        object witness = raw is System.Collections.IEnumerable values
            ? values.Cast<object>().Single()
            : throw new InvalidOperationException("receipt lacks typed registration witnesses");
        Equal(entryId, (witness.GetType().GetProperty(
            "EntryId")?.GetValue(witness) as string)!);
        Equal(registrationId, (witness.GetType().GetProperty(
            "RegistrationEntryId")?.GetValue(witness) as string)!);
    }

    private static void AssertFixtureSideEffects(ISyncExecutionProbeSession session)
    {
        True(Static<bool>(session, "PacketRegistry", "Registered"),
            "packet registration target did not run");
        Equal(2, Static<int>(session, "PacketSender", "SendCount"));
        True(Static<bool>(session, "ProbePacket", "Deserialized"),
            "packet deserialize target did not run");
        True(Static<bool>(session, "ProbePacket", "Dispatched"),
            "packet dispatch target did not run");
        Equal(1, Static<int>(session, "DoorPatch", "Invocations"));
        True(Static<bool>(session, "Door", "LastOpen"),
            "Harmony target did not run");
        Equal(1, Static<int>(session, "Game", "SubscribeCount"));
        Equal(1, Static<int>(session, "Game", "TriggerCount"));
        True(Static<bool>(session, "StateRuntime", "Transitioned"),
            "state-machine target did not run");
    }

    private static T Static<T>(
        ISyncExecutionProbeSession session,
        string typeName,
        string propertyName) where T : notnull
    {
        Type type = session.RuntimeAssembly.GetType(typeName, throwOnError: true)!;
        return (T)type.GetProperty(propertyName)!.GetValue(null)!;
    }

    private static IReadOnlyList<SurfaceError> Validate(
        SyncCatalogScan catalog,
        ValidationFixture fixture)
    {
        string testId = fixture.Disabled ? NegativeTestId : PositiveTestId;
        SyncCoverageManifest manifest = Manifest(
            fixture.Entry, testId, fixture.Disabled);
        SyncTestRegistry registry = SyncTestRegistry.Create([
            new SyncTestDefinition(testId, SyncExecutionTier.Headless, null),
            new SyncTestDefinition(
                "ingame:execution-probe-placeholder", SyncExecutionTier.Ingame, null),
            new SyncTestDefinition(
                "python:execution-probe-placeholder", SyncExecutionTier.Python, null),
            new SyncTestDefinition(
                "real:execution-probe-placeholder", SyncExecutionTier.Real, null),
        ]);
        return SyncCoverageExecutionValidator.Validate(new SyncCoverageExecutionInput
        {
            Catalog = catalog,
            Manifest = manifest,
            Registry = registry,
            Receipts = [fixture.Receipt],
            Envelope = new SyncExecutionEnvelope
            {
                RunId = fixture.Receipt.RunId,
                InventoryDigest = fixture.Receipt.InventoryDigest,
                CoverageDigest = fixture.Receipt.CoverageDigest,
            },
        });
    }

    private static SyncCoverageManifest Manifest(
        SyncEntry entry,
        string testId,
        bool disabled)
    {
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            inventoryDigest = InventoryDigest,
            entries = new[]
            {
                new
                {
                    id = entry.Id,
                    domain = "execution-probe-fixture",
                    testIds = disabled ? Array.Empty<string>() : new[] { testId },
                    negativeTestIds = disabled ? new[] { testId } : Array.Empty<string>(),
                    scenarioIds = Array.Empty<string>(),
                    variants = entry.Variants.Select(variant => variant.Key).ToArray(),
                    status = entry.Status.ToString(),
                },
            },
        });
        return SyncCoverageManifest.Parse(json);
    }

    private static SyncExecutionReceipt ParseForgedReceipt(string entryId)
    {
        return SyncExecutionReceipt.Parse(ForgedJson(entryId));
    }

    private static string ForgedJson(
        string entryId,
        IReadOnlyDictionary<string, object?>? claims = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["runId"] = "run-forged",
            ["inventoryDigest"] = InventoryDigest,
            ["coverageDigest"] = CoverageDigest,
            ["dllHash"] = new string('d', 64),
            ["pdbHash"] = new string('e', 64),
            ["testId"] = PositiveTestId,
            ["tier"] = "headless",
            ["scenarioId"] = null,
            ["polarity"] = "positive",
            ["executedEntryIds"] = new[] { entryId },
            ["absentEntryIds"] = Array.Empty<string>(),
            ["registrationWitnesses"] = Array.Empty<object>(),
            ["artifact"] = null,
        };
        if (claims is not null)
        {
            foreach ((string key, object? value) in claims)
                values[key] = value;
        }
        return JsonSerializer.Serialize(values);
    }

    private static void AssertStaticClaimsRejected(string entryId)
    {
        IReadOnlyDictionary<string, object?>[] mutations =
        [
            new Dictionary<string, object?> { ["classNames"] = new[] { "PacketSender" } },
            new Dictionary<string, object?> { ["sourceFiles"] = new[] { "Packet.cs" } },
            new Dictionary<string, object?> { ["entryCount"] = 1 },
        ];
        foreach (IReadOnlyDictionary<string, object?> mutation in mutations)
            Throws<FormatException>(() =>
                SyncExecutionReceipt.Parse(ForgedJson(entryId, mutation)));
    }

    private static void RequireKinds(
        IEnumerable<SyncEntry> entries,
        IEnumerable<SyncEntryKind> kinds)
    {
        IReadOnlySet<SyncEntryKind> actual = entries.Select(entry => entry.Kind).ToHashSet();
        foreach (SyncEntryKind kind in kinds)
            True(actual.Contains(kind), $"fixture missing {kind}");
    }

    private static void HasError(
        IEnumerable<SurfaceError> errors,
        string code)
    {
        True(errors.Any(error => error.Code == code), $"missing {code}");
    }

    private static void NoError(IEnumerable<SurfaceError> errors, string code)
    {
        False(errors.Any(error => error.Code == code), $"unexpected {code}");
    }

    private static void EqualSet(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        True(expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual),
            "entry ID sets differ");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}, got {actual}");
    }

    private static void True(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    private sealed record ProbeRun(
        string TestId,
        string RunId,
        SyncExecutionPolarity Polarity);

    private sealed record ValidationFixture(
        SyncEntry Entry,
        SyncExecutionReceipt Receipt,
        bool Disabled);
}

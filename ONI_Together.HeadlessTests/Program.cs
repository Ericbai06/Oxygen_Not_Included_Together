namespace ONI_Together.HeadlessTests;

internal static partial class Program
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string FlatulencePath = Path.Combine(
        RepositoryRoot, "ONI_Together/Patches/Duplicant/Flatulence_Patch.cs");
    private static readonly string ImmigrantPath = Path.Combine(
        RepositoryRoot, "ONI_Together/Patches/GamePatches/ImmigrantScreenPatch.cs");

    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "fault-debug-only",
                StringComparison.OrdinalIgnoreCase))
            return FaultDebugOnlyAssemblyTests.Run(args.Skip(1).ToArray());

        if (args.Length > 0 && string.Equals(args[0], "catalog",
                StringComparison.OrdinalIgnoreCase))
            return SyncCatalogCli.Run(args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "execution-probe-red",
                StringComparison.OrdinalIgnoreCase))
            return SyncExecutionProbeRedSuite.Run();

        if (args.Length > 0 && string.Equals(args[0], "actual-unit-probe-red",
                StringComparison.OrdinalIgnoreCase))
            return SyncActualUnitTestExecutionRedSuite.Run();

        if (args.Length > 0 && string.Equals(args[0], "actual-unit-batch-red",
                StringComparison.OrdinalIgnoreCase))
            return ActualDebugUnitTestBatchRunnerRedSuite.Run();

        if (args.Length > 0 && string.Equals(args[0], "actual-unit-skip-metadata",
                StringComparison.OrdinalIgnoreCase))
            return ActualDebugUnitTestSkipMetadataContractSuite.Run();

        if (args.Length > 0 && string.Equals(args[0],
                "actual-unit-coverage-digest-red",
                StringComparison.OrdinalIgnoreCase))
            return ActualDebugUnitTestCoverageDigestContractSuite.Run();

        if (args.Length > 0 && string.Equals(args[0], "coverage-migrate-red",
                StringComparison.OrdinalIgnoreCase))
            return SyncCoverageManifestMigratorRedSuite.Run();

        if (args.Length > 0 && string.Equals(args[0], "coverage-migrate",
                StringComparison.OrdinalIgnoreCase))
            return new SyncCoverageManifestMigrator().RunCli(
                args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "actual-unit-export",
                StringComparison.OrdinalIgnoreCase))
            return new ActualDebugUnitTestExporter().RunCli(
                args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0],
                ActualDebugUnitTestExecutionCommands.Preflight,
                StringComparison.OrdinalIgnoreCase))
            return new ActualDebugUnitTestPreflight().RunCli(
                args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0],
                ActualDebugUnitTestExecutionCommands.BatchOnce,
                StringComparison.OrdinalIgnoreCase))
            return new ActualDebugUnitTestBatchOnce().RunCli(
                args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0],
                ActualDebugUnitTestExecutionCommands.BatchMilestone,
                StringComparison.OrdinalIgnoreCase))
            return new ActualDebugUnitTestBatchMilestone().RunCli(
                args, Console.Out, Console.Error);

        if (args.Length > 0 && string.Equals(args[0],
                "coverage-migrate-process-red",
                StringComparison.OrdinalIgnoreCase))
            return SyncCoverageMigrateProcessRedSuite.Run();

        if (args.Length > 0 && (string.Equals(args[0], "build-architecture",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "build-architecture-red",
                    StringComparison.OrdinalIgnoreCase)))
            return BuildAuthorityArchitectureContractTests.RunCli(
                Console.Out, Console.Error);

        (string Name, Action Test)[] tests =
        [
            ("packet discovery follows runtime registration", PacketDiscoveryFollowsRuntime),
            ("packet structure errors are reported", PacketStructureErrorsAreReported),
            ("Harmony patches expose entrypoints", HarmonyPatchesExposeEntrypoints),
            ("production sync surface is structurally complete", ProductionSurfaceIsComplete),
            ("runtime registration excludes abstract packets", RegistrationExcludesAbstract),
            ("Personality precedes MinionIdentity.OnSpawn", PersonalityPrecedesOnSpawn),
            ("Flatulence authority follows preview and network role", FlatulenceAuthorityIsCorrect),
            ("controller precedes SetMinion", ControllerPrecedesSetMinion),
            ("production lifecycle patches are safe", ProductionLifecyclePatchesAreSafe),
            ("Release excludes fault injection surface", FaultDebugOnlyAssemblyTests.ReleaseAssemblyExcludesFaultSurface),
            ("Debug retains exact 40-case fault surface", FaultDebugOnlyAssemblyTests.DebugAssemblyRetainsFaultSurface),
            ("source gate detects inverted preview guard", DetectsInvertedPreviewGuard),
            ("source gate detects delayed SetMinion", DetectsDelayedSetMinion),
            ("source gate detects SetMinion before controller", DetectsMissingController),
            ("fault source gate detects armed SetMinion before controller", DetectsArmedMinionBeforeController),
            ("catalog finds method Harmony and dynamic patch", SyncCatalogTests.CatalogFindsMethodHarmonyAndDynamicPatch),
            ("catalog finds event subscribe and unsubscribe", SyncCatalogTests.CatalogFindsEventSubscribeAndUnsubscribe),
            ("catalog finds Trigger publisher", SyncCatalogTests.CatalogFindsTriggerPublisher),
            ("catalog links coroutine to IEnumerator", SyncCatalogTests.CatalogLinksCoroutineToIterator),
            ("catalog resolves generalized coroutine identity", SyncCoverageBlockerAdversarialTests.CoroutineIdentityResolvesDirectLocalAndAssignedForms),
            ("catalog finds state machine hooks", SyncCatalogTests.CatalogFindsStateMachineHooks),
            ("catalog tracks Debug Release and OS variants", SyncCatalogTests.CatalogTracksDebugReleaseAndOsVariants),
            ("catalog stable IDs ignore line numbers", SyncCatalogTests.CatalogStableIdsIgnoreLineNumbers),
            ("manifest accepts complete catalog mapping", SyncCatalogTests.ManifestAcceptsCompleteCatalogMapping),
            ("manifest rejects missing entry", SyncCatalogTests.ManifestRejectsMissingEntry),
            ("manifest rejects orphan entry", SyncCatalogTests.ManifestRejectsOrphanEntry),
            ("manifest rejects duplicate entry", SyncCatalogTests.ManifestRejectsDuplicateEntry),
            ("manifest rejects unknown test ID", SyncCatalogTests.ManifestRejectsUnknownTestId),
            ("manifest rejects unresolved active target", SyncCatalogTests.ManifestRejectsUnresolvedActiveTarget),
            ("manifest rejects undeclared variant", SyncCatalogTests.ManifestRejectsUndeclaredVariant),
            ("manifest requires negative execution for registered-disabled", SyncCatalogTests.ManifestRequiresNegativeExecutionForRegisteredDisabled),
            ("catalog evaluates eight build variants", SyncCatalogProjectTests.CatalogEvaluatesEightBuildVariants),
            ("MSBuild loader evaluates compile items and references", SyncCatalogProjectTests.MsBuildLoaderEvaluatesCompileItemsAndReferences),
            ("catalog includes full packet transport chain", SyncCatalogProjectTests.CatalogIncludesFullPacketTransportChain),
            ("inventory serialization is deterministic and complete", SyncCatalogProjectTests.InventorySerializationIsDeterministicAndComplete),
            ("coverage validator fails closed for execution and runtime layers", SyncCatalogProjectTests.CoverageValidatorFailsClosedForExecutionAndRuntimeLayers),
            ("repeated callsites have distinct stable IDs", SyncCatalogProjectTests.RepeatedCallsitesHaveDistinctStableIds),
            ("MSBuild loader isolates requested OS symbols", ProductionCatalogCommandTests.LoaderIsolatesRequestedOsSymbols),
            ("catalog command writes deterministic inventory and validates coverage", ProductionCatalogCommandTests.CatalogCommandWritesDeterministicInventoryAndValidatesCoverage),
            ("catalog command rejects coverage inventory digest mismatch", ProductionCatalogCommandTests.CatalogCommandRejectsCoverageInventoryDigestMismatch),
            ("catalog command rejects malformed and unresolved projects", ProductionCatalogCommandTests.CatalogCommandRejectsMalformedAndUnresolvedProjects),
            ("catalog CLI writes inventory and summaries", SyncCatalogCliTests.CatalogCliWritesInventory),
            ("catalog CLI rejects missing and unknown options", SyncCatalogCliTests.CatalogCliRejectsInvalidOptions),
            ("catalog CLI rejects missing project and game libs", SyncCatalogCliTests.CatalogCliRejectsMissingPaths),
            ("catalog CLI rejects invalid coverage", SyncCatalogCliTests.CatalogCliRejectsInvalidCoverage),
            ("actual ONI project catalog audit is fail closed", ProductionCatalogCommandTests.ActualOniProjectCatalogAudit),
            ("catalog classifies path ownership and status conflicts", SyncCatalogClassificationTests.CatalogClassifiesPathOwnership),
            ("registered-disabled status propagates to packet sends", SyncCatalogClassificationTests.RegisteredDisabledPropagatesToPacketSend),
            ("catalog records reflection packet registrations", SyncCatalogClassificationTests.CatalogRecordsReflectionPacketRegistrations),
            ("packet registration names do not match fingerprints", SyncCatalogClassificationTests.PacketRegistrationNamesAreExact),
            ("actual ONI catalog has complete status classification", SyncCatalogClassificationTests.ActualOniCatalogStatusClassification),
            ("coverage registry requires stable four-tier IDs", SyncCoverageExecutionReceiptTests.RegistryRequiresStableIdsForAllFourTiers),
            ("coverage registry rejects a missing tier", SyncCoverageExecutionReceiptTests.RegistryRejectsMissingExecutionTier),
            ("coverage receipt JSON is exact and fail closed", SyncCoverageExecutionReceiptTests.ReceiptJsonUsesExactFailClosedSchema),
            ("coverage receipt requires typed provenance fields", SyncExecutionReceiptProvenanceParityTests.ExactReceiptRequiresTypedProvenanceFields),
            ("coverage receipt rejects invalid provenance shapes", SyncExecutionReceiptProvenanceParityTests.ReceiptProvenanceRejectsInvalidPolarityAndWitnessShapes),
            ("type-only registration owner matches method owner", SyncExecutionReceiptProvenanceParityTests.TypeOnlyRegistrationOwnerMatchesMethodOwner),
            ("coverage receipt must execute mapped entry", SyncCoverageExecutionReceiptTests.GateRequiresMappedEntryInActualReceipt),
            ("coverage gate rejects unknown and duplicate receipts", SyncCoverageExecutionReceiptTests.GateRejectsUnknownAndDuplicateReceipts),
            ("coverage gate rejects digest scenario and tier drift", SyncCoverageExecutionReceiptTests.GateRejectsDigestScenarioAndTierDrift),
            ("unity-only mapping requires runtime artifact", SyncCoverageExecutionReceiptTests.UnityOnlyMappingRequiresRuntimeArtifact),
            ("registered-disabled mapping requires negative receipt", SyncCoverageExecutionReceiptTests.RegisteredDisabledRequiresNegativeReceipt),
            ("complete execution receipts satisfy coverage gate", SyncCoverageExecutionReceiptTests.CompleteExecutionReceiptsSatisfyGate),
            ("runtime probe records observed entry kinds", SyncExecutionProbeContractTests.RuntimeFixtureRecordsObservedEntryKinds),
            ("runtime probe distinguishes repeated callsites", SyncExecutionProbeContractTests.DuplicateCallsitesRemainDistinctAndUntriggeredStayAbsent),
            ("PDB identity is required for execution provenance", SyncExecutionProbeContractTests.PdbIdentityIsRequiredForExecutionProvenance),
            ("coroutine probe requires start and terminal observation", SyncExecutionProbeContractTests.CoroutineRequiresStartAndTerminalObservation),
            ("manual IDs cannot forge execution receipts", SyncExecutionProbeContractTests.ManualIdsAndStaticClaimsCannotForgeReceipt),
            ("registered-disabled probe proves registration without send", SyncExecutionProbeContractTests.RegisteredDisabledProvesRegistrationWithoutSend),
            ("registered-disabled absence requires same-owner registration", SyncExecutionProbeContractTests.RegisteredDisabledAbsenceRequiresSameOwnerRegistration),
            ("synthetic proof cannot authenticate another catalog origin", SyncCoverageBlockerAdversarialTests.SyntheticProofCannotAuthenticateAnotherCatalogOrigin),
            ("ingame and real artifacts verify log and result hashes", SyncExecutionArtifactContractTests.IngameAndRealArtifactsRequireAuthenticLogAndResultHashes),
            ("runtime artifact mutations fail closed", SyncExecutionArtifactContractTests.ArtifactMutationsFailClosed),
            ("coverage without root digest uses canonical parity hash", SyncCoverageAdversarialTests.CoverageWithoutRootDigestUsesCanonicalParityHash),
            ("synthetic coverage digest cannot override content", SyncCoverageAdversarialTests.SyntheticCoverageDigestCannotOverrideContent),
            ("missing coverage rows and orphan receipts fail", SyncCoverageAdversarialTests.MissingCoverageRowsAndKnownOrphanReceiptsFail),
            ("active positive proof cannot use negative mapping", SyncCoverageAdversarialTests.ActivePositiveProofCannotUseNegativeMapping),
            ("receipt binds to run and envelope digests", SyncCoverageAdversarialTests.ReceiptBindsToRunAndEnvelopeDigests),
            ("missing execution envelope fails closed", SyncCoverageAdversarialTests.MissingEnvelopeFailsClosed),
            ("mixed receipt runs fail closed", SyncCoverageAdversarialTests.MixedReceiptRunsFailClosed),
            ("runtime artifact binds controlled path and control flow", SyncCoverageAdversarialTests.RuntimeArtifactMustBindControlledPathAndControlFlow),
            ("runtime artifact rejects hash root and manual observe", SyncCoverageAdversarialTests.RuntimeArtifactRejectsHashRootAndManualObserve),
            ("build authority architecture contracts", BuildAuthorityArchitectureContractTests.Run),
        ];

        int failures = 0;
        foreach ((string name, Action test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {error.Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static void PacketDiscoveryFollowsRuntime()
    {
        var scan = SyncSurfaceScanner.ScanSources(
            new Dictionary<string, string> { ["Packets.cs"] = PacketFixture });
        string[] names = scan.Packets.Select(packet => packet.Name).Order().ToArray();

        EqualSequence(new[] { "DirectPacket", "IndirectPacket" }, names);
    }

    private static void PacketStructureErrorsAreReported()
    {
        var scan = SyncSurfaceScanner.ScanSources(
            new Dictionary<string, string> { ["BrokenPackets.cs"] = BrokenPacketFixture });

        True(scan.Errors.Any(error => error.Code == "conflicting_direction_markers" &&
            error.Subject == "ConflictPacket"), "missing conflict marker error");
        True(scan.Errors.Any(error => error.Code == "missing_packet_member" &&
            error.Subject == "MissingMemberPacket"), "missing packet member error");
        True(scan.Errors.Any(error => error.Code == "packet_not_constructible" &&
            error.Subject == "PrivateConstructorPacket"), "missing constructor error");
    }

    private static void HarmonyPatchesExposeEntrypoints()
    {
        var scan = SyncSurfaceScanner.ScanSources(
            new Dictionary<string, string> { ["Patches.cs"] = HarmonyFixture });

        Equal(3, scan.HarmonyPatches.Count);
        EqualSequence(new[] { "Prefix" }, scan.HarmonyPatches.Single(patch =>
            patch.Name == "PrefixPatch").Entrypoints);
        EqualSequence(new[] { "TargetMethod" }, scan.HarmonyPatches.Single(patch =>
            patch.Name == "TargetPatch").Entrypoints);
        True(scan.Errors.Any(error => error.Code == "harmony_patch_missing_entrypoint" &&
            error.Subject == "MissingEntrypointPatch"), "missing Harmony entrypoint error");
    }

    private static void ProductionSurfaceIsComplete()
    {
        var scan = SyncSurfaceScanner.ScanDirectory(RepositoryRoot);

        Console.WriteLine(
            $"INFO production packets={scan.Packets.Count} Harmony patches={scan.HarmonyPatches.Count}");
        True(scan.Packets.All(packet => packet.Direction is
                "host_to_clients" or "client_relay" or "bidirectional_direct"),
            "production packet registration contains an invalid direction");
        True(scan.HarmonyPatches.All(patch => patch.Entrypoints.Count > 0),
            "production Harmony patch has no executable entrypoint");
        Equal(0, scan.Errors.Count);
    }

    private static void RegistrationExcludesAbstract()
    {
        string helper = File.ReadAllText(Path.Combine(
            RepositoryRoot, "Shared/Helpers/PacketRegistrationHelper.cs"));

        True(SyncSurfaceScanner.RegistrationHelperExcludesAbstract(helper),
            "AutoRegisterPackets does not exclude abstract packet types");
    }

    private static void PersonalityPrecedesOnSpawn()
    {
        var trace = LifecycleSimulator.Run(
            LifecycleEvent.ActorCreated("preview", "MinionSelectPreview", "host"),
            LifecycleEvent.MinionIdentityOnSpawn("preview"),
            LifecycleEvent.PersonalityAssigned("preview", "Ada"));

        True(trace.Violations.Any(violation =>
                violation.Code == "personality_missing_at_minion_identity_on_spawn"),
            "missing personality lifecycle violation");
    }

    private static void FlatulenceAuthorityIsCorrect()
    {
        var trace = LifecycleSimulator.Run(
            LifecycleEvent.ActorCreated("preview", "MinionSelectPreview", "host"),
            LifecycleEvent.FlatulenceEmitRequested("preview"),
            LifecycleEvent.ActorCreated("host", "Minion", "host"),
            LifecycleEvent.FlatulenceEmitRequested("host"),
            LifecycleEvent.ActorCreated("client", "Minion", "client"),
            LifecycleEvent.FlatulenceEmitRequested("client"));

        EqualSequence(new[] { "host" }, trace.EmittedActorIds);
    }

    private static void ControllerPrecedesSetMinion()
    {
        var trace = LifecycleSimulator.Run(
            LifecycleEvent.ActorCreated("container", "MinionSelectPreview", "host"),
            LifecycleEvent.SetMinion("container", "Ada"),
            LifecycleEvent.SetController("container"));

        True(trace.Violations.Any(violation =>
                violation.Code == "container_controller_missing_at_set_minion"),
            "missing controller lifecycle violation");
    }

    private static void ProductionLifecyclePatchesAreSafe()
    {
        var trace = LifecycleSimulator.TraceProductionSources(
            File.ReadAllText(FlatulencePath), File.ReadAllText(ImmigrantPath));

        Equal(0, trace.Violations.Count);
    }

    private static void DetectsInvertedPreviewGuard()
    {
        string flatulence = ReplaceOnce(
            File.ReadAllText(FlatulencePath),
            "__instance.PrefabID() == GameTags.MinionSelectPreview",
            "__instance.PrefabID() != GameTags.MinionSelectPreview");

        var trace = LifecycleSimulator.TraceProductionSources(
            flatulence, File.ReadAllText(ImmigrantPath));

        True(trace.Violations.Any(violation =>
                violation.Code == "flatulence_emit_guard_mismatch"),
            "missing inverted preview guard violation");
    }

    private static void DetectsDelayedSetMinion()
    {
        string immigrant = ReplaceOnceInNormalMinionPath(
            File.ReadAllText(ImmigrantPath),
            "characterContainer.SetMinion(stats);",
            "Game.Instance.StartCoroutine(SetMinionDelayed(characterContainer, stats));");

        var trace = LifecycleSimulator.TraceProductionSources(
            File.ReadAllText(FlatulencePath), immigrant);

        True(trace.Violations.Any(violation =>
                violation.Code == "personality_missing_at_minion_identity_on_spawn"),
            "missing delayed personality violation");
    }

    private static void DetectsMissingController()
    {
        const string safe = "characterContainer.SetController(instance);\n\t\t\t\t\t\t" +
            "characterContainer.SetMinion(stats);";
        const string broken = "characterContainer.SetMinion(stats);\n\t\t\t\t\t\t" +
            "characterContainer.SetController(instance);";
        string immigrant = ReplaceOnceInNormalMinionPath(
            File.ReadAllText(ImmigrantPath), safe, broken);

        var trace = LifecycleSimulator.TraceProductionSources(
            File.ReadAllText(FlatulencePath), immigrant);

        True(trace.Violations.Any(violation =>
                violation.Code == "container_controller_missing_at_set_minion"),
            "missing controller source violation");
    }

    private static void DetectsArmedMinionBeforeController()
    {
        var trace = LifecycleSimulator.TraceArmedMinionBeforeController(
            File.ReadAllText(ImmigrantPath));

        True(trace.Violations.Any(violation =>
                violation.Code == "container_controller_missing_at_set_minion"),
            "missing armed SetMinion-before-controller fault violation");
    }

    private static string ReplaceOnceInNormalMinionPath(
        string source,
        string expected,
        string replacement)
    {
        int start = source.IndexOf("else", source.IndexOf("if (minionFirst)",
            StringComparison.Ordinal), StringComparison.Ordinal);
        int end = source.IndexOf("FaultInjectionUnitySeams.EmitReceipt", start,
            StringComparison.Ordinal);
        True(start >= 0 && end > start, "normal minion path was not found");
        string path = source[start..end];
        Equal(1, path.Split(expected).Length - 1);
        return source[..start] + path.Replace(expected, replacement,
            StringComparison.Ordinal) + source[end..];
    }

    private static string ReplaceOnce(string source, string expected, string replacement)
    {
        Equal(1, source.Split(expected).Length - 1);
        return source.Replace(expected, replacement, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new InvalidOperationException("ONI_Together repository root was not found");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}, actual {actual}");
    }

    private static void EqualSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                $"expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
    }
}

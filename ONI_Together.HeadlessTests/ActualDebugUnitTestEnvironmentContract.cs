using System.Reflection;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestEnvironmentContract
{
    private static readonly string[] MustRemainExecutable =
    [
        "IntegrationScenarioEvidenceTests.SpawnLifecycleEvidenceIsTypedAndPacketLocal",
        "IntegrationScenarioEvidenceTests.ReconnectRequiresPostReconnectMatch",
        "TypedEvidenceEnvelopeTests.MissingEnvelopeFactsAreRejected",
        "TypedEvidenceActionAdmissionTests.CanonicalHashesAreStable",
        "IntegrationScenarioEvidenceTests.TypedEvidenceLogIsStableAndParseable",
        "TypedEvidenceScenarioTests.ScenarioMustMatchConcreteTargetAndState",
        "TypedEvidenceActionAdmissionTests.PartialActionAdmissionShapesReject",
        "LifecyclePacketTests.RegistryActivation",
        "ReliableReadyReplayTests.ReadyReplayIdUsesUnsignedWire",
        "FrostySyncTests.AuthorityMarkers",
        "PacketBoundsTests.TrimsCursorPathToWireBudget",
        "ProtocolCompatibilityTests.LegacyRequestUsesReliableRejectionBarrier",
        "FaultProductionBindingTests.RuntimeCallsitesDirectlyUseBoundGates",
        "SyncBarrierTests.JournalsReliablePostSnapshotDeltas",
        "TransportTests.ReliablePacketsUseNativeTransportDirectly",
        "PacketBoundsTests.RejectsOversizedCounts",
        "ReconnectBaselineTests.OverflowedJournalCannotTransfer",
        "ReconnectBaselineTests.ReadyRequiresCurrentBaseline",
        "ReliableReadyReplayTests.TargetedReliableGameplayUsesReadyBacklog",
        "AnimSyncTests.AnimSyncBatchDoesNotUseReliableOrdering",
        "BuildingConfigAuthorityTests.StaleBaseCorrectionContract",
        "LargeImpactorSyncTests.RejectsDuplicateDestinationIds",
        "PrehistoricSyncTests.EntityEffectAuthority",
        "AnimSyncTests.AnimSyncPacketRoundtrip",
        "PresentationDataPlaneTests.CursorUtilityPathIsViewportLocal",
        "PrehistoricSyncTests.EntityEffectFinalFrameState",
        "PrehistoricSyncTests.EntityEffectRawHashRoundtrip",
        "BuildingConfigAuthorityTests.MutationSemanticsAreExplicit"
    ];

    internal static void ValidateStaticUnsupportedClassification(
        ActualDebugUnitTestBatchInput input)
    {
        RequireMetadata(input,
            "ProtocolSafetyTests.DllHashAndDlcSetAreRequired",
            "Requires live DlcManager state");
        RequireMetadata(input,
            "ProtocolSafetyTests.DllHashMismatchIsRejected",
            "Requires a file-backed mod assembly");
        RequireMetadata(input,
            "SessionStateResetTests.RestoresPacketProcessingGuards",
            "Touches Unity Time through PacketHandler");
        RequireMetadata(input,
            "SyncBarrierTests.ReconnectTokensMapExactClients",
            "Touches Unity Time through RiptideServer");
        ActualDebugUnitTestDescriptor steam = RequireMetadata(input,
            "NetworkingTests.IsSteamTransport",
            "Requires selected network transport");
        Equal("headless:unit:21762d4ab00831be5027c05d", steam.TestId);
        RequireMetadata(input,
            "ReliableReadyReplayTests.ReadyReplayCompletesAfterMissingBatchArrives",
            "Calls GameClient world-load ECall");
        RequireMetadata(input,
            "ReliableReadyReplayTests.ReadyReplayUsesOneFinalApplicationProof",
            "Calls GameClient world-load ECall");
        RequireMetadata(input,
            "ReliableReadyReplayTests.ReadyReplayRejectsApplicationFailure",
            "Calls GameClient world-load ECall");
        RequireMetadata(input,
            "ReliableReadyReplayTests.ReadyReplayRejectsStaleBatches",
            "Calls GameClient world-load ECall");
        RequireMetadata(input, "GroundItemTests.RegistryAccessible",
            "Requires Unity object registry runtime");
        RequireMetadata(input,
            "SoakHashDomainKeyframeTests.KeyframeApplyExceptionReportsFailure",
            "Calls game runtime during keyframe apply");
        RequireMetadata(input,
            "ReconnectBaselineTests.FreshSnapshotClearsStaleLoadingEpoch",
            "Calls Unity-backed loading reset");
        RequireMetadata(input, "ChorePacketTests.ReceiverSplitsCurrent",
            "Calls Unity-backed chore receiver");
        ActualDebugUnitTestDescriptor riptide = RequireMetadata(input,
            "TransportTests.RiptideTimeoutCorrect",
            "Requires active Riptide transport");
        Equal("headless:unit:3b7b22d19d835a13ac810eaf", riptide.TestId);
        ActualDebugUnitTestDescriptor worldGrid = RequireMetadata(input,
            "PacketBoundsTests.CompressedPacketRoundTrips",
            "Requires initialized world grid");
        Equal("headless:unit:444bf351aa8d4515957c43a8", worldGrid.TestId);
        RequireMetadata(input, "AnimSyncTests.DetectsWrongAnimation",
            "Requires a loaded colony with a duplicant");
        RequireMetadata(input, "AnimSyncTests.ElapsedTimeReadable",
            "Requires a loaded colony with a duplicant");
        RequireMetadata(input, "AnimSyncTests.NonMinionAnimEntitiesDiscoverable",
            "Requires a loaded colony with animated entities");
        RequireMetadata(input, "AnimSyncTests.ReflectionHelperResolves",
            "Requires a loaded colony with animated entities");
        RequireMetadata(input,
            "AutomationHardeningTests.DebugMenuFollowsGameUiScale",
            "Requires an active game UI canvas");
        RequireMetadata(input, "DuplicantTests.BaseMinionTagGuard",
            "Requires selected duplicant");
        RequireMetadata(input, "DuplicantTests.ClientInitDisablesAI",
            "Requires active client session and selected duplicant");
        RequireMetadata(input, "DuplicantTests.HasDuplicantSelected",
            "Requires selected duplicant");
        RequireMetadata(input, "DuplicantTests.HostInitAddsSyncComponents",
            "Requires active host session and selected duplicant");
        RequireMetadata(input,
            "DuplicantTests.MinionMultiplayerInitializerExists",
            "Requires selected duplicant");
        RequireMetadata(input, "GroundItemTests.ClearToolAccessible",
            "Requires a loaded colony");
        RequireMetadata(input, "NetworkingTests.AllClientsConnected",
            "Requires active multiplayer session");
        RequireMetadata(input, "NetworkingTests.IsRiptideTransport",
            "Requires selected Riptide transport");
        ActualDebugUnitTestDescriptor packetRouting = RequireMetadata(input,
            "NetworkingTests.PacketRouting",
            "Requires active multiplayer session");
        Equal("headless:unit:5f069644ef6849d3f34bd89a",
            packetRouting.TestId);
        RequireMetadata(input, "NetworkingTests.ServerStarts",
            "Requires active multiplayer session");
        RequireMetadata(input, "NetworkingTests.TcpTransferServerReady",
            "Requires active LAN host session");
        RequireMetadata(input,
            "SnapshotWireBoundsTests.SnapshotBoundaryRoundTrips",
            "Requires initialized world grid");
        RequireMetadata(input,
            "StorageTransferTests.SnapshotPreservesLiveItemIdentity",
            "Requires selected non-empty storage building");
        RequireMetadata(input,
            "SyncTests.AuthoritativeStateRevisionsRejectStalePackets",
            "Requires initialized world grid");
        RequireMetadata(input, "SyncTests.DuplicantPositionsInSync",
            "Requires active multiplayer session with registered duplicants");
        RequireMetadata(input, "TransportTests.ConnectionStable",
            "Requires active Riptide multiplayer session");
        RequireMetadata(input, "UITests.ChatWindowExistsAndActive",
            "Requires a loaded colony");
        RequireMetadata(input, "UITests.NoGhostCursorsPresent",
            "Requires active multiplayer session");
        Equal(38, input.ExpectedTests.Count(test =>
            test.HeadlessUnsupportedReason is not null));
        IReadOnlyList<string> directNative =
            ActualDebugUnitTestRuntimeClassifierLoader.Load().Classify(
                new ActualDebugUnitTestRuntimeClassificationInput(
                    "Contract.DirectNative()",
                    null,
                    [new ActualDebugUnitTestDirectCall(
                        "UnityEngine.GameObject.Find(System.String)",
                        "UnityEngine.CoreModule",
                        false,
                        false,
                        true,
                        false)]));
        True(directNative.Count == 1 &&
            directNative[0].StartsWith("direct-native|",
                StringComparison.Ordinal),
            "direct Unity native terminal was not statically classified");
        foreach (string suffix in MustRemainExecutable)
        {
            ActualDebugUnitTestDescriptor descriptor = Find(input, suffix);
            True(descriptor.DirectRuntimeReferences.Count == 0,
                $"pure executable UnitTest was over-classified: " +
                $"{descriptor.MethodSymbol} => " +
                string.Join(";", descriptor.DirectRuntimeReferences));
        }
        Console.WriteLine("ACTUAL_UNIT_STATIC_CLASSIFIER " +
            $"notRun={input.ExpectedTests.Count(test =>
                test.DirectRuntimeReferences.Count != 0)} " +
            $"environmentSkip={steam.TestId}");
    }

    internal static void RequirePermissionsAssembly()
    {
        Assembly permissions;
        try
        {
            permissions = Assembly.Load(
                new AssemblyName("System.Security.Permissions"));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "HeadlessTests cannot resolve System.Security.Permissions", error);
        }
        Type? reflectionPermission = permissions.GetType(
            "System.Security.Permissions.ReflectionPermission",
            throwOnError: false);
        True(reflectionPermission is not null,
            "System.Security.Permissions lacks ReflectionPermission");
        Equal(new Version(8, 0, 0, 0), permissions.GetName().Version);
    }

    private static ActualDebugUnitTestDescriptor RequireMetadata(
        ActualDebugUnitTestBatchInput input,
        string suffix,
        string expectedReason)
    {
        ActualDebugUnitTestDescriptor descriptor = Find(input, suffix);
        Equal(expectedReason, descriptor.HeadlessUnsupportedReason);
        True(descriptor.DirectRuntimeReferences.SequenceEqual([expectedReason]),
            $"NotRun evidence does not equal attribute metadata: " +
            descriptor.MethodSymbol);
        return descriptor;
    }

    private static ActualDebugUnitTestDescriptor Find(
        ActualDebugUnitTestBatchInput input,
        string suffix) =>
        input.ExpectedTests.Single(test =>
            test.MethodSymbol.EndsWith(suffix + "()", StringComparison.Ordinal));

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

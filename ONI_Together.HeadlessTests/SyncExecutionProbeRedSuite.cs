namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionProbeRedSuite
{
    public static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("runtime probe records observed entry kinds",
                SyncExecutionProbeContractTests.RuntimeFixtureRecordsObservedEntryKinds),
            ("runtime probe distinguishes repeated callsites",
                SyncExecutionProbeContractTests.DuplicateCallsitesRemainDistinctAndUntriggeredStayAbsent),
            ("PDB identity is required for execution provenance",
                SyncExecutionProbeContractTests.PdbIdentityIsRequiredForExecutionProvenance),
            ("coroutine requires start and terminal observation",
                SyncExecutionProbeContractTests.CoroutineRequiresStartAndTerminalObservation),
            ("manual IDs cannot forge execution receipts",
                SyncExecutionProbeContractTests.ManualIdsAndStaticClaimsCannotForgeReceipt),
            ("registered-disabled proves registration without send",
                SyncExecutionProbeContractTests.RegisteredDisabledProvesRegistrationWithoutSend),
            ("runtime artifacts verify log and result hashes",
                SyncExecutionArtifactContractTests.IngameAndRealArtifactsRequireAuthenticLogAndResultHashes),
            ("runtime artifact mutations fail closed",
                SyncExecutionArtifactContractTests.ArtifactMutationsFailClosed),
            ("actual production assembly receipt requires binary-bound origin",
                SyncActualAssemblyExecutionProvenanceRedTests
                    .ActualAssemblyReceiptRequiresBinaryBoundOrigin),
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
}

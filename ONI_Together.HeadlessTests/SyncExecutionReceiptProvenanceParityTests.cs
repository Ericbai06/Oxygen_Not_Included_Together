using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionReceiptProvenanceParityTests
{
    private const string RegistrationId = "sync:registration";
    private const string DisabledId = "sync:disabled-send";

    public static void ExactReceiptRequiresTypedProvenanceFields()
    {
        SyncExecutionReceipt positive = SyncExecutionReceipt.Parse(
            ReceiptJson("positive", [RegistrationId], [], []));
        EqualSet([], PropertyIds(positive, "AbsentEntryIds"));

        SyncExecutionReceipt negative = SyncExecutionReceipt.Parse(
            ReceiptJson("negative", [RegistrationId], [DisabledId],
                [Witness(DisabledId, RegistrationId)]));
        EqualSet([DisabledId], PropertyIds(negative, "AbsentEntryIds"));

        foreach (string missing in new[] { "absentEntryIds", "registrationWitnesses" })
            Throws<FormatException>(() => SyncExecutionReceipt.Parse(
                ReceiptJson("positive", [RegistrationId], [], [], missing)));
    }

    public static void ReceiptProvenanceRejectsInvalidPolarityAndWitnessShapes()
    {
        Throws<FormatException>(() => SyncExecutionReceipt.Parse(
            ReceiptJson("positive", [RegistrationId], [DisabledId],
                [Witness(DisabledId, RegistrationId)])));
        Throws<FormatException>(() => SyncExecutionReceipt.Parse(
            ReceiptJson("negative", [RegistrationId], [], [])));
        Throws<FormatException>(() => SyncExecutionReceipt.Parse(
            ReceiptJson("negative", [RegistrationId, DisabledId], [DisabledId],
                [Witness(DisabledId, RegistrationId)])));
        Throws<FormatException>(() => SyncExecutionReceipt.Parse(
            ReceiptJson("negative", [RegistrationId], [DisabledId],
                [Witness("sync:other-absent", RegistrationId)])));
        Throws<FormatException>(() => SyncExecutionReceipt.Parse(
            ReceiptJson("negative", [RegistrationId], [DisabledId],
                [Witness(DisabledId, "sync:not-executed")])));
    }

    public static void TypeOnlyRegistrationOwnerMatchesMethodOwner()
    {
        Type session = typeof(SyncExecutionProbeFactoryLoader).Assembly.GetType(
            "ONI_Together.HeadlessTests.SyncExecutionProbeSession",
            throwOnError: true)!;
        var method = session.GetMethod(
            "DeclaringType", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        string registration = (string)method.Invoke(null, ["Fixture.DisabledOwner"])!;
        string send = (string)method.Invoke(null, ["Fixture.DisabledOwner.Send()"])!;

        if (!string.Equals(registration, send, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"type-only registration owner {registration} != send owner {send}");
    }

    private static string ReceiptJson(
        string polarity,
        IReadOnlyList<string> executed,
        IReadOnlyList<string> absent,
        IReadOnlyList<object> witnesses,
        string? omit = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["runId"] = "run-provenance-parity",
            ["inventoryDigest"] = new string('a', 64),
            ["coverageDigest"] = "sha256:" + new string('b', 64),
            ["dllHash"] = new string('d', 64),
            ["pdbHash"] = new string('e', 64),
            ["testId"] = "headless:provenance-parity",
            ["tier"] = "headless",
            ["scenarioId"] = null,
            ["polarity"] = polarity,
            ["executedEntryIds"] = executed,
            ["absentEntryIds"] = absent,
            ["registrationWitnesses"] = witnesses,
            ["artifact"] = null,
        };
        if (omit is not null)
            values.Remove(omit);
        return JsonSerializer.Serialize(values);
    }

    private static object Witness(string entryId, string registrationEntryId)
        => new { entryId, registrationEntryId };

    private static string[] PropertyIds(object receipt, string property)
    {
        object? value = receipt.GetType().GetProperty(property)?.GetValue(receipt);
        if (value is not IEnumerable<string> ids)
            throw new InvalidOperationException($"receipt lacks typed {property}");
        return ids.ToArray();
    }

    private static void EqualSet(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        if (!expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual))
            throw new InvalidOperationException("receipt provenance IDs differ");
    }

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
}

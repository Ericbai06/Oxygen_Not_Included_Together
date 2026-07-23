namespace ONI_Together.HeadlessTests;

internal abstract record LifecycleEvent
{
    public static LifecycleEvent ActorCreated(
        string actorId,
        string prefabTag,
        string networkRole) => new ActorCreatedEvent(actorId, prefabTag, networkRole);

    public static LifecycleEvent PersonalityAssigned(string actorId, string personality) =>
        new PersonalityAssignedEvent(actorId, personality);

    public static LifecycleEvent MinionIdentityOnSpawn(string actorId) =>
        new MinionIdentityOnSpawnEvent(actorId);

    public static LifecycleEvent FlatulenceEmitRequested(string actorId) =>
        new FlatulenceEmitRequestedEvent(actorId, null);

    public static LifecycleEvent SetController(string actorId) =>
        new SetControllerEvent(actorId);

    public static LifecycleEvent SetMinion(string actorId, string personality) =>
        new SetMinionEvent(actorId, personality);
}

internal sealed record ActorCreatedEvent(
    string ActorId,
    string PrefabTag,
    string NetworkRole) : LifecycleEvent;
internal sealed record PersonalityAssignedEvent(
    string ActorId,
    string Personality) : LifecycleEvent;
internal sealed record MinionIdentityOnSpawnEvent(string ActorId) : LifecycleEvent;
internal sealed record FlatulenceEmitRequestedEvent(
    string ActorId,
    bool? SourceAllows) : LifecycleEvent;
internal sealed record SetControllerEvent(string ActorId) : LifecycleEvent;
internal sealed record SetMinionEvent(string ActorId, string Personality) : LifecycleEvent;

internal readonly record struct LifecycleViolation(string Code, string ActorId);
internal sealed record LifecycleTrace(
    IReadOnlyList<string> EmittedActorIds,
    IReadOnlyList<LifecycleViolation> Violations);
internal sealed record ActorState(
    string PrefabTag,
    string NetworkRole,
    bool PersonalityAssigned = false,
    bool ControllerAssigned = false);

internal static class LifecycleSimulator
{
    public static LifecycleTrace Run(params LifecycleEvent[] events)
    {
        var actors = new Dictionary<string, ActorState>(StringComparer.Ordinal);
        var emitted = new List<string>();
        var violations = new List<LifecycleViolation>();

        foreach (LifecycleEvent lifecycleEvent in events)
        {
            switch (lifecycleEvent)
            {
                case ActorCreatedEvent created:
                    actors[created.ActorId] = new ActorState(
                        created.PrefabTag, created.NetworkRole);
                    break;
                case PersonalityAssignedEvent assigned:
                    actors[assigned.ActorId] = actors[assigned.ActorId] with
                    {
                        PersonalityAssigned = true
                    };
                    break;
                case MinionIdentityOnSpawnEvent spawned:
                    if (!actors[spawned.ActorId].PersonalityAssigned)
                    {
                        violations.Add(new LifecycleViolation(
                            "personality_missing_at_minion_identity_on_spawn",
                            spawned.ActorId));
                    }
                    break;
                case FlatulenceEmitRequestedEvent request:
                    ApplyFlatulence(request, actors[request.ActorId], emitted, violations);
                    break;
                case SetControllerEvent controller:
                    actors[controller.ActorId] = actors[controller.ActorId] with
                    {
                        ControllerAssigned = true
                    };
                    break;
                case SetMinionEvent minion:
                    ActorState state = actors[minion.ActorId];
                    if (!state.ControllerAssigned)
                    {
                        violations.Add(new LifecycleViolation(
                            "container_controller_missing_at_set_minion",
                            minion.ActorId));
                    }
                    actors[minion.ActorId] = state with { PersonalityAssigned = true };
                    break;
            }
        }

        return new LifecycleTrace(emitted, violations);
    }

    public static LifecycleTrace TraceProductionSources(
        string flatulenceSource,
        string immigrantSource)
    {
        return TraceSources(flatulenceSource, SelectMinionPath(
            immigrantSource, armedFaultPath: false));
    }

    public static LifecycleTrace TraceArmedMinionBeforeController(string immigrantSource)
    {
        return TraceSources(string.Empty, SelectMinionPath(
            immigrantSource, armedFaultPath: true));
    }

    private static LifecycleTrace TraceSources(
        string flatulenceSource,
        string minionPath)
    {
        bool previewEquality = flatulenceSource.Contains(
            "__instance.PrefabID() == GameTags.MinionSelectPreview",
            StringComparison.Ordinal);
        bool clientAndPreviewGuard = flatulenceSource.Contains(
            "if (client || preview)", StringComparison.Ordinal);

        int controller = minionPath.IndexOf(
            "characterContainer.SetController(instance)", StringComparison.Ordinal);
        int minion = minionPath.IndexOf(
            "characterContainer.SetMinion(stats)", StringComparison.Ordinal);
        int delayed = minionPath.IndexOf(
            "StartCoroutine(SetMinionDelayed", StringComparison.Ordinal);

        var events = new List<LifecycleEvent>
        {
            LifecycleEvent.ActorCreated("preview", "MinionSelectPreview", "host"),
            new FlatulenceEmitRequestedEvent(
                "preview", !(previewEquality && clientAndPreviewGuard)),
            LifecycleEvent.ActorCreated("host", "Minion", "host"),
            new FlatulenceEmitRequestedEvent(
                "host", previewEquality && clientAndPreviewGuard),
            LifecycleEvent.ActorCreated("client", "Minion", "client"),
            new FlatulenceEmitRequestedEvent("client", !clientAndPreviewGuard),
            LifecycleEvent.ActorCreated("container", "MinionSelectPreview", "host")
        };

        if (controller >= 0 && controller < minion && delayed < 0)
        {
            events.Add(LifecycleEvent.SetController("container"));
            events.Add(LifecycleEvent.SetMinion("container", "production-stats"));
            events.Add(LifecycleEvent.MinionIdentityOnSpawn("container"));
        }
        else if (minion >= 0 && minion < controller && delayed < 0)
        {
            events.Add(LifecycleEvent.SetMinion("container", "production-stats"));
            events.Add(LifecycleEvent.SetController("container"));
            events.Add(LifecycleEvent.MinionIdentityOnSpawn("container"));
        }
        else
        {
            events.Add(LifecycleEvent.SetController("container"));
            events.Add(LifecycleEvent.MinionIdentityOnSpawn("container"));
            events.Add(LifecycleEvent.SetMinion("container", "production-stats"));
        }

        return Run(events.ToArray());
    }

    private static string SelectMinionPath(string source, bool armedFaultPath)
    {
        int gate = source.IndexOf(
            "ProductionFaultInputGates.MinionBeforeController", StringComparison.Ordinal);
        if (gate < 0)
            return source;
        int faultStart = source.IndexOf("if (minionFirst)", gate,
            StringComparison.Ordinal);
        int normalStart = source.IndexOf("else", faultStart,
            StringComparison.Ordinal);
        int end = source.IndexOf("FaultInjectionUnitySeams.EmitReceipt", normalStart,
            StringComparison.Ordinal);
        if (faultStart < 0 || normalStart < 0 || end < 0)
            return string.Empty;
        int start = armedFaultPath ? faultStart : normalStart;
        int stop = armedFaultPath ? normalStart : end;
        return source[start..stop];
    }

    private static void ApplyFlatulence(
        FlatulenceEmitRequestedEvent request,
        ActorState actor,
        ICollection<string> emitted,
        ICollection<LifecycleViolation> violations)
    {
        bool expected = actor.NetworkRole == "host" &&
            actor.PrefabTag != "MinionSelectPreview";
        bool actual = request.SourceAllows ?? expected;
        if (actual)
            emitted.Add(request.ActorId);
        if (actual != expected)
            violations.Add(new LifecycleViolation(
                "flatulence_emit_guard_mismatch", request.ActorId));
    }
}

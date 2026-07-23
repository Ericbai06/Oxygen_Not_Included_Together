using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools.Build;
using Shared.Profiling;
using UnityEngine;

[HarmonyPatch(typeof(Constructable), nameof(Constructable.FinishConstruction))]
public static class ConstructablePatch
{
	public static void Prefix(Constructable __instance, out BuildCompletePacket __state)
	{
		using var _ = Profiler.Scope();
		__state = MultiplayerSession.IsHostInSession ? Capture(__instance) : null;
		if (__state != null)
			NetworkIdentity.BeginManagedSpawn();
	}

	public static void Postfix(ref BuildCompletePacket __state)
	{
		BuildCompletePacket state = __state;
		if (state == null)
			return;
		try
		{
			if (!MultiplayerSession.IsHostInSession
			    || !TryFinalizeIdentity(state, out NetworkIdentity identity))
				return;
			NetworkIdentity.EndManagedSpawn();
			__state = null;
			PacketSender.SendToAllClients(state, PacketSendMode.ReliableImmediate);
#if DEBUG
			IntegrationScenarioEvidenceCore.Log(
				TypedEvidenceRuntimeContext.Create(
					scenario: "building-lifecycle", phase: "final-state",
					revision: (long)state.LifecycleRevision,
					target: new BuildingLifecycleTarget
					{
						Prefab = state.PrefabID, Cell = state.Cell, NetId = state.NetId,
					},
					state: new BuildingLifecycleState
					{
						LifecycleRevision = (long)state.LifecycleRevision,
						Queued = false, Completed = true,
					},
					entryId: "sync:dbfc8eeb5a623ab482197ef1"));
#endif
			DebugConsole.Log(
				$"[Host] Sent BuildCompletePacket for {state.PrefabID} NetId={state.NetId}");
		}
		finally
		{
			if (__state != null)
			{
				NetworkIdentity.EndManagedSpawn();
				__state = null;
			}
		}
	}

	public static System.Exception Finalizer(
		System.Exception __exception, BuildCompletePacket __state)
	{
		if (__state != null)
			NetworkIdentity.EndManagedSpawn();
		return __exception;
	}

	internal static BuildCompletePacket Capture(Constructable constructable)
	{
		Building building = constructable?.GetComponent<Building>();
		BuildingDef def = building?.Def;
		if (def == null)
			return null;
		NetworkIdentity identity = constructable.GetComponent<NetworkIdentity>();
		if (identity == null || identity.NetId == 0 || identity.LifecycleRevision == 0
		    || !NetworkIdentityRegistry.IsRegistered(identity, identity.NetId))
			return null;
		IList<Tag> selectedElementsTags = constructable.SelectedElementsTags;
#if DEBUG
		IFaultInputMutation fault = ProductionFaultInputGates.MissingSelectedElements(
			ref selectedElementsTags);
		FaultInjectionUnitySeams.EmitReceipt(fault, runtimeTarget: constructable);
#endif
		List<string> materials = selectedElementsTags?
			.Select(tag => tag.ToString()).ToList() ?? [];
		if (materials.Count == 0)
			return null;
		return new BuildCompletePacket
		{
			Cell = Grid.PosToCell(constructable),
			PrefabID = def.PrefabID,
			Orientation = constructable.GetComponent<Rotatable>()?.GetOrientation()
			              ?? Orientation.Neutral,
			MaterialTags = materials,
			Temperature = constructable.GetComponent<PrimaryElement>()?.Temperature
			              ?? def.Temperature,
			FacadeID = constructable.GetComponent<BuildingFacade>()?.CurrentFacade
			           ?? BuildAuthority.DefaultFacade,
			UtilityConnectionFlags = constructable.GetComponent<KAnimGraphTileVisualizer>()?
				.Connections ?? 0,
			ObjectLayer = def.ObjectLayer,
			NetId = identity.NetId,
			LifecycleRevision = identity.LifecycleRevision
		};
	}

	private static bool TryFinalizeIdentity(
		BuildCompletePacket state, out NetworkIdentity identity)
	{
		identity = null;
		GameObject built = FindCompletedBuilding(state);
		if (built == null)
			return false;
		identity = built.AddOrGet<NetworkIdentity>();
		identity.RegisterIdentity();
		return identity.NetId == state.NetId
		       && identity.LifecycleRevision == state.LifecycleRevision
		       && NetworkIdentityRegistry.IsRegistered(identity, state.NetId);
	}

	private static GameObject FindCompletedBuilding(BuildCompletePacket state)
	{
		int[] cells =
		{
			state.Cell, Grid.CellLeft(state.Cell), Grid.CellRight(state.Cell),
			Grid.CellAbove(state.Cell), Grid.CellBelow(state.Cell)
		};
		foreach (int cell in cells)
		{
			if (!Grid.IsValidCell(cell))
				continue;
			GameObject candidate = Grid.Objects[cell, (int)state.ObjectLayer];
			if (candidate?.GetComponent<BuildingComplete>()?.Def?.PrefabID == state.PrefabID)
				return candidate;
		}
		return null;
	}
}

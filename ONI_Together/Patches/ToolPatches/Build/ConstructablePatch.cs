using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
#if DEBUG
using ONI_Together.DebugTools;
#endif
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Build;
using Shared.Interfaces.Networking;
using Shared.Profiling;

[HarmonyPatch(typeof(Constructable), nameof(Constructable.FinishConstruction))]
public static class ConstructablePatch
{
	public static void Prefix(Constructable __instance, out BuildCommit __state)
	{
		using var _ = Profiler.Scope();
		__state = Capture(__instance);
		if (__state != null)
			NetworkIdentity.BeginManagedSpawn();
	}

	public static void Postfix(ref BuildCommit __state)
	{
		BuildCommit state = __state;
		if (state == null)
			return;
		try
		{
			if (!MultiplayerSession.IsHostInSession)
				return;
			NetworkIdentity.EndManagedSpawn();
			__state = null;
			PacketSender.SendToAllClients(
				BuildCommitPacket.FromDomain(state), PacketSendMode.ReliableImmediate);
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
		System.Exception __exception, BuildCommit __state)
	{
		if (__state != null)
			NetworkIdentity.EndManagedSpawn();
		return __exception;
	}

	internal static BuildCommit Capture(Constructable constructable)
	{
		Building building = constructable?.GetComponent<Building>();
		BuildingDef def = building?.Def;
		NetworkIdentity identity = constructable?.GetComponent<NetworkIdentity>();
		if (def == null || identity == null || identity.NetId == 0 ||
			identity.LifecycleRevision == 0 ||
			!BuildLifecycleRegistry.TryGet(identity.NetId, out BuildOperationId operationId))
			return null;
		IList<Tag> materials = constructable.SelectedElementsTags;
#if DEBUG
		IFaultInputMutation fault = ProductionFaultInputGates.MissingSelectedElements(
			ref materials);
		FaultInjectionUnitySeams.EmitReceipt(fault, runtimeTarget: constructable);
#endif
		if (materials == null || materials.Count == 0)
			return null;
		var request = new BuildRequest(
			operationId,
			def.PrefabID,
			new SinglePlacementGeometry(
				Grid.PosToCell(constructable),
				constructable.GetComponent<Rotatable>()?.GetOrientation() ?? Orientation.Neutral),
			materials.Select(tag => tag.ToString()),
			constructable.GetComponent<BuildingFacade>()?.CurrentFacade
				?? BuildRequestValidator.DefaultFacade,
			0, 5, (int)def.ObjectLayer);
		var placement = new PlacementOutcome(
			Grid.PosToCell(constructable), BuildPlacementKind.Completed,
			identity.NetId, identity.LifecycleRevision);
		return new BuildCommit(
			request, operationId, new[] { placement },
			System.Array.Empty<UtilityEdge>(),
			new BuildRevision(NetworkIdentityRevision.Next()));
	}
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Build
{
	[HarmonyPatch(typeof(BuildTool), nameof(BuildTool.TryBuild))]
	public static class BuildToolPatch
	{
		private static ulong nextSequence;

		static bool Prefix(BuildTool __instance, int cell)
		{
			using var _ = Profiler.Scope();
			if (!MultiplayerSession.InSession || BuildMutationContext.IsManaged)
				return true;
			try
			{
				if (!CanIssueClientRequest(__instance, cell) ||
					!TryCapture(__instance, cell, out BuildRequest request))
					return false;
				if (MultiplayerSession.IsHost)
					ExecuteHost(request);
				else
					PacketSender.SendToAllOtherPeers(new BuildRequestPacket(request));
				AdvanceClientToolState(__instance, cell);
			}
			catch (Exception exception)
			{
				DebugConsole.LogError($"[BuildToolPatch.Prefix] {exception}");
			}
			return false;
		}

		internal static bool TryCapture(
			BuildTool tool,
			int cell,
			out BuildRequest request)
		{
			request = null;
			if (tool?.def == null || tool.selectedElements == null)
				return false;
			PrioritySetting priority = PlanScreen.Instance != null
				? PlanScreen.Instance.GetBuildingPriority()
				: new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);
			request = new BuildRequest(
				NextOperationId(),
				tool.def.PrefabID,
				new SinglePlacementGeometry(cell, tool.GetBuildingOrientation),
				tool.selectedElements.Select(tag => tag.ToString()),
				tool.facadeID,
				(int)priority.priority_class,
				priority.priority_value,
				(int)tool.def.ObjectLayer);
			return true;
		}

		internal static bool ShouldRunLocally(bool inSession, bool isHost, bool processingIncoming)
			=> !inSession || isHost || processingIncoming;

		internal static BuildOperationId NextOperationId()
			=> new(
				Math.Max(1L, PacketHandler.ClientSessionEpoch),
				Math.Max(1UL, MultiplayerSession.LocalUserID),
				Interlocked.Increment(ref nextSequence));

		private static void ExecuteHost(BuildRequest request)
		{
			AuthoritativeBuildExecutor.SetSessionEpoch(request.OperationId.SessionEpoch);
			if (AuthoritativeBuildExecutor.Execute(request, HostBuildPolicyProvider.Current,
				out BuildCommit commit, out BuildRejected rejection))
				BuildPublisher.Publish(commit);
			else
				BuildPublisher.Publish(rejection);
		}

		private static bool CanIssueClientRequest(BuildTool tool, int cell)
		{
			if (tool?.def == null || tool.visualizer == null || tool.selectedElements == null ||
				!Grid.IsValidCell(cell) || !Grid.IsVisible(cell))
				return false;
			if (cell == tool.lastDragCell && tool.buildingOrientation == tool.lastDragOrientation)
				return false;
			bool positionBound = tool.def.BuildingComplete.GetComponent<LogicPorts>() != null ||
				tool.def.BuildingComplete.GetComponent<LogicGateBase>() != null;
			return !positionBound || Grid.PosToCell(tool.visualizer) == cell;
		}

		private static void AdvanceClientToolState(BuildTool tool, int cell)
		{
			tool.lastDragCell = cell;
			tool.lastDragOrientation = tool.buildingOrientation;
			tool.ClearTilePreview();
			if (PlanScreen.Instance != null)
				PlanScreen.Instance.LastSelectedBuildingFacade = tool.facadeID;
		}
	}
}

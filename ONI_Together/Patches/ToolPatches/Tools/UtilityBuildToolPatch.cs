using System;
using System.Linq;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Build
{
	[HarmonyPatch(typeof(BaseUtilityBuildTool), nameof(BaseUtilityBuildTool.BuildPath))]
	public static class UtilityBuildToolPatch
	{
		static bool Prefix(BaseUtilityBuildTool __instance)
		{
			using var _ = Profiler.Scope();
			if (!MultiplayerSession.InSession || BuildMutationContext.IsManaged)
				return true;
			try
			{
				if (!TryCapture(__instance, out BuildRequest request))
					return false;
				if (MultiplayerSession.IsHost)
				{
					AuthoritativeBuildExecutor.SetSessionEpoch(request.OperationId.SessionEpoch);
					if (AuthoritativeBuildExecutor.Execute(request, HostBuildPolicyProvider.Current,
						out BuildCommit commit, out BuildRejected rejection))
						BuildPublisher.Publish(commit);
					else
						BuildPublisher.Publish(rejection);
				}
				else
					PacketSender.SendToAllOtherPeers(new BuildRequestPacket(request));
			}
			catch (Exception exception)
			{
				DebugConsole.LogError($"[UtilityBuildToolPatch.Prefix] {exception}");
			}
			return false;
		}

		internal static bool TryCapture(
			BaseUtilityBuildTool tool,
			out BuildRequest request)
		{
			request = null;
			if (tool?.def == null || tool.path == null || tool.path.Count == 0 ||
				tool.selectedElements == null)
				return false;
			PrioritySetting priority = PlanScreen.Instance != null
				? PlanScreen.Instance.GetBuildingPriority()
				: new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);
			request = new BuildRequest(
				BuildToolPatch.NextOperationId(),
				tool.def.PrefabID,
				new UtilityPathGeometry(tool.path.Select(node => node.cell)),
				tool.selectedElements.Select(tag => tag.ToString()),
				tool.facadeID,
				(int)priority.priority_class,
				priority.priority_value,
				(int)tool.def.ObjectLayer);
			return true;
		}

		internal static bool ShouldRunLocally(bool inSession, bool isHost, bool processingIncoming)
			=> !inSession || isHost || processingIncoming;
	}
}

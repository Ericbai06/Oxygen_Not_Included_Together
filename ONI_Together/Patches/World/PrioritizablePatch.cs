using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Prioritize;
using ONI_Together.Networking.Packets.World;
using ONI_Together.DebugTools;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	[HarmonyPatch(typeof(Prioritizable), "SetMasterPriority")]
	public static class PrioritizablePatch
	{
		public static void Postfix(Prioritizable __instance)
		{
			using var _ = Profiler.Scope();

			if (PrioritizeStatePacket.IsApplying
			    || PrioritizeStatePacket.IsHostMutationSuppressed
			    || !MultiplayerSession.InSession ||
			    !MultiplayerSession.IsHost)
				return;

			NetworkIdentity identity = __instance.GetComponent<NetworkIdentity>();
			if (identity != null)
				PrioritizeStatePacket.PublishHostMutation(identity);
		}
	}

	[HarmonyPatch(typeof(UserMenuScreen), "OnPriorityClicked")]
	public static class UserMenuPriorityPatch
	{
		public static bool Prefix(UserMenuScreen __instance, PrioritySetting priority)
		{
			if (!MultiplayerSession.InSession || MultiplayerSession.IsHost || PrioritizeStatePacket.IsApplying)
				return true;
			if (!PriorityAuthority.IsValidClientPriority(priority))
				return false;

			GameObject selected = Traverse.Create(__instance).Field("selected").GetValue<GameObject>();
			NetworkIdentity identity = selected?.GetComponent<NetworkIdentity>();
			Prioritizable prioritizable = selected?.GetComponent<Prioritizable>();
			if (identity == null || identity.NetId == 0 || prioritizable == null || !prioritizable.IsPrioritizable())
				return false;

			if (PrioritizeTargetRequestPacket.TryCreateClientRequest(
				    identity, priority, out PrioritizeTargetRequestPacket request))
			{
				PacketSender.SendToAllOtherPeers(request);
#if DEBUG
				PrioritizeStatePacket.LogBlockedPriorityEvidence(
					request, "sync:a953b61fac04863eb59c6ec0");
#endif
			}
			return false;
		}
	}
}

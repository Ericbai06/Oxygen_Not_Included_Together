using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	public static class PickupablePatches
	{
		[HarmonyPatch(typeof(Pickupable), nameof(Pickupable.OnPrefabInit))]
		public static class PickupablePrefabPatch
		{
			public static void Postfix(Pickupable __instance)
				=> NetworkIdentity.EnsurePersistentPrefabIdentity(__instance.gameObject);
		}

		[HarmonyPatch(typeof(Pickupable), nameof(Pickupable.OnSpawn))]
		public static class PickupableSpawnPatch
		{
			public static void Postfix(Pickupable __instance)
			{
				NetworkIdentity identity = __instance.gameObject.AddOrGet<NetworkIdentity>();
				identity.RegisterIdentity();
				identity.EnsureAuthoritativeSpawnBroadcast();
			}
		}

		[HarmonyPatch(typeof(Pickupable), nameof(Pickupable.OnCleanUp))]
		public static class PickupableCleanedUpPatch
		{
			public static void Postfix(Pickupable __instance)
			{
				using var _ = Profiler.Scope();
				if (!MultiplayerSession.IsHostInSession || __instance == null)
				{
#if DEBUG
					if (MultiplayerSession.IsClient && __instance != null)
					{
						NetworkIdentity blockedIdentity = __instance.GetComponent<NetworkIdentity>();
						if (blockedIdentity != null && blockedIdentity.NetId != 0)
							IntegrationScenarioEvidenceCore.Log(
								"pickup", "client-original-blocked", 0, false,
								GroundItemPickedUpPacket.CanonicalState(blockedIdentity.NetId));
					}
#endif
					return;
				}
				NetworkIdentity identity = __instance.GetComponent<NetworkIdentity>();
				if (identity == null || identity.NetId == 0)
					return;
				var packet = new GroundItemPickedUpPacket(identity.NetId);
				PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
				packet.LogHostOutcome();
				DebugConsole.Log($"[GroundPickup] sent NetId={identity.NetId}");
			}
		}
	}
}

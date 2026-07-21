using System;
using System.Collections.Generic;
using HarmonyLib;
using Klei.AI;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.DuplicantActions;
using Shared.Profiling;

namespace ONI_Together.Patches.Duplicant
{
	internal class EffectsPatch
	{
		private static readonly Dictionary<(int NetId, int EffectHash),
			(NetworkIdentity Identity, ulong Lifecycle)>
			DirtyHostEffects = new();
		private static int _packetApplyDepth;
		private static bool IsApplyingPacket => _packetApplyDepth > 0;

		internal static bool ShouldRunMutation(
			bool inSession,
			bool isHost,
			bool isApplyingPacket,
			bool hasNetworkIdentity)
			=> !inSession || isHost || isApplyingPacket || !hasNetworkIdentity;

		public static EffectInstance AddEffect(
			Effects effects,
			string effectId,
			bool shouldSave,
			float timeRemaining)
		{
			using var _ = Profiler.Scope();
			Effect effect = Db.Get().effects.TryGet(effectId);
			if (effect == null)
			{
				DebugConsole.LogWarning("Could not find effect with id " + effectId);
				return null;
			}

			EffectInstance instance = AddLocally(effects, effect, shouldSave, null);
			if (instance != null)
				instance.timeRemaining = timeRemaining;
			return instance;
		}

		public static void RemoveEffect(Effects effects, HashedString effectId)
		{
			using var _ = Profiler.Scope();
			_packetApplyDepth++;
			try
			{
				effects.Remove(effectId);
			}
			finally
			{
				_packetApplyDepth--;
			}
		}

		private static EffectInstance AddLocally(
			Effects effects,
			Effect effect,
			bool shouldSave,
			Func<string, object, string> resolveTooltip)
		{
			_packetApplyDepth++;
			try
			{
				return effects.Add(effect, shouldSave, resolveTooltip);
			}
			finally
			{
				_packetApplyDepth--;
			}
		}

		private static bool TryGetIdentity(Effects effects, out NetworkIdentity identity)
		{
			identity = effects?.GetComponent<NetworkIdentity>();
			return identity != null && identity.NetId != 0;
		}

		private static void MarkDirty(NetworkIdentity identity, Effect effect)
		{
			if (identity == null || identity.NetId == 0 || effect == null)
				return;
			DirtyHostEffects[(identity.NetId, effect.IdHash.hash)] =
				(identity, identity.LifecycleRevision);
		}

		private static void MarkDirty(NetworkIdentity identity, HashedString effectHash)
		{
			if (identity == null || identity.NetId == 0 || effectHash.hash == 0)
				return;
			DirtyHostEffects[(identity.NetId, effectHash.hash)] =
				(identity, identity.LifecycleRevision);
		}

		public static void FlushDirtyEffects()
		{
			if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost)
			{
				DirtyHostEffects.Clear();
				return;
			}

			foreach (var entry in TakeDirtyEffects())
			{
				(int netId, int effectHash) = entry.Key;
				NetworkIdentity identity = entry.Value.Identity;
				if (!IsCurrentDirtyOwner(
					NetworkIdentityRegistry.IsRegistered(identity, netId),
					entry.Value.Lifecycle, identity.LifecycleRevision))
					continue;
				EffectInstance current = identity.GetComponent<Effects>()?
					.Get(new HashedString(effectHash));
				ToggleEffectPacket packet = current != null
					? new ToggleEffectPacket(identity, current)
					: new ToggleEffectPacket(identity, new HashedString(effectHash));
#if DEBUG
				string state = packet.EvidenceState();
				IntegrationScenarioEvidenceCore.Log(
					"effect", "host-submit", (long)packet.Revision, true, state);
				IntegrationScenarioEvidenceCore.Log(
					"effect", "final-state", (long)packet.Revision, true, state);
#endif
				PacketSender.SendToAllClients(packet);
			}
		}

		private static List<KeyValuePair<(int NetId, int EffectHash),
			(NetworkIdentity Identity, ulong Lifecycle)>>
			TakeDirtyEffects()
		{
			var snapshot = new List<KeyValuePair<(int, int),
				(NetworkIdentity, ulong)>>(DirtyHostEffects);
			DirtyHostEffects.Clear();
			return snapshot;
		}

		internal static void ResetSessionState()
		{
			_packetApplyDepth = 0;
			DirtyHostEffects.Clear();
		}

		internal static bool IsCurrentDirtyOwner(
			bool isRegistered, ulong capturedLifecycle, ulong currentLifecycle)
			=> isRegistered && capturedLifecycle == currentLifecycle;

		internal static void MarkDirtyForTests(int netId, int effectHash)
			=> DirtyHostEffects[(netId, effectHash)] = default;

		internal static ToggleEffectPacket[] DrainDirtyEffectsForTests(
			Func<int, int, ToggleEffectPacket> resolve)
		{
			var packets = new List<ToggleEffectPacket>();
			foreach (var entry in TakeDirtyEffects())
			{
				ToggleEffectPacket packet = resolve(entry.Key.NetId, entry.Key.EffectHash);
				if (packet != null)
					packets.Add(packet);
			}
			return packets.ToArray();
		}

		[HarmonyPatch(typeof(Effects), nameof(Effects.Add),
			[typeof(Effect), typeof(bool), typeof(Func<string, object, string>)])]
		public class EffectsAddPatch
		{
			public static bool Prefix(
				Effects __instance,
				Effect newEffect,
				bool should_save,
				Func<string, object, string> resolveTooltipCallback,
				ref EffectInstance __result)
			{
				using var scope = Profiler.Scope();
				bool hasIdentity = TryGetIdentity(__instance, out NetworkIdentity identity);
				bool shouldRun = ShouldRunMutation(MultiplayerSession.InSession,
					MultiplayerSession.IsHost, IsApplyingPacket, hasIdentity);
#if DEBUG
				if (!shouldRun)
					IntegrationScenarioEvidenceCore.Log(
						"effect", "client-original-blocked", 0, false,
						ToggleEffectPacket.EvidenceState(
							identity.NetId, newEffect?.IdHash.hash ?? 0, active: true));
#endif
				return shouldRun;
			}

			public static void Postfix(Effects __instance, Effect newEffect)
			{
				if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost ||
				    IsApplyingPacket || !TryGetIdentity(__instance, out NetworkIdentity identity))
					return;
				MarkDirty(identity, newEffect);
			}
		}

		[HarmonyPatch(typeof(Effects), nameof(Effects.Remove), [typeof(HashedString)])]
		public class EffectsRemovePatch
		{
			public static bool Prefix(Effects __instance, HashedString effect_id)
			{
				using var scope = Profiler.Scope();
				bool hasIdentity = TryGetIdentity(__instance, out NetworkIdentity identity);
				bool shouldRun = ShouldRunMutation(MultiplayerSession.InSession,
					MultiplayerSession.IsHost, IsApplyingPacket, hasIdentity);
#if DEBUG
				if (!shouldRun)
					IntegrationScenarioEvidenceCore.Log(
						"effect", "client-original-blocked", 0, false,
						ToggleEffectPacket.EvidenceState(
							identity.NetId, effect_id.hash, active: false));
#endif
				return shouldRun;
			}

			public static void Postfix(Effects __instance, HashedString effect_id)
			{
				if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost ||
				    IsApplyingPacket || !TryGetIdentity(__instance, out NetworkIdentity identity))
					return;
				MarkDirty(identity, effect_id);
			}
		}
	}
}

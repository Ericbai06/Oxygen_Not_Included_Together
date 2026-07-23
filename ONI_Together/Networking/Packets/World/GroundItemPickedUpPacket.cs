using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	public sealed class GroundItemPickedUpPacket : IPacket, IHostOnlyPacket
	{
		internal const int MaxPendingPickups = 2048;
		internal const float PendingPickupLifetimeSeconds = 120f;
		private static readonly Dictionary<(int NetId, ulong Revision), float>
			PendingPickups = [];

		public int NetId;
		public ulong Revision;
#if DEBUG
		internal string ScenarioActionProfile = string.Empty;
#endif

		public GroundItemPickedUpPacket()
		{
		}

		public GroundItemPickedUpPacket(int netId)
		{
			NetId = netId;
			Revision = NetworkIdentityRegistry.EndLifecycle(netId);
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			ValidateWire();
			writer.Write(NetId);
			writer.Write(Revision);
#if DEBUG
			writer.Write(ScenarioActionProfile ?? string.Empty);
#endif
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			NetId = reader.ReadInt32();
			Revision = reader.ReadUInt64();
#if DEBUG
			ScenarioActionProfile = reader.ReadString();
#endif
			ValidateWire();
		}

		public void OnDispatched()
		{
#if DEBUG
			if (!string.IsNullOrEmpty(ScenarioActionProfile))
			{
				if (ScenarioActionReceiverGate.TryEnter(ScenarioActionProfile, "pickup"))
					PickupActionFlow.ExecutePickupClient(this);
				return;
			}
#endif
				DispatchContext context = PacketHandler.CurrentContext;
				if (!CanApplyRuntimePacket(context)
				    || !NetworkIdentityRegistry.TryAcceptLifecycleRevision(
					    NetId, Revision, tombstone: true))
					return;
				ApplyRuntimePacket(lifecycleAccepted: true);
			}

			internal bool ApplyRuntimePacket(bool lifecycleAccepted = false)
			{
				using var _ = Profiler.Scope();
				DispatchContext context = PacketHandler.CurrentContext;
				if (!CanApplyRuntimePacket(context))
					return false;
				if (!lifecycleAccepted
				    && !NetworkIdentityRegistry.TryAcceptLifecycleRevision(
					    NetId, Revision, tombstone: true))
					return false;
			StorageItemPacket.CancelPending(NetId);
			SpawnPrefabPacket.CancelPendingBinding(NetId);
			if (!NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity))
			{
				StorePending(NetId, Revision, Time.realtimeSinceStartup);
				return false;
			}
			if (!ApplyResolvedPickup(identity, Revision))
				return false;
			return true;
		}

		private static bool CanApplyRuntimePacket(DispatchContext context)
			=> !MultiplayerSession.IsHost && context.SenderIsHost
			   && PacketHandler.IsCurrentDispatchContext(context);

		internal static bool ApplyResolvedPickup(
			NetworkIdentity identity, ulong revision)
		{
			if (identity == null || identity.IsNullOrDestroyed()
			    || !ShouldRemoveResolved(identity.LifecycleRevision, revision)
			    || identity.GetComponent<Pickupable>() == null)
				return false;
			Util.KDestroyGameObject(identity.gameObject);
			return true;
		}

		internal void LogHostOutcome(string entryId)
		{
#if DEBUG
			LogEvidence("host-submit", entryId);
			LogEvidence("final-state", entryId);
#endif
		}

		internal static string CanonicalState(int netId)
			=> netId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":removed";

#if DEBUG
		private void LogEvidence(string phase, string entryId)
		{
			if (!NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity))
				return;
			int targetCell = Grid.PosToCell(identity.gameObject);
			if (!Grid.IsValidCell(targetCell))
				return;
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				phase, (long)Revision, NetId, targetCell, entryId));
		}

		internal static TypedEvidenceEnvelope CreateEvidence(
			string phase, long revision, int itemNetId, int targetCell, string entryId)
		{
			return TypedEvidenceRuntimeContext.Create(
				"pickup", phase, revision,
				new PickupTarget { ItemNetId = itemNetId, TargetCell = targetCell },
				new PickupState { Action = "picked-up", Tombstone = true }, entryId);
		}
#endif

		internal static bool ShouldRemoveResolved(
			ulong entityRevision, ulong tombstoneRevision)
			=> entityRevision != 0 && tombstoneRevision > entityRevision;

		public static bool TryConsumePending(int netId, ulong spawnRevision)
		{
			float now = Time.realtimeSinceStartup;
			PrunePending(now);
			foreach (var entry in PendingPickups.Where(
				         value => value.Key.NetId == netId).ToArray())
			{
				if (spawnRevision > entry.Key.Revision)
				{
					PendingPickups.Remove(entry.Key);
					continue;
				}
				if (spawnRevision == entry.Key.Revision)
					PendingPickups.Remove(entry.Key);
				return true;
			}

			return false;
		}

		internal static void ReleaseForNewLifecycle(int netId, ulong revision)
		{
			foreach (var key in PendingPickups.Keys.Where(key =>
				         key.NetId == netId && key.Revision < revision).ToArray())
				PendingPickups.Remove(key);
		}

		public static void ClearPending()
		{
			int count = PendingPickups.Count;
			PendingPickups.Clear();
			DebugConsole.Log($"[PendingPickup] cleared count={count}");
		}

		internal static void CancelPending(int netId)
		{
			foreach (var key in PendingPickups.Keys.Where(
				         key => key.NetId == netId).ToArray())
				PendingPickups.Remove(key);
		}

		private static void StorePending(int netId, ulong revision, float now)
		{
			PrunePending(now);
			ReleaseForNewLifecycle(netId, revision);
			var key = (netId, revision);
			if (!PendingPickups.ContainsKey(key)
			    && PendingPickups.Count >= MaxPendingPickups)
				PendingPickups.Remove(PendingPickups.OrderBy(value => value.Value).First().Key);
			PendingPickups[key] = now + PendingPickupLifetimeSeconds;
		}

		private static void PrunePending(float now)
		{
			foreach (var entry in PendingPickups.Where(
				         value => value.Value <= now).ToArray())
				PendingPickups.Remove(entry.Key);
		}

		internal static void StorePendingForTests(int netId, ulong revision, float now)
			=> StorePending(netId, revision, now);

		internal static void PrunePendingForTests(float now) => PrunePending(now);
		internal static int PendingCountForTests => PendingPickups.Count;

		private void ValidateWire()
		{
			if (NetId == 0 || Revision == 0 || Revision > long.MaxValue)
				throw new InvalidDataException("Invalid ground-item lifecycle metadata");
		}
	}
}

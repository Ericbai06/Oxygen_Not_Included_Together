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
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			NetId = reader.ReadInt32();
			Revision = reader.ReadUInt64();
			ValidateWire();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost || !context.SenderIsHost
			    || !PacketHandler.IsCurrentDispatchContext(context))
				return;
			ulong current = NetworkIdentityRegistry.GetLastLifecycleRevision(NetId);
#if DEBUG
			string phase = Revision > current ? "revision-accepted"
				: Revision == current ? "revision-duplicate" : "revision-out-of-order";
			IntegrationScenarioEvidenceCore.Log(
				"pickup", phase, (long)Revision, Revision > current, CanonicalState(NetId));
#endif
			if (!NetworkIdentityRegistry.TryAcceptLifecycleRevision(
				    NetId, Revision, tombstone: true))
				return;
			StorageItemPacket.CancelPending(NetId);
			SpawnPrefabPacket.CancelPendingBinding(NetId);
			if (!NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity))
			{
				StorePending(NetId, Revision, Time.realtimeSinceStartup);
				return;
			}
			if (ShouldRemoveResolved(identity.LifecycleRevision, Revision)
			    && identity.GetComponent<Pickupable>() != null)
				Util.KDestroyGameObject(identity.gameObject);
#if DEBUG
			IntegrationScenarioEvidenceCore.Log(
				"pickup", "client-apply", (long)Revision, true, CanonicalState(NetId));
			IntegrationScenarioEvidenceCore.Log(
				"pickup", "final-state", (long)Revision, true, CanonicalState(NetId));
#endif
		}

		internal void LogHostOutcome()
		{
#if DEBUG
			IntegrationScenarioEvidenceCore.Log(
				"pickup", "host-submit", (long)Revision, true, CanonicalState(NetId));
			IntegrationScenarioEvidenceCore.Log(
				"pickup", "final-state", (long)Revision, true, CanonicalState(NetId));
#endif
		}

		internal static string CanonicalState(int netId)
			=> netId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":removed";

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

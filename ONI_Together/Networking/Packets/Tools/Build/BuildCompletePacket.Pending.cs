using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public sealed partial class BuildCompletePacket
	{
		internal const int MaxPendingCompletions = 2048;
		internal const float PendingLifetimeSeconds = 120f;
		private static readonly Dictionary<(int NetId, ulong Revision), PendingCompletion>
			PendingCompletions = [];

		internal static void TryApplyPending(int netId, ulong revision)
		{
			float now = Time.realtimeSinceStartup;
			PrunePending(now);
			ReleaseForLifecycle(netId, revision, tombstoned: false);
			if (!TryTakePending(netId, revision, out BuildCompletePacket packet))
				return;
			if (!packet.IsLifecycleApplicable())
			{
#if DEBUG
				packet.LogRejectedRevision();
#endif
				ulong current = NetworkIdentityRegistry.GetLastLifecycleRevision(netId);
				if (packet.LifecycleRevision > current
				    && !NetworkIdentityRegistry.IsLifecycleTombstoned(netId))
					StorePending(packet, now);
				return;
			}
			if (packet.TryApplyCompletion() == CompletionResult.MissingTarget)
				StorePending(packet, now);
		}

		internal static void ReleaseForLifecycle(
			int netId, ulong revision, bool tombstoned)
		{
			foreach (var key in PendingCompletions.Keys.Where(key =>
			         key.NetId == netId
			         && ShouldReleasePending(key.Revision, revision, tombstoned)).ToArray())
				PendingCompletions.Remove(key);
		}

		internal static bool ShouldReleasePending(
			ulong pendingRevision, ulong lifecycleRevision, bool tombstoned)
			=> lifecycleRevision > pendingRevision
			   || tombstoned && lifecycleRevision == pendingRevision;

		internal static bool ShouldApplyPending(
			ulong pendingRevision, ulong lifecycleRevision, bool tombstoned)
			=> !tombstoned && pendingRevision != 0
			   && pendingRevision == lifecycleRevision;

		internal static void ClearPending() => PendingCompletions.Clear();

		internal static void CancelPending(int netId, ulong throughLifecycle)
		{
			foreach (var key in PendingCompletions.Keys.Where(key =>
			         key.NetId == netId && key.Revision <= throughLifecycle).ToArray())
				PendingCompletions.Remove(key);
		}

		private static void StorePending(BuildCompletePacket packet, float now)
		{
			PrunePending(now);
			if (PendingCompletions.Keys.Any(key =>
			    key.NetId == packet.NetId && key.Revision > packet.LifecycleRevision))
				return;
			ReleaseForLifecycle(packet.NetId, packet.LifecycleRevision, tombstoned: false);
			var key = (packet.NetId, packet.LifecycleRevision);
			if (!PendingCompletions.ContainsKey(key)
			    && PendingCompletions.Count >= MaxPendingCompletions)
			{
				var oldest = PendingCompletions.OrderBy(entry => entry.Value.ExpiresAt).First();
				PendingCompletions.Remove(oldest.Key);
			}
			PendingCompletions[key] = new PendingCompletion(
				Clone(packet), now + PendingLifetimeSeconds);
		}

		private static bool TryTakePending(
			int netId, ulong revision, out BuildCompletePacket packet)
		{
			if (PendingCompletions.Remove((netId, revision), out PendingCompletion pending))
			{
				packet = pending.Packet;
				return true;
			}
			packet = null;
			return false;
		}

		private static BuildCompletePacket Clone(BuildCompletePacket packet)
			=> new()
			{
				Cell = packet.Cell,
				PrefabID = packet.PrefabID,
				Orientation = packet.Orientation,
				MaterialTags = packet.MaterialTags == null ? [] : [.. packet.MaterialTags],
				Temperature = packet.Temperature,
				FacadeID = packet.FacadeID,
				UtilityConnectionFlags = packet.UtilityConnectionFlags,
				ObjectLayer = packet.ObjectLayer,
				NetId = packet.NetId,
				LifecycleRevision = packet.LifecycleRevision
			};

		private static void PrunePending(float now)
		{
			foreach (var entry in PendingCompletions.Where(
			         entry => entry.Value.ExpiresAt <= now).ToArray())
				PendingCompletions.Remove(entry.Key);
		}

		internal static void StorePendingForTests(BuildCompletePacket packet, float now)
			=> StorePending(packet, now);

		internal static void PrunePendingForTests(float now) => PrunePending(now);
		internal static bool HasPendingForTests(int netId, ulong revision)
			=> PendingCompletions.ContainsKey((netId, revision));
		internal static bool TryTakePendingForTests(
			int netId, ulong revision, out BuildCompletePacket packet)
			=> TryTakePending(netId, revision, out packet);
		internal static int PendingCountForTests => PendingCompletions.Count;

		private readonly struct PendingCompletion
		{
			internal readonly BuildCompletePacket Packet;
			internal readonly float ExpiresAt;

			internal PendingCompletion(BuildCompletePacket packet, float expiresAt)
			{
				Packet = packet;
				ExpiresAt = expiresAt;
			}
		}
	}
}

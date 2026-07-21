using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.DuplicantActions
{
	internal sealed class DuplicantPresentationBatchPacket : IPacket, IHostOnlyPacket
	{
		private const int HeaderBytes = sizeof(int) + sizeof(int);
		internal const int MaxEntriesPerBatch =
			(PacketSender.MAX_PACKET_SIZE_UNRELIABLE - HeaderBytes)
			/ DuplicantPresentationEntry.WireBytes;
		private static readonly Dictionary<int, ulong> LastRevisions = [];

		public DuplicantPresentationEntry[] Entries = [];

		public void Serialize(BinaryWriter writer)
		{
			if (Entries == null || Entries.Length == 0 || Entries.Length > MaxEntriesPerBatch)
				throw new InvalidDataException("Invalid duplicant presentation batch");
			writer.Write(Entries.Length);
			foreach (DuplicantPresentationEntry entry in Entries) entry.Serialize(writer);
		}

		public void Deserialize(BinaryReader reader)
		{
			int count = reader.ReadInt32();
			if (count <= 0 || count > MaxEntriesPerBatch)
				throw new InvalidDataException("Invalid duplicant presentation batch");
			Entries = new DuplicantPresentationEntry[count];
			for (int index = 0; index < count; index++)
			{
				Entries[index] = new DuplicantPresentationEntry();
				Entries[index].Deserialize(reader);
			}
		}

		public void OnDispatched()
		{
			if (MultiplayerSession.IsHost) return;
			foreach (DuplicantPresentationEntry entry in Entries)
			{
				if (!AcceptEntityRevision(entry.NetId, entry.Revision)
				    || !NetworkIdentityRegistry.TryGet(entry.NetId, out NetworkIdentity identity))
					continue;
				identity.gameObject.AddOrGet<RemoteDuplicantPresenter>().ApplySnapshot(entry);
#if DEBUG
				LogClientEvidence("animation", entry);
				if (entry.ActionState == DuplicantActionState.Digging)
					LogClientEvidence("remote-dig", entry);
#endif
			}
		}

		internal static List<DuplicantPresentationBatchPacket> CreateBatches(
			IEnumerable<DuplicantPresentationEntry> entries)
		{
			DuplicantPresentationEntry[] ordered = (entries ?? [])
				.Where(entry => entry != null && entry.NetId != 0 && entry.Revision != 0)
				.GroupBy(entry => entry.NetId)
				.Select(group => group.OrderByDescending(entry => entry.Revision).First())
				.OrderBy(entry => entry.NetId).ToArray();
			var batches = new List<DuplicantPresentationBatchPacket>();
			for (int start = 0; start < ordered.Length; start += MaxEntriesPerBatch)
			{
				int count = Math.Min(MaxEntriesPerBatch, ordered.Length - start);
				var page = new DuplicantPresentationEntry[count];
				Array.Copy(ordered, start, page, 0, count);
				batches.Add(new DuplicantPresentationBatchPacket { Entries = page });
			}
			return batches;
		}

		internal static void ResetSessionState() => LastRevisions.Clear();
		internal static void ForgetNetId(int netId) => LastRevisions.Remove(netId);
		internal static bool AcceptEntityRevisionForTests(int netId, ulong revision)
			=> AcceptEntityRevision(netId, revision);

		private static bool AcceptEntityRevision(int netId, ulong revision)
		{
			if (netId == 0 || revision == 0
			    || LastRevisions.TryGetValue(netId, out ulong last) && revision <= last)
				return false;
			LastRevisions[netId] = revision;
			return true;
		}

#if DEBUG
		internal static string EvidenceState(DuplicantPresentationEntry entry)
			=> string.Join(",",
				FormattableString.Invariant($"netId={entry.NetId},tick={entry.StartSimTick},duration={entry.DurationTicks}"),
				FormattableString.Invariant($"action={(byte)entry.ActionState},anim={entry.AnimHash},mode={entry.PlayMode}"),
				FormattableString.Invariant($"speed={entry.AnimSpeed:R},elapsed={entry.AnimElapsedAtStart:R}"),
				FormattableString.Invariant($"working={(entry.IsWorking ? 1 : 0)},workVisual={(byte)entry.WorkVisual}"),
				FormattableString.Invariant($"targetCell={entry.TargetCell},targetNetId={entry.VisualTargetNetId}"),
				FormattableString.Invariant($"tool={(byte)entry.ToolVisual},facing={(byte)entry.Facing}"),
				FormattableString.Invariant($"showProgress={(entry.ShowProgress ? 1 : 0)},progress={entry.ProgressPercent:R}"),
				FormattableString.Invariant($"remaining={entry.WorkTimeRemaining:R},total={entry.WorkTimeTotal:R}"));

		private static void LogClientEvidence(
			string scenario, DuplicantPresentationEntry entry)
		{
			string state = EvidenceState(entry);
			long revision = (long)entry.Revision;
			IntegrationScenarioEvidenceCore.Log(scenario, "client-apply", revision, true, state);
			IntegrationScenarioEvidenceCore.Log(scenario, "revision-accepted", revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				scenario, "revision-duplicate", revision,
				AcceptEntityRevision(entry.NetId, entry.Revision), state);
			ulong olderRevision = entry.Revision - 1;
			IntegrationScenarioEvidenceCore.Log(
				scenario, "revision-out-of-order", (long)olderRevision,
				AcceptEntityRevision(entry.NetId, olderRevision), state);
			IntegrationScenarioEvidenceCore.Log(scenario, "final-state", revision, true, state);
		}
#endif
	}
}

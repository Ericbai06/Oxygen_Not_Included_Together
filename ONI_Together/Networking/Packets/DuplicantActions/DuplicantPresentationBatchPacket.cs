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
				#if DEBUG
				string revisionPhase = RevisionPhase(entry.NetId, entry.Revision);
				#endif
				bool accepted = AcceptEntityRevision(entry.NetId, entry.Revision);
				#if DEBUG
				LogRevisionEvidence(entry, revisionPhase);
				#endif
				if (!accepted || !NetworkIdentityRegistry.TryGet(
					    entry.NetId, out NetworkIdentity identity))
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
		private const string PacketDispatchEntryId = "sync:f60e38b805c1052cff0fec0d";

		private static string RevisionPhase(int netId, ulong revision)
		{
			if (!LastRevisions.TryGetValue(netId, out ulong last) || revision > last)
				return "revision-accepted";
			return revision == last ? "revision-duplicate" : "revision-out-of-order";
		}

		private static void LogRevisionEvidence(
			DuplicantPresentationEntry entry, string phase)
		{
			long revision = (long)entry.Revision;
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"animation", phase, revision, entry, PacketDispatchEntryId));
			if (entry.ActionState == DuplicantActionState.Digging)
				IntegrationScenarioEvidenceCore.Log(CreateEvidence(
					"remote-dig", phase, revision, entry, PacketDispatchEntryId));
		}

		internal static ITypedEvidenceTarget CreateEvidenceTarget(
			string scenario, DuplicantPresentationEntry entry)
			=> scenario == "remote-dig"
				? new RemoteDigTarget
				{
					MinionNetId = entry.NetId,
					TargetNetId = entry.VisualTargetNetId,
					TargetCell = entry.TargetCell,
				}
				: new AnimationTarget
				{
					MinionNetId = entry.NetId,
					TargetNetId = entry.VisualTargetNetId,
					TargetCell = entry.TargetCell,
				};

		internal static ITypedEvidenceState CreateEvidenceState(
			string scenario, DuplicantPresentationEntry entry)
		{
			string action = entry.ActionState.ToString();
			string animation = entry.AnimHash.ToString(
				System.Globalization.CultureInfo.InvariantCulture);
			string tool = entry.ToolVisual.ToString();
			double progress = entry.ShowProgress ? entry.ProgressPercent : 0d;
			return scenario == "remote-dig"
				? new RemoteDigState
				{
					Action = action, Animation = animation,
					Tool = tool, Progress = progress,
				}
				: new AnimationState
				{
					Action = action, Animation = animation,
					Tool = tool, Progress = progress,
				};
		}

		internal static TypedEvidenceEnvelope CreateEvidence(
			string scenario, string phase, long revision,
			DuplicantPresentationEntry entry, string entryId)
			=> TypedEvidenceRuntimeContext.Create(
				scenario, phase, revision,
				CreateEvidenceTarget(scenario, entry),
				CreateEvidenceState(scenario, entry), entryId);

		private static void LogClientEvidence(
			string scenario, DuplicantPresentationEntry entry)
		{
			long revision = (long)entry.Revision;
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				scenario, "client-apply", revision, entry, PacketDispatchEntryId));
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				scenario, "final-state", revision, entry, PacketDispatchEntryId));
		}
#endif
	}
}

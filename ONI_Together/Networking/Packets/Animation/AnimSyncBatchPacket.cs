using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Animation
{
	internal sealed class AnimSyncBatchPacket : IPacket, Shared.Interfaces.Networking.IHostOnlyPacket
	{
		private const int WireHeaderBytes = sizeof(int) + sizeof(ulong) + sizeof(int);
		private const int EntryBytes = sizeof(int) * 2 + sizeof(byte) + sizeof(float) * 2
		                               + sizeof(long) + sizeof(uint);
		internal const int MaxEntriesPerBatch =
			(PacketSender.MAX_PACKET_SIZE_UNRELIABLE - WireHeaderBytes) / EntryBytes;

		private static readonly Dictionary<int, ulong> LastEntityRevisions = [];

		public ulong Revision;
		public AnimSyncPacket[] States = [];

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			if (Revision == 0 || States == null || States.Length == 0
			    || States.Length > MaxEntriesPerBatch || !FitsUnreliableWire(States.Length)
			    || States.Any(state => state?.IsWireValid() != true))
				throw new InvalidDataException("Invalid animation batch");
			writer.Write(Revision);
			writer.Write(States.Length);
			foreach (AnimSyncPacket state in States)
				state.Serialize(writer);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			Revision = reader.ReadUInt64();
			int count = reader.ReadInt32();
			if (Revision == 0 || count <= 0 || count > MaxEntriesPerBatch
			    || !FitsUnreliableWire(count))
				throw new InvalidDataException("Invalid animation batch");
			States = new AnimSyncPacket[count];
			for (int index = 0; index < count; index++)
			{
				States[index] = new AnimSyncPacket();
				States[index].Deserialize(reader);
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost || Revision == 0)
				return;
			foreach (AnimSyncPacket state in States)
				if (state != null && AcceptEntityRevision(state.NetId, Revision))
					state.ApplySnapshot();
		}

		internal static List<AnimSyncBatchPacket> CreateBatches(
			ulong revision, IEnumerable<AnimSyncPacket> snapshots)
		{
			if (revision == 0)
				throw new ArgumentOutOfRangeException(nameof(revision));
			var latest = new Dictionary<int, AnimSyncPacket>();
			foreach (AnimSyncPacket snapshot in snapshots ?? [])
				if (snapshot != null && snapshot.NetId != 0)
					latest[snapshot.NetId] = snapshot;

			AnimSyncPacket[] ordered = latest.Values.OrderBy(state => state.NetId).ToArray();
			var batches = new List<AnimSyncBatchPacket>();
			for (int start = 0; start < ordered.Length; start += MaxEntriesPerBatch)
			{
				int count = Math.Min(MaxEntriesPerBatch, ordered.Length - start);
				var states = new AnimSyncPacket[count];
				Array.Copy(ordered, start, states, 0, count);
				batches.Add(new AnimSyncBatchPacket { Revision = revision, States = states });
			}
			return batches;
		}

		internal static void ResetSessionState()
		{
			LastEntityRevisions.Clear();
		}

		internal static void ForgetNetId(int netId) => LastEntityRevisions.Remove(netId);

		internal static bool AcceptBatchRevisionForTests(ulong revision)
			=> revision != 0;

		internal static bool AcceptEntityRevisionForTests(int netId, ulong revision)
			=> AcceptEntityRevision(netId, revision);

		private static bool FitsUnreliableWire(int count)
			=> WireHeaderBytes + count * EntryBytes < PacketSender.MAX_PACKET_SIZE_UNRELIABLE;

		private static bool AcceptEntityRevision(int netId, ulong revision)
		{
			if (netId == 0 || revision == 0
			    || LastEntityRevisions.TryGetValue(netId, out ulong last) && revision <= last)
				return false;
			LastEntityRevisions[netId] = revision;
			return true;
		}
	}
}

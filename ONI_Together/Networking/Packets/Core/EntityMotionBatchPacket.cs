using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Core
{
	internal enum EntityMotionKind : byte { Transition, Correction, Stop }

	[Flags]
	internal enum EntityMotionFlags : byte
	{
		None = 0,
		FlipX = 1,
		FlipY = 2,
	}

	internal sealed class EntityMotionState
	{
		internal const int WireBytes = sizeof(int) + sizeof(ulong) + sizeof(byte)
		                               + sizeof(long) + sizeof(float) * 6 + sizeof(uint)
		                               + sizeof(byte) * 3;
		public int NetId;
		public ulong Revision;
		public EntityMotionKind Kind;
		public long StartSimTick;
		public Vector3 Source;
		public Vector3 Target;
		public uint DurationTicks;
		public NavType StartNavType;
		public NavType EndNavType;
		public EntityMotionFlags Flags;

		internal bool FlipX => (Flags & EntityMotionFlags.FlipX) != 0;
		internal bool FlipY => (Flags & EntityMotionFlags.FlipY) != 0;

		internal void Serialize(BinaryWriter writer)
		{
			if (!IsWireValid()) throw new InvalidDataException("Invalid motion entry");
			writer.Write(NetId);
			writer.Write(Revision);
			writer.Write((byte)Kind);
			writer.Write(StartSimTick);
			writer.Write(Source);
			writer.Write(Target);
			writer.Write(DurationTicks);
			writer.Write((byte)StartNavType);
			writer.Write((byte)EndNavType);
			writer.Write((byte)Flags);
		}

		internal void Deserialize(BinaryReader reader)
		{
			NetId = reader.ReadInt32();
			Revision = reader.ReadUInt64();
			Kind = (EntityMotionKind)reader.ReadByte();
			StartSimTick = reader.ReadInt64();
			Source = reader.ReadVector3();
			Target = reader.ReadVector3();
			DurationTicks = reader.ReadUInt32();
			StartNavType = (NavType)reader.ReadByte();
			EndNavType = (NavType)reader.ReadByte();
			Flags = (EntityMotionFlags)reader.ReadByte();
			if (!IsWireValid()) throw new InvalidDataException("Invalid motion entry");
		}

		private bool IsWireValid()
			=> NetId != 0 && Revision != 0 && StartSimTick >= 0 && DurationTicks > 0
			   && Enum.IsDefined(typeof(EntityMotionKind), Kind)
			   && StartNavType < NavType.NumNavTypes && EndNavType < NavType.NumNavTypes
			   && (Flags & ~(EntityMotionFlags.FlipX | EntityMotionFlags.FlipY)) == 0
			   && IsFinite(Source) && IsFinite(Target);

		private static bool IsFinite(Vector3 value)
			=> IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}

	internal sealed class EntityMotionBatchPacket : IPacket, IHostOnlyPacket
	{
		private const int HeaderBytes = sizeof(int) + sizeof(int);
		internal const int MaxEntriesPerBatch =
			(PacketSender.MAX_PACKET_SIZE_UNRELIABLE - HeaderBytes) / EntityMotionState.WireBytes;
		private static readonly Dictionary<int, ulong> LastRevisions = [];
		public EntityMotionState[] States = [];

		public void Serialize(BinaryWriter writer)
		{
			if (States == null || States.Length == 0 || States.Length > MaxEntriesPerBatch)
				throw new InvalidDataException("Invalid motion batch");
			writer.Write(States.Length);
			foreach (EntityMotionState state in States) state.Serialize(writer);
		}

		public void Deserialize(BinaryReader reader)
		{
			int count = reader.ReadInt32();
			if (count <= 0 || count > MaxEntriesPerBatch)
				throw new InvalidDataException("Invalid motion batch");
			States = new EntityMotionState[count];
			for (int index = 0; index < count; index++)
			{
				States[index] = new EntityMotionState();
				States[index].Deserialize(reader);
			}
		}

		public void OnDispatched()
		{
			if (MultiplayerSession.IsHost) return;
			foreach (EntityMotionState state in States)
			{
				if (!AcceptEntityRevision(state.NetId, state.Revision)
				    || !NetworkIdentityRegistry.TryGet(state.NetId, out NetworkIdentity identity))
					continue;
				identity.gameObject.AddOrGet<RemoteMotionPresenter>().ApplySnapshot(state);
#if DEBUG
				LogClientEvidence(state);
#endif
			}
		}

		internal static List<EntityMotionBatchPacket> CreateBatches(
			IEnumerable<EntityMotionState> states)
		{
			EntityMotionState[] ordered = (states ?? [])
				.Where(state => state != null && state.NetId != 0 && state.Revision != 0)
				.GroupBy(state => state.NetId)
				.Select(group => group.OrderByDescending(state => state.Revision).First())
				.OrderBy(state => state.NetId).ToArray();
			var batches = new List<EntityMotionBatchPacket>();
			for (int start = 0; start < ordered.Length; start += MaxEntriesPerBatch)
			{
				int count = Math.Min(MaxEntriesPerBatch, ordered.Length - start);
				var page = new EntityMotionState[count];
				Array.Copy(ordered, start, page, 0, count);
				batches.Add(new EntityMotionBatchPacket { States = page });
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
		internal static string EvidenceState(EntityMotionState state)
			=> string.Join(",",
				FormattableString.Invariant($"netId={state.NetId},kind={(byte)state.Kind},tick={state.StartSimTick}"),
				FormattableString.Invariant($"source={state.Source.x:R}|{state.Source.y:R}|{state.Source.z:R}"),
				FormattableString.Invariant($"target={state.Target.x:R}|{state.Target.y:R}|{state.Target.z:R}"),
				FormattableString.Invariant($"duration={state.DurationTicks},startNav={(byte)state.StartNavType}"),
				FormattableString.Invariant($"endNav={(byte)state.EndNavType},flags={(byte)state.Flags}"));

		private static void LogClientEvidence(EntityMotionState state)
		{
			string evidenceState = EvidenceState(state);
			long revision = (long)state.Revision;
			IntegrationScenarioEvidenceCore.Log(
				"motion", "client-apply", revision, true, evidenceState);
			IntegrationScenarioEvidenceCore.Log(
				"motion", "revision-accepted", revision, true, evidenceState);
			IntegrationScenarioEvidenceCore.Log(
				"motion", "revision-duplicate", revision,
				AcceptEntityRevision(state.NetId, state.Revision), evidenceState);
			ulong olderRevision = state.Revision - 1;
			IntegrationScenarioEvidenceCore.Log(
				"motion", "revision-out-of-order", (long)olderRevision,
				AcceptEntityRevision(state.NetId, olderRevision), evidenceState);
			IntegrationScenarioEvidenceCore.Log(
				"motion", "final-state", revision, true, evidenceState);
		}
#endif
	}
}

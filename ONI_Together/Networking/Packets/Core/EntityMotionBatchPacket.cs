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
#if DEBUG
		internal string ScenarioActionProfile = string.Empty;
#endif

		public void Serialize(BinaryWriter writer)
		{
			if (States == null || States.Length == 0 || States.Length > MaxEntriesPerBatch)
				throw new InvalidDataException("Invalid motion batch");
			writer.Write(States.Length);
			foreach (EntityMotionState state in States) state.Serialize(writer);
#if DEBUG
			writer.Write(ScenarioActionProfile ?? string.Empty);
#endif
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
#if DEBUG
			ScenarioActionProfile = reader.ReadString();
#endif
		}

		public void OnDispatched()
		{
#if DEBUG
			if (!string.IsNullOrEmpty(ScenarioActionProfile))
			{
				if (ScenarioActionReceiverGate.TryEnter(ScenarioActionProfile, "motion"))
					MotionActionFlow.ExecuteClient(this);
				return;
			}
			ApplyRuntimePacket();
#else
			ApplyRuntimePacket();
#endif
		}

		internal bool ApplyRuntimePacket()
		{
			if (MultiplayerSession.IsHost) return false;
			bool applied = false;
			foreach (EntityMotionState state in States)
			{
				if (!AcceptEntityRevision(state.NetId, state.Revision)
				    || !NetworkIdentityRegistry.TryGet(state.NetId, out NetworkIdentity identity))
					continue;
				identity.gameObject.AddOrGet<RemoteMotionPresenter>().ApplySnapshot(state);
				applied = true;
			}
			return applied;
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
		private const string PacketDispatchEntryId = "sync:f60e38b805c1052cff0fec0d";

		internal static TypedEvidenceEnvelope CreateEvidence(
			string phase, long revision, EntityMotionState state, string entryId)
			=> TypedEvidenceRuntimeContext.Create(
				"motion", phase, revision,
				new MotionTarget { EntityNetId = state.NetId },
				new MotionState
				{
					Tick = state.StartSimTick,
					StartPosition = new[] { (double)state.Source.x, state.Source.y },
					EndPosition = new[] { (double)state.Target.x, state.Target.y },
					NavigationState = state.Kind + ":" + state.StartNavType + "->" + state.EndNavType,
					MotionRevision = revision,
				}, entryId);

		private static void LogClientEvidence(EntityMotionState state)
		{
			long revision = (long)state.Revision;
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"revision-accepted", revision, state, PacketDispatchEntryId));
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"client-apply", revision, state, PacketDispatchEntryId));
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"final-state", revision, state, PacketDispatchEntryId));
		}
#endif
	}
}

using System.IO;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.Core
{
	internal struct ReadyReplayBatchHeader
	{
		internal long SnapshotGeneration;
		internal ulong ReplayId;
		internal int BatchIndex;
		internal int BatchCount;

		internal bool IsValid()
			=> SnapshotGeneration > 0 && ReplayId != 0
			   && BatchCount > 0 && BatchCount <= DeferredReliableBatchPacket.MaxBatches
			   && BatchIndex >= 0 && BatchIndex < BatchCount;

		internal void Serialize(BinaryWriter writer)
		{
			writer.Write(SnapshotGeneration);
			writer.Write(ReplayId);
			writer.Write(BatchIndex);
			writer.Write(BatchCount);
		}

		internal static ReadyReplayBatchHeader Deserialize(BinaryReader reader)
			=> new()
			{
				SnapshotGeneration = reader.ReadInt64(),
				ReplayId = reader.ReadUInt64(),
				BatchIndex = reader.ReadInt32(),
				BatchCount = reader.ReadInt32(),
			};
	}

	internal struct ReadyReplayProof
	{
		internal ulong ReconnectToken;
		internal long SnapshotGeneration;
		internal ulong ReplayId;
		internal int BatchCount;

		internal bool IsValid()
			=> ReconnectToken != 0 && SnapshotGeneration > 0 && ReplayId != 0
			   && BatchCount >= 0 && BatchCount <= DeferredReliableBatchPacket.MaxBatches;

		internal bool Matches(ReadyReplayProof other)
			=> ReconnectToken == other.ReconnectToken
			   && SnapshotGeneration == other.SnapshotGeneration
			   && ReplayId == other.ReplayId
			   && BatchCount == other.BatchCount;

		internal void Serialize(BinaryWriter writer)
		{
			writer.Write(ReconnectToken);
			writer.Write(SnapshotGeneration);
			writer.Write(ReplayId);
			writer.Write(BatchCount);
		}

		internal static ReadyReplayProof Deserialize(BinaryReader reader)
			=> new()
			{
				ReconnectToken = reader.ReadUInt64(),
				SnapshotGeneration = reader.ReadInt64(),
				ReplayId = reader.ReadUInt64(),
				BatchCount = reader.ReadInt32(),
			};
	}

	internal sealed class ReadyReplayCommitPacket : IPacket, IHostOnlyPacket
	{
		private ReadyReplayProof _proof;

		public ReadyReplayCommitPacket() { }

		internal static ReadyReplayCommitPacket Create(ReadyReplayProof proof)
		{
			if (!proof.IsValid())
				throw new InvalidDataException("Invalid Ready replay commit proof");
			return new ReadyReplayCommitPacket { _proof = proof };
		}

		internal ReadyReplayProof Proof => _proof;

		public void Serialize(BinaryWriter writer)
		{
			Validate();
			_proof.Serialize(writer);
		}

		public void Deserialize(BinaryReader reader)
		{
			_proof = ReadyReplayProof.Deserialize(reader);
			Validate();
		}

		public void OnDispatched()
		{
			Validate();
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost || !context.SenderIsHost)
				throw new InvalidDataException("Ready replay commit requires the host");
			if (!GameClient.TryAcceptReadyReplayCommit(_proof, context))
				throw new InvalidDataException("Ready replay commit is incomplete or stale");
		}

		private void Validate()
		{
			if (!_proof.IsValid())
				throw new InvalidDataException("Invalid Ready replay commit proof");
		}
	}

	internal sealed class ReadyReplayAppliedPacket : IPacket
	{
		private ReadyReplayProof _proof;

		public ReadyReplayAppliedPacket() { }

		internal static ReadyReplayAppliedPacket Create(ReadyReplayProof proof)
		{
			if (!proof.IsValid())
				throw new InvalidDataException("Invalid Ready replay apply proof");
			return new ReadyReplayAppliedPacket { _proof = proof };
		}

		public void Serialize(BinaryWriter writer)
		{
			if (!_proof.IsValid())
				throw new InvalidDataException("Invalid Ready replay apply proof");
			_proof.Serialize(writer);
		}

		public void Deserialize(BinaryReader reader)
		{
			_proof = ReadyReplayProof.Deserialize(reader);
			if (!_proof.IsValid())
				throw new InvalidDataException("Invalid Ready replay apply proof");
		}

		public void OnDispatched()
		{
			DispatchContext context = PacketHandler.CurrentContext;
			if (!MultiplayerSession.IsHost || context.SenderIsHost
			    || !ReadyManager.AcceptReadyReplayApplied(
				    context.SenderId, context.ConnectionGeneration, _proof))
				throw new InvalidDataException("Ready replay apply proof is stale or unexpected");
		}
	}
}

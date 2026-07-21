using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.Core
{
	internal sealed class DeferredReliableBatchPacket : IPacket, IHostOnlyPacket
	{
		internal const int MaxWireBytes = 64 * 1024;
		internal const int MaxFrames = 4096;
		internal const int MaxBatches = 4096;
		internal const int FixedWireBytes = sizeof(int) * 4 + sizeof(long) + sizeof(ulong);
		internal const int MaxFrameCost = MaxWireBytes - FixedWireBytes;
		private readonly List<byte[]> _frames = new();
		private ReadyReplayBatchHeader _header;

		public DeferredReliableBatchPacket() { }

		internal static DeferredReliableBatchPacket Create(
			ReadyReplayBatchHeader header, IEnumerable<byte[]> frames)
		{
			var packet = new DeferredReliableBatchPacket { _header = header };
			if (frames != null)
				packet._frames.AddRange(frames);
			packet.Validate();
			return packet;
		}

		internal ReadyReplayBatchHeader Header => _header;
		internal int FrameCount => _frames.Count;

		public void Serialize(BinaryWriter writer)
		{
			Validate();
			_header.Serialize(writer);
			writer.Write(_frames.Count);
			foreach (byte[] frame in _frames)
			{
				writer.Write(frame.Length);
				writer.Write(frame);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			_header = ReadyReplayBatchHeader.Deserialize(reader);
			int count = reader.ReadInt32();
			if (count <= 0 || count > MaxFrames)
				throw new InvalidDataException("Invalid deferred reliable frame count");
			_frames.Clear();
			int wireBytes = FixedWireBytes;
			for (int index = 0; index < count; index++)
				ReadFrame(reader, _frames, ref wireBytes);
			Validate();
		}

		public void OnDispatched()
		{
			Validate();
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost || !context.SenderIsHost)
				throw new InvalidDataException("Deferred reliable batch requires the host");
			if (!GameClient.TryAcceptReadyReplayBatch(_header, _frames, context))
				throw new InvalidDataException("Deferred reliable batch is stale or conflicting");
		}

		private void Validate()
		{
			if (!_header.IsValid() || _frames.Count <= 0 || _frames.Count > MaxFrames)
				throw new InvalidDataException("Invalid deferred reliable batch metadata");
			int wireBytes = FixedWireBytes;
			foreach (byte[] frame in _frames)
			{
				if (frame == null || frame.Length < sizeof(int)
				    || PacketHandler.IsForbiddenReadyReplayFrame(frame))
					throw new InvalidDataException("Invalid deferred reliable frame");
				wireBytes = checked(wireBytes + sizeof(int) + frame.Length);
				if (wireBytes > MaxWireBytes)
					throw new InvalidDataException("Deferred reliable batch exceeds 64 KiB");
			}
		}

		private static void ReadFrame(
			BinaryReader reader, ICollection<byte[]> frames, ref int wireBytes)
		{
			int length = reader.ReadInt32();
			if (length < sizeof(int) || length > MaxWireBytes - wireBytes - sizeof(int))
				throw new InvalidDataException("Invalid deferred reliable frame length");
			byte[] frame = reader.ReadBytes(length);
			if (frame.Length != length)
				throw new EndOfStreamException("Deferred reliable batch is truncated");
			frames.Add(frame);
			wireBytes += sizeof(int) + length;
		}
	}
}

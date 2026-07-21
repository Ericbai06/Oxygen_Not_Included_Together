#if DEBUG
using System.IO;
using System.Threading;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.Core
{
	internal sealed class ReadyReplayLoadPacket : IPacket, IHostOnlyPacket
	{
		internal const int MaxFrames = 512;
		internal const int MinPayloadBytes = 256;
		internal const int MaxPayloadBytes = 4096;
		private static int _nextRunId;
		private int _runId;
		private int _index;
		private int _count;
		private byte[] _payload;

		public ReadyReplayLoadPacket() { }

		internal ReadyReplayLoadPacket(
			int runId, int index, int count, int payloadBytes)
		{
			_runId = runId;
			_index = index;
			_count = count;
			_payload = CreatePayload(runId, index, payloadBytes);
			Validate();
		}

		internal static int NextRunId()
		{
			int runId = Interlocked.Increment(ref _nextRunId);
			if (runId > 0)
				return runId;
			Interlocked.Exchange(ref _nextRunId, 1);
			return 1;
		}

		public void Serialize(BinaryWriter writer)
		{
			Validate();
			writer.Write(_runId);
			writer.Write(_index);
			writer.Write(_count);
			writer.Write(_payload.Length);
			writer.Write(_payload);
		}

		public void Deserialize(BinaryReader reader)
		{
			_runId = reader.ReadInt32();
			_index = reader.ReadInt32();
			_count = reader.ReadInt32();
			int payloadBytes = reader.ReadInt32();
			if (!IsValidShape(_runId, _index, _count, payloadBytes))
				throw new InvalidDataException("Invalid Ready replay load shape");
			_payload = reader.ReadBytes(payloadBytes);
			Validate();
		}

		public void OnDispatched()
		{
			Validate();
			if (MultiplayerSession.IsHost || !PacketHandler.CurrentContext.SenderIsHost)
				throw new InvalidDataException("Ready replay load requires the authoritative host");
			for (int offset = 0; offset < _payload.Length; offset++)
			{
				if (_payload[offset] != (byte)(_runId + _index + offset))
					throw new InvalidDataException("Ready replay load payload was corrupted");
			}
			if (_index == 0)
				DebugConsole.Log(
					$"[ReadyReplayLoad] Applying run={_runId} frames={_count} payloadBytes={_payload.Length}");
			if (_index == _count - 1)
				DebugConsole.Log(
					$"[ReadyReplayLoad] Applied run={_runId} frames={_count} payloadBytes={_payload.Length}");
		}

		private void Validate()
		{
			int payloadBytes = _payload?.Length ?? 0;
			if (!IsValidShape(_runId, _index, _count, payloadBytes))
				throw new InvalidDataException("Invalid Ready replay load packet");
		}

		internal static bool IsValidShape(
			int runId, int index, int count, int payloadBytes)
			=> runId > 0 && count > 0 && count <= MaxFrames
			   && index >= 0 && index < count
			   && payloadBytes >= MinPayloadBytes && payloadBytes <= MaxPayloadBytes;

		private static byte[] CreatePayload(int runId, int index, int payloadBytes)
		{
			var payload = new byte[payloadBytes];
			for (int offset = 0; offset < payload.Length; offset++)
				payload[offset] = (byte)(runId + index + offset);
			return payload;
		}
	}
}
#endif

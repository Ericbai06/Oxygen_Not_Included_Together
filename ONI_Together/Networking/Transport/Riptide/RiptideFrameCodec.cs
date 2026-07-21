using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.Networking.Transport.Lan
{
	internal enum RiptideFrameResult
	{
		Complete,
		Incomplete,
		Rejected
	}

	internal static class RiptideFrameCodec
	{
		internal const int MaxNativePayloadBytes = 1000;
		private const int HeaderBytes = sizeof(ulong) + sizeof(int) * 5;
		private const int MaxFrameDataBytes = MaxNativePayloadBytes - HeaderBytes;
		private const int MaxFrames = 32768;
		private const int MaxPendingAssemblies = 64;
		private const int MaxPendingDataBytes = PacketHandler.MaxPacketSize * 4;
		private const int MaxCompletedAssemblies = 1024;
		private const ulong Magic = 0x52465054494E4F31UL;
		private static readonly System.TimeSpan PendingTimeout = System.TimeSpan.FromSeconds(30);
		private static readonly Dictionary<AssemblyKey, PendingMessage> Pending = new();
		private static readonly HashSet<AssemblyKey> Completed = new();
		private static readonly Queue<AssemblyKey> CompletedOrder = new();
		private static int _nextSequence;
		private static int _pendingDataBytes;

		private readonly struct SenderKey : IEquatable<SenderKey>
		{
			private readonly ulong _senderId;
			private readonly long _generation;
			private readonly long _epoch;

			internal SenderKey(DispatchContext context)
			{
				_senderId = context.SenderId;
				_generation = context.ConnectionGeneration;
				_epoch = context.SessionEpoch;
			}

			public bool Equals(SenderKey other)
				=> _senderId == other._senderId && _generation == other._generation
				   && _epoch == other._epoch;

			public override bool Equals(object obj)
				=> obj is SenderKey other && Equals(other);

			public override int GetHashCode()
				=> (_senderId, _generation, _epoch).GetHashCode();
		}

		private readonly struct AssemblyKey : IEquatable<AssemblyKey>
		{
			private readonly SenderKey _sender;
			private readonly int _sequence;

			internal AssemblyKey(SenderKey sender, int sequence)
			{
				_sender = sender;
				_sequence = sequence;
			}

			public bool Equals(AssemblyKey other)
				=> _sender.Equals(other._sender) && _sequence == other._sequence;

			public override bool Equals(object obj)
				=> obj is AssemblyKey other && Equals(other);

			public override int GetHashCode()
				=> (_sender, _sequence).GetHashCode();
		}

		private sealed class PendingMessage
		{
			internal readonly byte[][] Frames;
			internal readonly int FrameCount;
			internal readonly int TotalBytes;
			internal int ReceivedFrames;
			internal int ReceivedBytes;
			internal System.DateTime LastUpdated;

			internal PendingMessage(FrameHeader header)
			{
				Frames = new byte[header.FrameCount][];
				FrameCount = header.FrameCount;
				TotalBytes = header.TotalBytes;
				LastUpdated = System.DateTime.UtcNow;
			}
		}

		private struct FrameHeader
		{
			internal int Sequence;
			internal int Index;
			internal int FrameCount;
			internal int TotalBytes;
			internal int DataBytes;
		}

		internal static void ResetSessionState()
		{
			Pending.Clear();
			Completed.Clear();
			CompletedOrder.Clear();
			_pendingDataBytes = 0;
			_nextSequence = 0;
		}

		internal static bool TryCreateFrames(byte[] payload, out List<byte[]> frames)
		{
			frames = null;
			if (payload == null || payload.Length <= MaxNativePayloadBytes
			    || payload.Length > PacketHandler.MaxPacketSize)
				return false;
			int frameCount = (payload.Length - 1) / MaxFrameDataBytes + 1;
			if (frameCount > MaxFrames)
				return false;
			int sequence = NextSequence();
			frames = new List<byte[]>(frameCount);
			for (int index = 0; index < frameCount; index++)
			{
				frames.Add(CreateFrame(payload, new FrameHeader
				{
					Sequence = sequence,
					Index = index,
					FrameCount = frameCount,
					TotalBytes = payload.Length,
					DataBytes = Math.Min(MaxFrameDataBytes, payload.Length - index * MaxFrameDataBytes),
				}));
			}
			return true;
		}

		internal static RiptideFrameResult Accept(
			byte[] raw, DispatchContext context, out byte[] complete)
		{
			complete = null;
			if (raw == null || raw.Length == 0 || raw.Length > MaxNativePayloadBytes)
				return RiptideFrameResult.Rejected;
			if (raw.Length < HeaderBytes || BitConverter.ToUInt64(raw, 0) != Magic)
			{
				complete = raw;
				return RiptideFrameResult.Complete;
			}
			CleanupExpired(System.DateTime.UtcNow);
			return AcceptFrame(raw, new SenderKey(context), out complete);
		}

		private static RiptideFrameResult AcceptFrame(
			byte[] raw, SenderKey sender, out byte[] complete)
		{
			complete = null;
			using var stream = new MemoryStream(raw, writable: false);
			using var reader = new BinaryReader(stream);
			reader.ReadUInt64();
			FrameHeader header = ReadHeader(reader);
			var key = new AssemblyKey(sender, header.Sequence);
			if (!ValidHeader(raw.Length, header))
				return Reject(key);
			if (Completed.Contains(key))
				return RiptideFrameResult.Incomplete;
			if (!TryGetPending(key, header, out PendingMessage pending))
				return Reject(key);
			byte[] data = reader.ReadBytes(header.DataBytes);
			if (data.Length != header.DataBytes || stream.Position != stream.Length)
				return Reject(key);
			byte[] existing = pending.Frames[header.Index];
			if (existing != null)
				return SameData(existing, data)
					? RiptideFrameResult.Incomplete
					: Reject(key);
			if (_pendingDataBytes > MaxPendingDataBytes - data.Length)
				return Reject(key);
			pending.Frames[header.Index] = data;
			pending.ReceivedFrames++;
			pending.ReceivedBytes += data.Length;
			_pendingDataBytes += data.Length;
			pending.LastUpdated = System.DateTime.UtcNow;
			if (pending.ReceivedFrames != pending.FrameCount)
				return RiptideFrameResult.Incomplete;
			if (pending.ReceivedBytes != pending.TotalBytes)
				return Reject(key);
			complete = Assemble(pending);
			RemovePending(key);
			RememberCompleted(key);
			return RiptideFrameResult.Complete;
		}

		private static bool TryGetPending(
			AssemblyKey key, FrameHeader header, out PendingMessage pending)
		{
			if (!Pending.TryGetValue(key, out pending))
			{
				if (Pending.Count >= MaxPendingAssemblies)
					return false;
				pending = new PendingMessage(header);
				Pending.Add(key, pending);
			}
			return pending.FrameCount == header.FrameCount
			       && pending.TotalBytes == header.TotalBytes;
		}

		private static FrameHeader ReadHeader(BinaryReader reader)
			=> new()
			{
				Sequence = reader.ReadInt32(),
				Index = reader.ReadInt32(),
				FrameCount = reader.ReadInt32(),
				TotalBytes = reader.ReadInt32(),
				DataBytes = reader.ReadInt32(),
			};

		private static bool ValidHeader(int rawBytes, FrameHeader header)
			=> header.Sequence > 0 && rawBytes == HeaderBytes + header.DataBytes
			   && ValidFrameBounds(header) && ValidFrameLayout(header);

		private static bool ValidFrameBounds(FrameHeader header)
			=> header.FrameCount > 0 && header.FrameCount <= MaxFrames
			   && header.Index >= 0 && header.Index < header.FrameCount
			   && header.TotalBytes > MaxNativePayloadBytes
			   && header.TotalBytes <= PacketHandler.MaxPacketSize
			   && header.DataBytes > 0 && header.DataBytes <= MaxFrameDataBytes;

		private static bool ValidFrameLayout(FrameHeader header)
		{
			int frameCount = (header.TotalBytes - 1) / MaxFrameDataBytes + 1;
			int dataBytes = Math.Min(
				MaxFrameDataBytes, header.TotalBytes - header.Index * MaxFrameDataBytes);
			return header.FrameCount == frameCount && header.DataBytes == dataBytes;
		}

		private static bool SameData(byte[] left, byte[] right)
		{
			if (left.Length != right.Length)
				return false;
			for (int index = 0; index < left.Length; index++)
			{
				if (left[index] != right[index])
					return false;
			}
			return true;
		}

		private static byte[] Assemble(PendingMessage pending)
		{
			var complete = new byte[pending.TotalBytes];
			int offset = 0;
			foreach (byte[] frame in pending.Frames)
			{
				Buffer.BlockCopy(frame, 0, complete, offset, frame.Length);
				offset += frame.Length;
			}
			return complete;
		}

		private static byte[] CreateFrame(byte[] payload, FrameHeader header)
		{
			int offset = header.Index * MaxFrameDataBytes;
			using var stream = new MemoryStream(HeaderBytes + header.DataBytes);
			using var writer = new BinaryWriter(stream);
			writer.Write(Magic);
			writer.Write(header.Sequence);
			writer.Write(header.Index);
			writer.Write(header.FrameCount);
			writer.Write(header.TotalBytes);
			writer.Write(header.DataBytes);
			writer.Write(payload, offset, header.DataBytes);
			return stream.ToArray();
		}

		private static int NextSequence()
		{
			int sequence = System.Threading.Interlocked.Increment(ref _nextSequence);
			if (sequence > 0)
				return sequence;
			System.Threading.Interlocked.Exchange(ref _nextSequence, 1);
			return 1;
		}

		private static void RememberCompleted(AssemblyKey key)
		{
			if (!Completed.Add(key))
				return;
			CompletedOrder.Enqueue(key);
			while (CompletedOrder.Count > MaxCompletedAssemblies)
				Completed.Remove(CompletedOrder.Dequeue());
		}

		private static RiptideFrameResult Reject(AssemblyKey key)
		{
			RemovePending(key);
			return RiptideFrameResult.Rejected;
		}

		private static void RemovePending(AssemblyKey key)
		{
			if (!Pending.TryGetValue(key, out PendingMessage pending))
				return;
			_pendingDataBytes -= pending.ReceivedBytes;
			Pending.Remove(key);
		}

		private static void CleanupExpired(System.DateTime now)
		{
			var expired = new List<AssemblyKey>();
			foreach (var entry in Pending)
			{
				if (now - entry.Value.LastUpdated > PendingTimeout)
					expired.Add(entry.Key);
			}
			foreach (AssemblyKey key in expired)
				Reject(key);
		}
	}
}

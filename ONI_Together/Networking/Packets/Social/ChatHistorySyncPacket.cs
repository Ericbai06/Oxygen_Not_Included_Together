using ONI_Together.UI;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.Social
{
	public sealed class ChatHistorySyncPacket : IPacket, IHostOnlyPacket
	{
		internal const int MaxMessageCount = 128;
		internal const int MaxMessageUtf8Bytes = 7 * 1024;
		internal const int MaxSerializedBytes = 10 * 1024;
		public ulong CutSequence;
		public List<ChatScreen.PendingMessage> Messages = new List<ChatScreen.PendingMessage>();

		public ChatHistorySyncPacket()
		{
		}

		public ChatHistorySyncPacket(
			List<ChatScreen.PendingMessage> messages, ulong cutSequence)
		{
			using var _ = Profiler.Scope();
			CutSequence = cutSequence;
			Messages = LatestSlice(messages, cutSequence);
		}

		public ChatHistorySyncPacket(List<ChatScreen.PendingMessage> messages)
			: this(messages, messages?.Count > 0 ? messages.Max(value => value.sequence) : 0)
		{
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			Validate(CutSequence, Messages);

			writer.Write(CutSequence);
			writer.Write(Messages.Count);
			foreach (var msg in Messages)
			{
				writer.Write(msg.sequence);
				writer.Write(msg.timestamp);
				ChatMessagePacket.WriteUtf8String(writer, msg.message);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			CutSequence = reader.ReadUInt64();
			int count = reader.ReadInt32();
			if (count < 0 || count > MaxMessageCount)
				throw new InvalidDataException($"Invalid chat history count: {count}");
			Messages = new List<ChatScreen.PendingMessage>(count);
			int serializedBytes = sizeof(ulong) + sizeof(int);
			for (int i = 0; i < count; i++)
			{
				ulong sequence = reader.ReadUInt64();
				long timestamp = reader.ReadInt64();
				string message = ChatMessagePacket.ReadUtf8String(reader, MaxMessageUtf8Bytes, "history message");
				Messages.Add(new ChatScreen.PendingMessage
				{
					sequence = sequence,
					timestamp = timestamp,
					message = message
				});
				serializedBytes = checked(serializedBytes + sizeof(ulong) + sizeof(long)
				                          + ChatMessagePacket.Utf8StringWireBytes(message));
				if (serializedBytes > MaxSerializedBytes)
					throw new InvalidDataException($"Chat history exceeds {MaxSerializedBytes} wire bytes");
			}
			Validate(CutSequence, Messages);
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost || !PacketHandler.CurrentContext.SenderIsHost)
				return;
			ChatScreen.ApplyHistory(CutSequence, Messages);
		}

		internal static void AppendBounded(List<ChatScreen.PendingMessage> messages, ChatScreen.PendingMessage message)
		{
			ValidateMessage(message);
			messages.Add(message);
			// ponytail: 128-entry ceiling keeps this scan trivial; track running bytes only if profiling says otherwise.
			while (messages.Count > MaxMessageCount || SerializedBytes(messages) > MaxSerializedBytes)
				messages.RemoveAt(0);
		}

		private static List<ChatScreen.PendingMessage> LatestSlice(
			List<ChatScreen.PendingMessage> messages, ulong cutSequence)
		{
			if (messages == null)
				throw new InvalidDataException("Chat history cannot be null");
			var latest = new List<ChatScreen.PendingMessage>();
			int serializedBytes = sizeof(ulong) + sizeof(int);
			for (int i = messages.Count - 1; i >= 0 && latest.Count < MaxMessageCount; i--)
			{
				ValidateMessage(messages[i], cutSequence);
				int entryBytes = sizeof(ulong) + sizeof(long)
				                 + ChatMessagePacket.Utf8StringWireBytes(messages[i].message);
				if (serializedBytes + entryBytes > MaxSerializedBytes) break;
				latest.Add(messages[i]);
				serializedBytes += entryBytes;
			}
			latest.Reverse();
			return latest;
		}

		private static void Validate(
			ulong cutSequence, List<ChatScreen.PendingMessage> messages)
		{
			if (cutSequence > long.MaxValue || messages == null
			    || messages.Count > MaxMessageCount || cutSequence == 0 && messages.Count != 0)
				throw new InvalidDataException("Invalid chat history message count");
			ulong previous = 0;
			foreach (var message in messages)
			{
				ValidateMessage(message, cutSequence);
				if (message.sequence <= previous)
					throw new InvalidDataException("Chat history sequence is not increasing");
				previous = message.sequence;
			}
			if (SerializedBytes(messages) > MaxSerializedBytes)
				throw new InvalidDataException($"Chat history exceeds {MaxSerializedBytes} wire bytes");
		}

		private static void ValidateMessage(
			ChatScreen.PendingMessage message, ulong cutSequence = long.MaxValue)
		{
			if (message.sequence == 0 || message.sequence > cutSequence
			    || message.sequence > long.MaxValue)
				throw new InvalidDataException("Chat history sequence is invalid");
			if (message.timestamp < 0 || message.timestamp > ChatMessagePacket.MaxUnixTimestampMilliseconds)
				throw new InvalidDataException("Chat history timestamp is outside the Unix millisecond range");
			if (string.IsNullOrEmpty(message.message))
				throw new InvalidDataException("Chat history message cannot be empty");
			try
			{
				if (ChatMessagePacket.Utf8Bytes(message.message) > MaxMessageUtf8Bytes)
					throw new InvalidDataException($"Chat history message exceeds {MaxMessageUtf8Bytes} UTF-8 bytes");
			}
			catch (System.Text.EncoderFallbackException ex)
			{
				throw new InvalidDataException("Chat history message is not valid UTF-16", ex);
			}
		}

		private static int SerializedBytes(List<ChatScreen.PendingMessage> messages)
		{
			int bytes = sizeof(ulong) + sizeof(int);
			foreach (var message in messages)
				bytes = checked(bytes + sizeof(ulong) + sizeof(long)
				                + ChatMessagePacket.Utf8StringWireBytes(message.message));
			return bytes;
		}
	}
}

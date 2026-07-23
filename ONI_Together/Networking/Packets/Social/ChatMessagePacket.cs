using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.UI;
using Steamworks;
using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Social
{
	public sealed class ChatMessagePacket : IPacket, IClientRelayable, ISenderBoundRelay,
		IHostAuthoritativeRelay
	{
		internal const int MaxSenderNameUtf8Bytes = 128;
		internal const int MaxMessageUtf8Bytes = 1024;
		internal const int MaxSerializedBytes = 1200;
		internal const long MaxUnixTimestampMilliseconds = 253402300799999;
		private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

		public ulong SenderId;
		ulong ISenderBoundRelay.RelaySenderId => SenderId;
		public ulong Sequence;
		public string Message;
		public Color PlayerColor;
		public long Timestamp;
		public string SenderName;

		public ChatMessagePacket()
		{
		}

		public ChatMessagePacket(string message)
		{
			using var _ = Profiler.Scope();

			SenderId = MultiplayerSession.LocalUserID;
            SenderName = Utils.GetLocalPlayerName();
            Message = message;
			PlayerColor = CursorManager.Instance.color;
			Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			Validate();

			writer.Write(SenderId);
			writer.Write(Sequence);
			WriteUtf8String(writer, SenderName);
			WriteUtf8String(writer, Message);
			writer.Write(PlayerColor.r);
			writer.Write(PlayerColor.g);
			writer.Write(PlayerColor.b);
			writer.Write(PlayerColor.a);
			writer.Write(Timestamp);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			SenderId = reader.ReadUInt64();
			Sequence = reader.ReadUInt64();
			SenderName = ReadUtf8String(reader, MaxSenderNameUtf8Bytes, "sender name");
			Message = ReadUtf8String(reader, MaxMessageUtf8Bytes, "message");
			float r = reader.ReadSingle();
			float g = reader.ReadSingle();
			float b = reader.ReadSingle();
			float a = reader.ReadSingle();
			PlayerColor = new Color(r, g, b, a);
			Timestamp = reader.ReadInt64();
			Validate();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			Validate();
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost)
			{
				if (!context.IsVerifiedHostBroadcast || context.SenderIsHost || Sequence != 0)
					throw new InvalidDataException("Client chat lacks verified zero-sequence authority");
				Sequence = ChatScreen.NextHostSequence();
				if (!Display())
					return;
#if DEBUG
				LogHostEvidence(HostRelayEntryId);
#endif
				PacketSender.SendToAllClients(this, PacketSendMode.ReliableImmediate);
				return;
			}
			if (!context.SenderIsHost || Sequence == 0)
				throw new InvalidDataException("Client received non-authoritative chat");
			if (!Display())
				return;
#if DEBUG
			LogClientEvidence();
#endif
		}

		internal void PublishHostLocal()
		{
			if (!MultiplayerSession.IsHost || Sequence != 0)
				throw new InvalidOperationException("Only the host can publish local chat");
			Sequence = ChatScreen.NextHostSequence();
			if (!Display())
				return;
#if DEBUG
			LogHostEvidence(HostLocalEntryId);
#endif
			PacketSender.SendToAllClients(this, PacketSendMode.ReliableImmediate);
		}

		private bool Display()
		{

            string senderName = SenderName;
            if (NetworkConfig.IsSteamConfig() && SteamFriends.HasFriend(SenderId.AsCSteamID(), EFriendFlags.k_EFriendFlagImmediate))
			{
				// Update the sender name to what we have them named as on our friends list
                senderName = SteamFriends.GetFriendPersonaName(SenderId.AsCSteamID());
            }
			ChatScreen.PendingMessage message = new ChatScreen.PendingMessage()
			{
				sequence = Sequence,
				timestamp = Timestamp,
				message = FormatDisplayMessage(senderName, PlayerColor, Message)
			};
			return ChatScreen.ApplyAuthoritativeLive(message);
		}

#if DEBUG
		private const string HostRelayEntryId = "sync:89f8e30a7cdde2074882edc4";
		private const string HostLocalEntryId = "sync:152411b58dacc6e2e9b6e504";

		private void LogHostEvidence(string entryId)
		{
			long revision = (long)Sequence;
			LogEvidence("host-submit", revision, Sequence, entryId);
			LogEvidence("final-state", revision, Sequence, entryId);
		}

		private void LogClientEvidence()
		{
			long revision = (long)Sequence;
			LogEvidence("client-apply", revision, Sequence, HostRelayEntryId);
			LogEvidence("revision-accepted", revision, Sequence, HostRelayEntryId);
			ChatScreen.PendingMessage duplicate = EvidenceMessage(Sequence);
			ChatScreen.ApplyAuthoritativeLive(duplicate);
			LogEvidence("revision-duplicate", revision, Sequence, HostRelayEntryId);
			ulong olderSequence = Sequence - 1;
			ChatScreen.ApplyAuthoritativeLive(EvidenceMessage(olderSequence));
			LogEvidence("revision-out-of-order", (long)olderSequence,
				olderSequence, HostRelayEntryId);
			if (SenderId == MultiplayerSession.LocalUserID)
				LogEvidence("client-original-blocked", revision, Sequence, HostRelayEntryId);
			LogEvidence("final-state", revision, Sequence, HostRelayEntryId);
		}

		private ChatScreen.PendingMessage EvidenceMessage(ulong sequence)
			=> new ChatScreen.PendingMessage
			{
				sequence = sequence,
				timestamp = Timestamp,
				message = string.Empty,
			};

		private void LogEvidence(string phase, long revision, ulong sequence, string entryId)
		{
			var target = new ChatTarget
			{
				Sender = SenderId.ToString(CultureInfo.InvariantCulture),
			};
			var state = new ChatState
			{
				Sequence = (long)sequence,
				Timestamp = Timestamp,
				MessageHash = HashMessage(Message),
			};
			IntegrationScenarioEvidenceCore.Log(TypedEvidenceRuntimeContext.Create(
				"chat", phase, revision, target, state, entryId));
		}

		private static string HashMessage(string message)
		{
			using SHA256 hash = SHA256.Create();
			byte[] digest = hash.ComputeHash(StrictUtf8.GetBytes(message));
			var result = new StringBuilder("sha256:", 71);
			foreach (byte value in digest)
				result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
			return result.ToString();
		}
#endif

		internal static string FormatDisplayMessage(string senderName, Color color, string message)
		{
			string colorHex = ColorUtility.ToHtmlStringRGB(color);
			return $"<color=#{colorHex}>{SecurityElement.Escape(senderName)}:</color> {SecurityElement.Escape(message)}";
		}

		internal static int Utf8StringWireBytes(string value)
		{
			int byteCount = Utf8Bytes(value);
			return SevenBitEncodedIntBytes(byteCount) + byteCount;
		}

		internal static int Utf8Bytes(string value) => StrictUtf8.GetByteCount(value);

		internal static void WriteUtf8String(BinaryWriter writer, string value)
		{
			byte[] bytes = StrictUtf8.GetBytes(value);
			WriteSevenBitEncodedInt(writer, bytes.Length);
			writer.Write(bytes);
		}

		internal static string ReadUtf8String(BinaryReader reader, int maxBytes, string fieldName)
		{
			int byteCount = ReadSevenBitEncodedInt(reader);
			if (byteCount < 0 || byteCount > maxBytes)
				throw new InvalidDataException($"Chat {fieldName} exceeds {maxBytes} UTF-8 bytes");
			byte[] bytes = reader.ReadBytes(byteCount);
			if (bytes.Length != byteCount)
				throw new EndOfStreamException($"Chat {fieldName} is truncated");
			try { return StrictUtf8.GetString(bytes); }
			catch (DecoderFallbackException ex) { throw new InvalidDataException($"Chat {fieldName} is not valid UTF-8", ex); }
		}

		private void Validate()
		{
			if (SenderId == 0)
				throw new InvalidDataException("Chat sender id cannot be zero");
			ValidateString(SenderName, MaxSenderNameUtf8Bytes, "sender name");
			ValidateString(Message, MaxMessageUtf8Bytes, "message");
			if (!ValidColor(PlayerColor.r) || !ValidColor(PlayerColor.g)
			    || !ValidColor(PlayerColor.b) || !ValidColor(PlayerColor.a))
				throw new InvalidDataException("Chat color must be finite and between zero and one");
			if (Timestamp < 0 || Timestamp > MaxUnixTimestampMilliseconds)
				throw new InvalidDataException("Chat timestamp is outside the Unix millisecond range");
			if (Sequence > long.MaxValue)
				throw new InvalidDataException("Chat sequence exceeds the supported range");
			int bytes = sizeof(ulong) * 2 + Utf8StringWireBytes(SenderName) + Utf8StringWireBytes(Message)
			            + sizeof(float) * 4 + sizeof(long);
			if (bytes > MaxSerializedBytes)
				throw new InvalidDataException($"Chat message exceeds {MaxSerializedBytes} wire bytes");
		}

		private static void ValidateString(string value, int maxBytes, string fieldName)
		{
			if (string.IsNullOrWhiteSpace(value))
				throw new InvalidDataException($"Chat {fieldName} cannot be empty");
			try
			{
				if (StrictUtf8.GetByteCount(value) > maxBytes)
					throw new InvalidDataException($"Chat {fieldName} exceeds {maxBytes} UTF-8 bytes");
			}
			catch (EncoderFallbackException ex)
			{
				throw new InvalidDataException($"Chat {fieldName} is not valid UTF-16", ex);
			}
		}

		private static bool ValidColor(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value <= 1f;

		private static int SevenBitEncodedIntBytes(int value)
		{
			int bytes = 1;
			while ((value >>= 7) != 0) bytes++;
			return bytes;
		}

		private static void WriteSevenBitEncodedInt(BinaryWriter writer, int value)
		{
			uint remaining = (uint)value;
			while (remaining >= 0x80)
			{
				writer.Write((byte)(remaining | 0x80));
				remaining >>= 7;
			}
			writer.Write((byte)remaining);
		}

		private static int ReadSevenBitEncodedInt(BinaryReader reader)
		{
			int value = 0;
			for (int shift = 0; shift < 35; shift += 7)
			{
				byte current = reader.ReadByte();
				if (shift == 28 && current > 0x0F)
					throw new InvalidDataException("Invalid chat string length prefix");
				value |= (current & 0x7F) << shift;
				if ((current & 0x80) == 0) return value;
			}
			throw new InvalidDataException("Invalid chat string length prefix");
		}
	}
}

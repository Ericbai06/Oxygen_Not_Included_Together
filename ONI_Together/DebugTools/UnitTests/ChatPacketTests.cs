using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.UI;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ChatPacketTests
	{
		[UnitTest(name: "Chat messages reject invalid wire fields", category: "Networking", liveSafe: true)]
		public static UnitTestResult RejectsInvalidWireFields()
		{
			if (!Rejects(new ChatMessagePacket(), writer =>
				    WriteChat(writer, 0, 0, "player", "hello", 1, 1, 1, 1, 1)))
				return UnitTestResult.Fail("ChatMessagePacket accepted sender id zero");
			if (!Rejects(new ChatMessagePacket(), writer =>
				    WriteChat(writer, 1, 0, "player", "hello", float.NaN, 1, 1, 1, 1)))
				return UnitTestResult.Fail("ChatMessagePacket accepted a non-finite color");
			if (!Rejects(new ChatMessagePacket(), writer =>
				    WriteChat(writer, 1, 0, "player", "hello", 1.1f, 1, 1, 1, 1)))
				return UnitTestResult.Fail("ChatMessagePacket accepted an out-of-range color");
			if (!Rejects(new ChatMessagePacket(), writer =>
				    WriteChat(writer, 1, 0, "player", "hello", 1, 1, 1, 1, -1)))
				return UnitTestResult.Fail("ChatMessagePacket accepted an invalid timestamp");
			if (!Rejects(new ChatMessagePacket(), writer =>
				    WriteChat(writer, 1, 0, "player", "hello", 1, 1, 1, 1, 253402300800000)))
				return UnitTestResult.Fail("ChatMessagePacket accepted a timestamp beyond DateTimeOffset.MaxValue");
			return UnitTestResult.Pass("Invalid chat wire fields are rejected");
		}

		[UnitTest(name: "Chat sequence wire order and bounds roundtrip", category: "Networking", liveSafe: true)]
		public static UnitTestResult SequenceWireOrderAndBounds()
		{
			ChatMessagePacket zero = ValidChat(0);
			byte[] bytes = Serialize(zero);
			using (var stream = new MemoryStream(bytes))
			using (var reader = new BinaryReader(stream))
				if (reader.ReadUInt64() != zero.SenderId || reader.ReadUInt64() != 0)
					return UnitTestResult.Fail("Sequence is not the second chat wire field");
			if (RoundTrip(zero).Sequence != 0)
				return UnitTestResult.Fail("Zero client-request sequence did not roundtrip");
			ChatMessagePacket upper = ValidChat((ulong)long.MaxValue);
			if (RoundTrip(upper).Sequence != (ulong)long.MaxValue)
				return UnitTestResult.Fail("Maximum supported sequence did not roundtrip");
			upper.Sequence++;
			if (!SerializeRejects(upper) || !Rejects(new ChatMessagePacket(), writer =>
				    WriteChat(writer, 7, (ulong)long.MaxValue + 1, "player", "hello", 1, 1, 1, 1, 1)))
				return UnitTestResult.Fail("Chat accepted a sequence above Int64.MaxValue");
			return UnitTestResult.Pass("Sequence is ordered, zero-capable, and bounded on both wire paths");
		}

		[UnitTest(name: "Chat strings use symmetric UTF-8 wire limits", category: "Networking", liveSafe: true)]
		public static UnitTestResult EnforcesUtf8WireLimits()
		{
			ChatMessagePacket valid = ValidChat(0);
			valid.SenderName = new string('名', 42);
			valid.Message = string.Concat(System.Linq.Enumerable.Repeat("🙂", 256));
			byte[] bytes = Serialize(valid);
			if (bytes.Length > ChatMessagePacket.MaxSerializedBytes)
				return UnitTestResult.Fail($"Valid chat message used {bytes.Length} wire bytes");
			ChatMessagePacket copy = RoundTrip(valid);
			if (copy.SenderName != valid.SenderName || copy.Message != valid.Message)
				return UnitTestResult.Fail("Valid multi-byte chat fields changed during roundtrip");
			valid.SenderName += "名";
			if (!SerializeRejects(valid) || !Rejects(new ChatMessagePacket(), writer =>
				    WriteChat(writer, 7, 0, valid.SenderName, "hello", 1, 1, 1, 1, 1)))
				return UnitTestResult.Fail("Sender name UTF-8 limit is asymmetric");
			valid.SenderName = "player";
			valid.Message += "🙂";
			if (!SerializeRejects(valid) || !Rejects(new ChatMessagePacket(), writer =>
				    WriteChat(writer, 7, 0, "player", valid.Message, 1, 1, 1, 1, 1)))
				return UnitTestResult.Fail("Message UTF-8 limit is asymmetric");
			return UnitTestResult.Pass("Chat strings are bounded symmetrically by UTF-8 wire bytes");
		}

		[UnitTest(name: "Chat history carries cut and strictly increasing sequences", category: "Networking", liveSafe: true)]
		public static UnitTestResult HistorySequenceRoundtripAndBounds()
		{
			var packet = new ChatHistorySyncPacket(new List<ChatScreen.PendingMessage>
			{
				Message(4, 50, "four"), Message(7, 50, "seven")
			}, 7);
			ChatHistorySyncPacket copy = RoundTrip(packet);
			if (copy.CutSequence != 7 || copy.Messages.Count != 2
			    || copy.Messages[0].sequence != 4 || copy.Messages[1].sequence != 7)
				return UnitTestResult.Fail("History cut or entry sequences changed during roundtrip");
			if (!SerializeRejects(new ChatHistorySyncPacket { CutSequence = (ulong)long.MaxValue + 1 }))
				return UnitTestResult.Fail("History accepted a cut above Int64.MaxValue");
			if (!SerializeRejects(History(2, Message(0, 1, "zero")))
			    || !SerializeRejects(History(1, Message(2, 1, "above-cut")))
			    || !SerializeRejects(History(2, Message(2, 1, "two"), Message(1, 2, "one")))
			    || !SerializeRejects(History(1, Message(1, 1, "one"), Message(1, 2, "duplicate"))))
				return UnitTestResult.Fail("History accepted zero, above-cut, unordered, or duplicate sequence");
			if (!Rejects(new ChatHistorySyncPacket(), writer =>
				    WriteHistory(writer, 2, Message(2, 1, "two"), Message(1, 2, "one"))))
				return UnitTestResult.Fail("History deserializer accepted decreasing sequences");
			return UnitTestResult.Pass("History cut and entries roundtrip with strict sequence bounds");
		}

		[UnitTest(name: "Chat history keeps a bounded latest slice", category: "Networking", liveSafe: true)]
		public static UnitTestResult KeepsBoundedLatestHistory()
		{
			var source = new List<ChatScreen.PendingMessage>();
			for (int i = 0; i < ChatHistorySyncPacket.MaxMessageCount + 40; i++)
				source.Add(Message((ulong)i + 1, i + 1, $"message-{i:D3}-" + new string('x', 80)));
			var packet = new ChatHistorySyncPacket(source, (ulong)source.Count);
			byte[] bytes = Serialize(packet);
			if (packet.Messages.Count > ChatHistorySyncPacket.MaxMessageCount
			    || bytes.Length > ChatHistorySyncPacket.MaxSerializedBytes)
				return UnitTestResult.Fail($"History exceeded bounds: count={packet.Messages.Count}, bytes={bytes.Length}");
			ulong expectedFirst = source[source.Count - packet.Messages.Count].sequence;
			if (packet.Messages.Count == 0 || packet.Messages[0].sequence != expectedFirst
			    || packet.Messages[packet.Messages.Count - 1].sequence != (ulong)source.Count)
				return UnitTestResult.Fail("History is not a contiguous latest slice");
			source.Clear();
			if (packet.Messages.Count == 0)
				return UnitTestResult.Fail("History retained the caller's mutable list");
			ChatHistorySyncPacket copy = RoundTrip(packet);
			if (copy.CutSequence != packet.CutSequence || copy.Messages[0].sequence != expectedFirst)
				return UnitTestResult.Fail("Bounded history changed during roundtrip");
			return UnitTestResult.Pass("History is a copied, bounded latest slice");
		}

		[UnitTest(name: "Chat history rejects invalid wire state", category: "Networking", liveSafe: true)]
		public static UnitTestResult RejectsInvalidHistoryState()
		{
			var oversized = new ChatHistorySyncPacket { CutSequence = 1000 };
			for (int i = 0; i <= ChatHistorySyncPacket.MaxMessageCount; i++)
				oversized.Messages.Add(Message((ulong)i + 1, i + 1, "x"));
			if (!SerializeRejects(oversized))
				return UnitTestResult.Fail("Serialize accepted an oversized history count");
			if (!SerializeRejects(History(1, Message(1, -1, "invalid"))))
				return UnitTestResult.Fail("Serialize accepted an invalid history timestamp");
			if (!Rejects(new ChatHistorySyncPacket(), writer =>
				    WriteHistory(writer, 1, Message(1, -1, "invalid"))))
				return UnitTestResult.Fail("Deserialize accepted an invalid history timestamp");
			string large = new string('x', ChatHistorySyncPacket.MaxMessageUtf8Bytes);
			if (!Rejects(new ChatHistorySyncPacket(), writer =>
				    WriteHistory(writer, 2, Message(1, 1, large), Message(2, 2, large))))
				return UnitTestResult.Fail("Deserialize accepted history beyond the total wire-byte limit");
			return UnitTestResult.Pass("History validates count, timestamp, and total bytes on both paths");
		}

		[UnitTest(name: "Chat display escapes user rich text", category: "UI", liveSafe: true)]
		public static UnitTestResult EscapesUserRichText()
		{
			string rendered = ChatMessagePacket.FormatDisplayMessage(
				"<size=99>name</size>", new Color(1, 1, 1, 1), "<color=red>owned</color>");
			if (rendered.Contains("<size=99>") || rendered.Contains("<color=red>"))
				return UnitTestResult.Fail("User-controlled rich-text tags reached the renderer");
			if (!rendered.StartsWith("<color=#FFFFFF>")
			    || !rendered.Contains("&lt;color=red&gt;owned&lt;/color&gt;"))
				return UnitTestResult.Fail("System color markup or escaped user text was not preserved");
			return UnitTestResult.Pass("Only system-generated chat markup remains active");
		}

		private static ChatMessagePacket ValidChat(ulong sequence)
			=> new()
			{
				SenderId = 7, Sequence = sequence, SenderName = "player", Message = "hello",
				PlayerColor = new Color(0, 0.5f, 1, 1), Timestamp = 1
			};

		private static ChatScreen.PendingMessage Message(ulong sequence, long timestamp, string text)
			=> new() { sequence = sequence, timestamp = timestamp, message = text };

		private static ChatHistorySyncPacket History(
			ulong cut, params ChatScreen.PendingMessage[] messages)
			=> new() { CutSequence = cut, Messages = new List<ChatScreen.PendingMessage>(messages) };

		private static void WriteChat(BinaryWriter writer, ulong senderId, ulong sequence,
			string senderName, string message, float r, float g, float b, float a, long timestamp)
		{
			writer.Write(senderId);
			writer.Write(sequence);
			ChatMessagePacket.WriteUtf8String(writer, senderName);
			ChatMessagePacket.WriteUtf8String(writer, message);
			writer.Write(r);
			writer.Write(g);
			writer.Write(b);
			writer.Write(a);
			writer.Write(timestamp);
		}

		private static void WriteHistory(
			BinaryWriter writer, ulong cut, params ChatScreen.PendingMessage[] messages)
		{
			writer.Write(cut);
			writer.Write(messages.Length);
			foreach (ChatScreen.PendingMessage message in messages)
			{
				writer.Write(message.sequence);
				writer.Write(message.timestamp);
				ChatMessagePacket.WriteUtf8String(writer, message.message);
			}
		}

		private static bool Rejects(IPacket packet, Action<BinaryWriter> write)
		{
			try
			{
				using var stream = new MemoryStream();
				using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) write(writer);
				stream.Position = 0;
				using var reader = new BinaryReader(stream);
				packet.Deserialize(reader);
				return false;
			}
			catch (InvalidDataException) { return true; }
		}

		private static bool SerializeRejects(IPacket packet)
		{
			try { Serialize(packet); return false; }
			catch (InvalidDataException) { return true; }
		}

		private static byte[] Serialize(IPacket packet)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) packet.Serialize(writer);
			return stream.ToArray();
		}

		private static T RoundTrip<T>(T packet) where T : IPacket, new()
		{
			byte[] bytes = Serialize(packet);
			using var stream = new MemoryStream(bytes);
			using var reader = new BinaryReader(stream, Encoding.UTF8, true);
			var copy = new T();
			copy.Deserialize(reader);
			return copy;
		}
	}
}

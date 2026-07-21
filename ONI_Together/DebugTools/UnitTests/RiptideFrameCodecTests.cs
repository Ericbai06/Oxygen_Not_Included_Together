#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Transport.Lan;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class RiptideFrameCodecTests
	{
		private const int FrameIndexOffset = sizeof(ulong) + sizeof(int);
		private const int TotalBytesOffset = sizeof(ulong) + sizeof(int) * 3;

		[UnitTest(name: "Riptide adapter framing reassembles before shared dispatch", category: "Networking")]
		public static UnitTestResult AdapterFramesRoundTrip()
		{
			RiptideFrameCodec.ResetSessionState();
			byte[] payload = Payload(DeferredReliableBatchPacket.MaxWireBytes);
			if (!RiptideFrameCodec.TryCreateFrames(payload, out List<byte[]> frames)
			    || frames.Count < 2 || frames.Any(frame => frame.Length > 1000))
				return UnitTestResult.Fail("64 KiB business payload was not privately framed below Riptide MTU");
			int nativeCalls = 0;
			if (!RiptidePacketSender.SendAdapterFrames(
				    frames, _ => { nativeCalls++; return true; })
			    || nativeCalls != frames.Count)
				return UnitTestResult.Fail("Adapter native send count did not equal its private frame count");
			var context = Context(7);
			byte[] complete = null;
			for (int index = 0; index < frames.Count; index++)
			{
				RiptideFrameResult result = RiptideFrameCodec.Accept(
					frames[index], context, out byte[] candidate);
				if (index + 1 < frames.Count && result != RiptideFrameResult.Incomplete)
					return UnitTestResult.Fail("Adapter exposed a partial business packet");
				if (result == RiptideFrameResult.Complete)
					complete = candidate;
			}
			return complete != null && complete.SequenceEqual(payload)
				? UnitTestResult.Pass("Only the complete byte-identical business packet reached dispatch")
				: UnitTestResult.Fail("Adapter framing changed or failed to complete the business payload");
		}

		[UnitTest(name: "Riptide frames reassemble when A1 arrives before A0", category: "Networking")]
		public static UnitTestResult OutOfOrderFramesReassemble()
		{
			RiptideFrameCodec.ResetSessionState();
			byte[] payload = Payload(2500);
			if (!RiptideFrameCodec.TryCreateFrames(payload, out List<byte[]> frames)
			    || frames.Count != 3)
				return UnitTestResult.Fail("Could not arrange a three-frame payload");
			var context = Context(8);
			if (!IsIncomplete(frames[1], context) || !IsIncomplete(frames[0], context)
			    || !Completes(frames[2], context, payload))
				return UnitTestResult.Fail("A1,A0,A2 did not reassemble A exactly once");
			return UnitTestResult.Pass("A1,A0,A2 reassembles A");
		}

		[UnitTest(name: "Riptide frames reassemble interleaved messages", category: "Networking")]
		public static UnitTestResult InterleavedMessagesReassemble()
		{
			RiptideFrameCodec.ResetSessionState();
			byte[] payloadA = Payload(1500, 3);
			byte[] payloadB = Payload(1700, 11);
			if (!TryCreateTwoFrames(payloadA, out List<byte[]> framesA)
			    || !TryCreateTwoFrames(payloadB, out List<byte[]> framesB))
				return UnitTestResult.Fail("Could not arrange two interleaved messages");
			var context = Context(9);
			if (!IsIncomplete(framesA[0], context) || !IsIncomplete(framesB[0], context)
			    || !Completes(framesA[1], context, payloadA)
			    || !Completes(framesB[1], context, payloadB))
				return UnitTestResult.Fail("A0,B0,A1,B1 did not independently reassemble A and B");
			return UnitTestResult.Pass("Interleaved messages reassemble independently");
		}

		[UnitTest(name: "Riptide late message survives a newer completion", category: "Networking")]
		public static UnitTestResult EarlierMessageCompletesAfterLaterMessage()
		{
			RiptideFrameCodec.ResetSessionState();
			byte[] payloadA = Payload(1500, 5);
			byte[] payloadB = Payload(1700, 13);
			if (!TryCreateTwoFrames(payloadA, out List<byte[]> framesA)
			    || !TryCreateTwoFrames(payloadB, out List<byte[]> framesB))
				return UnitTestResult.Fail("Could not arrange two overlapping messages");
			var context = Context(10);
			if (!IsIncomplete(framesA[0], context) || !IsIncomplete(framesB[0], context)
			    || !Completes(framesB[1], context, payloadB)
			    || !Completes(framesA[1], context, payloadA))
				return UnitTestResult.Fail("Completing B discarded the earlier pending A");
			return UnitTestResult.Pass("B completion does not discard late A");
		}

		[UnitTest(name: "Riptide duplicate frames are idempotent", category: "Networking")]
		public static UnitTestResult DuplicateFramesAreIdempotent()
		{
			RiptideFrameCodec.ResetSessionState();
			byte[] payload = Payload(2500, 17);
			if (!RiptideFrameCodec.TryCreateFrames(payload, out List<byte[]> frames)
			    || frames.Count != 3)
				return UnitTestResult.Fail("Could not arrange duplicate-frame coverage");
			var context = Context(11);
			if (!IsIncomplete(frames[1], context) || !IsIncomplete(frames[1], context)
			    || !IsIncomplete(frames[0], context) || !Completes(frames[2], context, payload))
				return UnitTestResult.Fail("A duplicate frame changed or poisoned reassembly");
			if (RiptideFrameCodec.Accept(frames[2], context, out _) == RiptideFrameResult.Complete)
				return UnitTestResult.Fail("A completed frame dispatched the business payload twice");
			return UnitTestResult.Pass("Duplicate frames neither corrupt nor redispatch payloads");
		}

		[UnitTest(name: "Riptide adapter rejects malformed and oversized frames", category: "Networking")]
		public static UnitTestResult InvalidFramesAreRejected()
		{
			RiptideFrameCodec.ResetSessionState();
			if (!RiptideFrameCodec.TryCreateFrames(Payload(2500), out List<byte[]> frames)
			    || frames.Count != 3)
				return UnitTestResult.Fail("Could not arrange invalid-frame coverage");
			byte[] invalidShape = (byte[])frames[0].Clone();
			WriteInt32(invalidShape, FrameIndexOffset, 3);
			byte[] declaredOversize = (byte[])frames[0].Clone();
			WriteInt32(declaredOversize, TotalBytesOffset, PacketHandler.MaxPacketSize + 1);
			byte[] nativeOversize = new byte[RiptideFrameCodec.MaxNativePayloadBytes + 1];
			Array.Copy(frames[0], nativeOversize, frames[0].Length);
			var context = Context(12);
			if (!IsRejected(invalidShape, context) || !IsRejected(declaredOversize, context)
			    || !IsRejected(nativeOversize, context))
				return UnitTestResult.Fail("Malformed or oversized frame entered reassembly");
			return UnitTestResult.Pass("Malformed and oversized frames remain rejected");
		}

		[UnitTest(name: "Riptide frame sequence can be reused after reset", category: "Networking")]
		public static UnitTestResult ResetAllowsSequenceReuse()
		{
			RiptideFrameCodec.ResetSessionState();
			byte[] payload = Payload(1500, 19);
			if (!TryCreateTwoFrames(payload, out List<byte[]> frames))
				return UnitTestResult.Fail("Could not arrange sequence reuse coverage");
			var context = Context(13);
			if (!IsIncomplete(frames[0], context) || !Completes(frames[1], context, payload))
				return UnitTestResult.Fail("Initial sequence did not complete");
			RiptideFrameCodec.ResetSessionState();
			if (!IsIncomplete(frames[0], context) || !Completes(frames[1], context, payload))
				return UnitTestResult.Fail("Reset retained the completed sequence tombstone");
			return UnitTestResult.Pass("Reset permits the same frame sequence in a new session");
		}

		private static DispatchContext Context(ulong senderId)
			=> new(senderId, true, 3, 11);

		private static bool TryCreateTwoFrames(byte[] payload, out List<byte[]> frames)
			=> RiptideFrameCodec.TryCreateFrames(payload, out frames) && frames.Count == 2;

		private static bool IsIncomplete(byte[] frame, DispatchContext context)
			=> RiptideFrameCodec.Accept(frame, context, out byte[] complete)
			   == RiptideFrameResult.Incomplete && complete == null;

		private static bool Completes(byte[] frame, DispatchContext context, byte[] expected)
			=> RiptideFrameCodec.Accept(frame, context, out byte[] complete)
			   == RiptideFrameResult.Complete && complete != null && complete.SequenceEqual(expected);

		private static bool IsRejected(byte[] frame, DispatchContext context)
			=> RiptideFrameCodec.Accept(frame, context, out byte[] complete)
			   == RiptideFrameResult.Rejected && complete == null;

		private static void WriteInt32(byte[] bytes, int offset, int value)
			=> Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, sizeof(int));

		private static byte[] Payload(int length, int salt = 7)
		{
			var payload = new byte[length];
			for (int index = 0; index < payload.Length; index++)
				payload[index] = (byte)(index * 31 + salt);
			return payload;
		}
	}
}
#endif

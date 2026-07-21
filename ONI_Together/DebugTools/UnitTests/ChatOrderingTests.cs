using System;
using System.Collections.Generic;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.UI;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ChatOrderingTests
	{
		[UnitTest(name: "Chat relay declares host authority and sender binding", category: "Networking", liveSafe: true)]
		public static UnitTestResult AuthorityContract()
		{
			var packet = new ChatMessagePacket { SenderId = 17 };
			bool valid = packet is IClientRelayable
			             && packet is ISenderBoundRelay
			             && packet is IHostAuthoritativeRelay
			             && ((ISenderBoundRelay)packet).RelaySenderId == 17;
			return valid
				? UnitTestResult.Pass("Chat relay binds its sender and requires host authority")
				: UnitTestResult.Fail("Chat relay is missing sender binding or host-authority marker");
		}

		[UnitTest(name: "Host chat sequence is globally monotonic", category: "Networking")]
		public static UnitTestResult HostSequenceIsGlobalAndMonotonic()
		{
			bool originalHost = MultiplayerSession.IsHost;
			try
			{
				ChatScreen.ResetSessionState();
				MultiplayerSession.IsHost = true;
				ulong first = ChatScreen.NextHostSequence();
				ulong second = ChatScreen.NextHostSequence();
				ulong third = ChatScreen.NextHostSequence();
				return first == 1 && second == 2 && third == 3
				       && ChatScreen.NextChatSequenceForTests == 3
					? UnitTestResult.Pass("One host-global counter assigns strictly increasing sequences")
					: UnitTestResult.Fail($"Host sequence was {first}, {second}, {third}");
			}
			finally
			{
				ChatScreen.ResetSessionState();
				MultiplayerSession.IsHost = originalHost;
			}
		}

		[UnitTest(name: "Client chat has no optimistic append path", category: "Networking", liveSafe: true)]
		public static UnitTestResult ClientDoesNotAppendOptimistically()
		{
			MethodInfo submit = typeof(ChatScreen).GetMethod(
				"OnInputSubmitted", BindingFlags.Instance | BindingFlags.NonPublic);
			MethodInfo publish = typeof(ChatMessagePacket).GetMethod(
				"PublishHostLocal", BindingFlags.Instance | BindingFlags.NonPublic);
			MethodInfo send = typeof(PacketSender).GetMethod(
				nameof(PacketSender.SendToAllOtherPeers), BindingFlags.Static | BindingFlags.Public);
			MethodInfo append = typeof(ChatScreen).GetMethod(
				nameof(ChatScreen.QueueMessage), BindingFlags.Static | BindingFlags.Public);
			if (!Calls(submit, publish) || !Calls(submit, send) || Calls(submit, append))
				return UnitTestResult.Fail("Input submission bypasses authority or appends optimistic client state");
			return UnitTestResult.Pass("Host publishes locally; client waits for the host relay before append");
		}

		[UnitTest(name: "Chat history and live arrival orders converge", category: "Networking")]
		public static UnitTestResult HistoryAndLiveOrdersConverge()
		{
			ChatScreen.PendingMessage[] historyFirst = MergeHistoryAndLive(historyFirst: true);
			ChatScreen.PendingMessage[] liveFirst = MergeHistoryAndLive(historyFirst: false);
			bool same = SameMessages(historyFirst, liveFirst);
			ChatScreen.ResetSessionState();
			return same
				? UnitTestResult.Pass("History-before-live and live-before-history converge exactly")
				: UnitTestResult.Fail("History/live arrival order changed final chat state");
		}

		[UnitTest(name: "Older chat cut cannot erase newer live state", category: "Networking")]
		public static UnitTestResult OlderCutPreservesNewLiveState()
		{
			ChatScreen.ResetSessionState();
			ChatScreen.ApplyHistory(2, new[] { Message(1, 10, "one"), Message(2, 20, "two") });
			ChatScreen.ApplyAuthoritativeLive(Message(3, 30, "three"));
			bool acceptedOld = ChatScreen.ApplyHistory(1, new[] { Message(1, 10, "one") });
			IReadOnlyList<ChatScreen.PendingMessage> result = ChatScreen.HistorySnapshotForTests;
			bool valid = !acceptedOld && ChatScreen.LastHistoryCutForTests == 2
			             && result.Count == 3 && result[2].sequence == 3;
			ChatScreen.ResetSessionState();
			return valid
				? UnitTestResult.Pass("Stale cuts leave newer authoritative live messages intact")
				: UnitTestResult.Fail("An older history cut replaced newer live state");
		}

		[UnitTest(name: "Chat sequence, not timestamp, defines identity", category: "Networking")]
		public static UnitTestResult SequenceDefinesIdentity()
		{
			ChatScreen.ResetSessionState();
			bool first = ChatScreen.ApplyAuthoritativeLive(Message(1, 99, "first"));
			bool second = ChatScreen.ApplyAuthoritativeLive(Message(2, 99, "second"));
			bool duplicate = ChatScreen.ApplyAuthoritativeLive(Message(2, 100, "duplicate"));
			IReadOnlyList<ChatScreen.PendingMessage> result = ChatScreen.HistorySnapshotForTests;
			bool valid = first && second && !duplicate && result.Count == 2
			             && result[0].sequence == 1 && result[1].sequence == 2;
			ChatScreen.ResetSessionState();
			return valid
				? UnitTestResult.Pass("Equal timestamps survive while duplicate sequences apply once")
				: UnitTestResult.Fail("Chat deduplicated timestamps or admitted a duplicate sequence");
		}

		[UnitTest(name: "Session reset clears every chat session field", category: "Networking")]
		public static UnitTestResult SessionResetClearsChatState()
		{
			bool originalHost = MultiplayerSession.IsHost;
			ulong originalHostId = MultiplayerSession.HostUserID;
			GameObject testObject = null;
			try
			{
				ArrangeDirtyChatState(ref testObject);
				SessionStateReset.Reset();
				bool cleared = ChatScreen.Instance == null
				               && ChatScreen.HistoryCountForTests == 0
				               && ChatScreen.PendingMessageCountForTests == 0
				               && ChatScreen.PendingHistoryRecipientCountForTests == 0
				               && ChatScreen.NextChatSequenceForTests == 0
				               && ChatScreen.LastHistoryCutForTests == 0
				               && !ChatScreen.HistoryCutAppliedForTests;
				return cleared
					? UnitTestResult.Pass("Session reset clears chat history, pending, sequence, cut, recipient, and Instance")
					: UnitTestResult.Fail("Chat session state survived SessionStateReset");
			}
			finally
			{
				ChatScreen.ResetSessionState();
				if (testObject != null) UnityEngine.Object.Destroy(testObject);
				MultiplayerSession.IsHost = originalHost;
				MultiplayerSession.HostUserID = originalHostId;
			}
		}

		private static void ArrangeDirtyChatState(ref GameObject testObject)
		{
			ChatScreen.ResetSessionState();
			MultiplayerSession.IsHost = true;
			MultiplayerSession.HostUserID = 1;
			ChatScreen.QueueMessage(Message(0, 1, "pending"));
			ChatScreen.ApplyAuthoritativeLive(Message(1, 2, "live"));
			ChatScreen.ApplyHistory(1, new[] { Message(1, 2, "live") });
			ChatScreen.BufferHistoryForPlayer(2);
			ChatScreen.NextHostSequence();
			ChatScreen.NextHostSequence();
			testObject = new GameObject("ChatResetUnitTest");
			ChatScreen.Instance = testObject.AddComponent<ChatScreen>();
		}

		private static ChatScreen.PendingMessage[] MergeHistoryAndLive(bool historyFirst)
		{
			ChatScreen.ResetSessionState();
			var history = new[] { Message(1, 50, "one"), Message(2, 50, "two") };
			ChatScreen.PendingMessage live = Message(3, 60, "three");
			if (historyFirst)
			{
				ChatScreen.ApplyHistory(2, history);
				ChatScreen.ApplyAuthoritativeLive(live);
			}
			else
			{
				ChatScreen.ApplyAuthoritativeLive(live);
				ChatScreen.ApplyHistory(2, history);
			}
			var result = new ChatScreen.PendingMessage[ChatScreen.HistorySnapshotForTests.Count];
			for (int i = 0; i < result.Length; i++) result[i] = ChatScreen.HistorySnapshotForTests[i];
			return result;
		}

		private static bool SameMessages(
			IReadOnlyList<ChatScreen.PendingMessage> left,
			IReadOnlyList<ChatScreen.PendingMessage> right)
		{
			if (left.Count != right.Count) return false;
			for (int i = 0; i < left.Count; i++)
				if (left[i].sequence != right[i].sequence
				    || left[i].timestamp != right[i].timestamp
				    || left[i].message != right[i].message)
					return false;
			return true;
		}

		private static ChatScreen.PendingMessage Message(ulong sequence, long timestamp, string text)
			=> new() { sequence = sequence, timestamp = timestamp, message = text };

		private static bool Calls(MethodInfo caller, MethodInfo callee)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			if (il == null || callee == null) return false;
			for (int index = 0; index <= il.Length - 5; index++)
			{
				if (il[index] != 0x28 && il[index] != 0x6F) continue;
				try
				{
					MethodBase target = caller.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1));
					if (target == callee) return true;
				}
				catch (ArgumentException) { }
			}
			return false;
		}
	}
}

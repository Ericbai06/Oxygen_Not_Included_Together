#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ReliableReadyReplayTests
	{
		private enum ReplayScenario
		{
			Success,
			MissingBatch,
			StaleGeneration,
			StaleReplay,
			ApplyFailure,
		}

		public sealed class ReplayProbePacket : IPacket, IHostOnlyPacket
		{
			internal static int Applied;
			internal int Value;
			internal byte[] Padding = Array.Empty<byte>();

			public void Serialize(BinaryWriter writer)
			{
				writer.Write(Value);
				writer.Write(Padding.Length);
				writer.Write(Padding);
			}

			public void Deserialize(BinaryReader reader)
			{
				Value = reader.ReadInt32();
				int length = reader.ReadInt32();
				if (length < 0 || length > 60 * 1024)
					throw new InvalidDataException("Invalid replay probe padding");
				Padding = reader.ReadBytes(length);
				if (Padding.Length != length)
					throw new EndOfStreamException();
			}

			public void OnDispatched()
			{
				Applied++;
				if (Value == 2)
					throw new InvalidDataException("Synthetic replay failure");
			}
		}

		private sealed class RecordingSender : TransportPacketSender
		{
			internal readonly List<IPacket> Packets = new();

			public override bool SendPacket(
				object connection,
				SerializedPacket packet,
				PacketSendMode sendMode = PacketSendMode.ReliableImmediate)
			{
				Packets.Add(packet.Packet);
				return true;
			}
		}

		[UnitTest(name: "Ready replay sends every batch before one application proof",
			category: "Networking",
			headlessUnsupportedReason: "Calls GameClient world-load ECall")]
		public static UnitTestResult ReadyReplayUsesOneFinalApplicationProof()
			=> Run(ReplayScenario.Success);

		[UnitTest(name: "Ready replay id is unsigned 64-bit on the wire", category: "Networking")]
		public static UnitTestResult ReadyReplayIdUsesUnsignedWire()
		{
			const ulong expected = ulong.MaxValue;
			var proof = new ReadyReplayProof
			{
				ReconnectToken = 1,
				SnapshotGeneration = 2,
				ReplayId = expected,
				BatchCount = 1,
			};
			byte[] bytes = PacketSender.SerializePacketForSending(
				ReadyReplayCommitPacket.Create(proof));
			var decoded = new ReadyReplayCommitPacket();
			using var stream = new MemoryStream(bytes, writable: false);
			using var reader = new BinaryReader(stream);
			reader.ReadInt32();
			decoded.Deserialize(reader);
			bool zeroRejected = false;
			try
			{
				proof.ReplayId = 0;
				ReadyReplayCommitPacket.Create(proof);
			}
			catch (InvalidDataException)
			{
				zeroRejected = true;
			}
			return decoded.Proof.ReplayId == expected && stream.Position == stream.Length
			       && bytes.Length == 32 && zeroRejected
				? UnitTestResult.Pass("UInt64 max round-tripped and zero was rejected")
				: UnitTestResult.Fail("Ready replay id is not a strict UInt64 wire value");
		}

		[UnitTest(name: "Ready replay batch id is unsigned 64-bit on the wire", category: "Networking")]
		public static UnitTestResult ReadyReplayBatchIdUsesUnsignedWire()
		{
			var header = new ReadyReplayBatchHeader
			{
				SnapshotGeneration = 2,
				ReplayId = ulong.MaxValue,
				BatchIndex = 0,
				BatchCount = 1,
			};
			byte[] frame = PacketSender.SerializePacketForSending(Probe(1, 0));
			byte[] bytes = PacketSender.SerializePacketForSending(
				DeferredReliableBatchPacket.Create(header, new[] { frame }));
			var decoded = new DeferredReliableBatchPacket();
			using var stream = new MemoryStream(bytes, writable: false);
			using var reader = new BinaryReader(stream);
			reader.ReadInt32();
			decoded.Deserialize(reader);
			bool zeroRejected = false;
			try
			{
				header.ReplayId = 0;
				DeferredReliableBatchPacket.Create(header, new[] { frame });
			}
			catch (InvalidDataException)
			{
				zeroRejected = true;
			}
			return decoded.Header.ReplayId == ulong.MaxValue && stream.Position == stream.Length
			       && zeroRejected
				? UnitTestResult.Pass("Batch UInt64 max round-tripped and zero was rejected")
				: UnitTestResult.Fail("Ready replay batch id is not a strict UInt64 wire value");
		}

		[UnitTest(name: "Ready replay holds an early commit until its missing batch arrives",
			category: "Networking",
			headlessUnsupportedReason: "Calls GameClient world-load ECall")]
		public static UnitTestResult ReadyReplayCompletesAfterMissingBatchArrives()
			=> Run(ReplayScenario.MissingBatch);

		[UnitTest(name: "Ready replay rejects stale batches",
			category: "Networking",
			headlessUnsupportedReason: "Calls GameClient world-load ECall")]
		public static UnitTestResult ReadyReplayRejectsStaleBatches()
		{
			UnitTestResult staleGeneration = Run(ReplayScenario.StaleGeneration);
			if (staleGeneration.State != TestState.Passed)
				return staleGeneration;
			return Run(ReplayScenario.StaleReplay);
		}

		[UnitTest(name: "Ready replay application exception withholds final proof",
			category: "Networking",
			headlessUnsupportedReason: "Calls GameClient world-load ECall")]
		public static UnitTestResult ReadyReplayRejectsApplicationFailure()
			=> Run(ReplayScenario.ApplyFailure);

		[UnitTest(name: "Targeted reliable gameplay joins the active Ready replay backlog", category: "Networking")]
		public static UnitTestResult TargetedReliableGameplayUsesReadyBacklog()
		{
			TransportPacketSender originalSender = NetworkConfig.TransportPacketSender;
			bool originalHost = MultiplayerSession.IsHost;
			var originalPlayers = new Dictionary<ulong, MultiplayerPlayer>(
				MultiplayerSession.ConnectedPlayers);
			var sender = new RecordingSender();
			try
			{
				PacketRegistry.TryRegister(typeof(ReplayProbePacket));
				NetworkConfig.TransportPacketSender = sender;
				PacketSender.ResetSessionState();
				MultiplayerSession.ConnectedPlayers.Clear();
				MultiplayerSession.IsHost = true;
				var client = new MultiplayerPlayer(2);
				client.BeginConnection(new object());
				client.ProtocolVerified = true;
				MultiplayerSession.ConnectedPlayers.Add(2, client);
				ReliableSyncBacklog.Begin(2);
				bool gameplayAccepted = PacketSender.SendToPlayer(
					2, Probe(1, 0), PacketSendMode.Reliable);
				bool controlAccepted = PacketSender.SendToPlayer(
					2, new LoadingAcceptedPacket
					{
						ReconnectToken = 7,
						SnapshotGeneration = 3
					}, PacketSendMode.ReliableImmediate);
				return gameplayAccepted && controlAccepted
				       && ReliableSyncBacklog.CountForTests(2) == 1
				       && sender.Packets.Count == 1
					? UnitTestResult.Pass("Gameplay was journalled while baseline control bypassed it")
					: UnitTestResult.Fail("Targeted gameplay bypassed the Ready replay fence");
			}
			finally
			{
				ReliableSyncBacklog.ClearAll();
				PacketSender.ResetSessionState();
				MultiplayerSession.ConnectedPlayers.Clear();
				foreach (var pair in originalPlayers)
					MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
				MultiplayerSession.IsHost = originalHost;
				NetworkConfig.TransportPacketSender = originalSender;
			}
		}

		private static UnitTestResult Run(ReplayScenario scenario)
		{
			TransportPacketSender originalSender = NetworkConfig.TransportPacketSender;
			bool originalHost = MultiplayerSession.IsHost;
			ulong originalHostId = MultiplayerSession.HostUserID;
			var originalPlayers = new Dictionary<ulong, MultiplayerPlayer>(
				MultiplayerSession.ConnectedPlayers);
			var sender = new RecordingSender();
			try
			{
				if (!ArrangeHost(sender, scenario, out MultiplayerPlayer client, out long generation))
					return UnitTestResult.Fail("Could not arrange two Ready replay batches and one commit");
				var host = new MultiplayerPlayer(1);
				host.BeginConnection(new object());
				MultiplayerSession.ConnectedPlayers.Add(1, host);
				MultiplayerSession.IsHost = false;
				ReadyManager.TryBeginClientSnapshot(generation);
				GameClient.BeginReadyAcceptanceWait(99, generation);
				ReplayProbePacket.Applied = 0;
				PacketHandler.BypassReadyGateForTests = true;
				PacketHandler.BypassTrackingForTests = true;
				var context = new DispatchContext(1, true, host.ConnectionGeneration);
				if (!Receive(sender.Packets[0], context))
					return UnitTestResult.Fail("First deferred batch was rejected");
				if (scenario is ReplayScenario.StaleGeneration or ReplayScenario.StaleReplay)
				{
					ReadyReplayBatchHeader current =
						((DeferredReliableBatchPacket)sender.Packets[1]).Header;
					var stale = DeferredReliableBatchPacket.Create(
						new ReadyReplayBatchHeader
						{
							SnapshotGeneration = scenario == ReplayScenario.StaleGeneration
								? generation + 1 : generation,
							ReplayId = scenario == ReplayScenario.StaleReplay
								? current.ReplayId + 1 : current.ReplayId,
							BatchIndex = 1,
							BatchCount = 2,
						}, new[] { PacketSender.SerializePacketForSending(Probe(3, 0)) });
					return ExpectRejected(Receive(stale, context), sender, client);
				}
				if (scenario != ReplayScenario.MissingBatch && !Receive(sender.Packets[1], context))
					return UnitTestResult.Fail("Second deferred batch was rejected");
				if (sender.Packets.Count != 3)
					return UnitTestResult.Fail("A per-batch ACK escaped before the header-only commit");
				bool committed = Receive(sender.Packets[2], context);
				if (scenario == ReplayScenario.MissingBatch)
				{
					if (!committed || sender.Packets.Count != 3
					    || ReplayProbePacket.Applied != 0
					    || client.readyState != ClientReadyState.Loading
					    || !ReadyManager.HasPendingReadyCommitForTests(2))
						return UnitTestResult.Fail("Early commit was not retained pending without proof or Ready");
					if (!Receive(sender.Packets[1], context))
						return UnitTestResult.Fail("Missing deferred batch was rejected after the pending commit");
				}
				else if (scenario != ReplayScenario.Success)
					return ExpectRejected(committed, sender, client);
				if (!committed || sender.Packets.Count != 4
				    || sender.Packets[3] is not ReadyReplayAppliedPacket
				    || ReplayProbePacket.Applied != 2)
					return UnitTestResult.Fail("Client did not return exactly one proof after all apply work");
				MultiplayerSession.IsHost = true;
				bool applied = PacketHandler.TryHandleIncoming(
					PacketSender.SerializePacketForSending(sender.Packets[3]),
					new DispatchContext(2, false, client.ConnectionGeneration));
				return applied && client.readyState == ClientReadyState.Ready
				       && sender.Packets.Count == 5
					? UnitTestResult.Pass("One exact final proof is the sole Ready commit point")
					: UnitTestResult.Fail("Host committed before or failed after the exact final proof");
			}
			finally
			{
				ReadyManager.ResetSessionState();
				PacketHandler.BypassReadyGateForTests = false;
				PacketHandler.BypassTrackingForTests = false;
				PacketSender.ResetSessionState();
				MultiplayerSession.ConnectedPlayers.Clear();
				foreach (var pair in originalPlayers)
					MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
				MultiplayerSession.IsHost = originalHost;
				MultiplayerSession.HostUserID = originalHostId;
				NetworkConfig.TransportPacketSender = originalSender;
			}
		}

		private static bool ArrangeHost(
			RecordingSender sender,
			ReplayScenario scenario,
			out MultiplayerPlayer client,
			out long generation)
		{
			PacketRegistry.TryRegister(typeof(ReplayProbePacket));
			NetworkConfig.TransportPacketSender = sender;
			ReadyManager.ResetSessionState();
			PacketSender.ResetSessionState();
			PacketHandler.ResetSessionState();
			MultiplayerSession.ConnectedPlayers.Clear();
			MultiplayerSession.IsHost = true;
			MultiplayerSession.HostUserID = 1;
			client = new MultiplayerPlayer(2);
			client.BeginConnection(new object());
			client.ProtocolVerified = true;
			MultiplayerSession.ConnectedPlayers.Add(2, client);
			generation = 0;
			return ReadyManager.BeginSyncBarrier(2)
			       && ReadyManager.BeginSnapshotEpoch(2, out generation)
			       && ReadyManager.SetPlayerReadyState(
				       client, ClientReadyState.Loading, 99, generation)
			       && ReadyManager.TryBeginWorldBaseline(2, generation)
			       && ReliableSyncBacklog.TryBuffer(
				       2, Probe(1, 40 * 1024), PacketSendMode.Reliable)
			       == SyncBacklogResult.Buffered
			       && ReliableSyncBacklog.TryBuffer(
				       2, Probe(scenario == ReplayScenario.ApplyFailure ? 2 : 3, 40 * 1024),
				       PacketSendMode.Reliable) == SyncBacklogResult.Buffered
			       && ReadyManager.SetPlayerReadyState(
				       client, ClientReadyState.Ready, 99, generation)
			       && sender.Packets.Count == 3
			       && sender.Packets[0] is DeferredReliableBatchPacket
			       && sender.Packets[1] is DeferredReliableBatchPacket
			       && sender.Packets[2] is ReadyReplayCommitPacket
			       && PacketSender.SerializePacketForSending(sender.Packets[2]).Length == 32
			       && client.readyState != ClientReadyState.Ready;
		}

		private static bool Receive(IPacket packet, DispatchContext context)
			=> PacketHandler.TryHandleIncoming(
				PacketSender.SerializePacketForSending(packet), context);

		private static UnitTestResult ExpectRejected(
			bool accepted, RecordingSender sender, MultiplayerPlayer client)
			=> !accepted && sender.Packets.Count == 3
			   && client.readyState == ClientReadyState.Loading
			   && ReadyManager.HasPendingReadyCommitForTests(2)
				? UnitTestResult.Pass("Invalid replay produced no final proof and did not commit Ready")
				: UnitTestResult.Fail("Invalid replay leaked a proof or Ready commit");

		private static ReplayProbePacket Probe(int value, int padding)
			=> new() { Value = value, Padding = new byte[padding] };
	}
}
#endif

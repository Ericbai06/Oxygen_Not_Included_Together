using System;
using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Handshake;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Transport;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ProtocolCompatibilityTests
	{
		private sealed class RecordingSender : TransportPacketSender
		{
			internal readonly System.Collections.Generic.List<IPacket> Packets = new();
			public override bool SendPacket(
				object connection,
				SerializedPacket packet,
				PacketSendMode sendMode = PacketSendMode.ReliableImmediate)
			{
				Packets.Add(packet.Packet);
				return true;
			}
		}

		private sealed class RecordingServer : TransportServer
		{
			internal int KickCount;
			public override void Prepare() { }
			public override void Start() { }
			public override void Stop() { }
			public override void CloseConnections() { }
			public override void Update() { }
			public override void OnMessageRecieved() { }
			public override void KickClient(ulong clientId) => KickCount++;
		}
		[UnitTest(name: "Protocol version: presentation wire is v10 only", category: "Networking")]
		public static UnitTestResult PresentationWireIsVersionTenOnly()
		{
			return ProtocolCompatibility.CurrentProtocolVersion == 10
			       && ProtocolCompatibility.SupportsVersion(10)
			       && !ProtocolCompatibility.SupportsVersion(9)
				? UnitTestResult.Pass("Protocol v10 rejects every legacy wire version")
				: UnitTestResult.Fail(
					$"Expected v10-only protocol, got {ProtocolCompatibility.CurrentProtocolVersion}");
		}

		[UnitTest(name: "Protocol rejection ACK: sender and generation are bound", category: "Networking")]
		public static UnitTestResult ProtocolRejectionAckBindsConnectionGeneration()
		{
			var original = new System.Collections.Generic.Dictionary<ulong, MultiplayerPlayer>(
				MultiplayerSession.ConnectedPlayers);
			try
			{
				MultiplayerSession.ConnectedPlayers.Clear();
				object connection = new();
				var player = new MultiplayerPlayer(42);
				long generation = player.BeginConnection(connection);
				MultiplayerSession.ConnectedPlayers.Add(42, player);
				var expected = new ProtocolRejectionKey(42, connection, generation);
				var valid = new DispatchContext(42, false, generation);
				var stale = new DispatchContext(42, false, generation - 1);
				bool acceptsCurrent = ProtocolRejectionBarrier.IsValidAck(
					expected, ProtocolCompatibility.CurrentProtocolVersion, valid);
				bool rejectsStale = !ProtocolRejectionBarrier.IsValidAck(
					expected, ProtocolCompatibility.CurrentProtocolVersion, stale);
				player.BeginConnection(new object());
				bool rejectsReconnect = !ProtocolRejectionBarrier.IsValidAck(
					expected, ProtocolCompatibility.CurrentProtocolVersion, valid);
				return acceptsCurrent && rejectsStale && rejectsReconnect
					? UnitTestResult.Pass("Protocol rejection ACK is bound to connection identity and generation")
					: UnitTestResult.Fail("Protocol rejection ACK accepted stale connection state");
			}
			finally
			{
				MultiplayerSession.ConnectedPlayers.Clear();
				foreach (var pair in original)
					MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
			}
		}

		[UnitTest(name: "Protocol rejection timeout cannot kick a reconnected client", category: "Networking")]
		public static UnitTestResult ProtocolRejectionTimeoutBindsConnectionObject()
		{
			TransportServer originalServer = NetworkConfig.TransportServer;
			var original = new System.Collections.Generic.Dictionary<ulong, MultiplayerPlayer>(
				MultiplayerSession.ConnectedPlayers);
			var server = new RecordingServer();
			try
			{
				NetworkConfig.TransportServer = server;
				MultiplayerSession.ConnectedPlayers.Clear();
				var player = new MultiplayerPlayer(42);
				object oldConnection = new();
				long oldGeneration = player.BeginConnection(oldConnection);
				MultiplayerSession.ConnectedPlayers.Add(42, player);
				var oldKey = new ProtocolRejectionKey(42, oldConnection, oldGeneration);
				ProtocolRejectionBarrier.Begin(42, oldConnection, oldGeneration);
				player.BeginConnection(new object());
				ProtocolRejectionBarrier.ExpireForTests(oldKey);
				return server.KickCount == 0
					? UnitTestResult.Pass("Old rejection timer ignored the replacement connection")
					: UnitTestResult.Fail("Old rejection timer kicked a reconnected client");
			}
			finally
			{
				ProtocolRejectionBarrier.Reset();
				NetworkConfig.TransportServer = originalServer;
				MultiplayerSession.ConnectedPlayers.Clear();
				foreach (var pair in original)
					MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
			}
		}

		[UnitTest(name: "Missing protocol metadata uses reliable rejection ACK barrier", category: "Networking")]
		public static UnitTestResult LegacyRequestUsesReliableRejectionBarrier()
		{
			TransportPacketSender originalSender = NetworkConfig.TransportPacketSender;
			TransportServer originalServer = NetworkConfig.TransportServer;
			bool originalHost = MultiplayerSession.IsHost;
			bool originalSession = MultiplayerSession.InSession;
			ulong originalHostId = MultiplayerSession.HostUserID;
			var original = new System.Collections.Generic.Dictionary<ulong, MultiplayerPlayer>(
				MultiplayerSession.ConnectedPlayers);
			var sender = new RecordingSender();
			var server = new RecordingServer();
			try
			{
				NetworkConfig.TransportPacketSender = sender;
				NetworkConfig.TransportServer = server;
				MultiplayerSession.IsHost = true;
				MultiplayerSession.InSession = true;
				MultiplayerSession.HostUserID = 1;
				MultiplayerSession.ConnectedPlayers.Clear();
				var player = new MultiplayerPlayer(42);
				player.BeginConnection(new object());
				MultiplayerSession.ConnectedPlayers.Add(42, player);
				bool rejected = PacketHandler.TryHandleIncoming(
					LegacyRequestBytes(42),
					new DispatchContext(42, false, player.ConnectionGeneration));
				bool acked = PacketHandler.TryHandleIncoming(
					PacketSender.SerializePacketForSending(
						ProtocolRejectedAckPacket.Create(ProtocolCompatibility.CurrentProtocolVersion)),
					new DispatchContext(42, false, player.ConnectionGeneration));
				return rejected && acked && sender.Packets.Count == 1
				       && sender.Packets[0] is GameStateRequestPacket response
				       && !response.ProtocolAccepted && server.KickCount == 1
					? UnitTestResult.Pass("Legacy request received reliable reason before explicit ACK disconnect")
					: UnitTestResult.Fail("Missing metadata bypassed the reliable rejection barrier");
			}
			finally
			{
				ProtocolRejectionBarrier.Reset();
				NetworkConfig.TransportPacketSender = originalSender;
				NetworkConfig.TransportServer = originalServer;
				MultiplayerSession.IsHost = originalHost;
				MultiplayerSession.InSession = originalSession;
				MultiplayerSession.HostUserID = originalHostId;
				MultiplayerSession.ConnectedPlayers.Clear();
				foreach (var pair in original)
					MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
			}
		}

		private static byte[] LegacyRequestBytes(ulong clientId)
		{
			using var stream = new MemoryStream();
			using var writer = new BinaryWriter(stream);
			writer.Write(PacketRegistry.GetPacketId(new GameStateRequestPacket()));
			writer.Write(clientId);
			writer.Write(0UL);
			writer.Write((byte)0);
			writer.Write(0);
			writer.Write(0);
			writer.Write(0);
			return stream.ToArray();
		}

		[UnitTest(name: "Save transfer: metadata bounds are enforced", category: "Networking")]
		public static UnitTestResult SaveTransferBoundsAreEnforced()
		{
			try
			{
				SaveFileChunkPacket.ValidateMetadata(
					0, SaveFileChunkPacket.MaxChunkBytes,
					SaveFileChunkPacket.MaxChunkBytes, SaveFileChunkPacket.MaxChunkBytes);
			}
			catch (Exception e)
			{
				return UnitTestResult.Fail("Valid save chunk was rejected: " + e.Message);
			}

			if (!InvalidSaveMetadata((-1, SaveFileChunkPacket.MaxChunkBytes,
				    SaveFileChunkPacket.MaxChunkBytes, SaveFileChunkPacket.MaxChunkBytes))
			    || !InvalidSaveMetadata((0, SaveFileChunkPacket.MaxSaveBytes + 1,
				    SaveFileChunkPacket.MaxChunkBytes, SaveFileChunkPacket.MaxChunkBytes))
			    || !InvalidSaveMetadata((0, 1024, SaveFileChunkPacket.MaxChunkBytes + 1, 512))
			    || !InvalidSaveMetadata((0, SaveFileChunkPacket.MaxChunkBytes, 1024, 1024))
			    || !InvalidSaveMetadata((SaveFileChunkPacket.MaxChunkBytes - 100,
				    SaveFileChunkPacket.MaxChunkBytes, SaveFileChunkPacket.MaxChunkBytes, 200)))
				return UnitTestResult.Fail("Invalid save metadata was accepted");
			return UnitTestResult.Pass("Save size, chunk size, offset, and copy bounds are enforced");
		}

		[UnitTest(name: "Save transfer: hash binds identity and payload", category: "Networking")]
		public static UnitTestResult SaveTransferHashBindsMetadata()
		{
			byte[] payload = { 1, 2, 3, 4 };
			byte[] baseline = SecureTransferPacket.ComputePayloadHash(3, "transfer-a", payload);
			if (baseline.Length != 32)
				return UnitTestResult.Fail("SHA-256 output length is not 32 bytes");
			if (Equal(baseline, SecureTransferPacket.ComputePayloadHash(4, "transfer-a", payload))
			    || Equal(baseline, SecureTransferPacket.ComputePayloadHash(3, "transfer-b", payload))
			    || Equal(baseline, SecureTransferPacket.ComputePayloadHash(
				    3, "transfer-a", new byte[] { 1, 2, 3, 5 })))
				return UnitTestResult.Fail("Transfer hash did not bind every identity and payload field");
			return UnitTestResult.Pass("SHA-256 binds sequence, transfer identity, and payload");
		}

		[UnitTest(name: "Mod fingerprint: fields are unambiguous", category: "Networking")]
		public static UnitTestResult ModFingerprintFieldsAreUnambiguous()
		{
			string left = ProtocolCompatibility.ComposeModFingerprint(
				0, "a", "bc", string.Empty, "1", "content", "config");
			string right = ProtocolCompatibility.ComposeModFingerprint(
				0, "ab", "c", string.Empty, "1", "content", "config");
			return string.Equals(left, right, StringComparison.Ordinal)
				? UnitTestResult.Fail("Length-shifted mod metadata collided")
				: UnitTestResult.Pass("Length-prefixed mod metadata is unambiguous");
		}

		[UnitTest(name: "Client relay: HostBroadcast owns one fanout", category: "Networking")]
		public static UnitTestResult HostBroadcastOwnsSingleFanout()
		{
			int dispatchCount = 0;
			int fanoutCount = 0;
			bool verified = false;
			var directClient = new DispatchContext(101, false);
			bool dispatched = HostBroadcastPacket.DispatchVerifiedRelayAndFanOut(
				new PingPacket { PlayerID = 101 }, directClient,
				new HostBroadcastPacket.RelayDispatchActions(
					(_, context) => { dispatchCount++; verified = context.IsVerifiedHostBroadcast; return true; },
					(_, _) => fanoutCount++));
			if (!dispatched || dispatchCount != 1 || fanoutCount != 1 || !verified)
				return UnitTestResult.Fail("Accepted relay did not dispatch and fan out exactly once");
			fanoutCount = 0;
			bool rejected = HostBroadcastPacket.DispatchVerifiedRelayAndFanOut(
				new PingPacket { PlayerID = 101 }, directClient,
				new HostBroadcastPacket.RelayDispatchActions(
					(_, _) => false, (_, _) => fanoutCount++));
			return !rejected && fanoutCount == 0
				? UnitTestResult.Pass("HostBroadcast owns one verified relay fanout")
				: UnitTestResult.Fail("Rejected nested dispatch was fanned out");
		}

		private static bool InvalidSaveMetadata(
			(int Offset, int Total, int Chunk, int Length) metadata)
		{
			try
			{
				SaveFileChunkPacket.ValidateMetadata(
					metadata.Offset, metadata.Total, metadata.Chunk, metadata.Length);
				return false;
			}
			catch (InvalidDataException)
			{
				return true;
			}
		}

		private static bool Equal(byte[] left, byte[] right)
		{
			if (left.Length != right.Length)
				return false;
			for (int i = 0; i < left.Length; i++)
				if (left[i] != right[i])
					return false;
			return true;
		}
	}
}

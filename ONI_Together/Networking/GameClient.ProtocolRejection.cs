using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Handshake;
using ONI_Together.Networking.States;

namespace ONI_Together.Networking
{
	public static partial class GameClient
	{
		private static void HandleHostProtocolRejection(GameStateRequestPacket packet)
		{
			string message = string.IsNullOrEmpty(packet.ProtocolFailureReason)
				? STRINGS.UI.PROTOCOL.VALIDATION.REJECTED
				: packet.ProtocolFailureReason;
			bool sent = PacketSender.SendToHost(
				ProtocolRejectedAckPacket.Create(packet.ProtocolVersion),
				PacketSendMode.ReliableImmediate);
			DebugConsole.Log(
				$"[ProtocolRejection] displayed reason and queued explicit ACK; sent={(sent ? 1 : 0)}");
			ReadyManager.CancelPendingClientWorldLoad();
			SetState(ClientState.Error);
			NetworkConfig.TransportClient.OnReturnToMenu?.Invoke(
				STRINGS.UI.PROTOCOL.VALIDATION.TITLE, message);
		}
	}
}

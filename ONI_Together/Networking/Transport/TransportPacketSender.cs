using System;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.Networking.Transport
{
	public readonly struct SerializedPacket
	{
		public IPacket Packet { get; }
		public byte[] Bytes { get; }
		public string PacketType => Packet.GetType().Name;

		public SerializedPacket(IPacket packet, byte[] bytes)
		{
			Packet = packet ?? throw new ArgumentNullException(nameof(packet));
			Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
		}
	}

	public abstract class TransportPacketSender
	{
		public bool SendToConnection(
			object connection,
			SerializedPacket packet,
			PacketSendMode sendMode = PacketSendMode.ReliableImmediate)
			=> SendPacket(connection, packet, sendMode);

		public abstract bool SendPacket(
			object connection,
			SerializedPacket packet,
			PacketSendMode sendMode = PacketSendMode.ReliableImmediate);
	}
}

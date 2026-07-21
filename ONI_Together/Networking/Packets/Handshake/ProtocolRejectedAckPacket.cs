using System.IO;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.Networking.Packets.Handshake
{
	internal sealed class ProtocolRejectedAckPacket : IPacket
	{
		private int _hostProtocolVersion;

		public ProtocolRejectedAckPacket() { }

		internal static ProtocolRejectedAckPacket Create(int hostProtocolVersion)
			=> new ProtocolRejectedAckPacket { _hostProtocolVersion = hostProtocolVersion };

		public void Serialize(BinaryWriter writer)
		{
			if (!ProtocolCompatibility.SupportsVersion(_hostProtocolVersion))
				throw new InvalidDataException("Invalid rejected host protocol version");
			writer.Write(_hostProtocolVersion);
		}

		public void Deserialize(BinaryReader reader)
		{
			_hostProtocolVersion = reader.ReadInt32();
			if (!ProtocolCompatibility.SupportsVersion(_hostProtocolVersion))
				throw new InvalidDataException("Invalid rejected host protocol version");
		}

		public void OnDispatched()
		{
			DispatchContext context = PacketHandler.CurrentContext;
			if (!MultiplayerSession.IsHost
			    || !ProtocolRejectionBarrier.TryAcknowledge(context, _hostProtocolVersion))
				throw new InvalidDataException("Protocol rejection ACK is stale or unexpected");
		}
	}
}

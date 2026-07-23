#if DEBUG
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.Architecture
{
	internal static class ScenarioActionPacketTransport
	{
		internal static bool SendScenarioAction(
			IPacket packet, PacketSendMode mode)
		{
			if (packet == null) return false;
			try
			{
				global::ONI_Together.Networking.PacketSender.SendToAllClients(packet, mode);
				return true;
			}
			catch (System.Exception exception)
			{
				global::Debug.LogError(
					"[ONI_Together] Scenario action packet send failed: " + exception);
				return false;
			}
		}
	}
}
#endif

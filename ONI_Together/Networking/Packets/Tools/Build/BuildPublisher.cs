using ONI_Together.Networking;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	internal static class BuildPublisher
	{
		internal static void Publish(BuildCommit commit)
		{
			if (commit == null)
				return;
			PacketSender.SendToAllClients(
				BuildCommitPacket.FromDomain(commit), PacketSendMode.ReliableImmediate);
		}

		internal static void Publish(BuildRejected rejection)
		{
			if (rejection == null)
				return;
			PacketSender.SendToAllClients(
				BuildRejectedPacket.FromDomain(rejection), PacketSendMode.ReliableImmediate);
		}
	}
}

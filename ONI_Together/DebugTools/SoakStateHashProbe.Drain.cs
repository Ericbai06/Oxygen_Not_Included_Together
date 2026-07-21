#if DEBUG
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	internal sealed partial class SoakStateHashProbe
	{
		private void SuppressAuthoritativeRepair()
		{
			if (_authoritativeRepairSuppressed)
				return;
			WorldStateSyncer.SetAuthoritativeRepairSuppressed(true);
			_authoritativeRepairSuppressed = true;
		}

		private void ResumeAuthoritativeRepair()
		{
			if (!_authoritativeRepairSuppressed)
				return;
			WorldStateSyncer.SetAuthoritativeRepairSuppressed(false);
			_authoritativeRepairSuppressed = false;
		}

		private void PauseWorldScan()
		{
			if (_worldScanPaused)
				return;
			WorldStateSyncer.SetWorldScanPaused(true);
			_worldScanPaused = true;
		}

		private void ResumeWorldScan()
		{
			if (!_worldScanPaused)
				return;
			WorldStateSyncer.SetWorldScanPaused(false);
			_worldScanPaused = false;
		}

		private bool HasPendingBulkPackets()
		{
			foreach (ulong clientId in _pendingClients)
			{
				if (MultiplayerSession.ConnectedPlayers.TryGetValue(clientId, out var player)
				    && PacketSender.PendingBulkCountForTests(player.Connection) > 0)
					return true;
			}
			return false;
		}

		private bool SendFence(IPacket fence, ProbeState waitingState)
		{
			if (_pendingClients.Count != 1
			    || !MultiplayerSession.ConnectedPlayers.TryGetValue(
				    _pendingClients.Single(), out MultiplayerPlayer player)
			    || player.Connection == null)
			{
				Abort("fence connection became unavailable");
				return false;
			}
			_state = waitingState;
			_stateStartedAt = Time.realtimeSinceStartup;
			bool sent = PacketSender.SendToConnection(
				player.Connection, fence, PacketSendMode.ReliableImmediate);
			if (!sent)
				Abort("fence transport send failed");
			return sent;
		}
	}
}
#endif

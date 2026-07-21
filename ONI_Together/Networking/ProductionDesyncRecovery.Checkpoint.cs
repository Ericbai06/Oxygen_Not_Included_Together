using ONI_Together.DebugTools;
using ONI_Together.Misc.World;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.Networking
{
	public static partial class ProductionDesyncRecovery
	{
		private static void AbortUnstableCheckpoint(string reason)
		{
			DebugConsole.LogWarning(
				$"[ProductionDesync] Discarding unstable checkpoint: {reason}; {HostDiagnostics()}");
			WorldUpdateBatcher.ResumeRepairDispatch();
			if (!TryReleaseClients()) return;
			SpeedChangePacket.SpeedState speed = _previousSpeed;
			ResetHostState();
			SetLocalSpeed(speed);
			SpeedChangePacket.SubmitLocalChange(speed);
		}

		private static bool TryReleaseClients()
		{
			var release = new ProductionDesyncReleasePacket { ProbeId = _probeId };
			foreach (ulong clientId in Clients)
				if (!PacketSender.SendToPlayer(clientId, release))
				{
					Escalate("release send failed");
					return false;
				}
			return true;
		}

		private static void PauseWorldScan()
		{
			if (_worldScanPaused) return;
			WorldStateSyncer.SetWorldScanPaused(true);
			_worldScanPaused = true;
		}

		private static void ResumeWorldScan()
		{
			if (!_worldScanPaused) return;
			WorldStateSyncer.SetWorldScanPaused(false);
			_worldScanPaused = false;
		}
	}
}

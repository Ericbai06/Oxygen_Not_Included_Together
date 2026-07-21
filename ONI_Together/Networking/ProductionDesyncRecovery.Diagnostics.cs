using ONI_Together.Misc.World;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.Networking
{
	public static partial class ProductionDesyncRecovery
	{
		internal static string HostDiagnostics()
			=> $"phase={_phase};probe={_probeId};attempt={_attempt};" +
			   $"cut={_repairSequenceCut};awaiting={Awaiting.Count};" +
			   $"foreground={WorldUpdatePacket.CurrentHostForegroundSequence};" +
			   $"journal={WorldUpdateBatcher.RepairJournalPendingCount}";

		internal static string ClientDiagnostics()
			=> $"probe={_clientProbeId};fenceCut={_clientFence?.RepairSequenceCut ?? -1};" +
			   $"resolved={WorldUpdatePacket.ClientResolvedRepairSequence};" +
			   $"foreground={WorldUpdatePacket.CurrentClientForegroundSequence};" +
			   $"deferred={WorldUpdatePacket.PendingRepairPacketCount};" +
			   $"observing={WorldUpdateRepairObservability.PendingCount}";
	}
}

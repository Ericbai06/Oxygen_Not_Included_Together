using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Patches.GamePatches;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	public class WorldCyclePacket : IPacket, Shared.Interfaces.Networking.IHostOnlyPacket
	{
		private static ulong lastAppliedRevision;

		public int Cycle { get; set; }
		public float CycleTime { get; set; }
		public long HostSimTick { get; set; }
		public ulong Revision { get; set; }

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			if (Revision == 0)
				Revision = NetworkIdentityRegistry.NextAuthorityRevision();
			writer.Write(Cycle);
			writer.Write(CycleTime);
			writer.Write(HostSimTick);
			writer.Write(Revision);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			Cycle = reader.ReadInt32();
			CycleTime = reader.ReadSingle();
			HostSimTick = reader.ReadInt64();
			Revision = reader.ReadUInt64();
			if (Cycle < 0 || float.IsNaN(CycleTime) || float.IsInfinity(CycleTime)
			    || CycleTime < 0f || CycleTime >= 600f || HostSimTick < 0 || Revision == 0)
				throw new InvalidDataException("Invalid world cycle state");
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;
			if (!AcceptRevision(Revision))
				return;

			float totalTime = Cycle * 600f + CycleTime;
			PresentationTickClock.ObserveWorldClock(HostSimTick, Revision);

			if (GameClock.Instance != null)
			{
				try
				{
					GameClockPatch.allowAddTimeForSetTime = true;
					GameClock.Instance.SetTime(totalTime);
				}
				finally
				{
					GameClockPatch.allowAddTimeForSetTime = false;
				}
			}
			else
			{
				DebugConsole.LogWarning("[Multiplayer] GameClock.Instance is null — cannot apply cycle sync.");
			}
		}

		internal static bool ShouldApplyRevision(ulong current, ulong incoming)
			=> NetworkIdentityRegistry.IsNewerRevision(current, incoming);

		internal static bool AcceptRevisionForTests(ulong revision) => AcceptRevision(revision);

		private static bool AcceptRevision(ulong revision)
		{
			if (!ShouldApplyRevision(lastAppliedRevision, revision)) return false;
			lastAppliedRevision = revision;
			return true;
		}

		internal static void ClearState() => lastAppliedRevision = 0;
	}
}

using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Tools.Dig;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Trackers;
using ONI_Together.Networking.Transport.Steamworks;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public partial class WorldStateSyncer : MonoBehaviour
	{
		// --- Research Logic ---
		private void SyncResearch()
		{
			using var _ = Profiler.Scope();
			ResearchSyncCoordinator.PublishRepairSnapshot();
		}

		// --- Research Progress Logic ---
		private void SyncResearchProgress()
		{
			using var _ = Profiler.Scope();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			ResearchSyncCoordinator.PublishProgressSample();
			sw.Stop();
			SyncStats.RecordSync(SyncStats.Research, 1, 20, sw.ElapsedMilliseconds);
		}

		// --- Priority convergence snapshot ---
		private void SyncPriorities()
		{
			using var _ = Profiler.Scope();

			try
			{
				foreach (NetworkIdentity identity in NetworkIdentityRegistry.AllIdentities)
				{
					if (identity != null)
						PrioritizeStatePacket.SendPeriodicSnapshot(identity);
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[WorldStateSyncer] Error in SyncPriorities: {ex.Message}");
			}
			BuildingConfigPacket.SendPeriodicSnapshots();
		}

	private System.Reflection.FieldInfo _disinfectChoreField;

	// --- Disinfect Logic (NOT USED - synced via event-driven patches) ---
		private void SyncDisinfectImpl()
		{
			using var _ = Profiler.Scope();

			try
			{
				// Use our tracker
				lock (DisinfectTracker.Disinfectables)
				{
					if (DisinfectTracker.Disinfectables.Count == 0) return;

					if (_disinfectChoreField == null)
					{
						_disinfectChoreField = typeof(Disinfectable).GetField("chore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
					}

					var packet = new DisinfectStatePacket();
					foreach (var disinfectable in DisinfectTracker.Disinfectables)
					{
						if (disinfectable == null) continue;

						object chore = _disinfectChoreField?.GetValue(disinfectable);
						if (chore != null)
						{
							int cell = Grid.PosToCell(disinfectable);
							packet.DisinfectCells.Add(cell);
						}
					}

					if (packet.DisinfectCells.Count > 0)
						PacketSender.SendToAllClients(packet, PacketSendMode.Unreliable);
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[WorldStateSyncer] Error in SyncDisinfectImpl: {ex.Message}");
			}
		}

		public void OnDisinfectStateReceived(DisinfectStatePacket packet)
		{
			using var _ = Profiler.Scope();

			try
			{
				lock (DisinfectTracker.Disinfectables)
				{
					if (DisinfectTracker.Disinfectables.Count == 0) return;

					if (_disinfectChoreField == null)
					{
						_disinfectChoreField = typeof(Disinfectable).GetField("chore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
					}

					foreach (var disinfectable in DisinfectTracker.Disinfectables)
					{
						if (disinfectable == null) continue;
						int cell = Grid.PosToCell(disinfectable);

						object chore = _disinfectChoreField?.GetValue(disinfectable);
						bool isMarked = chore != null;

						if (packet.DisinfectCells.Contains(cell))
						{
							if (!isMarked)
							{
								disinfectable.MarkForDisinfect();
							}
						}
						else
						{
							if (isMarked)
							{
								disinfectable.Trigger((int)GameHashes.Cancel, null);
							}
						}
					}
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[WorldStateSyncer] Error in OnDisinfectStateReceived: {ex.Message}");
			}
		}
	}
}

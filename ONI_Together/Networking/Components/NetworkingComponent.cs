using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport.Steamworks;
using ONI_Together.Patches.Duplicant;
using Shared.Profiling;
using Steamworks;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public class NetworkingComponent : MonoBehaviour
	{
		public static UnityTaskScheduler scheduler = new UnityTaskScheduler();
		private static long _lastPresentationFlushTick;

		/*
		 * TODO:
		 * Update this class now that we can have different relay types. This is not steam specific anymore
		 *
		 * **/

		private void Start()
		{
			//SteamNetworkingUtils.InitRelayNetworkAccess();
			//GameClient.Init();

			// NOTE: Client reconnection after world load is now handled in
			// GamePatch.OnSpawnPostfix which triggers AFTER the world is fully loaded.
			// This is safer than OnPostSceneLoaded which fires during scene unload.
		}

		private void Update()
		{
			using var _ = Profiler.Scope();

			scheduler.Tick();
			NetworkIdentityRegistry.PruneUnassigned();

			if (NetworkConfig.transport.Equals(NetworkConfig.NetworkTransport.STEAMWORKS))
			{
				if (!SteamManager.Initialized)
					return;
			}

			//if (!MultiplayerSession.InSession)
			//	return;

            if (MultiplayerSession.IsHost)
			{
				GameServer.Update();
			}
			else if (MultiplayerSession.IsClient && MultiplayerSession.HostUserID.IsValid())
			{
				GameClient.Poll();

				// Check for inactive transfers and request missing chunks
				ONI_Together.Misc.World.SaveChunkAssembler.CheckInactiveTransfers();
			}
			EffectsPatch.FlushDirtyEffects();
			if (ShouldFlushPresentation(PresentationTickClock.CurrentTick))
			{
				DuplicantStateSender.FlushPending();
				RemoteMotionPresenter.FlushPending();
			}
			PacketSender.DispatchPendingBulkPackets();
        }

		internal static void ResetPresentationFlushGate()
			=> _lastPresentationFlushTick = PresentationTickClock.CurrentTick;

		internal static void ResetPresentationFlushGateForTests(long tick)
			=> _lastPresentationFlushTick = tick;

		internal static bool ShouldFlushPresentationForTests(long tick)
			=> ShouldFlushPresentation(tick);

		private static bool ShouldFlushPresentation(long tick)
		{
			if (tick <= _lastPresentationFlushTick) return false;
			_lastPresentationFlushTick = tick;
			return true;
		}

        private void OnApplicationQuit()
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession)
				return;

			NetworkConfig.Stop();
		}
	}
}

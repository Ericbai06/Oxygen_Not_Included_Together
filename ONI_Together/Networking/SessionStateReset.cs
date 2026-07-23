using ONI_Together.Misc.World;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Animation;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Tools.Build;
using ONI_Together.Networking.Packets.DLC;
using ONI_Together.Networking.Packets.DLC.Aquatic;
using ONI_Together.Networking.Packets.DLC.Frosty;
using ONI_Together.Networking.Packets.DLC.SpacedOut;
using ONI_Together.Networking.Packets.DuplicantActions;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Patches.Bionics;
using ONI_Together.Patches.DLC.Aquatic;
using ONI_Together.Patches.DLC.Bionic;
using ONI_Together.Patches.DLC.Common;
using ONI_Together.Patches.DLC.Cosmetics;
using ONI_Together.Patches.DLC.Frosty;
using ONI_Together.Patches.DLC.Prehistoric;
using ONI_Together.Patches.DLC.SpacedOut;
using ONI_Together.Patches.Duplicant;
using ONI_Together.UI;

namespace ONI_Together.Networking
{
	public static class SessionStateReset
	{
		internal static bool ResetPresentationForBaseline(long hostTick, long generation)
		{
			if (!PresentationTickClock.ResetCorrectionForBaseline(hostTick, generation))
				return false;
			AnimSyncCoordinator.ResetSessionState();
			AnimSyncBatchPacket.ResetSessionState();
			DuplicantStateSender.ResetSessionState();
			DuplicantPresentationBatchPacket.ResetSessionState();
			CursorManager.ResetSessionState();
			PlayerCursorPacket.ResetSessionState();
			RemoteMotionPresenter.ResetSessionState();
			ToggleEffectPacket.ResetSessionState();
			ConduitContentsPacket.ResetClientRevisionState();
			LogicStatePacket.ResetClientRevisionState();
			ResearchSyncCoordinator.ResetClientForBaseline(generation);
			BuildingConfigPacket.ResetClientRevisionState();
			WorldCyclePacket.ClearState();
			PlayAnimPacket.ClearState();
			NetworkingComponent.ResetPresentationFlushGate();
			return true;
		}

		public static void Reset()
		{
			ResetCore();
			ResetAquatic();
			ResetPrehistoric();
			ResetBionic();
			ResetSpacedOut();
			ResetFrosty();
			PoiTechSync.ResetSessionState();
			CosmeticsSyncGuard.ResetSessionState();
		}

		private static void ResetCore()
		{
			ChatScreen.ResetSessionState();
			GroundItemPickedUpPacket.ClearPending();
			StorageItemPacket.ClearPending();
			PresentationTickClock.ResetSessionState();
			NetworkingComponent.ResetPresentationFlushGate();
			WorldStateSyncer.SetAuthoritativeRepairSuppressed(false);
			WorldStateSyncer.SetWorldScanPaused(false);
#if DEBUG
				ONI_Together.DebugTools.SoakTickBarrier.ResetSessionState();
				ONI_Together.Networking.Packets.World.SoakHashDomainKeyframeTracker.Reset();
#endif
				ReadyManager.ResetSessionState();
				WorldDataRequestPacket.ResetSessionState();
			PacketHandler.ResetSessionState();
			AnimSyncCoordinator.ResetSessionState();
			AnimSyncBatchPacket.ResetSessionState();
			DuplicantStateSender.ResetSessionState();
			DuplicantPresentationBatchPacket.ResetSessionState();
			CursorManager.ResetSessionState();
			PlayerCursorPacket.ResetSessionState();
			HostBroadcastPacket.ResetSessionState();
			ONI_Together.Networking.Transport.Lan.RiptideFrameCodec.ResetSessionState();
			PacketSender.ResetSessionState();
			ONI_Together.DebugTools.SyncStats.ResetNativeTransport();
			ProtocolRejectionBarrier.Reset();
			GameServerHardSync.ResetSessionState();
			InstantiationBatcher.ResetSessionState();
			WorldUpdateBatcher.ResetSessionState();
			ColonyDiagnostic_Patches.ResetSessionState();
			SaveChunkAssembler.ResetSessionState();
			SaveFileTransferManager.ResetSessionState();
			TcpTransferStartPacket.CancelActiveDownload();
			SyncProgressPacket.ResetSessionState();
			BuildingConfigPacket.ResetSessionState();
			ConduitFlowSyncer.ResetSessionState();
			LogicStateSyncer.ResetSessionState();
			ResearchSyncCoordinator.ResetSessionState();
			DuplicantChoreBroadcaster.ResetSessionState();
			StatusBroadcaster.ResetSessionState();
			RemoteMotionPresenter.ResetSessionState();
			SkillResumeSync.ResetSessionState();
			EffectsPatch.ResetSessionState();
			ToggleEffectPacket.ResetSessionState();
			PrioritizeStatePacket.ResetSessionState();
			DuplicantPriorityPacket.ResetSessionState();
			NetworkIdentity.ResetSessionState();
			AuthoritativeBuildExecutor.Reset();
			BuildCommitApplier.Reset();
			BuildLifecycleRegistry.Clear();
			PlantGrowthSyncer.ResetSessionState();
		}

		private static void ResetAquatic()
		{
			AquaticSync.ResetSessionState();
			UnderwaterVentSync.ResetSessionState();
			UnderwaterVentStatePacket.ResetSessionState();
			MinnowPoiSync.ResetSessionState();
			SeaTreeBranchSync.ResetSessionState();
			SeaTreeBranchStatePacket.ResetSessionState();
			OxyCoralSync.ResetSessionState();
			OxyCoralBubblePacket.ResetSessionState();
		}

		private static void ResetPrehistoric()
		{
			LargeImpactorOutcomePacket.ResetSessionState();
			LargeImpactorSync.ResetSessionState();
			CarnivorousPlantSync.ResetSessionState();
			VineBranchSync.ResetSessionState();
			FossilMarkerSync.ResetSessionState();
		}

		private static void ResetBionic()
		{
			BionicSyncGuard.ResetSessionState();
			ExplorerGeyserRevealSync.ResetSessionState();
			RemoteWorkerDockSync.ResetSessionState();
			RemoteWorkerDockSelectionSync.ResetSessionState();
			BionicExplosionSync.ResetSessionState();
			BionicRuntimeSync.ResetSessionState();
		}

		private static void ResetSpacedOut()
		{
			CryoTankSync.ResetSessionState();
			SpacedOutSyncGuard.ResetSessionState();
			HighEnergyParticleSync.ResetSessionState();
			RailGunPayloadSync.ResetSessionState();
			PlantMutationSync.ResetSessionState();
			SetLockerSync.ResetSessionState();
			CritterTrapGasSync.ResetSessionState();
			CritterTrapGasPacket.ResetSessionState();
			ClusterDiscoverySync.ResetSessionState();
		}

		private static void ResetFrosty()
		{
			FrostySyncGuard.ResetSessionState();
			MiniCometSync.ResetSessionState();
			IceKettleSync.ResetSessionState();
			SpaceTreeBranchSync.ResetSessionState();
			SpaceTreeSeededCometSync.ResetSessionState();
			SpaceTreeImpactPacket.ResetSessionState();
		}
	}
}

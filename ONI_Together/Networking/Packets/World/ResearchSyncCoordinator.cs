using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.States;
using System;
using System.Collections.Generic;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
	internal static partial class ResearchSyncCoordinator
	{
		private sealed class RequestCursor
		{
			internal long ConnectionGeneration;
			internal ulong LastRequestId;
		}

		private static readonly Dictionary<ulong, RequestCursor> RequestCursors = new();
		private static Research _trackedResearch;
		private static ResearchStatePacket _lastHostState;
		private static long _hostRevision;
		private static long _appliedRevision;
		private static ulong _nextClientRequestId;
		private static ulong _trackedHostId;
		private static long _trackedSnapshotGeneration;
		private static bool _applyingAuthoritativeState;

		internal static bool IsApplyingAuthoritativeState => _applyingAuthoritativeState;
		internal static long AppliedResearchRevision => _appliedRevision;

		internal static bool TrySendRequest(string techId)
		{
			if (!MultiplayerSession.IsClient || !TrackCurrentResearch() || _appliedRevision <= 0
			    || !IsKnownTech(techId) || _nextClientRequestId == ulong.MaxValue)
				return false;
			ulong requestId = _nextClientRequestId + 1;
			var packet = new ResearchRequestPacket
			{
				ClientRequestId = requestId,
				BaseResearchRevision = _appliedRevision,
				TechId = techId
			};
			if (!PacketSender.SendToHost(packet, PacketSendMode.ReliableImmediate))
				return false;
			_nextClientRequestId = requestId;
			return true;
		}

		internal static void HandleRequest(ResearchRequestPacket packet)
		{
			if (!MultiplayerSession.IsHost || !TrackCurrentResearch()
			    || !TryAcceptRequestStream(packet.ClientRequestId, out ulong senderId))
				return;
			if (!EnsureHostState() ||
			    !ResearchSyncProtocol.IsCurrentBase(packet.BaseResearchRevision, _hostRevision))
			{
				DebugConsole.LogWarning(
					$"[ResearchRequest] Rejected stale base {packet.BaseResearchRevision}, host={_hostRevision}");
				SendCurrentSnapshot(senderId);
				return;
			}
			Tech tech = Db.Get().Techs.TryGet(packet.TechId);
			TechInstance instance = tech == null ? null : Research.Instance.Get(tech);
			if (instance == null || instance.IsComplete())
			{
				SendCurrentSnapshot(senderId);
				return;
			}
			try
			{
				Research.Instance.SetActiveResearch(tech, true);
			}
			catch (Exception exception)
			{
				DebugConsole.LogWarning($"[ResearchRequest] Host mutation failed: {exception}");
				SendCurrentSnapshot(senderId);
			}
		}

		internal static void PublishHostState()
		{
			if (!MultiplayerSession.IsHostInSession || _applyingAuthoritativeState
			    || !TrackCurrentResearch() || !TryRefreshHostState(out ResearchStatePacket state, out _))
				return;
			PacketSender.SendToAllClients(state, PacketSendMode.ReliableImmediate);
#if DEBUG
			LogHostResearchEvidence(state.ResearchRevision, state,
				"sync:ca9c21c980551435f6399e0f");
#endif
		}

		internal static void PublishHostCompletion(string techId)
		{
			if (!MultiplayerSession.IsHostInSession || _applyingAuthoritativeState
			    || !TrackCurrentResearch() || !IsKnownTech(techId)
			    || !TryRefreshHostState(out _, out _))
				return;
			var packet = new ResearchCompletePacket
			{
				ResearchRevision = _hostRevision,
				TechId = techId
			};
			PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
#if DEBUG
			LogHostResearchEvidence(packet.ResearchRevision, packet,
				"sync:80d82b4d2b4caebd505d0434");
#endif
		}

		internal static void PublishProgressSample()
		{
			if (!MultiplayerSession.IsHostInSession || !TrackCurrentResearch()
			    || !TryCaptureState(1, out ResearchStatePacket captured))
				return;
			bool contentChanged = !ContentEquals(_lastHostState, captured);
			bool progressOnly = !contentChanged || OnlyActiveProgressChanged(
				_lastHostState, captured, captured.ActiveTechId);
			if (!MetadataEquals(_lastHostState, captured) || !progressOnly)
			{
				PublishCapturedState(captured, contentChanged, PacketSendMode.ReliableImmediate);
				return;
			}
			if (!TrySetCurrentRevision(captured, contentChanged)
			    || !TryGetActiveProgress(captured, out ResearchProgressData progress))
				return;
			var packet = new ResearchProgressPacket
			{
				ResearchRevision = _hostRevision,
				Progress = progress
			};
			PacketSender.SendToAllClients(packet, PacketSendMode.Unreliable);
#if DEBUG
			LogHostResearchEvidence(packet.ResearchRevision, packet,
				"sync:3f710aab91aa65a670ebc193");
#endif
		}

		internal static void PublishRepairSnapshot()
		{
			if (!MultiplayerSession.IsHostInSession || !TrackCurrentResearch()
			    || !TryRefreshHostState(out ResearchStatePacket state, out _))
				return;
			PacketSender.SendToAllClients(state, PacketSendMode.Reliable);
		}

		internal static bool SendBaselineSnapshot(ulong playerId)
		{
			if (!MultiplayerSession.IsHost || playerId == 0 || !TrackCurrentResearch()
			    || !TryRefreshHostState(out ResearchStatePacket state, out _))
				return false;
			RequestCursors.Remove(playerId);
			return PacketSender.SendToPlayer(
				playerId, state, PacketSendMode.ReliableImmediate);
		}

		private static bool TryRefreshHostState(
			out ResearchStatePacket state, out bool contentChanged)
		{
			state = null;
			contentChanged = false;
			if (!TryCaptureState(1, out ResearchStatePacket captured))
				return false;
			contentChanged = !ContentEquals(_lastHostState, captured);
			if (!TrySetCurrentRevision(captured, contentChanged))
				return false;
			state = captured;
			return true;
		}

		private static bool TrySetCurrentRevision(
			ResearchStatePacket captured, bool contentChanged)
		{
			if (_hostRevision == 0)
				_hostRevision = 1;
			else if (contentChanged && !TryAdvanceHostRevision())
				return false;
			captured.ResearchRevision = _hostRevision;
			_lastHostState = captured;
			return true;
		}

		private static bool TryAdvanceHostRevision()
		{
			if (_hostRevision == long.MaxValue)
			{
				DebugConsole.LogError("[ResearchSync] Research revision exhausted", false);
				return false;
			}
			_hostRevision++;
			return true;
		}

		private static void PublishCapturedState(
			ResearchStatePacket captured, bool changed, PacketSendMode mode)
		{
			if (!TrySetCurrentRevision(captured, changed))
				return;
			PacketSender.SendToAllClients(captured, mode);
#if DEBUG
			LogHostResearchEvidence(captured.ResearchRevision, captured,
				"sync:e12ebc359bfd1f5d92fc26f0");
#endif
		}

#if DEBUG
		private static void LogResearchEvidence(
			string phase, long revision, IPacket packet, string entryId)
		{
			if (!TryCreateResearchEvidence(packet, revision,
			    out ResearchTarget target, out ResearchState state))
				return;
			IntegrationScenarioEvidenceCore.Log(TypedEvidenceRuntimeContext.Create(
				"research", phase, revision, target, state, entryId));
		}

		private static void LogResearchEvidence(
			string phase, long revision, bool applied, IPacket packet)
			=> LogResearchEvidence(phase, revision, packet, ResearchPacketEntryId(packet));

		private static string ResearchPacketEntryId(IPacket packet)
		{
			return packet switch
			{
				ResearchStatePacket _ => "sync:9798fa456138883c55b27497",
				ResearchCompletePacket _ => "sync:a3506c035192240c88331cf7",
				ResearchProgressPacket _ => "sync:67909eef61960ac0c8a27023",
				_ => throw new ArgumentOutOfRangeException(nameof(packet)),
			};
		}

		private static void LogHostResearchEvidence(
			long revision, IPacket packet, string entryId)
		{
			LogResearchEvidence("host-submit", revision, packet, entryId);
			LogResearchEvidence("final-state", revision, packet, entryId);
		}

		private static bool TryCreateResearchEvidence(
			IPacket packet, long revision, out ResearchTarget target, out ResearchState state)
		{
			target = null;
			state = null;
			string techId;
			bool completed;
			double progress;
			if (packet is ResearchCompletePacket complete)
			{
				techId = complete.TechId;
				completed = true;
				progress = 1d;
			}
			else if (packet is ResearchProgressPacket sample)
			{
				techId = sample.Progress?.TechId;
				completed = false;
				progress = TotalProgress(sample.Progress);
			}
			else if (packet is ResearchStatePacket snapshot &&
			         TrySelectSnapshotTech(snapshot, out techId,
				         out completed, out progress))
			{
			}
			else
			{
				return false;
			}
			if (string.IsNullOrEmpty(techId))
				return false;
			target = new ResearchTarget { TechId = techId };
			state = new ResearchState
			{
				Revision = revision,
				Completed = completed,
				Progress = progress,
			};
			return true;
		}

		private static bool TrySelectSnapshotTech(
			ResearchStatePacket packet, out string techId,
			out bool completed, out double progress)
		{
			techId = packet.ActiveTechId;
			if (string.IsNullOrEmpty(techId) && packet.QueuedTechIds.Count > 0)
				techId = packet.QueuedTechIds[0];
			if (string.IsNullOrEmpty(techId) && packet.ProgressEntries.Count > 0)
				techId = packet.ProgressEntries[0].TechId;
			if (string.IsNullOrEmpty(techId) && packet.UnlockedTechIds.Count > 0)
				techId = packet.UnlockedTechIds[0];
			if (string.IsNullOrEmpty(techId))
			{
				completed = false;
				progress = 0d;
				return false;
			}
			completed = packet.UnlockedTechIds.Contains(techId);
			progress = 0d;
			foreach (ResearchProgressData entry in packet.ProgressEntries)
				if (entry.TechId == techId)
				{
					progress = TotalProgress(entry);
					break;
				}
			return true;
		}

		private static double TotalProgress(ResearchProgressData progress)
		{
			if (progress?.Points == null)
				return 0d;
			double total = 0d;
			foreach (ResearchPointData point in progress.Points)
				total += point.Points;
			return total;
		}
#endif

		private static bool EnsureHostState()
			=> _hostRevision > 0 || TryRefreshHostState(out _, out _);

		private static void SendCurrentSnapshot(ulong playerId)
		{
			if (TryRefreshHostState(out ResearchStatePacket state, out _))
				PacketSender.SendToPlayer(playerId, state, PacketSendMode.ReliableImmediate);
		}

		private static bool TryAcceptRequestStream(ulong requestId, out ulong senderId)
		{
			DispatchContext context = PacketHandler.CurrentContext;
			senderId = context.SenderId;
			if (context.SenderIsHost || senderId == 0 || context.ConnectionGeneration <= 0
			    || !MultiplayerSession.ConnectedPlayers.TryGetValue(senderId, out MultiplayerPlayer player)
			    || player.Connection == null || !player.ProtocolVerified
			    || !SyncBarrier.IsExactReady(player.readyState)
			    || player.ConnectionGeneration != context.ConnectionGeneration)
				return false;
			if (!RequestCursors.TryGetValue(senderId, out RequestCursor cursor)
			    || cursor.ConnectionGeneration != context.ConnectionGeneration)
			{
				cursor = new RequestCursor { ConnectionGeneration = context.ConnectionGeneration };
				RequestCursors[senderId] = cursor;
			}
			if (!ResearchSyncProtocol.IsStrictNextRequest(requestId, cursor.LastRequestId))
				return false;
			cursor.LastRequestId = requestId;
			return true;
		}

		private static bool TrackCurrentResearch()
		{
			Research research = Research.Instance;
			if (research == null)
				return false;
			ulong hostId = MultiplayerSession.InSession ? MultiplayerSession.HostUserID : 0;
			long generation = MultiplayerSession.IsClient ? ReadyManager.ClientSnapshotGeneration : 0;
			if (ReferenceEquals(research, _trackedResearch) && hostId == _trackedHostId
			    && generation == _trackedSnapshotGeneration)
				return true;
			ResetTrackedState(research, hostId, generation);
			return true;
		}

		private static void ResetTrackedState(Research research, ulong hostId, long generation)
		{
			_trackedResearch = research;
			_trackedHostId = hostId;
			_trackedSnapshotGeneration = generation;
			_lastHostState = null;
			_hostRevision = 0;
			_appliedRevision = 0;
			_nextClientRequestId = 0;
			RequestCursors.Clear();
			_applyingAuthoritativeState = false;
		}

		internal static void ResetClientForBaseline(long snapshotGeneration)
		{
			_appliedRevision = 0;
			_nextClientRequestId = 0;
			_trackedSnapshotGeneration = snapshotGeneration;
			_applyingAuthoritativeState = false;
		}

		internal static void ResetSessionState()
		{
			ResetTrackedState(null, 0, 0);
		}

		private static bool IsKnownTech(string techId)
			=> !string.IsNullOrEmpty(techId) && techId.Length <= ResearchSyncProtocol.MaxTechIdLength
			   && Db.Get()?.Techs?.TryGet(techId) != null;
	}
}

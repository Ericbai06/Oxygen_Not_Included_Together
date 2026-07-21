using System;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.States;
using UnityEngine;

namespace ONI_Together.Networking
{
	internal sealed class WorldBaselineProgressLease
	{
		private readonly long _generation;
		private readonly float _idleTimeoutSeconds;
		private readonly float _absoluteDeadline;
		private float _idleDeadline;
		private int _nextChunkIndex;
		private int _totalChunks;

		internal WorldBaselineProgressLease(
			long generation, float startedAt, (float idle, float absolute) timeouts)
		{
			if (generation <= 0 || !IsFinite(startedAt)
			    || !IsPositiveFinite(timeouts.idle)
			    || !IsPositiveFinite(timeouts.absolute))
				throw new ArgumentOutOfRangeException();
			_generation = generation;
			_idleTimeoutSeconds = timeouts.idle;
			_idleDeadline = startedAt + timeouts.idle;
			_absoluteDeadline = startedAt + timeouts.absolute;
		}

		internal bool TryAdvance(
			long generation, (int index, int total) chunk, float now)
		{
			if (generation != _generation || chunk.index != _nextChunkIndex
			    || chunk.total <= 0 || chunk.total > Packets.World.WorldDataPacket.MaxChunkCount
			    || _totalChunks != 0 && chunk.total != _totalChunks
			    || !IsFinite(now) || IsTimedOut(now))
				return false;
			_totalChunks = chunk.total;
			_nextChunkIndex++;
			_idleDeadline = Math.Min(_absoluteDeadline, now + _idleTimeoutSeconds);
			return true;
		}

		internal bool IsTimedOut(float now)
			=> !IsFinite(now) || now >= _idleDeadline || now >= _absoluteDeadline;

		private static bool IsPositiveFinite(float value)
			=> value > 0f && IsFinite(value);

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}

	internal sealed class ReadyAcceptanceProgressLease
	{
		private readonly long _generation;
		private readonly float _idleTimeoutSeconds;
		private float _idleDeadline;

		internal ReadyAcceptanceProgressLease(
			long generation, float startedAt, float idleTimeoutSeconds)
		{
			if (generation <= 0 || !IsFinite(startedAt)
			    || idleTimeoutSeconds <= 0f || !IsFinite(idleTimeoutSeconds))
				throw new ArgumentOutOfRangeException();
			_generation = generation;
			_idleTimeoutSeconds = idleTimeoutSeconds;
			_idleDeadline = startedAt + idleTimeoutSeconds;
		}

		internal bool TryAdvance(long generation, float now)
		{
			if (generation != _generation || !IsFinite(now) || IsTimedOut(now))
				return false;
			_idleDeadline = now + _idleTimeoutSeconds;
			return true;
		}

		internal bool IsTimedOut(float now)
			=> !IsFinite(now) || now >= _idleDeadline;

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}

	public static partial class GameClient
	{
		internal const float BaselineAbsoluteTimeoutSeconds =
			ReadyManager.LoadingAbsoluteLeaseSeconds;

		private enum WorldLoadPhase
		{
			None,
			LoadingApproval,
			WorldBaseline,
			ReadyAcceptance
		}

		private static WorldLoadPhase _worldLoadPhase;
		private static ulong _worldLoadPhaseToken;
		private static long _worldLoadPhaseGeneration;
		private static float _worldLoadPhaseDeadline;
		private static float _nextWorldLoadRetryAt;
		private static int _worldLoadPhaseRetries;
		private static WorldBaselineProgressLease _worldBaselineProgressLease;
		private static ReadyReplayAssembly _readyReplayAssembly;

		internal static bool ShouldTerminateConnectionValidation(bool inMenu) => !inMenu;

		internal static bool ShouldRetryReconnectStartFailure(bool inGame, int attempt)
			=> inGame && attempt >= 0 && attempt < MAX_RECONNECT_ATTEMPTS;

		internal static bool ShouldFailWorldLoadRetryBudget(
			bool readyAcceptance, int attempts)
			=> !readyAcceptance && attempts >= MAX_RECONNECT_ATTEMPTS;

		internal static void FailWorldLoadHandshake(string message)
			=> FailConnectionValidation(STRINGS.UI.PROTOCOL.VALIDATION.TITLE, message);

		internal static void BeginLoadingApprovalWait(ulong token, long generation)
			=> BeginWorldLoadPhase(WorldLoadPhase.LoadingApproval, token, generation);

		internal static void EndLoadingApprovalWait(ulong token, long generation)
			=> EndWorldLoadPhase(WorldLoadPhase.LoadingApproval, token, generation);

		internal static void BeginWorldBaselineWait(ulong token, long generation)
			=> BeginWorldLoadPhase(WorldLoadPhase.WorldBaseline, token, generation);

		internal static void EndWorldBaselineWait(ulong token, long generation)
			=> EndWorldLoadPhase(WorldLoadPhase.WorldBaseline, token, generation);

		internal static bool RecordWorldBaselineProgress(
			long generation, int chunkIndex, int totalChunks)
			=> _worldLoadPhase == WorldLoadPhase.WorldBaseline
			   && _worldBaselineProgressLease != null
			   && _worldBaselineProgressLease.TryAdvance(
				   generation, (chunkIndex, totalChunks), Time.unscaledTime);

		internal static void BeginReadyAcceptanceWait(ulong token, long generation)
			=> BeginWorldLoadPhase(WorldLoadPhase.ReadyAcceptance, token, generation);

		internal static bool TryAcceptReadyReplayBatch(
			ReadyReplayBatchHeader header,
			IReadOnlyList<byte[]> frames,
			DispatchContext context)
		{
			if (!CanAcceptReadyReplay(context))
				return false;
			ReadyReplayAssemblyResult result = _readyReplayAssembly.AcceptBatch(
				header, frames, (context, Time.unscaledTime));
			return FinishReadyReplayReceive(result, context);
		}

		internal static bool TryAcceptReadyReplayCommit(
			ReadyReplayProof proof, DispatchContext context)
		{
			if (!CanAcceptReadyReplay(context))
				return false;
			ReadyReplayAssemblyResult result = _readyReplayAssembly.AcceptCommit(
				proof, context, Time.unscaledTime);
			return FinishReadyReplayReceive(result, context);
		}

		private static bool CanAcceptReadyReplay(DispatchContext context)
			=> _worldLoadPhase == WorldLoadPhase.ReadyAcceptance
			   && _readyReplayAssembly != null && context.SenderIsHost;

		private static bool FinishReadyReplayReceive(
			ReadyReplayAssemblyResult result, DispatchContext context)
		{
			if (result is ReadyReplayAssemblyResult.Pending
			    or ReadyReplayAssemblyResult.Duplicate)
				return true;
			return result == ReadyReplayAssemblyResult.Ready
			       && ApplyReadyReplay(context);
		}

		private static bool ApplyReadyReplay(DispatchContext context)
		{
			ReadyReplayAssembly assembly = _readyReplayAssembly;
			if (assembly == null || !assembly.TryBeginApply(out ReadyReplayApply apply))
				return false;
			bool succeeded = false;
			try
			{
				succeeded = ApplyReadyReplayFrames(apply, context)
				            && PacketSender.SendToHost(
					            ReadyReplayAppliedPacket.Create(apply.Proof),
					            PacketSendMode.ReliableImmediate);
				return succeeded;
			}
			finally
			{
				assembly.CompleteApply(succeeded);
			}
		}

		private static bool ApplyReadyReplayFrames(
			ReadyReplayApply apply, DispatchContext context)
		{
			foreach (byte[][] batch in apply.Batches)
				foreach (byte[] frame in batch)
					if (!PacketHandler.TryHandleIncoming(frame, context))
						return false;
			return true;
		}

		internal static void CancelWorldLoadPhase()
		{
			_worldLoadPhase = WorldLoadPhase.None;
			_worldLoadPhaseToken = 0;
			_worldLoadPhaseGeneration = 0;
			_worldLoadPhaseDeadline = 0;
			_nextWorldLoadRetryAt = 0;
			_worldLoadPhaseRetries = 0;
			_worldBaselineProgressLease = null;
			_readyReplayAssembly = null;
		}

		private static void BeginWorldLoadPhase(
			WorldLoadPhase phase, ulong token, long generation)
		{
			_worldLoadPhase = phase;
			_worldLoadPhaseToken = token;
			_worldLoadPhaseGeneration = generation;
			_worldLoadPhaseRetries = 0;
			_readyReplayAssembly = null;
			float now = Time.unscaledTime;
			int timeout = Math.Max(10, Configuration.Instance.Client.TimeoutSeconds);
			_worldLoadPhaseDeadline = now + timeout;
			_nextWorldLoadRetryAt = now + RECONNECT_BASE_DELAY;
			if (phase == WorldLoadPhase.WorldBaseline)
			{
				_worldBaselineProgressLease = new WorldBaselineProgressLease(
					generation, now, (timeout, BaselineAbsoluteTimeoutSeconds));
			}
			else if (phase == WorldLoadPhase.ReadyAcceptance)
			{
				_readyReplayAssembly = new ReadyReplayAssembly(
					token,
					generation,
					new ReadyReplayAssemblyLimits
					{
						StartedAt = now,
						IdleSeconds = timeout,
						AbsoluteSeconds = ReadyManager.LoadingAbsoluteLeaseSeconds,
						MaxBufferedBytes = ReliableSyncBacklog.MaxBytesPerClient,
					});
			}
		}

		private static void EndWorldLoadPhase(
			WorldLoadPhase phase, ulong token, long generation)
		{
			if (_worldLoadPhase == phase
			    && _worldLoadPhaseToken == token
			    && _worldLoadPhaseGeneration == generation)
				CancelWorldLoadPhase();
		}

		private static void UpdateWorldLoadPhase()
		{
			if (_worldLoadPhase == WorldLoadPhase.None)
				return;
			float now = Time.unscaledTime;
			if (WorldLoadTimedOut(now))
			{
				FailWorldLoadPhase("World-load handshake timed out.");
				return;
			}
			if (ShouldWaitBeforeWorldLoadRetry(now))
				return;
			bool readyAcceptance = _worldLoadPhase == WorldLoadPhase.ReadyAcceptance;
			if (ShouldFailWorldLoadRetryBudget(
				    readyAcceptance, _worldLoadPhaseRetries))
			{
				FailWorldLoadPhase("World-load handshake exhausted its retry budget.");
				return;
			}
			if (readyAcceptance && _worldLoadPhaseRetries >= MAX_RECONNECT_ATTEMPTS)
				_worldLoadPhaseRetries = 0;

			ClientReadyState state = _worldLoadPhase == WorldLoadPhase.LoadingApproval
				? ClientReadyState.Loading
				: ClientReadyState.Ready;
			bool sent = ReadyManager.SendReadyStatusPacket(state);
			_worldLoadPhaseRetries++;
			float delay = Mathf.Min(
				RECONNECT_BASE_DELAY * Mathf.Pow(2, _worldLoadPhaseRetries), 8f);
			_nextWorldLoadRetryAt = now + delay;
			if (!sent)
				DebugConsole.LogWarning($"[GameClient] Retry send failed for {_worldLoadPhase}");
		}

		private static bool WorldLoadTimedOut(float now)
			=> _worldLoadPhase switch
			{
				WorldLoadPhase.WorldBaseline => _worldBaselineProgressLease == null
				                                 || _worldBaselineProgressLease.IsTimedOut(now),
				WorldLoadPhase.ReadyAcceptance => _readyReplayAssembly == null
				                                    || _readyReplayAssembly.IsTimedOut(now),
				_ => now >= _worldLoadPhaseDeadline
			};

		private static bool ShouldWaitBeforeWorldLoadRetry(float now)
			=> _worldLoadPhase == WorldLoadPhase.WorldBaseline
			   || now < _nextWorldLoadRetryAt;

		private static void FailWorldLoadPhase(string message)
		{
			DebugConsole.LogError($"[GameClient] {message}", false);
			FailConnectionValidation(STRINGS.UI.PROTOCOL.VALIDATION.TITLE, message);
		}

		private static void FailConnectionValidation(string reason, string message)
		{
			ReadyManager.TrySendWorldLoadAbort();
			ReadyManager.CancelPendingClientWorldLoad();
			SetState(ClientState.Error);
			Disconnect();
			NetworkConfig.TransportClient.OnReturnToMenu?.Invoke(reason, message);
		}
	}
}

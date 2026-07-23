#if DEBUG
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ONI_Together.Networking;
using ONI_Together.Networking.Transport.Steamworks;

namespace ONI_Together.DebugTools
{
	public sealed class TypedEvidenceContextSnapshot
	{
		public string RunId { get; set; }
		public string DllHash { get; set; }
		public long SessionEpoch { get; set; }
		public long ConnectionGeneration { get; set; }
		public long SnapshotGeneration { get; set; }
	}

	public static class TypedEvidenceRuntimeContext
	{
		private static readonly object Sync = new object();
		private static TypedEvidenceContextSnapshot _current;
		private static long _sequence;
		private static string _dllHash;

		public static void Configure(TypedEvidenceContextSnapshot context)
		{
			if (context == null || string.IsNullOrEmpty(context.RunId)
			    || context.SessionEpoch < 0 || context.ConnectionGeneration < 0
			    || context.SnapshotGeneration < 0)
				throw new ArgumentException("Typed evidence context is incomplete.", nameof(context));
			if (!IsHash(context.DllHash))
				throw new ArgumentException("Typed evidence DLL hash is invalid.", nameof(context));
			lock (Sync)
			{
				_current = Copy(context);
				Interlocked.Exchange(ref _sequence, 0);
			}
		}

		public static void Reset()
		{
			lock (Sync) _current = null;
			Interlocked.Exchange(ref _sequence, 0);
		}

		internal static TypedEvidenceEnvelope Create(
			string scenario, string phase, long revision,
			ITypedEvidenceTarget target, ITypedEvidenceState state,
			string entryId, string revisionDomain = null,
			long? connectionGeneration = null, long? snapshotGeneration = null,
			long actionGeneration = 0, string actionCorrelation = "",
			long actionSequence = 0)
		{
			TypedEvidenceContextSnapshot context = CurrentOrSession();
			bool isHost = MultiplayerSession.IsHost;
			if (!PhaseAllowedForEndpoint(isHost, phase))
				throw new InvalidOperationException(
					$"Typed evidence phase '{phase}' is invalid for the " +
					(isHost ? "host" : "client") + " endpoint.");
			var envelope = new TypedEvidenceEnvelope
			{
				SchemaVersion = 1, RunId = context.RunId, DllHash = context.DllHash,
				Scenario = scenario, EntryId = entryId,
				Role = isHost ? "host" : "client",
				SessionEpoch = context.SessionEpoch,
				ConnectionGeneration = connectionGeneration ?? context.ConnectionGeneration,
				SnapshotGeneration = snapshotGeneration ?? context.SnapshotGeneration,
				Phase = phase, RevisionDomain = revisionDomain ?? scenario,
				Revision = revision, Sequence = Interlocked.Increment(ref _sequence),
				ActionGeneration = actionGeneration,
				ActionCorrelation = actionCorrelation ?? string.Empty,
				ActionSequence = actionSequence,
				Target = target, State = state,
			};
			envelope.StateHash = TypedEvidenceContract.ComputeStateHash(state);
			return envelope;
		}

		internal static string CurrentRunId()
			=> CurrentOrSession().RunId;

		private static bool PhaseAllowedForEndpoint(bool isHost, string phase)
			=> isHost
				? phase is "host-submit" or "final-state" or "post-reconnect-state"
				: phase is "revision-accepted" or "revision-duplicate"
					or "revision-out-of-order" or "client-apply"
					or "client-original-blocked" or "final-state"
					or "post-reconnect-state";

		private static TypedEvidenceContextSnapshot CurrentOrSession()
		{
			lock (Sync)
			{
				if (_current != null) return Copy(_current);
			}
			return DeriveCurrentSession();
		}

		private static TypedEvidenceContextSnapshot DeriveCurrentSession()
		{
			if (!MultiplayerSession.InSession || !MultiplayerSession.HostUserID.IsValid())
				throw new InvalidOperationException("Typed evidence requires an active multiplayer session or explicit context.");
			string runId = SteamLobby.InLobby
				? "steam:" + SteamLobby.CurrentLobby.m_SteamID.ToString(CultureInfo.InvariantCulture)
				: "direct:" + MultiplayerSession.HostUserID.ToString(CultureInfo.InvariantCulture)
				  + ":" + MultiplayerSession.ServerIp + ":" + MultiplayerSession.ServerPort;
			long epoch = StableNonNegativeInt64(runId);
			MultiplayerPlayer peer = MultiplayerSession.LocalPlayer
			                         ?? MultiplayerSession.ConnectedPlayers.Values.FirstOrDefault();
			return new TypedEvidenceContextSnapshot
			{
				RunId = runId, DllHash = CurrentDllHash(), SessionEpoch = epoch,
				ConnectionGeneration = peer?.ConnectionGeneration ?? 0,
				SnapshotGeneration = ReadyManager.ClientSnapshotGeneration,
			};
		}

		private static long StableNonNegativeInt64(string value)
		{
			using (SHA256 sha = SHA256.Create())
			{
				byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
				return BitConverter.ToInt64(bytes, 0) & long.MaxValue;
			}
		}

		private static string CurrentDllHash()
		{
			if (_dllHash != null) return _dllHash;
			string path = Assembly.GetExecutingAssembly().Location;
			using (SHA256 sha = SHA256.Create())
			using (FileStream stream = File.OpenRead(path))
			{
				byte[] digest = sha.ComputeHash(stream);
				var result = new StringBuilder("sha256:", 71);
				foreach (byte value in digest) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
				_dllHash = result.ToString();
				return _dllHash;
			}
		}

		private static TypedEvidenceContextSnapshot Copy(TypedEvidenceContextSnapshot value)
			=> new TypedEvidenceContextSnapshot
			{
				RunId = value.RunId, DllHash = value.DllHash, SessionEpoch = value.SessionEpoch,
				ConnectionGeneration = value.ConnectionGeneration,
				SnapshotGeneration = value.SnapshotGeneration,
			};

		private static bool IsHash(string value)
			=> value != null && value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal)
			   && value.Substring(7).All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
	}
}
#endif

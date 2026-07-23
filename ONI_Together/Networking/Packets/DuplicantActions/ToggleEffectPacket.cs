using System;
using System.IO;
using System.Collections.Generic;
using Klei.AI;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Patches.Duplicant;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.DuplicantActions
{
	internal class ToggleEffectPacket : IPacket, IHostOnlyPacket
	{
		internal const int MaxEffectIdLength = 128;
		internal const float MaxAbsTimeRemaining = 1_000_000_000f;
		private static readonly Dictionary<(int NetId, int EffectHash), ulong> LastRevisions = [];
		public int MinionNetId;
		public ulong Revision;
		public int EffectHash;
		public string EffectId;
		public bool IsAdding;
		public bool ShouldSave;
		public float TimeRemaining;
#if DEBUG
		internal string ScenarioActionProfile = string.Empty;
#endif

		public ToggleEffectPacket() { }

		public ToggleEffectPacket(NetworkIdentity identity, HashedString toRemove)
		{
			MinionNetId = identity?.NetId ?? 0;
			Revision = NetworkIdentityRegistry.NextAuthorityRevision();
			EffectHash = toRemove.hash;
			EffectId = string.Empty;
		}

		public ToggleEffectPacket(NetworkIdentity identity, EffectInstance toAdd)
		{
			MinionNetId = identity?.NetId ?? 0;
			Revision = NetworkIdentityRegistry.NextAuthorityRevision();
			IsAdding = true;
			EffectId = toAdd?.effect?.Id;
			EffectHash = toAdd?.effect?.IdHash.hash ?? 0;
			ShouldSave = toAdd?.shouldSave ?? false;
			TimeRemaining = toAdd?.timeRemaining ?? 0f;
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			MinionNetId = reader.ReadInt32();
			Revision = reader.ReadUInt64();
			EffectHash = reader.ReadInt32();
			EffectId = reader.ReadString();
			IsAdding = reader.ReadBoolean();
			ShouldSave = reader.ReadBoolean();
			TimeRemaining = reader.ReadSingle();
#if DEBUG
			ScenarioActionProfile = reader.ReadString();
#endif
			if (!IsWireValid())
				throw new InvalidDataException("Invalid entity effect packet");
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			if (!IsWireValid())
				throw new InvalidDataException("Invalid entity effect packet");
			writer.Write(MinionNetId);
			writer.Write(Revision);
			writer.Write(EffectHash);
			writer.Write(EffectId ?? string.Empty);
			writer.Write(IsAdding);
			writer.Write(ShouldSave);
			writer.Write(TimeRemaining);
#if DEBUG
			writer.Write(ScenarioActionProfile ?? string.Empty);
#endif
		}

		public void OnDispatched()
		{
#if DEBUG
			if (!string.IsNullOrEmpty(ScenarioActionProfile))
			{
				if (ScenarioActionReceiverGate.TryEnter(ScenarioActionProfile, "effect"))
					EffectActionFlow.ExecuteClient(this);
				return;
			}
			ApplyRuntimePacket();
#else
			ApplyRuntimePacket();
#endif
		}

		internal bool ApplyRuntimePacket()
		{
			using var _ = Profiler.Scope();
			if (!ShouldApply(MultiplayerSession.IsHost, PacketHandler.CurrentContext.SenderIsHost))
				return false;
			bool accepted = AcceptRevision(MinionNetId, EffectHash, Revision);
			if (!accepted)
				return false;
			if (!NetworkIdentityRegistry.TryGet(MinionNetId, out var identity))
			{
				DebugConsole.LogWarning($"Could not find entity {MinionNetId} for effect {EffectId}");
				return false;
			}
			if (!identity.TryGetComponent(out Effects effects))
			{
				DebugConsole.LogWarning($"Could not find Effects on entity {MinionNetId}");
				return false;
			}
			if (IsAdding)
				EffectsPatch.AddEffect(effects, EffectId, ShouldSave, TimeRemaining);
			else
				EffectsPatch.RemoveEffect(effects, new HashedString(EffectHash));
#if DEBUG
			bool active = effects.Get(new HashedString(EffectHash)) != null;
			if (active != IsAdding)
				return false;
#endif
			return true;
		}

#if DEBUG
		internal TypedEvidenceEnvelope CreateEvidence(
			string phase, string entryId)
			=> CreateEvidence(
				phase, (long)Revision, MinionNetId, EffectHash, IsAdding, entryId);

		internal static TypedEvidenceEnvelope CreateEvidence(
			string phase, long revision, int minionNetId,
			int effectHash, bool active, string entryId)
			=> TypedEvidenceRuntimeContext.Create(
				"effect", phase, revision,
				new EffectTarget { MinionNetId = minionNetId },
				new EffectState
				{
					EffectHash = effectHash.ToString(System.Globalization.CultureInfo.InvariantCulture),
					Active = active,
				}, entryId);

		private static string RevisionPhase(int netId, int effectHash, ulong revision)
		{
			var key = (netId, effectHash);
			if (!LastRevisions.TryGetValue(key, out ulong last) || revision > last)
				return "revision-accepted";
			return revision == last ? "revision-duplicate" : "revision-out-of-order";
		}
#endif

		internal bool IsWireValid()
		{
			if (MinionNetId == 0 || Revision == 0 || EffectHash == 0 || !IsValidTime(TimeRemaining))
				return false;
			if (!IsAdding)
				return string.IsNullOrEmpty(EffectId) && TimeRemaining == 0f && !ShouldSave;
			return !string.IsNullOrEmpty(EffectId) && EffectId.Length <= MaxEffectIdLength &&
			       new HashedString(EffectId).hash == EffectHash;
		}

		private static bool IsValidTime(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value) &&
			   Math.Abs(value) <= MaxAbsTimeRemaining;

		internal static bool ShouldApply(bool localIsHost, bool senderIsHost)
			=> !localIsHost && senderIsHost;

		internal static void ResetSessionState() => LastRevisions.Clear();
		internal static bool AcceptRevisionForTests(int netId, int effectHash, ulong revision)
			=> AcceptRevision(netId, effectHash, revision);

		private static bool AcceptRevision(int netId, int effectHash, ulong revision)
		{
			var key = (netId, effectHash);
			if (netId == 0 || effectHash == 0 || revision == 0
			    || LastRevisions.TryGetValue(key, out ulong last) && revision <= last)
				return false;
			LastRevisions[key] = revision;
			return true;
		}
	}
}

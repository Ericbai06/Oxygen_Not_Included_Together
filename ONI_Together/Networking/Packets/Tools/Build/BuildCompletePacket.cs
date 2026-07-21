using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Rendering;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public sealed partial class BuildCompletePacket : IPacket, IHostOnlyPacket
	{
		private const int MaxMaterialTagCount = 64;

		public int Cell;
		public string PrefabID = string.Empty;
		public Orientation Orientation;
		public List<string> MaterialTags = [];
		public float Temperature;
		public string FacadeID = BuildAuthority.DefaultFacade;
		public UtilityConnections UtilityConnectionFlags;
		public ObjectLayer ObjectLayer;
		public int NetId;
		public ulong LifecycleRevision;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			ValidateWire();
			writer.Write(Cell);
			writer.Write(PrefabID);
			writer.Write((int)Orientation);
			writer.Write(Temperature);
			writer.Write(FacadeID);
			writer.Write(MaterialTags.Count);
			foreach (string tag in MaterialTags)
				writer.Write(tag);
			writer.Write((int)UtilityConnectionFlags);
			writer.Write((int)ObjectLayer);
			writer.Write(NetId);
			writer.Write(LifecycleRevision);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			Cell = reader.ReadInt32();
			PrefabID = reader.ReadString();
			Orientation = (Orientation)reader.ReadInt32();
			Temperature = reader.ReadSingle();
			FacadeID = reader.ReadString();
			int count = reader.ReadInt32();
			if (count < 0 || count > MaxMaterialTagCount)
				throw new InvalidDataException("Invalid build material count");
			MaterialTags = new List<string>(count);
			for (int i = 0; i < count; i++)
				MaterialTags.Add(reader.ReadString());
			UtilityConnectionFlags = (UtilityConnections)reader.ReadInt32();
			ObjectLayer = (ObjectLayer)reader.ReadInt32();
			NetId = reader.ReadInt32();
			LifecycleRevision = reader.ReadUInt64();
			ValidateWire();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			if (!IsAuthoritativeInbound())
				return;
			if (!IsLifecycleApplicable())
			{
#if DEBUG
				LogRejectedRevision();
#endif
				return;
			}
			CompletionResult result = TryApplyCompletion();
			if (result == CompletionResult.MissingTarget)
				StorePending(this, Time.realtimeSinceStartup);
		}

		private CompletionResult TryApplyCompletion()
		{
			BuildingDef def = Assets.GetBuildingDef(PrefabID);
			if (IsAlreadyApplied(def))
			{
#if DEBUG
				LogEvidence("revision-duplicate", false);
#endif
				return CompletionResult.Applied;
			}
			if (!TryResolveTarget(def, out GameObject constructable))
				return CompletionResult.MissingTarget;
			constructable.DeleteObject();
			GameObject built = Build(def);
			if (!IsExpectedCompletedBuilding(built, def)
			    || !NetworkIdentityRegistry.TryBindAuthoritativeLifecycle(
				    built, NetId, LifecycleRevision))
			{
				built?.DeleteObject();
				return CompletionResult.Failed;
			}
			ApplyUtilityConnections(built);
#if DEBUG
			LogEvidence("revision-accepted", true);
			LogEvidence("client-apply", true);
			LogEvidence("final-state", true);
#endif
			DebugConsole.Log(
				$"[BuildCompletePacket] Finalized {PrefabID} NetId={NetId} at cell {Cell}");
			return CompletionResult.Applied;
		}

#if DEBUG
		private void LogRejectedRevision()
		{
			ulong current = NetworkIdentityRegistry.GetLastLifecycleRevision(NetId);
			LogEvidence(
				LifecycleRevision == current ? "revision-duplicate" : "revision-out-of-order",
				false);
		}

		private void LogEvidence(string phase, bool applied)
			=> IntegrationScenarioEvidenceCore.Log(
				"building-lifecycle", phase, (long)LifecycleRevision, applied,
				BuildAuthority.EvidenceState(PrefabID, Cell, NetId, LifecycleRevision));
#endif

		private static bool IsAuthoritativeInbound()
			=> !MultiplayerSession.IsHost && PacketHandler.CurrentContext.SenderIsHost;

		private bool IsLifecycleApplicable()
		{
			if (NetId == 0)
				return false;
			ulong current = NetworkIdentityRegistry.GetLastLifecycleRevision(NetId);
			return ShouldApplyLifecycle(
				current, LifecycleRevision,
				NetworkIdentityRegistry.IsLifecycleTombstoned(NetId));
		}

		internal static bool ShouldApplyLifecycle(
			ulong current, ulong incoming, bool tombstoned)
			=> incoming != 0 && incoming >= current
			   && (incoming > current || !tombstoned);

		private bool IsAlreadyApplied(BuildingDef def)
		{
			if (def == null
			    || !NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity))
				return false;
			GameObject gameObject = identity.gameObject;
			return identity.LifecycleRevision == LifecycleRevision
			       && gameObject.GetComponent<BuildingComplete>()?.Def == def
			       && Grid.PosToCell(gameObject) == Cell;
		}

		private bool TryResolveTarget(BuildingDef def, out GameObject target)
		{
			target = null;
			if (def == null || def.ObjectLayer != ObjectLayer
			    || !NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity))
				return false;
			GameObject candidate = identity.gameObject;
			Constructable constructable = candidate.GetComponent<Constructable>();
			Building building = candidate.GetComponent<Building>();
			if (constructable == null || building?.Def != def
			    || Grid.PosToCell(candidate) != Cell)
				return false;
			target = candidate;
			return true;
		}

		private GameObject Build(BuildingDef def)
		{
			List<Tag> tags = MaterialTags.Select(value => new Tag(value)).ToList();
			return def.Build(
				Cell, Orientation, null, tags, Temperature,
				FacadeID, playsound: false, GameClock.Instance.GetTime());
		}

		private bool IsExpectedCompletedBuilding(GameObject built, BuildingDef def)
			=> built != null && !built.IsNullOrDestroyed()
			   && built.GetComponent<BuildingComplete>()?.Def == def
			   && Grid.PosToCell(built) == Cell;

		private void ApplyUtilityConnections(GameObject gameObject)
		{
			if (UtilityConnectionFlags == 0
			    || !gameObject.TryGetComponent(out KAnimGraphTileVisualizer visualizer))
				return;
			visualizer.UpdateConnections(UtilityConnectionFlags);
			visualizer.Refresh();
		}

		private void ValidateWire()
		{
			if (!BuildAuthority.IsWireCell(Cell) || string.IsNullOrEmpty(PrefabID)
			    || !BuildAuthority.IsKnownOrientationValue(Orientation)
			    || MaterialTags == null || MaterialTags.Count == 0
			    || MaterialTags.Count > MaxMaterialTagCount
			    || float.IsNaN(Temperature) || float.IsInfinity(Temperature)
			    || Temperature < 0f || string.IsNullOrEmpty(FacadeID)
			    || ObjectLayer < 0 || ObjectLayer >= global::ObjectLayer.NumLayers
			    || NetId == 0 || LifecycleRevision == 0)
				throw new InvalidDataException("Invalid build completion payload");
		}

		private enum CompletionResult : byte
		{
			Applied,
			MissingTarget,
			Failed
		}
	}
}

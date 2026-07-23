using ONI_Together.Misc;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.States;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using UnityEngine;
using YamlDotNet.Core;

namespace ONI_Together.Networking.Packets.Core
{
	public class PlayerCursorPacket : IPacket, IClientRelayable, ISenderBoundRelay
	{
		internal const int MaxUtilityPathCount = 192;
		private const int MaxPrefabIdLength = 256;
		private static readonly Dictionary<(ulong PlayerId, long Generation), ulong> LastRevisions = [];
		private static readonly Dictionary<ulong, long> LatestGenerations = [];
		public ulong PlayerID;
		public long SenderConnectionGeneration;
		public ulong Revision;
		ulong ISenderBoundRelay.RelaySenderId => PlayerID;
		public Vector3 Position;
		public Color Color;
		public CursorState CursorState;

		// Building visualizer
		public string BuildingPrefabId;
		public Orientation BuildingOrientation = Orientation.Neutral;
		public bool BuildingAllowed;

		// Build area display (The <number> x <numer> display area)
		public bool Dragging = false;
		public Vector3 AreaDownPos;
		public DragTool.Mode DragMode = DragTool.Mode.Box;
		public Vector2 LengthLimit = Vector2.zero;

        // Utility path visualizer
        public bool HasUtilityPath = false;
        public uint[] UtilityPathData;

        // Viewport for targeted sync
        public int ViewMinX, ViewMinY, ViewMaxX, ViewMaxY;

		public void Serialize(BinaryWriter writer)
		{
		    using var _ = Profiler.Scope();
		    ValidateWire();
#if DEBUG
		    if (MultiplayerSession.IsClient && PlayerID == MultiplayerSession.LocalUserID)
		        IntegrationScenarioEvidenceCore.Log(CreateEvidence(
			        "client-original-blocked", (long)Revision,
			        "sync:3aeb24dde3a9e060ff8c1445"));
#endif
		    writer.Write(PlayerID);
		    writer.Write(SenderConnectionGeneration);
		    writer.Write(Revision);
		    writer.Write(Position);
		    writer.Write(Color);

		    ushort flags = 0;
		    flags |= (ushort)((int)CursorState & 0x1F);
		    flags |= (ushort)(((int)BuildingOrientation & 0x7) << 5);
		    flags |= (ushort)(((int)DragMode & 0x7) << 8);

		    if (BuildingAllowed)
		        flags |= 1 << 11;

		    if (Dragging)
		        flags |= 1 << 12;

		    if (HasUtilityPath)
		        flags |= 1 << 13;

		    writer.Write(flags);

		    uint viewMin = ((uint)(ushort)ViewMinX << 16) | (ushort)ViewMinY;
		    uint viewMax = ((uint)(ushort)ViewMaxX << 16) | (ushort)ViewMaxY;

		    writer.Write(viewMin);
		    writer.Write(viewMax);

		    writer.Write(BuildingPrefabId ?? string.Empty);

		    if (Dragging)
		    {
		        writer.Write(AreaDownPos);
		        writer.Write(LengthLimit);
		    }

		    if (HasUtilityPath)
		    {
		        writer.Write(UtilityPathData.Length);
		        for (int i = 0; i < UtilityPathData.Length; i++)
		            writer.Write(UtilityPathData[i]);
		    }
		}

		public void Deserialize(BinaryReader reader)
		{
		    using var _ = Profiler.Scope();

		    PlayerID = reader.ReadUInt64();
		    SenderConnectionGeneration = reader.ReadInt64();
		    if (SenderConnectionGeneration < 0)
		        throw new InvalidDataException("Cursor sender generation cannot be negative");
		    Revision = reader.ReadUInt64();
		    if (Revision == 0)
		        throw new InvalidDataException("Cursor revision must be nonzero");
		    Position = reader.ReadVector3();
		    Color = reader.ReadColor();

		    ushort flags = reader.ReadUInt16();
		    CursorState = (CursorState)(flags & 0x1F);
		    BuildingOrientation = (Orientation)((flags >> 5) & 0x7);
		    DragMode = (DragTool.Mode)((flags >> 8) & 0x7);
		    BuildingAllowed = (flags & (1 << 11)) != 0;
		    Dragging = (flags & (1 << 12)) != 0;
		    HasUtilityPath = (flags & (1 << 13)) != 0;

		    uint viewMin = reader.ReadUInt32();
		    uint viewMax = reader.ReadUInt32();

		    ViewMinX = (short)(viewMin >> 16);
		    ViewMinY = (short)(viewMin & 0xFFFF);

		    ViewMaxX = (short)(viewMax >> 16);
		    ViewMaxY = (short)(viewMax & 0xFFFF);

		    BuildingPrefabId = reader.ReadString();

		    if (Dragging)
		    {
		        AreaDownPos = reader.ReadVector3();
		        LengthLimit = reader.ReadVector2();
		    }

		    if (HasUtilityPath)
		    {
		        int count = reader.ReadInt32();
		        if (count <= 0 || count > MaxUtilityPathCount)
		            throw new InvalidDataException($"Invalid cursor utility path count: {count}");
		        UtilityPathData = new uint[count];
		        for (int i = 0; i < count; i++)
		            UtilityPathData[i] = reader.ReadUInt32();
		    }
		    else
		        UtilityPathData = null;
		    ValidateWire();
		}
		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (PlayerID == MultiplayerSession.LocalUserID)
				return;
			if (!AcceptDispatchRevision())
				return;
			if (!MultiplayerSession.IsConnectedRemotePlayer(PlayerID))
			{
				MultiplayerSession.RemovePlayerCursor(PlayerID);
				return;
			}

			bool applied = false;
			if (MultiplayerSession.TryGetCursorObject(PlayerID, out PlayerCursor cursor))
			{
				cursor.SetState(CursorState);
				cursor.SetColor(Color);
				cursor.SetVisibility(true);
				cursor.StopCoroutine("InterpolateCursorPosition");
				cursor.StartCoroutine(InterpolateCursorPosition(cursor, cursor.transform, Position));
				applied = true;
			}
			else
			{
				if (Utils.IsInGame())
				{
					MultiplayerSession.CreateNewPlayerCursor(PlayerID); // Create a cursor if one doesn't exist.
				}
			}


			// Forward to others if host
			if (MultiplayerSession.IsHost)
			{
				// Update Viewport in Syncer
				if (WorldStateSyncer.Instance != null)
				{
					WorldStateSyncer.Instance.UpdateClientView(PlayerID, ViewMinX, ViewMinY, ViewMaxX, ViewMaxY);
				}
			}
#if DEBUG
			if (MultiplayerSession.IsHost)
				LogHostEvidence();
			else if (applied)
				LogClientEvidence();
#endif
		}

		internal static void ResetSessionState()
		{
			LastRevisions.Clear();
			LatestGenerations.Clear();
		}

		internal static void ForgetPlayer(ulong playerId)
		{
			LatestGenerations.Remove(playerId);
			var keys = new List<(ulong PlayerId, long Generation)>();
			foreach (var key in LastRevisions.Keys)
				if (key.PlayerId == playerId)
					keys.Add(key);
			foreach (var key in keys)
				LastRevisions.Remove(key);
		}

		internal static bool AcceptRevisionForTests(
			ulong playerId, long generation, ulong revision)
			=> AcceptRevision(playerId, generation, revision);

		internal void EnforceUtilityPathBound()
		{
			if (!HasUtilityPath)
				return;
			if (UtilityPathData == null || UtilityPathData.Length == 0)
			{
				HasUtilityPath = false;
				UtilityPathData = null;
				return;
			}
			if (UtilityPathData.Length > MaxUtilityPathCount)
				System.Array.Resize(ref UtilityPathData, MaxUtilityPathCount);
		}

		internal void TrimUtilityPathTo(int count)
		{
			if (!HasUtilityPath || UtilityPathData == null || count >= UtilityPathData.Length)
				return;
			if (count <= 0)
			{
				HasUtilityPath = false;
				UtilityPathData = null;
				return;
			}
			System.Array.Resize(ref UtilityPathData, count);
		}

		private void ValidateUtilityPath()
		{
			if (!HasUtilityPath)
				return;
			if (UtilityPathData == null || UtilityPathData.Length == 0
			    || UtilityPathData.Length > MaxUtilityPathCount)
				throw new InvalidDataException("Invalid cursor utility path");
		}

		private void ValidateWire()
		{
			if (PlayerID == 0 || SenderConnectionGeneration < 0 || Revision == 0)
				throw new InvalidDataException("Invalid cursor sender identity or revision");
			if (!IsFinite(Position) || !IsFinite(Color)
			    || !IsFinite(AreaDownPos) || !IsFinite(LengthLimit))
				throw new InvalidDataException("Cursor geometry must be finite");
			if (!System.Enum.IsDefined(typeof(CursorState), CursorState)
			    || ((int)CursorState & ~0x1F) != 0
			    || !System.Enum.IsDefined(typeof(Orientation), BuildingOrientation)
			    || ((int)BuildingOrientation & ~0x7) != 0
			    || !System.Enum.IsDefined(typeof(DragTool.Mode), DragMode)
			    || ((int)DragMode & ~0x7) != 0)
				throw new InvalidDataException("Invalid cursor presentation enum");
			if (ViewMinX > ViewMaxX || ViewMinY > ViewMaxY
			    || !FitsInt16(ViewMinX) || !FitsInt16(ViewMinY)
			    || !FitsInt16(ViewMaxX) || !FitsInt16(ViewMaxY))
				throw new InvalidDataException("Invalid cursor viewport");
			if ((BuildingPrefabId?.Length ?? 0) > MaxPrefabIdLength)
				throw new InvalidDataException("Cursor building prefab ID is too long");
			ValidateUtilityPath();
		}

		private static bool FitsInt16(int value)
			=> value >= short.MinValue && value <= short.MaxValue;

		private static bool IsFinite(Vector3 value)
			=> IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

		private static bool IsFinite(Vector2 value)
			=> IsFinite(value.x) && IsFinite(value.y);

		private static bool IsFinite(Color value)
			=> IsFinite(value.r) && IsFinite(value.g)
			   && IsFinite(value.b) && IsFinite(value.a);

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);

		private static bool AcceptRevision(ulong playerId, long generation, ulong revision)
		{
			if (playerId == 0 || generation <= 0 || revision == 0)
				return false;
			if (LatestGenerations.TryGetValue(playerId, out long latestGeneration))
			{
				if (generation < latestGeneration)
					return false;
				if (generation > latestGeneration)
					ForgetPlayer(playerId);
			}
			LatestGenerations[playerId] = generation;
			var key = (playerId, generation);
			if (LastRevisions.TryGetValue(key, out ulong last) && revision <= last)
				return false;
			LastRevisions[key] = revision;
			return true;
		}

		private bool AcceptDispatchRevision()
		{
			#if DEBUG
			string phase = RevisionPhase(PlayerID, SenderConnectionGeneration, Revision);
			#endif
			bool accepted = AcceptRevision(PlayerID, SenderConnectionGeneration, Revision);
			#if DEBUG
			if (!MultiplayerSession.IsHost && !accepted)
				LogClientRevisionEvidence(phase);
			#endif
			return accepted;
		}

#if DEBUG
		private static string RevisionPhase(
			ulong playerId, long generation, ulong revision)
		{
			if (LatestGenerations.TryGetValue(playerId, out long latest))
			{
				if (generation < latest) return "revision-out-of-order";
				if (generation > latest) return "revision-accepted";
			}
			var key = (playerId, generation);
			if (!LastRevisions.TryGetValue(key, out ulong last) || revision > last)
				return "revision-accepted";
			return revision == last ? "revision-duplicate" : "revision-out-of-order";
		}

		private TypedEvidenceEnvelope CreateEvidence(
			string phase, long revision, string entryId)
			=> TypedEvidenceRuntimeContext.Create(
				"cursor", phase, revision,
				new CursorTarget
				{
					PlayerId = PlayerID.ToString(System.Globalization.CultureInfo.InvariantCulture),
				},
				new CursorEvidenceState
				{
					ConnectionGeneration = SenderConnectionGeneration,
					WorldPosition = new[] { (double)Position.x, Position.y },
					ViewPosition = new[]
					{
						(ViewMinX + ViewMaxX) / 2d,
						(ViewMinY + ViewMaxY) / 2d,
					},
					DragState = Dragging
						? FormattableString.Invariant(
							$"{DragMode}:{AreaDownPos.x:R},{AreaDownPos.y:R}:{LengthLimit.x:R},{LengthLimit.y:R}")
						: "None",
					BuildState = string.IsNullOrEmpty(BuildingPrefabId)
						? "None"
						: $"{BuildingPrefabId}:{BuildingOrientation}:{BuildingAllowed}",
				}, entryId);

		private void LogHostEvidence()
		{
			long revision = (long)Revision;
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"host-submit", revision, "sync:f60e38b805c1052cff0fec0d"));
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"final-state", revision, "sync:f60e38b805c1052cff0fec0d"));
		}

		private void LogClientEvidence()
		{
			long revision = (long)Revision;
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"revision-accepted", revision, "sync:b3dc3818cae201b355058938"));
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"client-apply", revision, "sync:b3dc3818cae201b355058938"));
			IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				"final-state", revision, "sync:b3dc3818cae201b355058938"));
		}

		private void LogClientRevisionEvidence(string phase)
			=> IntegrationScenarioEvidenceCore.Log(CreateEvidence(
				phase, (long)Revision, "sync:b3dc3818cae201b355058938"));
#endif

		private IEnumerator InterpolateCursorPosition(PlayerCursor cursor, Transform target, Vector3 targetPos)
		{
			using var _ = Profiler.Scope();

			Vector3 start = target.position;
			float duration = CursorManager.SendInterval;
			float elapsed = 0f;

			while (elapsed < duration)
			{
				elapsed += Time.unscaledDeltaTime;
				float t = elapsed / duration;
				target.position = Vector3.Lerp(start, targetPos, t);
				UpdateVisualizers(cursor, target.position);
				yield return null;
			}

			target.position = targetPos;
			UpdateVisualizers(cursor, target.position);
		}

		private void UpdateVisualizers(PlayerCursor cursor, Vector3 position)
		{
			cursor.buildingVisualiser.UpdateVisualizer(CreateBuildingVisualState(position));
			cursor.areaVisualizer.UpdateArea(CreateAreaVisualState());
			cursor.utilityVisualizer.UpdatePath(BuildingPrefabId, UtilityPathData, Color);
		}

		internal PlayerBuildingVisualizer.VisualState CreateBuildingVisualState(Vector3 position)
			=> new()
			{
				BuildingPrefabId = BuildingPrefabId,
				Position = position,
				Orientation = BuildingOrientation,
				Color = Color,
				AllowedToPlace = BuildingAllowed,
			};

		internal PlayerAreaVisualizer.VisualState CreateAreaVisualState()
			=> new()
			{
				Color = Color,
				DownPosition = AreaDownPos,
				CursorPosition = Position,
				Dragging = Dragging,
				DragMode = DragMode,
				LengthLimit = LengthLimit,
			};

	}
}

using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.States;
using Shared.Profiling;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public class CursorManager : MonoBehaviour
	{
		internal struct CursorViewport
		{
			public int CursorCell;
			public int Width;
			public int MinX;
			public int MinY;
			public int MaxX;
			public int MaxY;
		}

		internal static bool TryNormalizeViewport(
			CursorViewport candidate, out CursorViewport normalized)
		{
			normalized = candidate;
			if (!FitsInt16(candidate.MinX) || !FitsInt16(candidate.MinY)
				|| !FitsInt16(candidate.MaxX) || !FitsInt16(candidate.MaxY))
				return false;

			normalized.MinX = System.Math.Min(candidate.MinX, candidate.MaxX);
			normalized.MinY = System.Math.Min(candidate.MinY, candidate.MaxY);
			normalized.MaxX = System.Math.Max(candidate.MinX, candidate.MaxX);
			normalized.MaxY = System.Math.Max(candidate.MinY, candidate.MaxY);
			return true;
		}

		private static bool FitsInt16(int value)
			=> value >= short.MinValue && value <= short.MaxValue;

		public static CursorManager Instance { get; private set; }

		public static float SendInterval = 0.1f;

		private float timeSinceLastSend = 0f;
		private static ulong _nextRevision;

		public Color color;

		public CursorState cursorState = CursorState.NONE;

		private void Awake()
		{
			using var _ = Profiler.Scope();

			if (Instance != null)
			{
				Destroy(this);
				return;
			}

			Instance = this;
			DontDestroyOnLoad(gameObject);
		}

		private void Start()
		{
			using var _ = Profiler.Scope();

			AssignColor();
		}

		public void ResetColor()
		{
			using var _ = Profiler.Scope();

			color = Color.white;
		}

		public void AssignColor()
		{
			using var _ = Profiler.Scope();

			bool useRandom = Configuration.GetClientProperty<bool>("UseRandomPlayerColor");
			if (useRandom)
			{
				color = UnityEngine.Random.ColorHSV(0f, 1f, 0.6f, 1f, 0.8f, 1f);
				DebugConsole.Log("[CursorManager] Setting cursor color to random color " + color.ToString());
			}
			else
			{
				Color32 set_color = Configuration.Instance.CursorColor;
				color = set_color;
				DebugConsole.Log("[CursorManager] Setting cursor color from config to " + set_color.ToString() + " | " + color.ToString());
			}
		}

		private void Update()
		{
			using var _ = Profiler.Scope();

			if (!Utils.IsInGame())
				return;

			if (!MultiplayerSession.InSession || !MultiplayerSession.LocalUserID.IsValid())
				return;

			timeSinceLastSend += Time.unscaledDeltaTime;
			if (timeSinceLastSend >= SendInterval)
			{
				SendCursorPosition();
				timeSinceLastSend = 0f;
			}
		}
		private void SendCursorPosition()
		{
			using var _ = Profiler.Scope();

			Vector3 cursorWorldPos = GetCursorWorldPosition();

			// We do not want to lock cursor sending to a threshold as this updates the cursor position relative to the clients viewport

			// Calculate Viewport
			int minX = 0, minY = 0, maxX = 0, maxY = 0;
			if (Camera.main != null)
			{
				Camera cam = Camera.main;
				// Get corners
				Vector3 bl = cam.ViewportToWorldPoint(new Vector3(0, 0, 0));
				Vector3 tr = cam.ViewportToWorldPoint(new Vector3(1, 1, 0));

				minX = Grid.PosToCell(bl);
				maxX = Grid.PosToCell(tr);
				// Grid.PosToCell returns cell index, not XY.
				// We want XY coordinates to define a rectangle.

				Grid.PosToXY(bl, out int x1, out int y1);
				Grid.PosToXY(tr, out int x2, out int y2);

				minX = x1; minY = y1;
				maxX = x2; maxY = y2;
			}

			var viewportCandidate = new CursorViewport
			{
				CursorCell = Grid.PosToCell(cursorWorldPos),
				Width = Grid.WidthInCells,
				MinX = minX,
				MinY = minY,
				MaxX = maxX,
				MaxY = maxY,
			};
			if (!TryNormalizeViewport(viewportCandidate, out CursorViewport viewport))
				return;

			var interfaceTool = PlayerController.Instance.ActiveTool;
            
			// Building visualizer
            string buildToolPrefabId = string.Empty;
            Orientation buildingOrientation = Orientation.Neutral;
            bool allowedToPlaceBuilding = true;

            // Utility path visualizer
            bool hasUtilityPath = false;
            uint[] utilityPathData = null;
            
			if (interfaceTool is BuildTool buildTool)
			{
				if (buildTool.def != null)
				{
					buildToolPrefabId = buildTool.def.PrefabID;
					buildingOrientation = buildTool.buildingOrientation;
					allowedToPlaceBuilding = buildTool.def.IsValidPlaceLocation(buildTool.visualizer, cursorWorldPos, buildingOrientation, out var _) || buildTool.def.IsValidReplaceLocation(cursorWorldPos, buildingOrientation, buildTool.def.ReplacementLayer, buildTool.def.ObjectLayer);
				}
			}
			else if (interfaceTool is BaseUtilityBuildTool utilityBuildTool)
			{
				if (utilityBuildTool.def != null)
				{
					buildToolPrefabId = utilityBuildTool.def.PrefabID;
					allowedToPlaceBuilding = utilityBuildTool.CheckValidPathPiece(Grid.PosToCell(cursorWorldPos));

					List<BaseUtilityBuildTool.PathNode> viewportPath =
						SelectViewportPath(utilityBuildTool.path, viewport);
					utilityPathData = BuildingUtils.EncodeUtilityPath(viewportPath);
					if (utilityPathData != null && utilityPathData.Length > 0)
						hasUtilityPath = true;
				}
			}

			// Area visualizer
			Vector3 areaDownPos = Vector3.zero;
			bool dragging = false;
			DragTool.Mode dragMode = DragTool.Mode.Box;
			Vector2 lengthLimit = Vector2.zero;

			if (interfaceTool is DragTool dragTool and not BuildTool and not BaseUtilityBuildTool)
			{
                dragging = dragTool.Dragging;

                if (dragging)
				{
					dragMode = dragTool.mode;
					areaDownPos = dragTool.downPos;

					if(Input.GetKey((KeyCode)Global.GetInputManager().GetDefaultController().GetInputForAction(Action.DragStraight))) {
						dragMode = DragTool.Mode.Line;
					}

                    if (dragTool is DisconnectTool disconnectTool)
                    {
                        // Disconnect tool uses Line mode
                        dragMode = DragTool.Mode.Line;
						lengthLimit = new Vector2(2, 2);
                    }
                }
			}

			var packet = new PlayerCursorPacket
			{
				PlayerID = MultiplayerSession.LocalUserID,
				SenderConnectionGeneration = MultiplayerSession.IsHost ? 1 : 0,
				Revision = NextRevision(),
				Position = cursorWorldPos,
				Color = color,
				CursorState = cursorState,
				ViewMinX = viewport.MinX,
				ViewMinY = viewport.MinY,
				ViewMaxX = viewport.MaxX,
				ViewMaxY = viewport.MaxY,
				
				BuildingPrefabId = buildToolPrefabId,
				BuildingOrientation = buildingOrientation,
				BuildingAllowed = allowedToPlaceBuilding,

				Dragging = dragging,
                AreaDownPos = areaDownPos,
				DragMode = dragMode,
				LengthLimit = lengthLimit,

				HasUtilityPath = hasUtilityPath,
				UtilityPathData = utilityPathData
            };

			// Host fans out directly; clients use the validated HostBroadcast relay.
			PacketSender.SendToAllOtherPeers(packet);
		}

		internal static void ResetSessionState() => _nextRevision = 0;

		internal static List<BaseUtilityBuildTool.PathNode> SelectViewportPath(
			List<BaseUtilityBuildTool.PathNode> path, CursorViewport viewport)
		{
			var selected = new List<BaseUtilityBuildTool.PathNode>();
			if (!CanSelectPath(path, viewport))
				return selected;

			int cursorIndex = FindNearestPathIndex(path, viewport);
			int direction = cursorIndex <= (path.Count - 1) / 2 ? 1 : -1;
			for (int index = cursorIndex; index >= 0 && index < path.Count; index += direction)
			{
				int cell = path[index].cell;
				if (!ContainsCell(viewport, cell)
				    || !ContinuesPath(selected, cell, viewport.Width))
					break;
				selected.Add(path[index]);
			}
			return selected;
		}

		private static bool CanSelectPath(
			List<BaseUtilityBuildTool.PathNode> path, CursorViewport viewport)
			=> path != null && path.Count > 0
			   && viewport.CursorCell >= 0 && viewport.Width > 0;

		private static int FindNearestPathIndex(
			List<BaseUtilityBuildTool.PathNode> path, CursorViewport viewport)
		{
			int cursorX = viewport.CursorCell % viewport.Width;
			int cursorY = viewport.CursorCell / viewport.Width;
			int nearestIndex = 0;
			int nearestDistance = int.MaxValue;
			for (int index = 0; index < path.Count; index++)
			{
				int cell = path[index].cell;
				int distance = System.Math.Abs(cell % viewport.Width - cursorX)
				               + System.Math.Abs(cell / viewport.Width - cursorY);
				if (distance >= nearestDistance)
					continue;
				nearestDistance = distance;
				nearestIndex = index;
			}
			return nearestIndex;
		}

		private static bool ContainsCell(CursorViewport viewport, int cell)
		{
			int x = cell % viewport.Width;
			int y = cell / viewport.Width;
			return x >= viewport.MinX && x <= viewport.MaxX
			       && y >= viewport.MinY && y <= viewport.MaxY;
		}

		private static bool ContinuesPath(
			List<BaseUtilityBuildTool.PathNode> selected, int cell, int width)
		{
			if (selected.Count == 0)
				return true;
			int previous = selected[selected.Count - 1].cell;
			return System.Math.Abs(previous % width - cell % width)
			       + System.Math.Abs(previous / width - cell / width) == 1;
		}

		private static ulong NextRevision()
		{
			_nextRevision++;
			if (_nextRevision == 0)
				_nextRevision = 1;
			return _nextRevision;
		}

		

		private Vector3 GetCursorWorldPosition()
		{
			using var _ = Profiler.Scope();

			var camera = GameScreenManager.Instance.GetCamera(GameScreenManager.UIRenderTarget.ScreenSpaceCamera);
			if (camera == null) return Vector3.zero;

			var canvas = GameScreenManager.Instance.ssCameraCanvas?.GetComponent<Canvas>();
			var planeZ = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceCamera ? canvas.planeDistance : 10f; // default fallback

			Vector3 screenPos = Input.mousePosition;
			screenPos.z = planeZ; // match the UI plane

			return camera.ScreenToWorldPoint(screenPos);
		}

	}
}

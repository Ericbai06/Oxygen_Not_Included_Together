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
		public static WorldStateSyncer Instance { get; private set; }

		// Staggered sync - each sync runs every 5s but distributed across frames
		private const float STAGGERED_SYNC_INTERVAL = 1f;
		private float _lastSyncTime;
		private int _syncCycleIndex = 0;

		// Gas/Liquid Sync - adaptive based on FPS
		private float _lastGasSyncTime;
		private const float GAS_SYNC_INTERVAL = 1.5f; // Increased from 0.2s
		private float _effectiveGasInterval = GAS_SYNC_INTERVAL;

		// Grace period - skip syncs for first few seconds after world load
		private bool _initialized = false;
		private float _initializationTime;
		private const float INITIAL_DELAY = 5f;

		// Game info update - runs regardless of client count for lobby browser
		private float _lastGameInfoTime;
		private const float GAME_INFO_INTERVAL = 5f;

		private ushort[] _shadowElements;
		private float[] _shadowMass;
		private float[] _shadowTemperature;
		private byte[] _shadowDiseaseIdx;
		private int[] _shadowDiseaseCount;

		// Rotating background scan - covers off-screen areas
		private const int BG_SCAN_CHUNK_SIZE = 32;
		private const float BACKGROUND_SWEEP_TARGET_SECONDS = 30f;
		private int _bgScanIndex = 0;
		private int _bgScanCellOffset;
		private static bool _authoritativeRepairSuppressed;
		private static bool _worldScanPaused;

		// Pinned areas - always synced regardless of viewport
		private static readonly List<RectInt> _pinnedAreas = new List<RectInt>();

		public static void PinArea(int x, int y, int width, int height)
		{
			_pinnedAreas.Add(new RectInt(x, y, width, height));
		}

		public static void UnpinArea(int x, int y, int width, int height)
		{
			_pinnedAreas.RemoveAll(r => r.x == x && r.y == y && r.width == width && r.height == height);
		}

		public static void ClearPinnedAreas()
		{
			_pinnedAreas.Clear();
		}

		/// <summary>
		/// All the connected players viewports as updated by their Player Cursor Packet
		/// </summary>
		private readonly Dictionary<ulong, RectInt> _clientViewports = new Dictionary<ulong, RectInt>();

		private void Awake()
		{
			using var _ = Profiler.Scope();

			Instance = this;
		}

		public void UpdateClientView(ulong userId, int minX, int minY, int maxX, int maxY)
		{
			using var _ = Profiler.Scope();

			// Update or add
			_clientViewports[userId] = new RectInt(minX, minY, maxX - minX, maxY - minY);
		}

		public void GetClientsViewingCell(int cell, HashSet<ulong> recipients, int margin = 2)
		{
			using var _ = Profiler.Scope();

			recipients.Clear();
			if (!Grid.IsValidCell(cell))
				return;

			Grid.CellToXY(cell, out int x, out int y);
			foreach (var kvp in _clientViewports)
			{
				if (!MultiplayerSession.ConnectedPlayers.TryGetValue(kvp.Key, out var player)
				    || !CanReceiveViewportRuntime(player))
					continue;

				var rect = kvp.Value;
				if (x >= rect.xMin - margin
					&& x < rect.xMax + margin
					&& y >= rect.yMin - margin
					&& y < rect.yMax + margin)
				{
					recipients.Add(kvp.Key);
				}
			}
		}

		internal static bool CanReceiveViewportRuntime(MultiplayerPlayer player)
			=> player?.Connection != null
			   && player.ProtocolVerified
			   && SyncBarrier.IsExactReady(player.readyState);

		public bool IsCellVisibleToAnyClient(int cell, int margin = 2)
		{
			using var _ = Profiler.Scope();

			var recipients = new HashSet<ulong>();
			GetClientsViewingCell(cell, recipients, margin);
			return recipients.Count > 0;
		}

		/// <summary>
		/// Uses the existing _clientViewports list to check if it is visible
		/// </summary>
        public bool IsCellInPlayerViewport(ulong userId, int cell, int margin = 2)
        {
            if (!_clientViewports.TryGetValue(userId, out var rect))
                return false;
            Grid.CellToXY(cell, out int x, out int y);
            return x >= rect.xMin - margin && x < rect.xMax + margin &&
                   y >= rect.yMin - margin && y < rect.yMax + margin;
        }

		/// <summary>
		/// Uses the existing _clientViewports list to check if it is visible
		/// </summary>
        public bool IsCellVisibleToAnyClientViewport(int cell, int margin = 2)
        {
            if (!Grid.IsValidCell(cell)) return false;
            Grid.CellToXY(cell, out int x, out int y);
            foreach (var kvp in _clientViewports)
            {
                if (!MultiplayerSession.ConnectedPlayers.TryGetValue(kvp.Key, out var player)
                    || !CanReceiveViewportRuntime(player))
                    continue;
                var rect = kvp.Value;
                if (x >= rect.xMin - margin && x < rect.xMax + margin &&
                    y >= rect.yMin - margin && y < rect.yMax + margin)
                    return true;
            }
            return false;
        }

        public static bool IsCellInRect(int cell, RectInt rect, int margin = 2)
        {
            Grid.CellToXY(cell, out int x, out int y);
            return x >= rect.xMin - margin && x < rect.xMax + margin &&
                   y >= rect.yMin - margin && y < rect.yMax + margin;
        }

        public static bool TryGetLocalViewport(out RectInt viewport, int margin = 2)
		{
			using var _ = Profiler.Scope();

			viewport = default;
			if (Camera.main == null || Grid.WidthInCells == 0 || Grid.HeightInCells == 0)
				return false;

			Camera cam = Camera.main;
			Vector3 bl = cam.ViewportToWorldPoint(new Vector3(0, 0, 0));
			Vector3 tr = cam.ViewportToWorldPoint(new Vector3(1, 1, 0));
			Grid.PosToXY(bl, out int x1, out int y1);
			Grid.PosToXY(tr, out int x2, out int y2);

			x1 = Mathf.Max(0, x1 - margin);
			y1 = Mathf.Max(0, y1 - margin);
			x2 = Mathf.Min(Grid.WidthInCells, x2 + margin);
			y2 = Mathf.Min(Grid.HeightInCells, y2 + margin);

			viewport = new RectInt(x1, y1, Mathf.Max(0, x2 - x1), Mathf.Max(0, y2 - y1));
			return viewport.width > 0 && viewport.height > 0;
		}

		private void Update()
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost)
				return;

			// Update game info even when no clients connected (for lobby browser)
			// This runs every 5 seconds regardless of client count
			if (Time.unscaledTime - _lastGameInfoTime > GAME_INFO_INTERVAL)
			{
				_lastGameInfoTime = Time.unscaledTime;
				SteamLobby.UpdateGameInfo();
			}

			// Skip other syncs if no clients connected
			if (MultiplayerSession.ConnectedPlayers.Count == 0)
				return;

			// Grace period after world load
			if (!_initialized)
			{
				_initializationTime = Time.unscaledTime;
				_initialized = true;
				return;
			}

			if (Time.unscaledTime - _initializationTime < INITIAL_DELAY)
				return;

			try
			{
				// Adaptive gas sync based on FPS and client count
				_effectiveGasInterval = GAS_SYNC_INTERVAL * GetSyncMultiplier();

				if (ShouldRunWorldScan(_worldScanPaused)
				    && Time.unscaledTime - _lastGasSyncTime > _effectiveGasInterval)
				{
					_lastGasSyncTime = Time.unscaledTime;
					SyncGasLiquid();
				}

				// Staggered syncs - one per second; full snapshots repair lost outcomes.
				if (Time.unscaledTime - _lastSyncTime > STAGGERED_SYNC_INTERVAL)
				{
					_lastSyncTime = Time.unscaledTime;
					switch (_syncCycleIndex++ % 6)
					{
						case 0: SyncDigging(); break;
						case 1: SyncChores(); break;
						case 2: SyncResearchProgress(); break;
						case 3: SyncResearch(); break;
						case 4: SyncPriorities(); break;
						case 5: SteamLobby.UpdateGameInfo(); break;
					}
				}
			}
			catch (System.Exception)
			{
				// Silently ignore - sync may fail on freshly loaded world
			}
		}

		// --- Digging Logic ---

			private void SyncDigging()
		{
			using var _ = Profiler.Scope();

			var sw = System.Diagnostics.Stopwatch.StartNew();
			var digPacket = new DiggingStatePacket();

			try
			{
				foreach (var diggable in global::Components.Diggables.Items)
				{
					if (diggable == null) continue;
					int cell = Grid.PosToCell(diggable);
					if (Grid.IsValidCell(cell))
					{
						digPacket.DigCells.Add(cell);
					}
				}

				PacketSender.SendToAllClients(digPacket, PacketSendMode.Unreliable);

				sw.Stop();
				SyncStats.RecordSync(SyncStats.Digging, digPacket.DigCells.Count, digPacket.DigCells.Count * 4, sw.ElapsedMilliseconds);
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[WorldStateSyncer] Error in SyncDigging: {ex.Message}");
			}
		}

		public void OnDiggingStateReceived(DiggingStatePacket packet)
		{
			using var _ = Profiler.Scope();

			// Reconcile
			// 1. Get all local diggables
			// 2. Remove extra
			// 3. Add missing

			try
			{
				var localDigs = new HashSet<int>();
				var toRemove = new List<Diggable>();

				foreach (var diggable in global::Components.Diggables.Items)
				{
					int cell = Grid.PosToCell(diggable);
					localDigs.Add(cell);
					if (!packet.DigCells.Contains(cell))
					{
						toRemove.Add(diggable);
					}
				}

				// Remove Phantoms
				foreach (var d in toRemove)
				{
					//DebugConsole.Log($"[WorldStateSyncer] Removing phantom dig at {Grid.PosToCell(d)}");
					d.gameObject.DeleteObject();
				}

				// Add Missing
				foreach (var cell in packet.DigCells)
				{
					if (!localDigs.Contains(cell))
					{
						//DebugConsole.Log($"[WorldStateSyncer] Adding missing dig at {cell}");
						// Use ONI's native lifecycle without sending a packet back.
						if (Grid.IsValidCell(cell) && Grid.Solid[cell])
							DiggablePacket.PlaceLocally(cell, animationDelay: 0);
					}
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[WorldStateSyncer] Error in OnDiggingStateReceived: {ex.Message}");
			}
		}

		// --- Chore Logic (Mopping) ---

		private void SyncChores()
		{
			using var _ = Profiler.Scope();

			var sw = System.Diagnostics.Stopwatch.StartNew();
			var chorePacket = new ChoreStatePacket();

			try
			{
				// Use our tracked mop placers
				lock (MopTracker.MopPlacers)
				{
					foreach (var go in MopTracker.MopPlacers)
					{
						if (go == null) continue;
						int cell = Grid.PosToCell(go);
						chorePacket.Chores.Add(new ChoreData { Cell = cell, Type = SyncedChoreType.Mop });
					}
				}

				PacketSender.SendToAllClients(chorePacket, PacketSendMode.Unreliable);

				sw.Stop();
				SyncStats.RecordSync(SyncStats.Chores, chorePacket.Chores.Count, chorePacket.Chores.Count * 5, sw.ElapsedMilliseconds);
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[WorldStateSyncer] Error in SyncChores: {ex}");
			}
		}

		public void OnChoreStateReceived(ChoreStatePacket packet)
		{
			using var _ = Profiler.Scope();

			try
			{
				// Reconcile Mops
				var localMops = new HashSet<int>();
				var toRemove = new List<GameObject>();

				lock (MopTracker.MopPlacers)
				{
					// Identification Phase
					foreach (var go in MopTracker.MopPlacers)
					{
						if (go == null) continue;
						int cell = Grid.PosToCell(go);
						localMops.Add(cell);

						// Check if phantom
						bool existsRemote = false;
						foreach (var c in packet.Chores)
						{
							if (c.Cell == cell && c.Type == SyncedChoreType.Mop)
							{
								existsRemote = true;
								break;
							}
						}

						if (!existsRemote)
						{
							toRemove.Add(go);
						}
					}
				}

				// Removal Phase
				foreach (var go in toRemove)
				{
					go.DeleteObject();
					// MopTracker will update via OnCleanUp patch automatically
				}

				// Addition Phase
				foreach (var c in packet.Chores)
				{
					if (c.Type == SyncedChoreType.Mop && !localMops.Contains(c.Cell))
					{
						// Spawn Mop Placer
						if (Grid.IsValidCell(c.Cell))
						{
							var mopPrefab = Assets.GetPrefab(new Tag("MopPlacer"));
							if (mopPrefab != null)
							{
								GameObject placer = Util.KInstantiate(mopPrefab);
								Vector3 position = Grid.CellToPosCBC(c.Cell, MopTool.Instance.visualizerLayer);
								position.z -= 0.15f;
								placer.transform.SetPosition(position);
								placer.SetActive(true);

								// Set standard priority if possible (default 5)
								var prioritizable = placer.GetComponent<Prioritizable>();
								if (prioritizable != null && ToolMenu.Instance != null)
									prioritizable.SetMasterPriority(ToolMenu.Instance.PriorityScreen.GetLastSelectedPriority());
							}
						}
					}
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[WorldStateSyncer] Error in OnChoreStateReceived: {ex.Message}");
			}
		}

	}
}

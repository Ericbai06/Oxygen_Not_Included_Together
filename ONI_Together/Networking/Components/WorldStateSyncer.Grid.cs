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
		// --- Gas and Liquid Logic ---
		private void SyncGasLiquid()
		{
			using var _ = Profiler.Scope();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			if (Grid.WidthInCells == 0 || Grid.HeightInCells == 0
			    || !EnsureShadowGrid())
				return;

			int cellsScanned = ScanVisibleAreas();
			cellsScanned += ScanBackgroundSweep();
			int packetSize = ONI_Together.Misc.World.WorldUpdateBatcher.Flush();
			sw.Stop();
			SyncStats.RecordSync(
				SyncStats.Gas, cellsScanned, packetSize, sw.ElapsedMilliseconds);
		}

		private bool EnsureShadowGrid()
		{
			if (_shadowElements != null && _shadowElements.Length == Grid.CellCount)
				return true;
			_shadowElements = new ushort[Grid.CellCount];
			_shadowMass = new float[Grid.CellCount];
			_shadowTemperature = new float[Grid.CellCount];
			_shadowDiseaseIdx = new byte[Grid.CellCount];
			_shadowDiseaseCount = new int[Grid.CellCount];
			_bgScanIndex = 0;
			_bgScanCellOffset = 0;
			for (int cell = 0; cell < Grid.CellCount; cell++)
				UpdateShadow(cell, CaptureCell(cell));
			return false;
		}

		private int ScanVisibleAreas()
		{
			int cellsScanned = 0;
			if (CursorManager.Instance != null && Camera.main != null)
			{
				Camera cam = Camera.main;
				Vector3 bl = cam.ViewportToWorldPoint(new Vector3(0, 0, 0));
				Vector3 tr = cam.ViewportToWorldPoint(new Vector3(1, 1, 0));
				Grid.PosToXY(bl, out int x1, out int y1);
				Grid.PosToXY(tr, out int x2, out int y2);

				// Add margin
				int margin = 2;
				x1 = Mathf.Max(0, x1 - margin);
				y1 = Mathf.Max(0, y1 - margin);
				x2 = Mathf.Min(Grid.WidthInCells, x2 + margin);
				y2 = Mathf.Min(Grid.HeightInCells, y2 + margin);

				cellsScanned += (x2 - x1) * (y2 - y1);
				ScanArea(new RectInt(x1, y1, x2 - x1, y2 - y1));
			}

			// Scan Client Viewports
			foreach (var kvp in _clientViewports)
			{
				var rect = kvp.Value;
				int x1 = Mathf.Max(0, rect.xMin - 2);
				int y1 = Mathf.Max(0, rect.yMin - 2);
				int x2 = Mathf.Min(Grid.WidthInCells, rect.xMax + 2);
				int y2 = Mathf.Min(Grid.HeightInCells, rect.yMax + 2);

				cellsScanned += (x2 - x1) * (y2 - y1);
				ScanArea(new RectInt(x1, y1, x2 - x1, y2 - y1));
			}

			// Scan pinned areas
			foreach (var rect in _pinnedAreas)
			{
				int px1 = Mathf.Max(0, rect.xMin);
				int py1 = Mathf.Max(0, rect.yMin);
				int px2 = Mathf.Min(Grid.WidthInCells, rect.xMax);
				int py2 = Mathf.Min(Grid.HeightInCells, rect.yMax);
				cellsScanned += (px2 - px1) * (py2 - py1);
				ScanArea(new RectInt(px1, py1, px2 - px1, py2 - py1));
			}
			return cellsScanned;
		}

		private int ScanBackgroundSweep()
		{
			int cellsScanned = 0;
			int totalChunks = BackgroundChunkCount(Grid.WidthInCells, Grid.HeightInCells);
			int chunkBudget = BackgroundChunksPerPass(totalChunks, _effectiveGasInterval);
			int requestedCells = chunkBudget * BG_SCAN_CHUNK_SIZE * BG_SCAN_CHUNK_SIZE;
			int cellBudget = ONI_Together.Misc.World.WorldUpdateBatcher
				.RepairProducerCellBudget(requestedCells, Grid.CellCount);
			for (int chunk = 0; chunk < chunkBudget && cellBudget > 0; chunk++)
			{
				RectInt area = BackgroundChunkBounds(
					Grid.WidthInCells, Grid.HeightInCells, _bgScanIndex);
				int chunkCells = area.width * area.height;
				int attemptBudget = Mathf.Min(
					cellBudget, Mathf.Max(0, chunkCells - _bgScanCellOffset));
				int processed = ScanAuthoritativeArea(
					area, _bgScanCellOffset, attemptBudget,
					_authoritativeRepairSuppressed);
				cellsScanned += processed;
				cellBudget -= processed;
				int previousOffset = _bgScanCellOffset;
				AdvanceBackgroundSweepPosition(
					_bgScanIndex, previousOffset, processed, chunkCells, totalChunks,
					out _bgScanIndex, out _bgScanCellOffset);
				if (previousOffset + processed < chunkCells)
					break;
			}
			return cellsScanned;
		}

		internal bool QueueChangedCellsForCheckpoint()
		{
			if (Grid.WidthInCells == 0 || Grid.HeightInCells == 0)
				return false;
			if (!EnsureShadowGrid())
				return true;
			for (int cell = 0; cell < Grid.CellCount; cell++)
			{
				if (!Grid.IsValidCell(cell))
					continue;
				WorldUpdatePacket.CellUpdate current = CaptureCell(cell);
				if (!ShouldQueueCheckpointCell(CaptureShadow(cell), current))
					continue;
				if (!ONI_Together.Misc.World.WorldUpdateBatcher.Queue(current))
					return false;
				UpdateShadow(cell, current);
			}
			return true;
		}

		/// <summary>
		/// Adaptive sync frequency based on FPS and client count.
		/// Returns multiplier: 1.0 (normal) to 6.0 (heavy load).
		/// </summary>
		private float GetSyncMultiplier()
		{
			float multiplier = 1f;

			// FPS factor
			float fps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.001f);
			if (fps < 20f) multiplier *= 3f;
			else if (fps < 30f) multiplier *= 2f;
			else if (fps < 45f) multiplier *= 1.5f;

			// Client count factor
			int clients = MultiplayerSession.ConnectedPlayers.Count;
			if (clients > 4) multiplier *= 2f;
			else if (clients > 2) multiplier *= 1.5f;

			return Mathf.Min(multiplier, 6f);
		}

		private void ScanArea(RectInt area, bool authoritative = false)
		{
			using var _ = Profiler.Scope();

			for (int y = area.yMin; y < area.yMax; y++)
			for (int x = area.xMin; x < area.xMax; x++)
				TryScanCell(y * Grid.WidthInCells + x, authoritative);
		}

		private int ScanAuthoritativeArea(
			RectInt area, int cellOffset, int cellBudget, bool repairSuppressed)
		{
			int processed = 0;
			int chunkCells = area.width * area.height;
			while (processed < cellBudget && cellOffset + processed < chunkCells)
			{
				int local = cellOffset + processed;
				int x = area.xMin + local % area.width;
				int y = area.yMin + local / area.width;
				if (!TryScanCell(
					    y * Grid.WidthInCells + x, authoritative: true,
					    repairSuppressed: repairSuppressed))
					break;
				processed++;
			}
			return processed;
		}

		private bool TryScanCell(
			int cell, bool authoritative, bool repairSuppressed = false)
		{
			if (!Grid.IsValidCell(cell))
				return true;
			WorldUpdatePacket.CellUpdate current = CaptureCell(cell);
			WorldUpdatePacket.CellUpdate shadow = CaptureShadow(cell);
			bool changed = CellStateChanged(shadow, current);
			if (!ShouldQueueCell(authoritative, shadow, current)
			    || authoritative && !ShouldQueueAuthoritativeSweepCell(
				    repairSuppressed, shadow, current))
				return true;
			if (!ONI_Together.Misc.World.WorldUpdateBatcher.Queue(
				    current, ShouldUseBackgroundRepair(authoritative, changed)))
				return false;
			UpdateShadow(cell, current);
			return true;
		}

		private static WorldUpdatePacket.CellUpdate CaptureCell(int cell)
			=> new WorldUpdatePacket.CellUpdate
			{
				Cell = cell,
				ElementIdx = Grid.ElementIdx[cell],
				Mass = Grid.Mass[cell],
				Temperature = Grid.Temperature[cell],
				DiseaseIdx = Grid.DiseaseIdx[cell],
				DiseaseCount = Grid.DiseaseCount[cell],
				ReplaceType = SimMessages.ReplaceType.Replace,
			};

		private WorldUpdatePacket.CellUpdate CaptureShadow(int cell)
			=> new WorldUpdatePacket.CellUpdate
			{
				ElementIdx = _shadowElements[cell],
				Mass = _shadowMass[cell],
				Temperature = _shadowTemperature[cell],
				DiseaseIdx = _shadowDiseaseIdx[cell],
				DiseaseCount = _shadowDiseaseCount[cell],
			};

		private void UpdateShadow(int cell, WorldUpdatePacket.CellUpdate current)
		{
			_shadowElements[cell] = current.ElementIdx;
			_shadowMass[cell] = current.Mass;
			_shadowTemperature[cell] = current.Temperature;
			_shadowDiseaseIdx[cell] = current.DiseaseIdx;
			_shadowDiseaseCount[cell] = current.DiseaseCount;
		}

		internal static bool CellStateChanged(
			WorldUpdatePacket.CellUpdate previous,
			WorldUpdatePacket.CellUpdate current)
		{
			return previous.ElementIdx != current.ElementIdx
				|| !previous.Mass.Equals(current.Mass)
				|| !previous.Temperature.Equals(current.Temperature)
				|| previous.DiseaseIdx != current.DiseaseIdx
				|| previous.DiseaseCount != current.DiseaseCount;
		}

		internal static bool ShouldQueueCell(
			bool authoritative,
			WorldUpdatePacket.CellUpdate previous,
			WorldUpdatePacket.CellUpdate current)
		{
			return authoritative || CellStateChanged(previous, current);
		}

		internal static bool ShouldUseBackgroundRepair(bool authoritative, bool changed)
			=> authoritative && !changed;

		internal static bool ShouldQueueAuthoritativeSweepCell(
			bool repairSuppressed,
			WorldUpdatePacket.CellUpdate previous,
			WorldUpdatePacket.CellUpdate current)
			=> !repairSuppressed || CellStateChanged(previous, current);

		internal static bool ShouldQueueCheckpointCell(
			WorldUpdatePacket.CellUpdate previous,
			WorldUpdatePacket.CellUpdate current)
			=> CellStateChanged(previous, current);

		internal static void SetAuthoritativeRepairSuppressed(bool suppressed)
			=> _authoritativeRepairSuppressed = suppressed;

		internal static void SetWorldScanPaused(bool paused)
			=> _worldScanPaused = paused;

		internal static bool ShouldRunWorldScan(bool paused) => !paused;

		internal static bool AuthoritativeRepairSuppressedForTests
			=> _authoritativeRepairSuppressed;

		internal static bool WorldScanPausedForTests => _worldScanPaused;

		internal static void AdvanceBackgroundSweepPosition(
			int chunkIndex,
			int cellOffset,
			int processedCells,
			int chunkCellCount,
			int totalChunks,
			out int nextChunkIndex,
			out int nextCellOffset)
		{
			nextChunkIndex = chunkIndex;
			nextCellOffset = cellOffset;
			if (chunkIndex < 0 || chunkIndex >= totalChunks || cellOffset < 0
			    || cellOffset >= chunkCellCount || processedCells < 0)
				return;
			nextCellOffset = Mathf.Min(chunkCellCount, cellOffset + processedCells);
			if (nextCellOffset < chunkCellCount)
				return;
			nextChunkIndex = (chunkIndex + 1) % totalChunks;
			nextCellOffset = 0;
		}

		internal static int BackgroundChunkCount(int width, int height)
		{
			if (width <= 0 || height <= 0)
				return 0;
			int chunksX = (width + BG_SCAN_CHUNK_SIZE - 1) / BG_SCAN_CHUNK_SIZE;
			int chunksY = (height + BG_SCAN_CHUNK_SIZE - 1) / BG_SCAN_CHUNK_SIZE;
			return chunksX * chunksY;
		}

		internal static int BackgroundChunksPerPass(int totalChunks, float intervalSeconds)
		{
			if (totalChunks <= 0 || intervalSeconds <= 0f
			    || float.IsNaN(intervalSeconds) || float.IsInfinity(intervalSeconds))
				return 0;
			int budget = Mathf.CeilToInt(
				totalChunks * intervalSeconds / BACKGROUND_SWEEP_TARGET_SECONDS);
			return Mathf.Clamp(budget, 1, totalChunks);
		}

		internal static RectInt BackgroundChunkBounds(int width, int height, int chunkIndex)
		{
			int chunkCount = BackgroundChunkCount(width, height);
			if (chunkIndex < 0 || chunkIndex >= chunkCount)
				return default;
			int chunksPerRow = (width + BG_SCAN_CHUNK_SIZE - 1) / BG_SCAN_CHUNK_SIZE;
			int x = chunkIndex % chunksPerRow * BG_SCAN_CHUNK_SIZE;
			int y = chunkIndex / chunksPerRow * BG_SCAN_CHUNK_SIZE;
			return new RectInt(x, y,
				Mathf.Min(BG_SCAN_CHUNK_SIZE, width - x),
				Mathf.Min(BG_SCAN_CHUNK_SIZE, height - y));
		}
	}
}

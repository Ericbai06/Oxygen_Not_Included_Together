using System;
using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;
namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class GeneralizedReplacementAdmissionGameSideTests
	{
		private const string FixturePrefix = "ONI_Together_ReplacementAdmissionFixture_";
		private const long FixtureSession = 9001;
		private const ulong FixtureSender = 9001001;
		private static ulong nextSequence = 1;
		[UnitTest(name: "Build replacement: metadata catalog separates executable pairs", category: "Building",
			headlessUnsupportedReason: "Requires loaded BuildingDef and Grid runtime")]
		public static UnitTestResult CapabilityCatalogSeparatesExecutablePairs()
		{
			if (!RuntimeReady())
				return UnitTestResult.Skip("Requires a loaded colony and building definitions");
			List<BuildingDef> metadata = MetadataEligibleDefs();
			DiscoveryResult discovery = DiscoverExecutablePairs(metadata);
			if (metadata.Count == 0)
				return UnitTestResult.Fail("No metadata-eligible replacement definitions were loaded");
			if (discovery.Pairs.Count == 0)
				return UnitTestResult.Fail(
					$"No executable old-to-new replacement pair was discovered ({discovery.Stats})");
			if (discovery.Pairs.Any(pair => !metadata.Contains(pair.NewDef) ||
					!pair.OldDef.Replaceable || pair.OldDef == pair.NewDef))
				return UnitTestResult.Fail("Executable pair escaped metadata or layer capability discovery");
			return UnitTestResult.Pass(
				$"{discovery.Stats} without prefab whitelist");
		}
		[UnitTest(name: "Build replacement: second operation rejects before mutation", category: "Building",
			headlessUnsupportedReason: "Requires loaded colony and real Unity Grid fixture")]
		public static UnitTestResult DifferentOperationIsRejectedBeforeMutation()
		{
			if (!RuntimeReady())
				return UnitTestResult.Skip("Requires a loaded colony and building definitions");
			OwnedFixture fixture = null;
			try
			{
				if (!TryCreateQueuedReplacement(out fixture, out BuildCommit firstCommit,
					out BuildRequest firstRequest, out string discoveryFailure))
					return UnitTestResult.Fail(discoveryFailure);
				PlacementOutcome firstOutcome = firstCommit.Placements.SingleOrDefault();
				if (firstOutcome == null || firstOutcome.Kind != BuildPlacementKind.QueuedReplacement ||
					!firstOutcome.HasIdentity)
					return UnitTestResult.Fail("First replacement was not a queued identity-bearing placement");
				GameObject firstObject = FindPlacement(fixture, firstOutcome);
				if (firstObject == null)
					return UnitTestResult.Fail("First replacement instance was not found on its footprint");
				CaptureFirstState(fixture, firstObject, firstOutcome);
				ApplyResult bindExisting = BuildCommitApplier.Apply(firstCommit);
				if (!bindExisting.Applied)
					return UnitTestResult.Fail("Client bind-existing rejected the legal first replacement");
				BuildOperationId secondOperation = new(FixtureSession, FixtureSender, NextSequence());
				fixture.OperationIds.Add(secondOperation);
				BuildRequest secondRequest = CopyWithOperation(firstRequest, secondOperation);
				bool secondAccepted = AuthoritativeBuildExecutor.Execute(secondRequest,
					new HostBuildPolicy(false), out _, out BuildRejected rejection);
				bool unchanged = FirstStateIsUnchanged(fixture);
				if (secondAccepted || rejection?.Reason != BuildRejectionReason.Occupied || !unchanged)
					return UnitTestResult.Fail(
						$"second operation mutated admission: accepted={secondAccepted}, " +
						$"reason={rejection?.Reason.ToString() ?? "none"}, unchanged={unchanged}");
				return UnitTestResult.Pass(
					$"first={fixture.FirstInstanceId}/{fixture.FirstNetId}/{fixture.FirstLifecycleRevision}, " +
					$"second={rejection.Reason}, layerSlots={fixture.FirstLayers.Count}");
			}
			catch (Exception exception)
			{
				return UnitTestResult.Fail("Replacement fixture execution failed: " + exception);
			}
			finally
			{
				DisposeFixture(fixture);
			}
		}
		private static bool RuntimeReady()
			=> Game.Instance != null && Grid.CellCount > 0 &&
				Assets.BuildingDefs != null && Assets.BuildingDefs.Count > 0;
		private static List<BuildingDef> MetadataEligibleDefs()
			=> Assets.BuildingDefs.Where(IsMetadataEligible).Distinct().ToList();
		private static bool IsMetadataEligible(BuildingDef def)
			=> def != null && def.BuildingComplete != null &&
				def.ObjectLayer != ObjectLayer.NumLayers &&
				def.ReplacementLayer != ObjectLayer.NumLayers &&
				def.MaterialCategory != null && def.MaterialCategory.Length > 0;
		private static DiscoveryResult DiscoverExecutablePairs(
			IReadOnlyList<BuildingDef> metadata)
		{
			var pairs = new List<ReplacementPair>();
			var stats = new DiscoveryStats(metadata.Count);
			foreach (BuildingDef newDef in metadata)
			{
				if (!TryMaterials(newDef, out List<Tag> materials))
					continue;
				stats.MaterialReady++;
				foreach (int cell in ExistingCells())
				{
					if (!TryExistingCandidate(newDef, cell, out BuildingDef oldDef,
						out GameObject existing) || !IsProbeCandidate(oldDef, newDef))
						continue;
					stats.PairCandidates++;
					if (!CanReplace(newDef, existing))
						continue;
					if (TryProbePair(oldDef, newDef, materials, stats, out ReplacementPair pair))
					{
						pairs.Add(pair);
						break;
					}
				}
			}
			stats.Pairs = pairs.Count;
			return new DiscoveryResult(pairs, stats);
		}
		private static IEnumerable<int> ExistingCells()
		{
			for (int cell = 0; cell < Grid.CellCount; cell++)
				if (Grid.IsValidCell(cell) && Grid.IsVisible(cell))
					yield return cell;
		}
		private static bool TryExistingCandidate(BuildingDef newDef, int cell,
			out BuildingDef oldDef, out GameObject existing)
		{
			oldDef = null;
			existing = null;
			try
			{
				existing = newDef.GetReplacementCandidate(cell);
				oldDef = existing?.GetComponent<BuildingComplete>()?.Def;
				return existing != null && oldDef != null;
			}
			catch (Exception)
			{
				return false;
			}
		}
		private static bool CanReplace(BuildingDef newDef, GameObject existing)
		{
			try
			{
				return newDef.CanReplace(existing);
			}
			catch (Exception)
			{
				return false;
			}
		}
		private static bool IsProbeCandidate(BuildingDef oldDef, BuildingDef newDef)
			=> oldDef != null && oldDef != newDef && oldDef.BuildingComplete != null &&
				oldDef.Replaceable && newDef.ReplacementLayer != ObjectLayer.NumLayers &&
				FixtureSafe(oldDef);
		private static bool FixtureSafe(BuildingDef def)
			=> def.BuildingComplete.GetComponentsInChildren<Component>(true)
				.All(component => !component.GetType().Name.Contains("CometDetector",
					StringComparison.OrdinalIgnoreCase));
		private static bool TryProbePair(BuildingDef oldDef, BuildingDef newDef,
			List<Tag> materials, DiscoveryStats stats, out ReplacementPair pair)
		{
			pair = null;
			if (!TryFindCell(new ReplacementPair(oldDef, newDef, materials),
				out int cell, out _))
				return false;
			stats.CellsFound++;
			GameObject original;
			try
			{
				original = BuildOriginal(oldDef, cell);
			}
			catch (Exception)
			{
				return false;
			}
			if (original == null)
				return false;
			stats.OriginalsBuilt++;
			original.name = FixturePrefix + "probe_" + oldDef.PrefabID + "_" + cell;
			try
			{
				GameObject candidate = newDef.GetReplacementCandidate(cell);
				if (!ReferenceEquals(candidate, original))
					return false;
				stats.CandidateMatches++;
				if (!newDef.CanReplace(original))
					return false;
				stats.CanReplaceMatches++;
				pair = new ReplacementPair(oldDef, newDef, materials);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
			finally
			{
				DisposeObjects(new[] { original });
			}
		}
		private static bool TryCreateQueuedReplacement(
			out OwnedFixture fixture, out BuildCommit firstCommit,
			out BuildRequest request, out string failure)
		{
			fixture = null;
			firstCommit = null;
			request = null;
		failure = "No executable queued replacement fixture was found";
			List<BuildingDef> metadata = MetadataEligibleDefs();
			DiscoveryResult discovery = DiscoverExecutablePairs(metadata);
			failure = $"No executable queued replacement fixture was found ({discovery.Stats})";
			foreach (ReplacementPair pair in discovery.Pairs)
				if (TryCreateQueuedReplacementPair(pair, out fixture, out firstCommit, out request))
				return true;
			return false;
		}
		private static bool TryCreateQueuedReplacementPair(ReplacementPair pair,
			out OwnedFixture fixture, out BuildCommit commit, out BuildRequest request)
		{
			fixture = null;
			commit = null;
			request = null;
			if (!TryFindCell(pair, out int cell, out List<int> footprint))
				return false;
			GameObject original = BuildOriginal(pair.OldDef, cell);
			if (original == null)
				return false;
			original.name = FixturePrefix + pair.OldDef.PrefabID + "_old_" + cell;
			request = Request(pair, cell, NextSequence());
			if (!AuthoritativeBuildExecutor.Execute(request, new HostBuildPolicy(false),
				out commit, out _))
			{
				CleanupFailedFixture(footprint, original, request.OperationId, 0);
				return false;
			}
			PlacementOutcome outcome = commit.Placements.SingleOrDefault();
			GameObject placed = outcome == null ? null : FindPlacement(footprint, outcome.NetId);
			if (outcome?.Kind != BuildPlacementKind.QueuedReplacement || placed == null)
			{
				CleanupFailedFixture(footprint, original, request.OperationId,
					outcome?.NetId ?? 0);
				return false;
			}
			placed.name = FixturePrefix + pair.NewDef.PrefabID + "_new_" + cell;
			fixture = new OwnedFixture(pair, request, cell, footprint, original, placed);
			fixture.Sequence = request.OperationId.Sequence;
			return true;
		}
		private static ulong NextSequence()
		{
			while (true)
			{
				ulong sequence = nextSequence++;
				var operation = new BuildOperationId(FixtureSession, FixtureSender, sequence);
				if (!IsBuildOperationOccupied(operation))
					return sequence;
			}
		}
		private static bool TryFindCell(
			ReplacementPair pair, out int cell, out List<int> footprint)
		{
			for (int candidate = 0; candidate < Grid.CellCount; candidate++)
			{
				if (!Grid.IsValidCell(candidate) || !Grid.IsVisible(candidate) ||
					!TryFootprint(pair.NewDef, candidate, out List<int> cells) ||
					!LayersFree(cells, pair.OldDef.ObjectLayer) ||
					!LayersFree(cells, pair.NewDef.ReplacementLayer) ||
					!LayersFree(cells, pair.NewDef.ObjectLayer))
					continue;
				cell = candidate;
				footprint = cells;
				return true;
			}
			cell = Grid.InvalidCell;
			footprint = null;
			return false;
		}
		private static bool TryFootprint(BuildingDef def, int cell, out List<int> cells)
		{
			var discovered = new List<int>();
			def.RunOnArea(cell, Orientation.Neutral, offset => discovered.Add(offset));
			cells = discovered;
			return cells.Count > 0 && cells.All(Grid.IsValidCell);
		}
		private static bool LayersFree(IReadOnlyList<int> cells, ObjectLayer layer)
		{
			if (layer == ObjectLayer.NumLayers)
				return false;
			foreach (int cell in cells)
				if (Grid.Objects[cell, (int)layer] != null)
					return false;
			return true;
		}
		private static GameObject BuildOriginal(BuildingDef def, int cell)
		{
			if (!TryMaterials(def, out List<Tag> materials))
				return null;
			return def.Build(cell, Orientation.Neutral, null, materials, def.Temperature,
				BuildRequestValidator.DefaultFacade, false, GameClock.Instance.GetTime());
		}
		private static bool TryMaterials(BuildingDef def, out List<Tag> materials)
		{
			materials = new List<Tag>();
			if (def?.MaterialCategory == null)
				return false;
			foreach (Tag category in def.MaterialCategory)
			{
				Tag material = MaterialSelector.GetValidMaterials(category).FirstOrDefault();
				if (!material.IsValid)
					return false;
				materials.Add(material);
			}
			return materials.Count > 0;
		}
		private static BuildRequest Request(ReplacementPair pair, int cell, ulong sequence)
			=> new(new BuildOperationId(FixtureSession, FixtureSender, sequence),
				pair.NewDef.PrefabID, new SinglePlacementGeometry(cell, pair.Orientation),
				pair.Materials.Select(material => material.Name), BuildRequestValidator.DefaultFacade,
				0, 5, (int)pair.NewDef.ObjectLayer);
		private static BuildRequest CopyWithOperation(BuildRequest source, BuildOperationId operation)
			=> new(operation, source.PrefabId, source.Geometry, source.MaterialTags, source.FacadeId,
				source.PriorityClass, source.PriorityValue, source.ObjectLayer);
		private static void CaptureFirstState(OwnedFixture fixture, GameObject placed,
			PlacementOutcome outcome)
		{
			NetworkIdentity identity = placed.GetComponent<NetworkIdentity>();
			fixture.FirstInstanceId = placed.GetInstanceID();
			fixture.FirstNetId = identity?.NetId ?? outcome.NetId;
			fixture.FirstLifecycleRevision = identity?.LifecycleRevision ?? outcome.LifecycleRevision;
			fixture.FirstLayers = CaptureLayers(fixture.Footprint, placed);
		}
		private static bool FirstStateIsUnchanged(OwnedFixture fixture)
		{
			GameObject current = FindPlacement(fixture.Footprint, fixture.FirstNetId);
			if (current == null || current.GetInstanceID() != fixture.FirstInstanceId)
				return false;
			NetworkIdentity identity = current.GetComponent<NetworkIdentity>();
			return identity != null && identity.NetId == fixture.FirstNetId &&
				identity.LifecycleRevision == fixture.FirstLifecycleRevision &&
				CaptureLayers(fixture.Footprint, current).SequenceEqual(fixture.FirstLayers);
		}
		private static List<LayerSlot> CaptureLayers(
			IReadOnlyList<int> cells, GameObject target)
		{
			var layers = new List<LayerSlot>();
			for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
				for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
					if (ReferenceEquals(Grid.Objects[cells[cellIndex], layer], target))
						layers.Add(new LayerSlot(cells[cellIndex], layer));
			return layers;
		}
		private static GameObject FindPlacement(OwnedFixture fixture, PlacementOutcome outcome)
			=> FindPlacement(fixture.Footprint, outcome.NetId);
		private static GameObject FindPlacement(IReadOnlyList<int> cells, int netId)
		{
			if (netId == 0)
				return null;
			foreach (int cell in cells)
				for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
				{
					GameObject candidate = Grid.Objects[cell, layer];
					if (candidate?.GetComponent<NetworkIdentity>()?.NetId == netId)
						return candidate;
				}
			return null;
		}
		private static List<GameObject> CollectObjects(IReadOnlyList<int> cells)
		{
			var result = new HashSet<GameObject>();
			foreach (int cell in cells)
				for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
				{
					GameObject value = Grid.Objects[cell, layer];
					if (value != null && value.name.StartsWith(FixturePrefix, StringComparison.Ordinal))
						result.Add(value);
				}
			return result.ToList();
		}
		private static void DisposeFixture(OwnedFixture fixture)
		{
			var failures = new List<Exception>();
			List<GameObject> objects = CollectObjectsSafely(fixture?.Footprint, failures);
			if (fixture?.Original != null) objects.Add(fixture.Original);
			if (fixture?.Placed != null) objects.Add(fixture.Placed);
			List<OwnedState> states = CaptureOwnedStates(objects, failures);
			try
			{
				DisposeObjects(objects.Distinct().ToList());
			}
			catch (Exception exception)
			{
				failures.Add(exception);
			}
			int deferred = FinalizeFixtureTeardown(states, fixture?.OperationIds, failures);
			if (failures.Count != 0)
			{
				Debug.LogError("[ReplacementFixtureCleanup] disposed=false error=" +
					new AggregateException(failures));
				throw new AggregateException(failures);
			}
			Debug.Log($"[ReplacementFixtureCleanup] owned={states.Count} disposed=true " +
				$"registry=targeted deferred={deferred}");
		}

		private sealed class DiscoveryResult
		{
			internal List<ReplacementPair> Pairs { get; }
			internal DiscoveryStats Stats { get; }
			internal DiscoveryResult(List<ReplacementPair> pairs, DiscoveryStats stats)
			{
				Pairs = pairs;
				Stats = stats;
			}
		}
		private sealed class DiscoveryStats
		{
			internal int MetadataEligible { get; }
			internal int MaterialReady { get; set; }
			internal int PairCandidates { get; set; }
			internal int CellsFound { get; set; }
			internal int OriginalsBuilt { get; set; }
			internal int CandidateMatches { get; set; }
			internal int CanReplaceMatches { get; set; }
			internal int Pairs { get; set; }
			internal DiscoveryStats(int metadataEligible)
			{
				MetadataEligible = metadataEligible;
			}
			public override string ToString()
				=> $"metadata={MetadataEligible};materials={MaterialReady};candidates={PairCandidates};" +
					$"cells={CellsFound};built={OriginalsBuilt};refs={CandidateMatches};" +
					$"canReplace={CanReplaceMatches};pairs={Pairs}";
		}
		private sealed class ReplacementPair
		{
			internal BuildingDef OldDef { get; }
			internal BuildingDef NewDef { get; }
			internal Orientation Orientation { get; } = Orientation.Neutral;
			internal List<Tag> Materials { get; }
			internal ReplacementPair(BuildingDef oldDef, BuildingDef newDef, List<Tag> materials)
			{
				OldDef = oldDef;
				NewDef = newDef;
				Materials = materials;
			}
		}
		private sealed class OwnedFixture
		{
			internal ReplacementPair Pair { get; }
			internal BuildRequest Request { get; }
			internal int Cell { get; }
			internal List<int> Footprint { get; }
			internal GameObject Original { get; }
			internal GameObject Placed { get; }
			internal List<BuildOperationId> OperationIds { get; } = new();
			internal ulong Sequence { get; set; }
			internal int FirstInstanceId { get; set; }
			internal int FirstNetId { get; set; }
			internal ulong FirstLifecycleRevision { get; set; }
			internal List<LayerSlot> FirstLayers { get; set; } = new();
			internal OwnedFixture(ReplacementPair pair, BuildRequest request, int cell,
				List<int> footprint, GameObject original, GameObject placed)
			{
				Pair = pair;
				Request = request;
				Cell = cell;
				Footprint = footprint;
				Original = original;
				Placed = placed;
				OperationIds.Add(request.OperationId);
			}
		}
		private readonly struct LayerSlot : IEquatable<LayerSlot>
		{
			private readonly int cell;
			private readonly int layer;
			internal LayerSlot(int cell, int layer)
			{
				this.cell = cell;
				this.layer = layer;
			}
			public bool Equals(LayerSlot other) => cell == other.cell && layer == other.layer;
			public override bool Equals(object obj) => obj is LayerSlot other && Equals(other);
			public override int GetHashCode() => HashCode.Combine(cell, layer);
		}
	}
}

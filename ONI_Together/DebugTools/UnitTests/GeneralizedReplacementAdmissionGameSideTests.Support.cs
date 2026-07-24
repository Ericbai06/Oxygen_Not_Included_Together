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
		private enum SpecialMode { SameDefinitionMaterials, RotatedFootprint }
		[UnitTest(name: "Build replacement: same definition different materials rejects before mutation",
			category: "Building", headlessUnsupportedReason: "Requires loaded colony and material inventory")]
		public static UnitTestResult SameDefinitionDifferentMaterialsRejects()
			=> RunSpecialAdmission(SpecialMode.SameDefinitionMaterials);
		[UnitTest(name: "Build replacement: oriented footprint rejects before mutation", category: "Building",
			headlessUnsupportedReason: "Requires loaded colony and a non-neutral building orientation")]
		public static UnitTestResult OrientedFootprintRejects()
			=> RunSpecialAdmission(SpecialMode.RotatedFootprint);
		private static UnitTestResult RunSpecialAdmission(SpecialMode mode)
		{
			if (!RuntimeReady())
				return UnitTestResult.Skip("Requires a loaded colony and building definitions");
			SpecialPair pair;
			try
			{
				if (!TryFindSpecialPair(mode, out pair, out string failure))
					return UnitTestResult.Fail(failure);
			}
			catch (Exception exception)
			{
				return UnitTestResult.Fail("Special pair discovery failed: " + exception);
			}
			SpecialFixture fixture = null;
			UnitTestResult result;
			Exception cleanupFailure = null;
			try
			{
				if (!TryCreateSpecial(pair, out fixture, out BuildCommit commit,
					out BuildRequest request, out string failure))
					result = UnitTestResult.Fail(failure);
				else
					result = ExecuteSpecial(fixture, commit, request, mode);
			}
			catch (Exception exception)
			{
				result = UnitTestResult.Fail("Special fixture execution failed: " + exception);
			}
			finally
			{
				cleanupFailure = CleanupSpecial(fixture);
			}
		return cleanupFailure == null ? result : UnitTestResult.Fail(
			"Special fixture cleanup failed: " + cleanupFailure);
		}
		private static bool TryFindSpecialPair(SpecialMode mode, out SpecialPair result,
			out string failure)
		{
			result = null;
			failure = "No loaded executable special replacement pair was discovered";
			foreach (BuildingDef newDef in MetadataEligibleDefs())
				foreach (int cell in ExistingCells())
				{
					if (!TryExistingCandidate(newDef, cell, out BuildingDef oldDef,
						out GameObject existing) || !SpecialCandidate(oldDef, newDef, mode) ||
						!CanReplace(newDef, existing) || !TrySpecialMaterials(oldDef, newDef, mode,
							out List<Tag> oldMaterials, out List<Tag> requestMaterials))
						continue;
					foreach (Orientation orientation in SpecialOrientations(mode))
					{
						var pair = new SpecialPair(oldDef, newDef, orientation, oldMaterials,
							requestMaterials);
						if (!TrySpecialProbe(pair, out int probeCell))
							continue;
						result = pair.WithCell(probeCell);
						return true;
					}
				}
			return false;
		}
		private static bool SpecialCandidate(BuildingDef oldDef, BuildingDef newDef, SpecialMode mode)
			=> oldDef != null && oldDef.BuildingComplete != null && oldDef.Replaceable &&
				newDef.ReplacementLayer != ObjectLayer.NumLayers && FixtureSafe(oldDef) &&
				(mode != SpecialMode.SameDefinitionMaterials || oldDef == newDef);
		private static IEnumerable<Orientation> SpecialOrientations(SpecialMode mode)
		{
			if (mode == SpecialMode.SameDefinitionMaterials)
				yield return Orientation.Neutral;
			else
				foreach (Orientation value in Enum.GetValues(typeof(Orientation)))
					if (value != Orientation.Neutral)
						yield return value;
		}
		private static bool TrySpecialMaterials(BuildingDef oldDef, BuildingDef newDef,
			SpecialMode mode, out List<Tag> oldMaterials, out List<Tag> requestMaterials)
		{
			oldMaterials = null;
			requestMaterials = null;
			if (mode == SpecialMode.SameDefinitionMaterials)
				return TryAlternateMaterials(oldDef, out oldMaterials, out requestMaterials);
			return TryMaterials(oldDef, out oldMaterials) && TryMaterials(newDef, out requestMaterials);
		}
		private static bool TryAlternateMaterials(BuildingDef def, out List<Tag> original,
			out List<Tag> alternate)
		{
			alternate = null;
			if (!TryMaterials(def, out original))
				return false;
			alternate = new List<Tag>(original);
			for (int index = 0; index < def.MaterialCategory.Length; index++)
			{
				List<Tag> choices = MaterialSelector.GetValidMaterials(def.MaterialCategory[index])
					.Where(tag => tag.IsValid).Distinct().ToList();
				if (choices.Count > 1 && !choices[1].Equals(original[index]))
				{
					alternate[index] = choices[1];
					return !original.SequenceEqual(alternate);
				}
			}
			return false;
		}
		private static bool TrySpecialProbe(SpecialPair pair, out int cell)
		{
			cell = Grid.InvalidCell;
			if (!TrySpecialCell(pair, out cell, out _))
				return false;
			GameObject original = null;
			try
			{
				original = pair.OldDef.Build(cell, pair.Orientation, null, pair.OldMaterials,
					pair.OldDef.Temperature, BuildRequestValidator.DefaultFacade, false,
					GameClock.Instance.GetTime());
				if (original == null)
					return false;
				original.name = FixturePrefix + "special_probe_" + cell;
				GameObject candidate = pair.NewDef.GetReplacementCandidate(cell);
				return ReferenceEquals(candidate, original) && pair.NewDef.CanReplace(original);
			}
			catch (Exception)
			{
				return false;
			}
			finally
			{
				if (original != null)
					DisposeObjects(new[] { original });
			}
		}
		private static bool TrySpecialCell(SpecialPair pair, out int cell, out List<int> footprint)
		{
			for (int candidate = 0; candidate < Grid.CellCount; candidate++)
			{
				if (!Grid.IsValidCell(candidate) || !Grid.IsVisible(candidate) ||
					!TrySpecialFootprint(pair.NewDef, candidate, pair.Orientation, out List<int> cells) ||
					!TrySpecialFootprint(pair.OldDef, candidate, pair.Orientation, out List<int> oldCells) ||
					!LayersFree(oldCells, pair.OldDef.ObjectLayer) ||
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
		private static bool TrySpecialFootprint(BuildingDef def, int cell, Orientation orientation,
			out List<int> cells)
		{
			var discovered = new List<int>();
			try { def.RunOnArea(cell, orientation, offset => discovered.Add(offset)); }
			catch (Exception)
			{
				cells = discovered;
				return false;
			}
			cells = discovered;
			return cells.Count > 0 && cells.All(Grid.IsValidCell);
		}
		private static bool TryCreateSpecial(SpecialPair pair, out SpecialFixture fixture,
			out BuildCommit commit, out BuildRequest request, out string failure)
		{
			fixture = null;
			commit = null;
			request = null;
			failure = "Special pair had no safe executable footprint";
			if (!TrySpecialCell(pair, out int cell, out List<int> footprint))
				return false;
			GameObject original = null;
			PlacementOutcome outcome = null;
			try
			{
				original = pair.OldDef.Build(cell, pair.Orientation, null, pair.OldMaterials,
					pair.OldDef.Temperature, BuildRequestValidator.DefaultFacade, false,
					GameClock.Instance.GetTime());
				if (original == null)
					return false;
				original.name = FixturePrefix + "special_old_" + cell;
				request = new BuildRequest(new BuildOperationId(FixtureSession, FixtureSender,
					NextSequence()), pair.NewDef.PrefabID,
					new SinglePlacementGeometry(cell, pair.Orientation),
					pair.RequestMaterials.Select(material => material.Name),
					BuildRequestValidator.DefaultFacade, 0, 5, (int)pair.NewDef.ObjectLayer);
				if (!AuthoritativeBuildExecutor.Execute(request, new HostBuildPolicy(false),
					out commit, out _))
					return false;
				outcome = commit.Placements.SingleOrDefault();
				GameObject placed = outcome == null ? null : FindPlacement(footprint, outcome.NetId);
				if (outcome?.Kind != BuildPlacementKind.QueuedReplacement || placed == null)
					return false;
				placed.name = FixturePrefix + "special_new_" + cell;
				fixture = new SpecialFixture(pair, request, cell, footprint, original, placed);
				return true;
			}
			finally
			{
				if (fixture == null)
					CleanupFailedFixture(footprint, original, request?.OperationId ?? default,
						outcome?.NetId ?? 0);
			}
		}
		private static UnitTestResult ExecuteSpecial(SpecialFixture fixture, BuildCommit commit,
			BuildRequest request, SpecialMode mode)
		{
			PlacementOutcome outcome = commit.Placements.SingleOrDefault();
			if (outcome == null || outcome.Kind != BuildPlacementKind.QueuedReplacement || !outcome.HasIdentity)
				return UnitTestResult.Fail("Special replacement was not identity-bearing");
			GameObject placed = FindPlacement(fixture.Footprint, outcome.NetId);
			if (placed == null)
				return UnitTestResult.Fail("Special replacement instance was not found");
			CaptureSpecialState(fixture, placed, outcome);
			if (mode == SpecialMode.RotatedFootprint && !OrientedState(fixture, placed))
				return UnitTestResult.Fail("Oriented replacement footprint was not fully admitted");
			if (!BuildCommitApplier.Apply(commit).Applied)
				return UnitTestResult.Fail("Special client bind-existing rejected the legal replacement");
			BuildOperationId secondOperation = new(FixtureSession, FixtureSender, NextSequence());
			fixture.OperationIds.Add(secondOperation);
			BuildRequest second = CopySpecialOperation(request, secondOperation);
			bool accepted = AuthoritativeBuildExecutor.Execute(second, new HostBuildPolicy(false),
			out _, out BuildRejected rejection);
			bool unchanged = SpecialStateUnchanged(fixture);
			if (mode == SpecialMode.RotatedFootprint && !OrientedState(fixture, placed))
				unchanged = false;
			if (accepted || rejection?.Reason != BuildRejectionReason.Occupied || !unchanged)
				return UnitTestResult.Fail($"special second operation mutated admission: accepted={accepted}, " +
					$"reason={rejection?.Reason.ToString() ?? "none"}, unchanged={unchanged}");
			return UnitTestResult.Pass($"pair={fixture.Pair.OldDef.PrefabID}->{fixture.Pair.NewDef.PrefabID}, " +
				$"orientation={fixture.Pair.Orientation}, layerSlots={fixture.FirstLayers.Count}");
		}
		private static BuildRequest CopySpecialOperation(BuildRequest source, BuildOperationId operation)
			=> new(operation, source.PrefabId, source.Geometry, source.MaterialTags, source.FacadeId,
				source.PriorityClass, source.PriorityValue, source.ObjectLayer);
		private static void CaptureSpecialState(SpecialFixture fixture, GameObject placed,
			PlacementOutcome outcome)
		{
			NetworkIdentity identity = placed.GetComponent<NetworkIdentity>();
			fixture.FirstInstanceId = placed.GetInstanceID();
			fixture.FirstNetId = identity?.NetId ?? outcome.NetId;
			fixture.FirstLifecycleRevision = identity?.LifecycleRevision ?? outcome.LifecycleRevision;
			fixture.FirstLayers = CaptureLayers(fixture.Footprint, placed);
		}
		private static bool SpecialStateUnchanged(SpecialFixture fixture)
		{
			GameObject current = FindPlacement(fixture.Footprint, fixture.FirstNetId);
			if (current == null || current.GetInstanceID() != fixture.FirstInstanceId)
				return false;
			NetworkIdentity identity = current.GetComponent<NetworkIdentity>();
			return identity != null && identity.NetId == fixture.FirstNetId &&
				identity.LifecycleRevision == fixture.FirstLifecycleRevision &&
				CaptureLayers(fixture.Footprint, current).SequenceEqual(fixture.FirstLayers);
		}
		private static bool OrientedState(SpecialFixture fixture, GameObject target)
		{
			if (fixture.Pair.Orientation == Orientation.Neutral ||
				!TrySpecialFootprint(fixture.Pair.NewDef, fixture.Cell, fixture.Pair.Orientation,
					out List<int> cells) || !new HashSet<int>(cells).SetEquals(fixture.Footprint))
				return false;
			foreach (int cell in cells)
				if (!ReferenceEquals(Grid.Objects[cell, (int)fixture.Pair.NewDef.ReplacementLayer], target))
					return false;
			return true;
		}
		private sealed class OwnedState
		{
			internal GameObject Object { get; }
			internal NetworkIdentity Identity { get; }
			internal int NetId { get; }
			internal OwnedState(GameObject value)
			{
				Object = value; Identity = value.GetComponent<NetworkIdentity>();
				NetId = Identity?.NetId ?? 0;
			}
		}
		private static List<GameObject> CollectObjectsSafely(IReadOnlyList<int> cells,
			List<Exception> failures)
		{
			try { return cells == null ? new List<GameObject>() : CollectObjects(cells); }
			catch (Exception exception) { failures.Add(exception); return new List<GameObject>(); }
		}
		private static List<OwnedState> CaptureOwnedStates(IEnumerable<GameObject> objects,
			List<Exception> failures)
		{
			var states = new List<OwnedState>();
			foreach (GameObject value in objects.Distinct())
				try { if (!ReferenceEquals(value, null)) states.Add(new OwnedState(value)); }
				catch (Exception exception) { failures.Add(exception); }
			return states;
		}
		private static Exception CleanupSpecial(SpecialFixture fixture)
		{
			var failures = new List<Exception>();
			List<GameObject> objects = CollectObjectsSafely(fixture?.Footprint, failures);
			if (fixture?.Original != null) objects.Add(fixture.Original);
			if (fixture?.Placed != null) objects.Add(fixture.Placed);
			List<OwnedState> states = CaptureOwnedStates(objects, failures);
			try { DisposeObjects(objects.Distinct().ToList()); }
			catch (Exception exception) { failures.Add(exception); }
			int deferred = FinalizeFixtureTeardown(states, fixture?.OperationIds, failures);
			if (failures.Count == 0)
			{
				Debug.Log($"[ReplacementFixtureCleanup] special=true owned={states.Count} " +
					$"disposed=true registry=targeted deferred={deferred}");
				return null;
			}
			var aggregate = new AggregateException(failures);
			Debug.LogError("[ReplacementFixtureCleanup] special=false error=" + aggregate);
			return aggregate;
		}
		private static int FinalizeFixtureTeardown(IReadOnlyList<OwnedState> states,
			IReadOnlyList<BuildOperationId> operationIds, List<Exception> failures)
		{
			DetachFixtureState(states, failures);
			RemoveOwnedBuildState(operationIds, states.Select(state => state.NetId), failures);
			return VerifyFixtureState(states, operationIds, failures);
		}
		private static void DetachFixtureState(IReadOnlyList<OwnedState> states,
			List<Exception> failures)
		{
			foreach (OwnedState state in states)
				try
				{
					if (state.Identity != null)
					{
						state.Identity.MarkDestructionPending();
						NetworkIdentityRegistry.UntrackUnassigned(state.Identity);
						if (state.NetId != 0 && NetworkIdentityRegistry.IsRegistered(
							state.Identity, state.NetId) && !NetworkIdentityRegistry.Unregister(
							state.Identity, state.NetId))
							throw new InvalidOperationException($"NetId {state.NetId} unregister failed");
					}
					if (state.Object != null && state.Object.activeSelf)
						state.Object.SetActive(false);
				}
				catch (Exception exception) { failures.Add(exception); }
		DetachGridReferences(states, failures);
		}
		private static void DetachGridReferences(IReadOnlyList<OwnedState> states,
			List<Exception> failures)
		{
			GameObject[] owned = states.Select(state => state.Object)
				.Where(value => !ReferenceEquals(value, null)).ToArray();
			for (int cell = 0; cell < Grid.CellCount; cell++)
				for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
					try
					{
						GameObject value = Grid.Objects[cell, layer];
						if (owned.Any(candidate => ReferenceEquals(candidate, value)))
							Grid.Objects[cell, layer] = null;
					}
					catch (Exception exception) { failures.Add(exception); }
		}
		private static int VerifyFixtureState(IReadOnlyList<OwnedState> states,
			IReadOnlyList<BuildOperationId> operationIds, List<Exception> failures)
		{
			VerifyGridDetached(states, failures);
			NetworkIdentity[] unassigned = Array.Empty<NetworkIdentity>();
			try { unassigned = NetworkIdentityRegistry.GetUnassignedLiveSnapshot(); }
			catch (Exception exception) { failures.Add(exception); }
			int deferred = 0;
			foreach (OwnedState state in states)
				try
				{
					if (state.NetId != 0 && NetworkIdentityRegistry.IsRegistered(
						state.Identity, state.NetId))
						failures.Add(new InvalidOperationException($"NetId {state.NetId} remains registered"));
					if (unassigned.Any(value => ReferenceEquals(value, state.Identity)))
						failures.Add(new InvalidOperationException($"NetId {state.NetId} remains unassigned"));
					if (state.Object != null)
					{
						if (state.Object.activeSelf)
							failures.Add(new InvalidOperationException("Owned object remains active"));
						else
							deferred++;
					}
				}
				catch (Exception exception) { failures.Add(exception); }
		VerifyBuildState(states, operationIds, failures);
			return deferred;
		}
		private static void VerifyGridDetached(IReadOnlyList<OwnedState> states,
			List<Exception> failures)
		{
			GameObject[] owned = states.Select(state => state.Object)
				.Where(value => !ReferenceEquals(value, null)).ToArray();
			for (int cell = 0; cell < Grid.CellCount; cell++)
				for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
					try
					{
						GameObject value = Grid.Objects[cell, layer];
						if (owned.Any(candidate => ReferenceEquals(candidate, value)))
							failures.Add(new InvalidOperationException($"Grid retains owned object at {cell}/{layer}"));
					}
					catch (Exception exception) { failures.Add(exception); }
		}
		private static void VerifyBuildState(IReadOnlyList<OwnedState> states,
			IReadOnlyList<BuildOperationId> operationIds, List<Exception> failures)
		{
			foreach (OwnedState state in states)
				if (state.NetId != 0 && BuildLifecycleRegistry.TryGet(state.NetId, out _))
					failures.Add(new InvalidOperationException($"NetId {state.NetId} remains in build lifecycle"));
			VerifyOwnedBuildStateRemoved(operationIds, failures);
		}
		private sealed class SpecialPair
		{
			internal BuildingDef OldDef { get; }
			internal BuildingDef NewDef { get; }
			internal Orientation Orientation { get; }
			internal List<Tag> OldMaterials { get; }
			internal List<Tag> RequestMaterials { get; }
			internal int Cell { get; }
			internal SpecialPair(BuildingDef oldDef, BuildingDef newDef, Orientation orientation,
				List<Tag> oldMaterials, List<Tag> requestMaterials, int cell = -1)
			{
				OldDef = oldDef; NewDef = newDef; Orientation = orientation;
				OldMaterials = oldMaterials; RequestMaterials = requestMaterials; Cell = cell;
			}
			internal SpecialPair WithCell(int cell)
				=> new(OldDef, NewDef, Orientation, OldMaterials, RequestMaterials, cell);
		}
		private sealed class SpecialFixture
		{
			internal SpecialPair Pair { get; }
			internal BuildRequest Request { get; }
			internal int Cell { get; }
			internal List<int> Footprint { get; }
			internal GameObject Original { get; }
			internal GameObject Placed { get; }
			internal ulong Sequence => Request.OperationId.Sequence;
			internal List<BuildOperationId> OperationIds { get; } = new();
			internal int FirstInstanceId { get; set; }
			internal int FirstNetId { get; set; }
			internal ulong FirstLifecycleRevision { get; set; }
			internal List<LayerSlot> FirstLayers { get; set; } = new();
			internal SpecialFixture(SpecialPair pair, BuildRequest request, int cell,
				List<int> footprint, GameObject original, GameObject placed)
			{
				Pair = pair; Request = request; Cell = cell; Footprint = footprint;
				Original = original; Placed = placed;
				OperationIds.Add(request.OperationId);
			}
		}
		private static void DisposeObjects(IEnumerable<GameObject> objects)
		{
			var failures = new List<Exception>();
			foreach (GameObject value in objects)
				try
				{
					if (!ReferenceEquals(value, null) && value.name.StartsWith(
						FixturePrefix, StringComparison.Ordinal)) DisposeObject(value);
				}
				catch (Exception exception) { failures.Add(exception); }
			if (failures.Count != 0)
				throw new AggregateException(failures);
		}
		private static void DisposeObject(GameObject value)
		{
			try { value.DeleteObject(); }
			catch (Exception exception)
			{
				try { UnityEngine.Object.Destroy(value); }
				catch (Exception fallback) { throw new AggregateException(exception, fallback); }
				throw new InvalidOperationException("DeleteObject failed; Destroy fallback requested", exception);
			}
		}
	}
}

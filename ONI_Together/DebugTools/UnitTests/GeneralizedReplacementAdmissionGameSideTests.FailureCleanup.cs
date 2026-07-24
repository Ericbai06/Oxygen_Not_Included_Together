using System;
using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class GeneralizedReplacementAdmissionGameSideTests
	{
		private static void CleanupFailedFixture(IReadOnlyList<int> footprint,
			GameObject original, BuildOperationId operation, int expectedNetId)
		{
			var failures = new List<Exception>();
			List<GameObject> objects = CollectObjectsSafely(footprint, failures);
			if (original != null)
				objects.Add(original);
			List<OwnedState> states = CaptureOwnedStates(objects, failures);
			try { DisposeObjects(objects.Distinct().ToList()); }
			catch (Exception exception) { failures.Add(exception); }
			DetachFixtureState(states, failures);
			HashSet<int> netIds = states.Select(state => state.NetId).Where(netId => netId != 0).ToHashSet();
			if (expectedNetId != 0)
				netIds.Add(expectedNetId);
			RemoveOwnedBuildState(new[] { operation }, netIds, failures);
			VerifyFixtureState(states, new[] { operation }, failures);
			if (failures.Count != 0)
				throw new AggregateException(failures);
		}

		[UnitTest(name: "Build replacement: targeted setup cleanup preserves unrelated state",
			category: "Building")]
		public static UnitTestResult TargetedSetupCleanupPreservesUnrelatedState()
		{
			BuildOperationId before = new(FixtureSession, FixtureSender, NextSequence());
			BuildOperationId owned = new(FixtureSession, FixtureSender, NextSequence());
			BuildOperationId after = new(FixtureSession, FixtureSender, NextSequence());
			int unrelatedNetId = FindUnusedLifecycleNetId();
			int ownedNetId = FindUnusedLifecycleNetId(unrelatedNetId);
			Queue<BuildOperationId> queue = GetResponseOrder();
			List<BuildOperationId> baseline = queue.ToList();
			var failures = new List<Exception>();
			try
			{
				SeedOperation(before);
				SeedOperation(owned);
				SeedOperation(after);
				BuildLifecycleRegistry.Bind(unrelatedNetId, before);
				BuildLifecycleRegistry.Bind(ownedNetId, owned);
				RemoveOwnedBuildState(new[] { owned }, new[] { ownedNetId }, failures);
				AssertRemoved(owned, ownedNetId);
				AssertPreserved(before, after, unrelatedNetId, baseline);
			}
			catch (Exception exception) { failures.Add(exception); }
			finally
			{
				RemoveOwnedBuildState(new[] { before, owned, after },
					new[] { unrelatedNetId, ownedNetId }, failures);
			}
			return failures.Count == 0 ? UnitTestResult.Pass(
				"targeted setup cleanup preserved unrelated caches and queue order") :
				UnitTestResult.Fail("Targeted setup cleanup seam failed: " +
					new AggregateException(failures));
		}

		private static void SeedOperation(BuildOperationId operation)
		{
			AddDefaultDictionaryEntry(typeof(AuthoritativeBuildExecutor), "Responses", operation);
			AddDefaultDictionaryEntry(typeof(BuildCommitApplier), "Applied", operation);
			GetResponseOrder().Enqueue(operation);
		}

		private static void AddDefaultDictionaryEntry(Type owner, string fieldName, object key)
		{
			var dictionary = (System.Collections.IDictionary)GetPrivateStaticField(owner, fieldName);
			Type valueType = dictionary.GetType().GetGenericArguments()[1];
			dictionary.Add(key, Activator.CreateInstance(valueType));
		}

		private static Queue<BuildOperationId> GetResponseOrder()
			=> (Queue<BuildOperationId>)GetPrivateStaticField(
				typeof(AuthoritativeBuildExecutor), "ResponseOrder");

		private static int FindUnusedLifecycleNetId(int excluded = 0)
		{
			var dictionary = (System.Collections.IDictionary)GetPrivateStaticField(
				typeof(BuildLifecycleRegistry), "Operations");
			for (int candidate = int.MaxValue - 1024; candidate > 0; candidate--)
				if (candidate != excluded && !dictionary.Contains(candidate))
					return candidate;
			throw new InvalidOperationException("No unused lifecycle NetId was found");
		}

		private static void AssertRemoved(BuildOperationId operation, int netId)
		{
			if (IsBuildOperationPresent(operation) || BuildLifecycleRegistry.TryGet(netId, out _))
				throw new InvalidOperationException("Owned setup state remained after targeted cleanup");
		}

		private static void AssertPreserved(BuildOperationId before, BuildOperationId after,
			int unrelatedNetId, IReadOnlyList<BuildOperationId> baseline)
		{
			if (!DictionaryContains(typeof(AuthoritativeBuildExecutor), "Responses", before) ||
				!DictionaryContains(typeof(AuthoritativeBuildExecutor), "Responses", after) ||
				!DictionaryContains(typeof(BuildCommitApplier), "Applied", before) ||
				!DictionaryContains(typeof(BuildCommitApplier), "Applied", after))
				throw new InvalidOperationException("Unrelated build cache entry was removed");
			if (!BuildLifecycleRegistry.TryGet(unrelatedNetId, out BuildOperationId bound) ||
				bound != before)
				throw new InvalidOperationException("Unrelated lifecycle binding was removed");
			List<BuildOperationId> expected = baseline.Concat(new[] { before, after }).ToList();
			if (!GetResponseOrder().SequenceEqual(expected))
				throw new InvalidOperationException("Unrelated response order changed");
		}
	}
}

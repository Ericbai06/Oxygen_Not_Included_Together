using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking.Packets.Tools.Build;

namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class GeneralizedReplacementAdmissionGameSideTests
	{
		private static readonly BindingFlags PrivateStaticField =
			BindingFlags.Static | BindingFlags.NonPublic;
		private static void TryCleanupStep(System.Action cleanup, List<Exception> failures)
		{
			try { cleanup(); }
			catch (Exception exception) { failures.Add(exception); }
		}

		private static bool IsBuildOperationOccupied(BuildOperationId operationId)
		{
			try
			{
				return DictionaryContains(typeof(AuthoritativeBuildExecutor), "Responses", operationId) ||
					QueueContains(operationId) ||
					DictionaryContains(typeof(BuildCommitApplier), "Applied", operationId) ||
					LifecycleContainsOperation(operationId);
			}
			catch (Exception)
			{
				return true;
			}
		}

		private static void RemoveOwnedBuildState(
			IEnumerable<BuildOperationId> operationIds, IEnumerable<int> netIds,
			List<Exception> failures)
		{
			HashSet<BuildOperationId> operations = (operationIds ?? Enumerable.Empty<BuildOperationId>())
				.Where(operation => operation.IsValid).ToHashSet();
			HashSet<int> identities = (netIds ?? Enumerable.Empty<int>())
				.Where(netId => netId != 0).ToHashSet();
			TryCleanupStep(() => RemoveDictionaryKeys(typeof(BuildLifecycleRegistry), "Operations",
				identities.Cast<object>()), failures);
			TryCleanupStep(() => RemoveDictionaryKeys(typeof(AuthoritativeBuildExecutor), "Responses",
				operations.Cast<object>()), failures);
			TryCleanupStep(() => RemoveResponseOrder(operations), failures);
			TryCleanupStep(() => RemoveDictionaryKeys(typeof(BuildCommitApplier), "Applied",
				operations.Cast<object>()), failures);
		}

		private static void VerifyOwnedBuildStateRemoved(
			IEnumerable<BuildOperationId> operationIds, List<Exception> failures)
		{
			foreach (BuildOperationId operation in operationIds ?? Enumerable.Empty<BuildOperationId>())
				if (operation.IsValid && IsBuildOperationPresent(operation))
					failures.Add(new InvalidOperationException(
						$"Build operation {operation} remains in targeted state"));
		}

		private static bool IsBuildOperationPresent(BuildOperationId operation)
			=> DictionaryContains(typeof(AuthoritativeBuildExecutor), "Responses", operation) ||
				QueueContains(operation) ||
				DictionaryContains(typeof(BuildCommitApplier), "Applied", operation) ||
				LifecycleContainsOperation(operation);

		private static bool DictionaryContains(Type owner, string fieldName, object key)
		{
			object value = GetPrivateStaticField(owner, fieldName);
			if (!(value is IDictionary dictionary))
				throw new InvalidOperationException($"{owner.Name}.{fieldName} is not a dictionary");
			return dictionary.Contains(key);
		}

		private static void RemoveDictionaryKeys(Type owner, string fieldName,
			IEnumerable<object> keys)
		{
			object value = GetPrivateStaticField(owner, fieldName);
			if (!(value is IDictionary dictionary))
				throw new InvalidOperationException($"{owner.Name}.{fieldName} is not a dictionary");
			foreach (object key in keys)
				dictionary.Remove(key);
		}

		private static bool QueueContains(BuildOperationId operation)
		{
			object value = GetPrivateStaticField(typeof(AuthoritativeBuildExecutor), "ResponseOrder");
			if (!(value is Queue<BuildOperationId> queue))
				throw new InvalidOperationException("AuthoritativeBuildExecutor.ResponseOrder is not a queue");
			return queue.Contains(operation);
		}

		private static void RemoveResponseOrder(HashSet<BuildOperationId> operations)
		{
			object value = GetPrivateStaticField(typeof(AuthoritativeBuildExecutor), "ResponseOrder");
			if (!(value is Queue<BuildOperationId> queue))
				throw new InvalidOperationException("AuthoritativeBuildExecutor.ResponseOrder is not a queue");
			int count = queue.Count;
			for (int index = 0; index < count; index++)
			{
				BuildOperationId operation = queue.Dequeue();
				if (!operations.Contains(operation))
					queue.Enqueue(operation);
			}
		}

		private static bool LifecycleContainsOperation(BuildOperationId operation)
		{
			object value = GetPrivateStaticField(typeof(BuildLifecycleRegistry), "Operations");
			if (!(value is IDictionary dictionary))
				throw new InvalidOperationException("BuildLifecycleRegistry.Operations is not a dictionary");
			foreach (DictionaryEntry entry in dictionary)
				if (entry.Value is BuildOperationId bound && bound == operation)
					return true;
			return false;
		}

		private static object GetPrivateStaticField(Type owner, string fieldName)
		{
			FieldInfo field = owner.GetField(fieldName, PrivateStaticField);
			if (field == null)
				throw new MissingFieldException(owner.FullName, fieldName);
			return field.GetValue(null);
		}
	}
}

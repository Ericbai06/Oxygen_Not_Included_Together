using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildAuthorityGameSideTests
	{
		private const string BuildNamespace = "ONI_Together.Networking.Packets.Tools.Build.";
		private static readonly string[] ForbiddenUiNames =
		{
			"PlanScreen", "BuildMenu", "ToolMenu", "ResourceRemainingDisplayScreen",
			"SandboxToolParameterMenu", "BaseUtilityBuildTool", "UtilityBuildTool", "WireBuildTool"
		};

		[UnitTest(name: "Build domain model preserves operation and geometry intent", category: "Building")]
		public static UnitTestResult DomainModelPreservesIntent()
		{
			try
			{
				string[] placementKinds = Enum.GetNames(typeof(BuildPlacementKind));
				if (!new[] { "Queued", "Completed", "QueuedReplacement", "CompletedReplacement" }
					.SequenceEqual(placementKinds, StringComparer.Ordinal))
					return UnitTestResult.Fail("Build placement lifecycle kinds are incomplete");
				Type operationType = Required("BuildOperationId");
				Type geometryType = Required("BuildGeometry");
				Type requestType = Required("BuildRequest");
				object operation = Construct(operationType, 8L, 17UL, 41UL);
				object single = ConstructNested(geometryType, "SinglePlacement", 123, Orientation.R90);
				object utility = ConstructNested(geometryType, "UtilityPath", new[] { 10, 11, 12 });
				object request = Construct(requestType, operation, "Wire", utility,
					new[] { "Copper" }, "DEFAULT_FACADE", 1, 5, (int)ObjectLayer.Wire);
				if (!(bool)Property(operation, "IsValid") || !(bool)Property(request, "IsUtility")
				    || !Equals(Property(request, "OperationId"), operation)
				    || (string)Property(request, "PrefabId") != "Wire"
				    || !Equals(Property(request, "Geometry"), utility))
					return UnitTestResult.Fail("Utility request lost operation or geometry intent");
				object singleRequest = Construct(requestType, operation, "Tile", single,
					new[] { "SandStone" }, "DEFAULT_FACADE", 1, 5, (int)ObjectLayer.Building);
				Type policyType = Required("HostBuildPolicy");
				object queuedPolicy = Construct(policyType, false);
				object instantPolicy = Construct(policyType, true);
				if ((bool)Member(queuedPolicy, "InstantBuild") || !(bool)Member(instantPolicy, "InstantBuild"))
					return UnitTestResult.Fail("Host build policy did not preserve queued/instant mode");
				return !(bool)Property(singleRequest, "IsUtility")
					? UnitTestResult.Pass("Single and utility request geometry remain explicit and distinct")
					: UnitTestResult.Fail("Single placement was classified as utility geometry");
			}
			catch (Exception exception)
			{
				return UnitTestResult.Fail("Build domain model unavailable: " + exception.Message);
			}
		}

		[UnitTest(name: "Authoritative executor is UI-independent with closed build screens", category: "Building",
			liveSafe: true, headlessUnsupportedReason: "Requires a loaded colony")]
		public static UnitTestResult ExecutorRunsWithoutBuildUi()
		{
			if (Game.Instance == null)
				return UnitTestResult.Skip("Requires a loaded colony");
			foreach (string name in ForbiddenUiNames)
				if (IsActiveUi(name))
					return UnitTestResult.Fail(name + " is active during the host build test");
			Type executor = Required("AuthoritativeBuildExecutor");
			MethodInfo execute = FindMethod(executor, "Execute", 4);
			if (execute == null)
				return UnitTestResult.Fail("AuthoritativeBuildExecutor.Execute seam is missing");
			foreach (string name in ForbiddenUiNames)
			{
				Type forbidden = FindSceneType(name);
				if (forbidden != null && ReflectionExecutionGraph.ReachesType(execute, forbidden))
					return UnitTestResult.Fail("Executor reaches forbidden UI type " + name);
			}
			return UnitTestResult.Pass("Host executor is callable while build UI and active tools are closed");
		}

		[UnitTest(name: "Utility planner covers wire liquid and gas families", category: "Building")]
		public static UnitTestResult UtilityFamiliesHaveDedicatedPlanner()
		{
			Type single = FindType("SinglePlacementPlanner");
			Type utility = FindType("UtilityPathPlanner");
			if (single == null || utility == null)
				return UnitTestResult.Fail("SinglePlacementPlanner and UtilityPathPlanner are required");
			if (!HasPlanningMethod(single) || !HasPlanningMethod(utility))
				return UnitTestResult.Fail("Planner has no executable planning method");
			foreach (MethodInfo method in utility.GetMethods(BindingFlags.Static | BindingFlags.Instance |
				BindingFlags.Public | BindingFlags.NonPublic))
				foreach (string name in ForbiddenUiNames)
				{
					Type forbidden = FindSceneType(name);
					if (forbidden != null && ReflectionExecutionGraph.ReachesType(method, forbidden))
						return UnitTestResult.Fail("Utility planner reaches UI type " + name);
				}
			return UnitTestResult.Pass("Ordinary placement and utility paths use separate UI-free planners");
		}

		[UnitTest(name: "Build commit applies host outcomes without client prediction", category: "Building")]
		public static UnitTestResult CommitApplyBoundaryIsCausal()
		{
			try
			{
				Type operationType = Required("BuildOperationId");
				Type commitType = Required("BuildCommit");
				Type outcomeType = Required("PlacementOutcome");
				Type edgeType = Required("UtilityEdge");
				Type revisionType = Required("BuildRevision");
				object operation = Construct(operationType, 8L, 17UL, 42UL);
				object placement = Construct(outcomeType, 50, BuildPlacementKind.Completed, 9001, 12UL);
				object placements = ArrayOf(outcomeType, placement);
				object connections = Array.CreateInstance(edgeType, 0);
				object revision = Construct(revisionType, 12UL);
				object commit = Construct(commitType, operation, placements, connections, revision);
				MethodInfo apply = FindMethod(Required("BuildCommitApplier"), "Apply", 1);
				if (apply == null || apply.GetParameters()[0].ParameterType != commitType)
					return UnitTestResult.Fail("BuildCommitApplier.Apply does not consume BuildCommit");
				foreach (string name in ForbiddenUiNames)
				{
					Type forbidden = FindSceneType(name);
					if (forbidden != null && ReflectionExecutionGraph.ReachesType(apply, forbidden))
						return UnitTestResult.Fail("Commit applier reaches client UI type " + name);
				}
				object result = Invoke(apply, apply.IsStatic ? null : Activator.CreateInstance(apply.DeclaringType), commit);
				return result != null
					? UnitTestResult.Pass("Client applies the host commit boundary without request re-resolution")
					: UnitTestResult.Fail("BuildCommitApplier.Apply returned no result");
			}
			catch (Exception exception)
			{
				return UnitTestResult.Fail("Commit/apply contract failed: " + exception.Message);
			}
		}

		[UnitTest(name: "Build rejection and lifecycle context are non-disconnecting", category: "Building")]
		public static UnitTestResult RejectionAndLifecycleContextContracts()
		{
			try
			{
				Type operationType = Required("BuildOperationId");
				Type rejectedType = Required("BuildRejected");
				Type reasonType = Required("BuildRejectionReason");
				object operation = Construct(operationType, 8L, 17UL, 43UL);
				object reason = reasonType.IsEnum
					? Enum.GetValues(reasonType).GetValue(0)
					: Activator.CreateInstance(reasonType);
				object rejected = Construct(rejectedType, operation, reason, "occupied");
				if (rejected == null || !Equals(Property(rejected, "OperationId"), operation))
					return UnitTestResult.Fail("BuildRejected lost stable operation identity");
				Type contextType = Required("BuildMutationContext");
				MethodInfo enter = FindMethod(contextType, "Enter", 1);
				if (enter == null || !typeof(IDisposable).IsAssignableFrom(enter.ReturnType))
					return UnitTestResult.Fail("BuildMutationContext.Enter is not a scoped disposable context");
				object scope = Invoke(enter, null, operation);
				(scope as IDisposable)?.Dispose();
				return UnitTestResult.Pass("Domain rejection carries causal identity without a transport disconnect");
			}
			catch (Exception exception)
			{
				return UnitTestResult.Fail("Rejection/context contract failed: " + exception.Message);
			}
		}

		[UnitTest(name: "Lifecycle publisher is unique and stale identity is rejected", category: "Building")]
		public static UnitTestResult LifecyclePublisherAndStaleIdentity()
		{
			Type publisher = FindType("BuildPublisher");
			if (publisher == null)
				return UnitTestResult.Fail("BuildPublisher is missing");
			MethodInfo[] publishMethods = publisher.GetMethods(
				BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				.Where(method => method.Name.StartsWith("Publish", StringComparison.Ordinal))
				.ToArray();
			if (publishMethods.Length != 2)
				return UnitTestResult.Fail("Expected one commit and one rejection publisher");
			Type spawn = FindSceneType("SpawnPrefabPacket");
			if (spawn != null && publishMethods.Any(method => ReflectionExecutionGraph.ReachesType(method, spawn)))
				return UnitTestResult.Fail("Build publisher reaches generic SpawnPrefab materialization");
			if (NetworkIdentityRegistry.ShouldAcceptLifecycleRevision(7, 6)
			    || NetworkIdentityRegistry.ShouldAcceptLifecycleRevision(7, 7)
			    || !NetworkIdentityRegistry.ShouldAcceptLifecycleRevision(7, 8)
			    || NetworkIdentityRegistry.CanRegisterExisting(7, true, false)
			    || NetworkIdentityRegistry.CanApplyDomainState(true, true, 7, false, 6)
			    || NetworkIdentityRegistry.CanApplyDomainState(true, true, 7, true, 7)
			    || !NetworkIdentityRegistry.CanApplyDomainState(true, true, 7, false, 7)
			    || !BuildLifecycleAdmission.CanComplete(true, true, true, true, true)
			    || BuildLifecycleAdmission.CanComplete(true, true, false, true, true))
				return UnitTestResult.Fail("Stale or tombstoned build lifecycle identity was accepted");
			return UnitTestResult.Pass("Exactly one dedicated publisher owns build lifecycle and stale identities are gated");
		}

		private static bool HasPlanningMethod(Type type)
			=> type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic).Any(method => method.Name is "Plan" or "TryPlan" or "TryPlace" or "Build");

		private static Type Required(string name)
			=> FindType(name) ?? throw new MissingMemberException(BuildNamespace + name);

		private static Type FindType(string name)
			=> typeof(BuildAuthorityGameSideTests).Assembly.GetTypes().FirstOrDefault(type =>
				type.Name == name && type.Namespace == "ONI_Together.Networking.Packets.Tools.Build");

		private static Type FindSceneType(string name)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type[] types;
				try { types = assembly.GetTypes(); }
				catch (ReflectionTypeLoadException exception)
				{
					types = exception.Types.Where(type => type != null).ToArray();
				}
				Type match = types.FirstOrDefault(type => type.Name == name);
				if (match != null)
					return match;
			}
			return null;
		}

		private static bool IsActiveUi(string name)
		{
			Type type = FindSceneType(name);
			if (type == null)
				return false;
			PropertyInfo instance = type.GetProperty("Instance",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			if (instance?.GetValue(null) != null)
				return true;
			FieldInfo singleton = type.GetField("Instance",
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			return singleton?.GetValue(null) != null || GameObject.Find(name) != null;
		}

		private static MethodInfo FindMethod(Type type, string name, int parameterCount)
			=> type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic).FirstOrDefault(method => method.Name == name &&
				method.GetParameters().Length == parameterCount);

		private static object Construct(Type type, params object[] arguments)
			=> Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic, null, arguments, null);

		private static object ConstructNested(Type type, string name, params object[] arguments)
		{
			Type nested = type.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
			if (nested == null)
				throw new MissingMemberException(type.FullName, name);
			return Construct(nested, arguments);
		}

		private static object Invoke(MethodInfo method, object target, params object[] arguments)
		{
			try { return method.Invoke(target, arguments); }
			catch (TargetInvocationException exception)
			{
				throw exception.InnerException ?? exception;
			}
		}

		private static object Property(object value, string name)
			=> value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public |
				BindingFlags.NonPublic)?.GetValue(value);

		private static object Member(object value, string name)
		{
			PropertyInfo property = value.GetType().GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null)
				return property.GetValue(value);
			FieldInfo field = value.GetType().GetField(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			return field?.GetValue(value);
		}

		private static object ArrayOf(Type elementType, object value)
		{
			Array result = Array.CreateInstance(elementType, 1);
			result.SetValue(value, 0);
			return result;
		}
	}
}

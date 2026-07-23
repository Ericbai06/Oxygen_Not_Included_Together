using System;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildAuthorityLifecycleGameSideTests
	{
		[UnitTest(name: "Lifecycle publisher is unique and stale identity is rejected", category: "Building")]
		public static UnitTestResult LifecyclePublisherAndStaleIdentity()
		{
			Type publisher = FindBuildType("BuildPublisher");
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

		private static Type FindBuildType(string name)
			=> typeof(BuildAuthorityLifecycleGameSideTests).Assembly.GetTypes().FirstOrDefault(type =>
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
	}
}

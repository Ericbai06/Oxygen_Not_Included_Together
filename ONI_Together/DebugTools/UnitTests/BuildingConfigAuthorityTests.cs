using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Packets.World.Handlers;
using Shared;
using System;
using System.Collections;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildingConfigAuthorityTests
	{
		[UnitTest(name: "Building config registry separates actions from assignments", category: "Networking")]
		public static UnitTestResult MutationSemanticsAreExplicit()
		{
			string[] actions = { "UprootPlant", "DoorUnseal", "CounterReset" };
			foreach (string action in actions)
				if (Semantics(action) != BuildingConfigMutationSemantics.MustExecuteAction)
					return UnitTestResult.Fail($"{action} was classified as replayable state");
			string[] assignments = { "Slider", "SliderIndex", "Threshold", "DoorState" };
			foreach (string assignment in assignments)
				if (Semantics(assignment) != BuildingConfigMutationSemantics.StateAssignment)
					return UnitTestResult.Fail($"{assignment} was classified as an execute-once action");
			return UnitTestResult.Pass("Uproot/unseal/reset execute once; slider and config values are state");
		}

		[UnitTest(name: "Building config revision keys isolate identity and config dimensions", category: "Networking")]
		public static UnitTestResult RevisionKeyIsolation()
		{
			object baseline = ConfigKey(WithSlider(Packet(-42, 7, 19), 3));
			if (!baseline.Equals(ConfigKey(WithSlider(Packet(-42, 7, 19), 3))))
				return UnitTestResult.Fail("Identical building config keys were not equal");
			if (baseline.Equals(ConfigKey(WithSlider(Packet(-43, 7, 19), 3)))
			    || baseline.Equals(ConfigKey(WithSlider(Packet(-42, 8, 19), 3))))
				return UnitTestResult.Fail("NetId or lifecycle revision leaked across state keys");
			if (baseline.Equals(ConfigKey(WithSlider(Packet(-42, 7, 20), 3)))
			    || baseline.Equals(ConfigKey(WithSlider(Packet(-42, 7, 19), 4))))
				return UnitTestResult.Fail("Config hash or slider index leaked across state keys");
			return UnitTestResult.Pass("NetId, lifecycle, config hash, and slider index form one exact key");
		}

		[UnitTest(name: "Building config latest revision wins and reset reopens one", category: "Networking")]
		public static UnitTestResult LatestRevisionAndReset()
		{
			BuildingConfigPacket.ResetSessionState();
			try
			{
				IDictionary revisions = DictionaryField("ClientRevisions");
				revisions[ConfigKey(Packet(-42, 7, 19))] = 2UL;
				ulong current = BuildingConfigPacket.GetClientRevisionForTests(-42, 7, 19);
				if (current != 2 || NetworkIdentityRegistry.IsNewerRevision(current, 1))
					return UnitTestResult.Fail("Revision 1 could replace already-applied revision 2");
				BuildingConfigPacket.ResetClientRevisionState();
				current = BuildingConfigPacket.GetClientRevisionForTests(-42, 7, 19);
				return current == 0 && NetworkIdentityRegistry.IsNewerRevision(current, 1)
					? UnitTestResult.Pass("2 then 1 is latest-only; reset accepts fresh revision 1")
					: UnitTestResult.Fail("Client baseline reset retained the old revision cut");
			}
			finally { BuildingConfigPacket.ResetSessionState(); }
		}

		[UnitTest(name: "Building config request streams reject duplicate and gaps", category: "Networking")]
		public static UnitTestResult RequestStreamOrdering()
		{
			BuildingConfigPacket.ResetSessionState();
			try
			{
				var first = new DispatchContext(41, false, 9);
				if (!AcceptRequest(first, 1) || AcceptRequest(first, 1)
				    || AcceptRequest(first, 3) || !AcceptRequest(first, 2))
					return UnitTestResult.Fail("Duplicate or non-contiguous request crossed one stream");
				var nextGeneration = new DispatchContext(41, false, 10);
				var nextSender = new DispatchContext(42, false, 10);
				if (!AcceptRequest(nextGeneration, 1) || !AcceptRequest(nextSender, 1))
					return UnitTestResult.Fail("Sender or generation cursor contaminated a fresh stream");
				var gapAtStart = new DispatchContext(43, false, 10);
				return !AcceptRequest(gapAtStart, 2)
					? UnitTestResult.Pass("Streams start at one and remain contiguous per sender generation")
					: UnitTestResult.Fail("A fresh request stream accepted an initial gap");
			}
			finally { BuildingConfigPacket.ResetSessionState(); }
		}

		[UnitTest(name: "Building config stale base corrects from cached state before apply", category: "Networking")]
		public static UnitTestResult StaleBaseCorrectionContract()
		{
			BuildingConfigPacket.ResetSessionState();
			try
			{
				BuildingConfigPacket state = Packet(-42, 7, Hash("Slider"));
				Invoke(state, "RememberHostSnapshot");
				if (DictionaryField("HostSnapshots").Count != 1)
					return UnitTestResult.Fail("Replayable host state was not cached");
				BuildingConfigPacket action = Packet(-42, 7, Hash("UprootPlant"));
				Invoke(action, "RememberHostSnapshot");
				if (DictionaryField("HostSnapshots").Count != 1)
					return UnitTestResult.Fail("Must-execute action entered periodic state snapshots");
				MethodInfo handle = Method(typeof(BuildingConfigPacket), "HandleClientRequest");
				int correction = FindCall(handle, typeof(BuildingConfigPacket), "TrySendHostCorrection");
				int apply = FindCall(handle, typeof(BuildingConfigPacket), "ApplyPacket");
				MethodInfo send = Method(typeof(BuildingConfigPacket), "TrySendHostCorrection");
				bool isolated = Calls(send, typeof(PacketSender), nameof(PacketSender.SendToPlayer))
				                && !Calls(send, typeof(BuildingConfigPacket), "ApplyPacket");
				return correction >= 0 && apply > correction && isolated
					? UnitTestResult.Pass("Stale base can send cached assignment before the only apply site")
					: UnitTestResult.Fail("Stale-base correction is missing or occurs after host mutation");
			}
			finally { BuildingConfigPacket.ResetSessionState(); }
		}

		[UnitTest(name: "Building config identity requires exact live lifecycle", category: "Networking")]
		public static UnitTestResult ExactLifecycleContract()
		{
			MethodInfo resolve = Method(typeof(BuildingConfigPacket), "TryGetCurrentIdentity");
			bool exactRegistry = Calls(resolve, typeof(NetworkIdentityRegistry), "TryGet")
			                     && Calls(resolve, typeof(NetworkIdentityRegistry), "IsRegistered")
			                     && Calls(resolve, typeof(NetworkIdentityRegistry), "IsLifecycleTombstoned")
			                     && Calls(resolve, typeof(NetworkIdentityRegistry), "GetLastLifecycleRevision");
			bool noFallback = Method(typeof(BuildingConfigPacket), "ResolveIdentity") == null
			                  && Method(typeof(BuildingConfigPacket), "AllowsCellResolution") == null;
			return exactRegistry && noFallback
				? UnitTestResult.Pass("Only exact registered non-tombstoned NetId lifecycle resolution remains")
				: UnitTestResult.Fail("Building config retained fallback identity or skipped lifecycle rejection");
		}

		[UnitTest(name: "Building config baseline and session reset revision state", category: "Networking")]
		public static UnitTestResult ResetIntegration()
		{
			MethodInfo baseline = Method(typeof(SessionStateReset), "ResetPresentationForBaseline");
			MethodInfo session = Method(typeof(SessionStateReset), "ResetCore");
			bool wired = Calls(baseline, typeof(BuildingConfigPacket), "ResetClientRevisionState")
			             && Calls(session, typeof(BuildingConfigPacket), "ResetSessionState");
			return wired
				? UnitTestResult.Pass("World baseline and full session reset both clear building config cuts")
				: UnitTestResult.Fail("Building config lifecycle reset is detached from reset orchestration");
		}

		private static BuildingConfigMutationSemantics Semantics(string key)
			=> BuildingConfigHandlerRegistry.GetMutationSemantics(Hash(key));

		private static int Hash(string key) => NetworkingHash.ForConfigKey(key);

		private static BuildingConfigPacket Packet(int netId, ulong lifecycle, int configHash)
			=> new()
			{
				NetId = netId,
				TargetLifecycleRevision = lifecycle,
				ConfigHash = configHash
			};

		private static BuildingConfigPacket WithSlider(BuildingConfigPacket packet, int sliderIndex)
		{
			packet.SliderIndex = sliderIndex;
			return packet;
		}

		private static object ConfigKey(BuildingConfigPacket packet)
		{
			Type type = typeof(BuildingConfigPacket).GetNestedType(
				"ConfigKey", BindingFlags.NonPublic);
			MethodInfo from = type?.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic);
			return from?.Invoke(null, new object[] { packet })
			       ?? throw new MissingMethodException("BuildingConfigPacket.ConfigKey.From");
		}

		private static IDictionary DictionaryField(string name)
		{
			FieldInfo field = typeof(BuildingConfigPacket).GetField(
				name, BindingFlags.Static | BindingFlags.NonPublic);
			return field?.GetValue(null) as IDictionary
			       ?? throw new MissingFieldException(typeof(BuildingConfigPacket).Name, name);
		}

		private static bool AcceptRequest(DispatchContext context, ulong requestId)
			=> (bool)Method(typeof(BuildingConfigPacket), "AcceptRequestStream")
				.Invoke(null, new object[] { context, requestId });

		private static void Invoke(BuildingConfigPacket packet, string name)
			=> Method(typeof(BuildingConfigPacket), name).Invoke(packet, null);

		private static MethodInfo Method(Type type, string name)
		{
			const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance |
			                           BindingFlags.Public | BindingFlags.NonPublic;
			return type.GetMethod(name, flags);
		}

		private static bool Calls(MethodInfo method, Type declaringType, string name)
			=> FindCall(method, declaringType, name) >= 0;

		private static int FindCall(MethodInfo method, Type declaringType, string name)
		{
			byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
			if (il == null) return -1;
			for (int index = 0; index <= il.Length - sizeof(int); index++)
			{
				MethodBase called = null;
				try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, index)); }
				catch (ArgumentException) { }
				if (called?.DeclaringType == declaringType && called.Name == name) return index;
			}
			return -1;
		}
	}
}

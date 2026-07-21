using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Tools.Prioritize;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Packets.DuplicantActions;
using ONI_Together.Patches.World;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class PriorityAuthorityTests
	{
		private readonly struct TargetGateCase
		{
			internal readonly bool LocalIsHost;
			internal readonly DispatchContext Context;
			internal readonly bool ProtocolVerified;

			internal TargetGateCase(
				bool localIsHost, DispatchContext context, bool protocolVerified)
			{
				LocalIsHost = localIsHost;
				Context = context;
				ProtocolVerified = protocolVerified;
			}
		}

		[UnitTest(name: "Priority Harmony targets match build 740622", category: "Sync")]
		public static UnitTestResult HarmonyTargets()
		{
			if (AccessTools.Method(typeof(PrioritizeTool), nameof(PrioritizeTool.OnDragTool),
				    new[] { typeof(int), typeof(int) }) == null ||
			    AccessTools.Method(typeof(Prioritizable), "SetMasterPriority",
				    new[] { typeof(PrioritySetting) }) == null ||
			    AccessTools.Method(typeof(UserMenuScreen), "OnPriorityClicked",
				    new[] { typeof(PrioritySetting) }) == null)
				return UnitTestResult.Fail("A priority Harmony target changed");
			return UnitTestResult.Pass("Priority Harmony targets match build 740622");
		}

		[UnitTest(name: "Priority requests require verified relay and outcomes require host", category: "Sync")]
		public static UnitTestResult Authority()
		{
			var direct = new DispatchContext(9, false);
			var verified = direct.AsVerifiedHostBroadcast();
			var target = new PrioritizeTargetRequestPacket { SenderId = 9 };
			bool originalHost = MultiplayerSession.IsHost;
			MultiplayerSession.IsHost = true;
			try
			{
				if (new PrioritizePacket() is not IClientRelayable ||
				    target is not IClientRelayable || target is not ISenderBoundRelay ||
				    target is not IHostAuthoritativeRelay ||
				    ((ISenderBoundRelay)target).RelaySenderId != target.SenderId ||
				    new PrioritizeStatePacket() is not IHostOnlyPacket)
					return UnitTestResult.Fail("Priority packet authority marker is missing");
				if (AcceptTarget(target, Gate(true, direct, true)) ||
				    AcceptTarget(target, Gate(true, verified, false)) ||
				    !AcceptTarget(target, Gate(true, verified, true)) ||
				    AcceptTarget(target, Gate(false, verified, true)) ||
				    PrioritizePacket.ShouldAccept(true, direct, true) ||
				    PrioritizePacket.ShouldAccept(true, verified, false) ||
				    !PrioritizePacket.ShouldAccept(true, verified, true) ||
				    PrioritizePacket.ShouldAccept(false, verified, true) ||
				    !PrioritizeStatePacket.ShouldApply(false, true) ||
				    PrioritizeStatePacket.ShouldApply(true, true) ||
				    PrioritizeStatePacket.ShouldApply(false, false))
					return UnitTestResult.Fail("Priority authority gate is incorrect");
				return UnitTestResult.Pass("Clients send verified requests and only host outcomes mutate peers");
			}
			finally { MultiplayerSession.IsHost = originalHost; }
		}

		[UnitTest(name: "Priority request and absolute outcome are bounded", category: "Sync")]
		public static UnitTestResult RoundtripAndBounds()
		{
			string failure = PriorityRoundtripFailure() ?? PriorityBoundsFailure();
			return failure == null
				? UnitTestResult.Pass(
					"Signed NetIds, authority revisions, and valid priorities roundtrip with strict bounds")
				: UnitTestResult.Fail(failure);
		}

		private static string PriorityRoundtripFailure()
		{
			PrioritizeTargetRequestPacket request = Roundtrip(new PrioritizeTargetRequestPacket
			{
				SenderId = 7,
				ClientRequestId = 11,
				NetId = -17,
				TargetLifecycleRevision = 3,
				BasePriorityRevision = 2,
				PriorityClass = (int)PriorityScreen.PriorityClass.high,
				PriorityValue = 9
			}, new PrioritizeTargetRequestPacket());
			if (request.SenderId != 7 || request.ClientRequestId != 11 || request.NetId != -17
			    || request.TargetLifecycleRevision != 3 || request.BasePriorityRevision != 2
			    || request.PriorityClass != (int)PriorityScreen.PriorityClass.high
			    || request.PriorityValue != 9)
				return "Priority target request did not roundtrip";

			var state = new PrioritizeStatePacket();
			state.Priorities.Add(new PrioritizeStatePacket.PriorityData
			{
				NetId = -23,
				LifecycleRevision = 4,
				StateRevision = 8,
				PriorityClass = (int)PriorityScreen.PriorityClass.topPriority,
				PriorityValue = 1
			});
			PrioritizeStatePacket output = Roundtrip(state, new PrioritizeStatePacket());
			if (output.Priorities.Count != 1 || output.Priorities[0].NetId != -23
			    || output.Priorities[0].LifecycleRevision != 4
			    || output.Priorities[0].StateRevision != 8
			    || output.Priorities[0].PriorityClass != (int)PriorityScreen.PriorityClass.topPriority ||
			    output.Priorities[0].PriorityValue != 1)
				return "Priority absolute outcome did not roundtrip";
			return null;
		}

		private static string PriorityBoundsFailure()
		{
			if (!PriorityAuthority.IsValidClientPriority(
				    new PrioritySetting(PriorityScreen.PriorityClass.basic, 1)) ||
			    !PriorityAuthority.IsValidClientPriority(
				    new PrioritySetting(PriorityScreen.PriorityClass.high, 9)) ||
			    !PriorityAuthority.IsValidClientPriority(
				    new PrioritySetting(PriorityScreen.PriorityClass.topPriority, 1)) ||
			    PriorityAuthority.IsValidClientPriority(
				    new PrioritySetting(PriorityScreen.PriorityClass.compulsory, 1)) ||
			    PriorityAuthority.IsValidClientPriority(
				    new PrioritySetting(PriorityScreen.PriorityClass.basic, 10)))
				return "Priority client value bounds are incorrect";
			if (!Rejects(new PrioritizeTargetRequestPacket
			    {
				    NetId = 0,
				    PriorityClass = (int)PriorityScreen.PriorityClass.basic,
				    PriorityValue = 5
			    }) ||
			    !Rejects(new PrioritizeStatePacket
			    {
				    Priorities =
				    {
					    new PrioritizeStatePacket.PriorityData
					    {
						    NetId = 9,
						    LifecycleRevision = 1,
						    StateRevision = 1,
						    PriorityClass = 99,
						    PriorityValue = 5
					    }
				    }
			    }))
				return "Priority wire validation accepted an invalid payload";
			return null;
		}

		[UnitTest(name: "Priority client tool is request-only", category: "Sync")]
		public static UnitTestResult ClientToolGate()
		{
			if (!PrioritizeToolPatch.ShouldRunLocally(false, false, false) ||
			    !PrioritizeToolPatch.ShouldRunLocally(true, true, false) ||
			    !PrioritizeToolPatch.ShouldRunLocally(true, false, true) ||
			    PrioritizeToolPatch.ShouldRunLocally(true, false, false))
				return UnitTestResult.Fail("Priority client tool authority gate is incorrect");
			return UnitTestResult.Pass("Only offline, host, or incoming host execution runs the priority tool");
		}

		[UnitTest(name: "Duplicant priority requests are signed and bounded", category: "Sync")]
		public static UnitTestResult DuplicantPriorityBounds()
		{
			if (!DuplicantPriorityPacket.IsValidRequest(-7, "Research", 0)
			    || !DuplicantPriorityPacket.IsValidRequest(-7, "Research", 5)
			    || DuplicantPriorityPacket.IsValidRequest(0, "Research", 3)
			    || DuplicantPriorityPacket.IsValidRequest(-7, string.Empty, 3)
			    || DuplicantPriorityPacket.IsValidRequest(-7, "Research", -1)
			    || DuplicantPriorityPacket.IsValidRequest(-7, "Research", 6))
				return UnitTestResult.Fail("Duplicant priority validation accepted an unsigned or out-of-range request");
			if (!Rejects(new DuplicantPriorityPacket
			    {
				    NetId = 0,
				    ChoreGroupId = "Research",
				    Priority = 3
			    }))
				return UnitTestResult.Fail("Zero duplicant priority NetId serialized successfully");

			return UnitTestResult.Pass("Duplicant priority requests require nonzero NetIds and values 0 through 5");
		}

		private static T Roundtrip<T>(T input, T output) where T : IPacket
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				input.Serialize(writer);
			stream.Position = 0;
			using var reader = new BinaryReader(stream);
			output.Deserialize(reader);
			if (stream.Position != stream.Length)
				throw new InvalidDataException("Priority packet left unread bytes");
			return output;
		}

		private static bool Rejects(IPacket packet)
		{
			try
			{
				using var stream = new MemoryStream();
				using var writer = new BinaryWriter(stream);
				packet.Serialize(writer);
				return false;
			}
			catch (InvalidDataException)
			{
				return true;
			}
		}

		private static bool AcceptTarget(
			PrioritizeTargetRequestPacket packet,
			TargetGateCase input)
		{
			MethodInfo method = typeof(PrioritizeTargetRequestPacket).GetMethod(
				"ShouldAccept", BindingFlags.Instance | BindingFlags.NonPublic);
			if (method == null) throw new MissingMethodException("Priority request authority gate");
			return (bool)method.Invoke(packet, new object[]
			{
				input.LocalIsHost, input.Context, input.ProtocolVerified
			});
		}

		private static TargetGateCase Gate(
			bool localIsHost, DispatchContext context, bool protocolVerified)
			=> new(localIsHost, context, protocolVerified);
	}
}

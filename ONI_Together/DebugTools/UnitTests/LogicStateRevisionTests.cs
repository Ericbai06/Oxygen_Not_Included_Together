#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ONI_Together.Misc;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class LogicStateRevisionTests
	{
		[UnitTest(name: "Logic state carries lifecycle and state revisions", category: "Sync")]
		public static UnitTestResult RevisionWireRoundtrip()
		{
			var source = Packet(-950001, 11, 23);
			LogicStatePacket copy = Roundtrip(source);
			if (source.NetId != copy.NetId || source.Cell != copy.Cell
			    || source.LifecycleRevision != copy.LifecycleRevision
			    || source.StateRevision != copy.StateRevision
			    || source.Value.Int != copy.Value.Int || source.IsActive != copy.IsActive)
				return UnitTestResult.Fail("Logic packet lost identity, revisions, or state");
			return UnitTestResult.Pass("Logic lifecycle and state revisions roundtrip exactly");
		}

		[UnitTest(name: "Logic state rejects zero identity and revisions", category: "Sync")]
		public static UnitTestResult ZeroRevisionWireRejection()
		{
			LogicStatePacket zeroNetId = Packet(0, 1, 1);
			LogicStatePacket zeroLifecycle = Packet(-950002, 0, 1);
			LogicStatePacket zeroState = Packet(-950003, 1, 0);
			return RejectsSerialization(zeroNetId)
			       && RejectsSerialization(zeroLifecycle)
			       && RejectsSerialization(zeroState)
				? UnitTestResult.Pass("Logic wire rejects zero identity and revisions")
				: UnitTestResult.Fail("Logic wire admitted zero identity or revision");
		}

		[UnitTest(name: "Logic state is host-authoritative", category: "Sync")]
		public static UnitTestResult HostAuthorityContract()
		{
			bool hostOnly = typeof(IHostOnlyPacket).IsAssignableFrom(typeof(LogicStatePacket));
			bool valid = LogicStatePacket.ShouldApplyState(false, true, 7, false, 7, 8, 9);
			bool localHost = LogicStatePacket.ShouldApplyState(true, true, 7, false, 7, 8, 9);
			bool nonHostSender = LogicStatePacket.ShouldApplyState(false, false, 7, false, 7, 8, 9);
			return hostOnly && valid && !localHost && !nonHostSender
				? UnitTestResult.Pass("Logic state accepts only remote host authority")
				: UnitTestResult.Fail("Logic state authority gate admitted local host or non-host sender");
		}

		[UnitTest(name: "Reliable logic repair rejects late unreliable state", category: "Sync")]
		public static UnitTestResult ReliableRepairWinsReordering()
		{
			bool repairTwo = LogicStatePacket.ShouldApplyState(
				false, true, 10, false, 10, 1, 2);
			bool lateOne = LogicStatePacket.ShouldApplyState(
				false, true, 10, false, 10, 2, 1);
			bool duplicate = LogicStatePacket.ShouldApplyState(
				false, true, 10, false, 10, 2, 2);
			bool zero = LogicStatePacket.ShouldApplyState(
				false, true, 10, false, 10, 2, 0);
			return repairTwo && !lateOne && !duplicate && !zero
				? UnitTestResult.Pass("Reliable repair cut rejects late unreliable and duplicate state")
				: UnitTestResult.Fail("Logic revision gate admitted zero, stale, or duplicate state");
		}

		[UnitTest(name: "Logic lifecycle gate runs before state and freshness", category: "Sync")]
		public static UnitTestResult LifecycleAndFreshnessOrdering()
		{
			bool mismatch = LogicStatePacket.ShouldApplyState(
				false, true, 10, false, 9, 1, 2);
			bool tombstone = LogicStatePacket.ShouldApplyState(
				false, true, 10, true, 10, 1, 2);
			bool zeroLifecycle = LogicStatePacket.ShouldApplyState(
				false, true, 0, false, 0, 1, 2);
			if (mismatch || tombstone || zeroLifecycle)
				return UnitTestResult.Fail("Logic state admitted lifecycle mismatch, tombstone, or zero");
			if (LogicStatePacket.ShouldRefreshLastPacketTime(false)
			    || !LogicStatePacket.ShouldRefreshLastPacketTime(true))
				return UnitTestResult.Fail("Rejected logic packet can refresh last-packet time");
			return UnitTestResult.Pass("Lifecycle mismatch and stale state return before freshness refresh");
		}

		[UnitTest(name: "Logic revision state resets with baseline and session", category: "Sync")]
		public static UnitTestResult RevisionStateResets()
		{
			const int netId = -950005;
			LogicStatePacket.ResetClientRevisionState();
			NetworkIdentityRegistry.TryAcceptStateRevision(
				netId, LogicStatePacket.RevisionDomain, 9);
			bool baselineReset = SessionStateReset.ResetPresentationForBaseline(100, 1);
			bool baselineOne = NetworkIdentityRegistry.TryAcceptStateRevision(
				netId, LogicStatePacket.RevisionDomain, 1);
			NetworkIdentityRegistry.TryAcceptStateRevision(
				netId, LogicStatePacket.RevisionDomain, 9);
			SessionStateReset.Reset();
			bool sessionOne = NetworkIdentityRegistry.TryAcceptStateRevision(
				netId, LogicStatePacket.RevisionDomain, 1);
			LogicStatePacket.ResetClientRevisionState();
			return baselineReset && baselineOne && sessionOne
				? UnitTestResult.Pass("New baseline and session accept logic revision one")
				: UnitTestResult.Fail("Logic revision state survived baseline or session reset");
		}

		[UnitTest(name: "Logic repair does not consume next unreliable broadcast", category: "Sync")]
		public static UnitTestResult RepairDoesNotAdvanceBroadcastMarker()
		{
			Type entry = typeof(LogicStateSyncer).GetNestedType(
				"BuildingEntry", BindingFlags.NonPublic);
			FieldInfo marker = entry?.GetField(
				"broadcastStateRevision", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			MethodInfo hostUpdate = typeof(LogicStateSyncer).GetMethod(
				"HostUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
			MethodInfo repair = typeof(LogicStateSyncer).GetMethod(
				nameof(LogicStateSyncer.SendStateToClient));
			MethodInfo capture = typeof(LogicStateSyncer).GetMethod(
				"TryCaptureState", BindingFlags.Instance | BindingFlags.NonPublic);
			return WritesField(hostUpdate, marker)
			       && !WritesField(repair, marker) && !WritesField(capture, marker)
				? UnitTestResult.Pass("Repair samples state without suppressing its next unreliable broadcast")
				: UnitTestResult.Fail("Repair consumed broadcast marker or host update failed to commit it");
		}

		private static LogicStatePacket Packet(int netId, ulong lifecycle, ulong state)
			=> new()
			{
				NetId = netId,
				Cell = 123,
				LifecycleRevision = lifecycle,
				StateRevision = state,
				Value = (Variant)7,
				IsActive = true,
				OptionalValues = new Dictionary<string, Variant> { ["mode"] = (Variant)3 }
			};

		private static LogicStatePacket Roundtrip(LogicStatePacket source)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				source.Serialize(writer);
			stream.Position = 0;
			var copy = new LogicStatePacket();
			using var reader = new BinaryReader(stream);
			copy.Deserialize(reader);
			if (stream.Position != stream.Length)
				throw new InvalidDataException("Logic packet left unread bytes");
			return copy;
		}

		private static bool RejectsSerialization(LogicStatePacket packet)
		{
			try
			{
				using var stream = new MemoryStream();
				using var writer = new BinaryWriter(stream);
				packet.Serialize(writer);
				return false;
			}
			catch (InvalidDataException) { return true; }
		}

		private static bool WritesField(MethodInfo method, FieldInfo target)
		{
			byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
			if (il == null || target == null) return false;
			for (int index = 0; index <= il.Length - 5; index++)
			{
				if (il[index] != 0x7D) continue;
				try
				{
					FieldInfo field = method.Module.ResolveField(BitConverter.ToInt32(il, index + 1));
					if (field == target) return true;
				}
				catch (ArgumentException) { }
			}
			return false;
		}

	}
}
#endif

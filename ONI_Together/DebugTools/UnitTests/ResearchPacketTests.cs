using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using ONI_Together.Patches.World;
using Shared.Interfaces.Networking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ResearchPacketTests
	{
		[UnitTest(name: "Research snapshot roundtrips complete explicit state", category: "Sync")]
		public static UnitTestResult SnapshotRoundtrip()
		{
			var source = new ResearchStatePacket
			{
				ResearchRevision = 7,
				UnlockedTechIds = new List<string> { "UnlockedTech" },
				QueuedTechIds = new List<string> { "QueuedTech" },
				ActiveTechId = string.Empty,
				ProgressEntries = new List<ResearchProgressData> { Progress("ProgressTech") }
			};
			ResearchStatePacket copy = Roundtrip(source, new ResearchStatePacket());
			bool exact = SnapshotMetadataEquals(copy) && SnapshotProgressEquals(copy);
			return exact
				? UnitTestResult.Pass("Unlocked, queued, explicit empty active, and exact progress survived")
				: UnitTestResult.Fail("Research snapshot lost authoritative state");
		}

		[UnitTest(name: "Research progress and completion roundtrip one revision domain", category: "Sync")]
		public static UnitTestResult DeltaRoundtrip()
		{
			ResearchProgressPacket progress = Roundtrip(new ResearchProgressPacket
			{
				ResearchRevision = 8,
				Progress = Progress("ProgressTech")
			}, new ResearchProgressPacket());
			ResearchCompletePacket completion = Roundtrip(new ResearchCompletePacket
			{
				ResearchRevision = 9,
				TechId = "CompletedTech"
			}, new ResearchCompletePacket());
			bool exact = progress.ResearchRevision == 8 && progress.Progress.TechId == "ProgressTech"
			             && progress.Progress.Points.Count == 2
			             && completion.ResearchRevision == 9 && completion.TechId == "CompletedTech";
			return exact
				? UnitTestResult.Pass("Progress and completion preserve their shared research revision")
				: UnitTestResult.Fail("Research delta packet did not roundtrip exactly");
		}

		[UnitTest(name: "Research latest revision wins and baseline reopens revision one", category: "Sync")]
		public static UnitTestResult LatestRevisionAndReset()
		{
			ResearchSyncCoordinator.ResetSessionState();
			try
			{
				SetCoordinatorField("_appliedRevision", 2L);
				if (ResearchSyncProtocol.ShouldApply(1, ResearchSyncCoordinator.AppliedResearchRevision))
					return UnitTestResult.Fail("Revision 1 replaced already-applied revision 2");
				ResearchSyncCoordinator.ResetClientForBaseline(71);
				if (ResearchSyncCoordinator.AppliedResearchRevision != 0
				    || !ResearchSyncProtocol.ShouldApply(1, ResearchSyncCoordinator.AppliedResearchRevision))
					return UnitTestResult.Fail("Fresh baseline did not reopen revision 1");
				SetCoordinatorField("_appliedRevision", 2L);
				ResearchSyncCoordinator.ResetSessionState();
				return ResearchSyncCoordinator.AppliedResearchRevision == 0
				       && ResearchSyncProtocol.ShouldApply(1, 0)
					? UnitTestResult.Pass("2 then 1 is latest-only; baseline and session reset accept fresh 1")
					: UnitTestResult.Fail("Session reset retained the old revision cut");
			}
			finally { ResearchSyncCoordinator.ResetSessionState(); }
		}

		[UnitTest(name: "Research state progress and completion share one ordering gate", category: "Sync")]
		public static UnitTestResult SharedRevisionDomain()
		{
			MethodInfo state = Method(typeof(ResearchSyncCoordinator), "ApplyState");
			MethodInfo progress = Method(typeof(ResearchSyncCoordinator), "ApplyProgress");
			MethodInfo completion = Method(typeof(ResearchSyncCoordinator), "ApplyCompletion");
			foreach (MethodInfo apply in new[] { state, progress, completion })
			{
				if (!Calls(apply, typeof(ResearchSyncCoordinator), "CanApply")
				    || !Calls(apply, typeof(ResearchSyncCoordinator), "CommitApplied"))
					return UnitTestResult.Fail($"{apply?.Name ?? "missing apply"} bypasses the shared cut");
			}
			long applied = 0;
			if (!Accept(1, ref applied) || !Accept(3, ref applied) || Accept(2, ref applied)
			    || applied != 3)
				return UnitTestResult.Fail("Mixed research packet kinds did not obey latest-only ordering");
			return UnitTestResult.Pass("State, progress, and completion commit through one revision cut");
		}

		[UnitTest(name: "Research request wire is transport-bound and bounded", category: "Sync")]
		public static UnitTestResult RequestWireBounds()
		{
			string maximumId = new('T', ResearchSyncProtocol.MaxTechIdLength);
			ResearchRequestPacket copy = Roundtrip(new ResearchRequestPacket
			{
				ClientRequestId = ulong.MaxValue,
				BaseResearchRevision = long.MaxValue,
				TechId = maximumId
			}, new ResearchRequestPacket());
			if (copy.ClientRequestId != ulong.MaxValue || copy.BaseResearchRevision != long.MaxValue
			    || copy.TechId != maximumId || HasSenderWireMember())
				return UnitTestResult.Fail("Request lost bounds or exposed spoofable sender wire state");
			var invalid = new IPacket[]
			{
				Request(0, 1, "Tech"), Request(1, 0, "Tech"), Request(1, -1, "Tech"),
				Request(1, 1, string.Empty), Request(1, 1, null),
				Request(1, 1, new string('T', ResearchSyncProtocol.MaxTechIdLength + 1))
			};
			foreach (IPacket packet in invalid)
				if (!Rejects(packet))
					return UnitTestResult.Fail("Invalid request ID, base revision, or tech ID serialized");
			return UnitTestResult.Pass("Only request id, base revision, and bounded tech intent cross the wire");
		}

		[UnitTest(name: "Research clients are request-only; outcomes require host authority", category: "Sync")]
		public static UnitTestResult ClientHasNoAuthoritativeMutationPath()
		{
			IPacket[] outcomes =
			{
				new ResearchStatePacket(), new ResearchProgressPacket(), new ResearchCompletePacket()
			};
			foreach (IPacket packet in outcomes)
				if (packet is not IHostOnlyPacket)
					return UnitTestResult.Fail($"{packet.GetType().Name} is not host-only");
			MethodInfo click = Method(typeof(ResearchEntryPatch), nameof(ResearchEntryPatch.Prefix));
			if (!Calls(click, typeof(ResearchSyncCoordinator), "TrySendRequest")
			    || Calls(click, typeof(Research), "SetActiveResearch"))
				return UnitTestResult.Fail("Client research click can mutate authoritative state directly");
			IPacket request = new ResearchRequestPacket();
			if (request is IHostOnlyPacket
			    || !PacketHandler.CanDispatchClientPacket(request, true, ClientReadyState.Ready)
			    || PacketHandler.CanDispatchClientPacket(request, false, ClientReadyState.Ready)
			    || PacketHandler.CanDispatchClientPacket(request, true, ClientReadyState.Unready))
				return UnitTestResult.Fail("Research request bypassed verified Ready client ingress");
			return UnitTestResult.Pass("Clients send intent only; all authoritative outcomes are host-only");
		}

		private static ResearchProgressData Progress(string techId)
			=> new()
			{
				TechId = techId,
				Points = new List<ResearchPointData>
				{
					new() { ResearchTypeId = "Basic", Points = 12.5f },
					new() { ResearchTypeId = "Advanced", Points = 3f }
				}
			};

		private static bool Equal(IReadOnlyList<string> values, string expected)
			=> values.Count == 1 && values[0] == expected;

		private static bool SnapshotMetadataEquals(ResearchStatePacket packet)
			=> packet.ResearchRevision == 7 && packet.ActiveTechId == string.Empty
			   && Equal(packet.UnlockedTechIds, "UnlockedTech")
			   && Equal(packet.QueuedTechIds, "QueuedTech");

		private static bool SnapshotProgressEquals(ResearchStatePacket packet)
		{
			if (packet.ProgressEntries.Count != 1) return false;
			ResearchProgressData progress = packet.ProgressEntries[0];
			return progress.TechId == "ProgressTech" && progress.Points.Count == 2
			       && progress.Points[0].ResearchTypeId == "Basic"
			       && progress.Points[0].Points == 12.5f
			       && progress.Points[1].ResearchTypeId == "Advanced"
			       && progress.Points[1].Points == 3f;
		}

		private static ResearchRequestPacket Request(ulong id, long revision, string techId)
			=> new() { ClientRequestId = id, BaseResearchRevision = revision, TechId = techId };

		private static bool Accept(long revision, ref long applied)
		{
			if (!ResearchSyncProtocol.ShouldApply(revision, applied)) return false;
			applied = revision;
			return true;
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
				throw new InvalidDataException("Research packet left unread bytes");
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
			catch (InvalidDataException) { return true; }
		}

		private static bool HasSenderWireMember()
		{
			const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
			Type type = typeof(ResearchRequestPacket);
			return type.GetField("SenderId", flags) != null || type.GetProperty("SenderId", flags) != null;
		}

		private static void SetCoordinatorField(string name, object value)
		{
			FieldInfo field = typeof(ResearchSyncCoordinator).GetField(
				name, BindingFlags.Static | BindingFlags.NonPublic);
			if (field == null) throw new MissingFieldException(typeof(ResearchSyncCoordinator).Name, name);
			field.SetValue(null, value);
		}

		private static MethodInfo Method(Type type, string name)
			=> AccessTools.Method(type, name);

		private static bool Calls(MethodInfo method, Type declaringType, string name)
		{
			byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
			if (il == null) return false;
			for (int index = 0; index <= il.Length - sizeof(int); index++)
			{
				MethodBase called = null;
				try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, index)); }
				catch (ArgumentException) { }
				if (called?.DeclaringType == declaringType && called.Name == name) return true;
			}
			return false;
		}
	}
}

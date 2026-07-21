using ONI_Together.Networking.Packets.World;
using System;
using System.IO;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildingConfigWireTests
	{
		[UnitTest(name: "Building config request and outcome wire fields are distinct", category: "Networking")]
		public static UnitTestResult RequestAndOutcomeRoundTrip()
		{
			BuildingConfigPacket request = ValidPacket();
			request.ClientRequestId = 11;
			request.BaseStateRevision = 5;
			BuildingConfigPacket requestCopy = RoundTrip(request);
			if (requestCopy.ClientRequestId != 11 || requestCopy.BaseStateRevision != 5
			    || requestCopy.StateRevision != 0 || requestCopy.TargetLifecycleRevision != 7
			    || GetSender(requestCopy) != 9)
				return UnitTestResult.Fail("Request authority fields changed on the wire");

			BuildingConfigPacket outcome = ValidPacket();
			outcome.StateRevision = 6;
			BuildingConfigPacket outcomeCopy = RoundTrip(outcome);
			bool exact = GetSender(outcomeCopy) == 9 && outcomeCopy.ClientRequestId == 0
			             && outcomeCopy.BaseStateRevision == 0
			             && outcomeCopy.StateRevision == 6 && outcomeCopy.NetId == -42
			             && outcomeCopy.ConfigHash == 19 && outcomeCopy.SliderIndex == 3
			             && outcomeCopy.StringValue == "primary"
			             && outcomeCopy.SecondaryStringValue == "secondary";
			return exact
				? UnitTestResult.Pass("Request intent and host outcome preserve separate revision fields")
				: UnitTestResult.Fail("Outcome authority or configuration fields changed on the wire");
		}

		[UnitTest(name: "Building config wire rejects zero and out-of-range authority", category: "Networking")]
		public static UnitTestResult AuthorityBoundsAndZeroRevision()
		{
			BuildingConfigPacket neutral = ValidPacket();
			BuildingConfigPacket zeroLifecycle = Outcome();
			zeroLifecycle.TargetLifecycleRevision = 0;
			BuildingConfigPacket zeroSender = Outcome();
			SetSender(zeroSender, 0);
			BuildingConfigPacket mixedRequest = Request();
			mixedRequest.StateRevision = 1;
			BuildingConfigPacket mixedOutcome = Outcome();
			mixedOutcome.BaseStateRevision = 1;
			BuildingConfigPacket excessiveLifecycle = Outcome();
			excessiveLifecycle.TargetLifecycleRevision = (ulong)long.MaxValue + 1;
			BuildingConfigPacket excessiveRequest = Request();
			excessiveRequest.ClientRequestId = (ulong)long.MaxValue + 1;
			BuildingConfigPacket excessiveState = Outcome();
			excessiveState.StateRevision = (ulong)long.MaxValue + 1;
			BuildingConfigPacket excessiveBase = Request();
			excessiveBase.BaseStateRevision = (ulong)long.MaxValue + 1;
			foreach (BuildingConfigPacket packet in new[]
			         {
			          neutral, zeroLifecycle, zeroSender, mixedRequest, mixedOutcome,
			          excessiveLifecycle, excessiveRequest, excessiveState, excessiveBase
			         })
				if (!Rejects(packet))
					return UnitTestResult.Fail("Invalid zero, mixed, or out-of-range authority serialized");
			byte[] malformedWire = SerializeBytes(Outcome());
			const int lifecycleOffset = sizeof(ulong) + sizeof(int);
			Array.Clear(malformedWire, lifecycleOffset, sizeof(ulong));
			if (!RejectsWire(malformedWire))
				return UnitTestResult.Fail("Inbound zero lifecycle revision crossed deserialization");
			BuildingConfigPacket maximum = Request();
			maximum.TargetLifecycleRevision = (ulong)long.MaxValue;
			maximum.ClientRequestId = (ulong)long.MaxValue;
			maximum.BaseStateRevision = (ulong)long.MaxValue;
			return RoundTrip(maximum).BaseStateRevision == (ulong)long.MaxValue
				? UnitTestResult.Pass("Zero/mixed authority loses while signed wire maxima remain valid")
				: UnitTestResult.Fail("Valid maximum authority fields did not round-trip");
		}

		private static BuildingConfigPacket Request()
		{
			BuildingConfigPacket packet = ValidPacket();
			packet.ClientRequestId = 1;
			return packet;
		}

		private static BuildingConfigPacket Outcome()
		{
			BuildingConfigPacket packet = ValidPacket();
			packet.StateRevision = 1;
			return packet;
		}

		private static BuildingConfigPacket ValidPacket()
		{
			var packet = new BuildingConfigPacket
			{
				NetId = -42,
				TargetLifecycleRevision = 7,
				Cell = 123,
				DeterministicBuildingId = -77,
				ConfigHash = 19,
				ConfigType = BuildingConfigType.String,
				SliderIndex = 3,
				ReferenceNetId = -99,
				Value = 4.5f,
				StringValue = "primary",
				SecondaryStringValue = "secondary"
			};
			SetSender(packet, 9);
			return packet;
		}

		private static BuildingConfigPacket RoundTrip(BuildingConfigPacket packet)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);
			stream.Position = 0;
			var copy = new BuildingConfigPacket();
			using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);
			if (stream.Position != stream.Length)
				throw new InvalidDataException("Building config packet left unread bytes");
			return copy;
		}

		private static bool Rejects(BuildingConfigPacket packet)
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

		private static byte[] SerializeBytes(BuildingConfigPacket packet)
		{
			using var stream = new MemoryStream();
			using var writer = new BinaryWriter(stream);
			packet.Serialize(writer);
			return stream.ToArray();
		}

		private static bool RejectsWire(byte[] bytes)
		{
			try
			{
				using var stream = new MemoryStream(bytes);
				using var reader = new BinaryReader(stream);
				new BuildingConfigPacket().Deserialize(reader);
				return false;
			}
			catch (InvalidDataException) { return true; }
		}

		private static void SetSender(BuildingConfigPacket packet, ulong sender)
		{
			SenderField().SetValue(packet, sender);
		}

		private static ulong GetSender(BuildingConfigPacket packet)
			=> (ulong)SenderField().GetValue(packet);

		private static FieldInfo SenderField()
			=> typeof(BuildingConfigPacket).GetField(
				"Sender", BindingFlags.Instance | BindingFlags.NonPublic)
			   ?? throw new MissingFieldException(typeof(BuildingConfigPacket).Name, "Sender");
	}
}

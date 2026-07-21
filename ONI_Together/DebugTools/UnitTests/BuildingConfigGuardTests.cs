using System.IO;
using System.Reflection;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildingConfigGuardTests
	{
		[UnitTest(name: "Building config apply guard is nested", category: "Networking")]
		public static UnitTestResult ApplyGuardIsNested()
		{
			BuildingConfigPacket.ResetApplyingPacketForTests();
			try
			{
				BuildingConfigPacket.BeginApplyingPacket();
				BuildingConfigPacket.BeginApplyingPacket();
				BuildingConfigPacket.EndApplyingPacket();

				if (!BuildingConfigPacket.IsApplyingPacket)
					return UnitTestResult.Fail("First completion cleared a nested apply guard");

				BuildingConfigPacket.EndApplyingPacket();
				if (BuildingConfigPacket.IsApplyingPacket)
					return UnitTestResult.Fail("Apply guard remained set after the final completion");

				return UnitTestResult.Pass("Nested apply guard remains active until the outer apply completes");
			}
			finally
			{
				BuildingConfigPacket.ResetApplyingPacketForTests();
			}
		}

		[UnitTest(name: "Building config metadata is bounded", category: "Networking")]
		public static UnitTestResult MetadataIsBounded()
		{
			if (!MetadataIsStrict())
				return UnitTestResult.Fail("Invalid building metadata was accepted");
			return UnitTestResult.Pass("Building metadata rejects invalid IDs, values, and bounds");
		}

		private static bool MetadataIsStrict()
		{
			BuildingConfigMetadata metadata = ValidMetadata();
			if (!BuildingConfigPacket.IsValidMetadata(metadata))
				return false;
			metadata.NetId = 0;
			bool zeroNetId = BuildingConfigPacket.IsValidMetadata(metadata);
			metadata = ValidMetadata();
			metadata.ConfigType = (BuildingConfigType)255;
			bool invalidType = BuildingConfigPacket.IsValidMetadata(metadata);
			metadata = ValidMetadata();
			metadata.Value = float.NaN;
			bool invalidFloat = BuildingConfigPacket.IsValidMetadata(metadata);
			metadata = ValidMetadata();
			metadata.StringValue = new string('x', 1025);
			bool oversizedString = BuildingConfigPacket.IsValidMetadata(metadata);
			metadata = ValidMetadata();
			metadata.SliderIndex = -1;
			return !zeroNetId && !invalidType && !invalidFloat && !oversizedString
			       && !BuildingConfigPacket.IsValidMetadata(metadata);
		}

		private static BuildingConfigMetadata ValidMetadata() => new()
		{
			NetId = -42,
			Cell = 123,
			DeterministicId = -77,
			ConfigType = BuildingConfigType.Boolean,
			ReferenceNetId = -99,
			Value = 1f,
			StringValue = "state"
		};

		[UnitTest(name: "Building config semantic primitives reject coercion", category: "Networking")]
		public static UnitTestResult SemanticPrimitivesRejectCoercion()
		{
			if (!BuildingConfigPacket.IsBooleanValue(0f)
			    || !BuildingConfigPacket.IsBooleanValue(1f)
			    || BuildingConfigPacket.IsBooleanValue(0.51f)
			    || !BuildingConfigPacket.IsIntegralValue(-77f)
			    || BuildingConfigPacket.IsIntegralValue(1.5f)
			    || !BuildingConfigPacket.IsInRange(5f, 0f, 5f)
			    || BuildingConfigPacket.IsInRange(-1f, 0f, 5f))
				return UnitTestResult.Fail("Boolean, integer, or range validation accepted coercion");

			return UnitTestResult.Pass("Semantic primitives require exact booleans, integers, and bounds");
		}

		[UnitTest(name: "Building config paired strings round-trip atomically", category: "Networking")]
		public static UnitTestResult PairedStringsRoundTripAtomically()
		{
			var packet = new BuildingConfigPacket
			{
				NetId = -42,
				Cell = 123,
				DeterministicBuildingId = -77,
				TargetLifecycleRevision = 7,
				StateRevision = 1,
				ConfigHash = 19,
				ConfigType = BuildingConfigType.String,
				StringValue = "entity",
				SecondaryStringValue = "filter"
			};
			typeof(BuildingConfigPacket).GetField(
				"Sender", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(packet, 9UL);

			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);

			stream.Position = 0;
			var copy = new BuildingConfigPacket();
			using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);

			return copy.StringValue == "entity" && copy.SecondaryStringValue == "filter"
			       && copy.StateRevision == 1 && copy.TargetLifecycleRevision == 7
				? UnitTestResult.Pass("Paired strings preserve one packet boundary")
				: UnitTestResult.Fail("Paired strings changed during serialization");
		}
	}
}

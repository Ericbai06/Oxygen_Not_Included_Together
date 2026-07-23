using System;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class TypedEvidenceEnvelopeTests
	{
		[UnitTest(name: "Typed evidence JSON round trips concrete target, state, and canonical hash", category: "Integration")]
		public static UnitTestResult ConcreteEnvelopeRoundTrips()
		{
			TypedEvidenceEnvelope expected = TypedEvidenceTestFixture.RemoteDigEnvelope();
			if (!typeof(ITypedEvidenceTarget).IsAssignableFrom(typeof(RemoteDigTarget))
			    || !typeof(ITypedEvidenceState).IsAssignableFrom(typeof(RemoteDigState)))
				return UnitTestResult.Fail("Remote-dig target/state do not implement typed evidence interfaces");
			if (TypedEvidenceContract.Validate(expected).Count != 0)
				return UnitTestResult.Fail("A complete remote-dig typed envelope was rejected");

			string line = TypedEvidenceLogCodec.Serialize(expected);
			if (!line.StartsWith("[IntegrationEvidence] {", StringComparison.Ordinal))
				return UnitTestResult.Fail("Typed evidence did not use the one-line JSON envelope");
			TypedEvidenceEnvelope actual = TypedEvidenceLogCodec.Parse(line);
			if (!(actual.Target is RemoteDigTarget target) || target.TargetCell != 42
			    || !(actual.State is RemoteDigState state) || state.Action != "Digging"
			    || state.Animation != "dig_loop" || state.Tool != "DigTool"
			    || Math.Abs(state.Progress - 0.5) > 0.000001
			    || actual.StateHash != expected.StateHash)
				return UnitTestResult.Fail("Concrete target/state or canonical hash changed during round trip");

			return UnitTestResult.Pass("Typed JSON preserves the real target, state, and canonical hash");
		}

		[UnitTest(name: "Typed evidence rejects missing envelope facts and mismatched state hash", category: "Integration")]
		public static UnitTestResult MissingEnvelopeFactsAreRejected()
		{
			TypedEvidenceEnvelope missingRun = TypedEvidenceTestFixture.RemoteDigEnvelope();
			missingRun.RunId = null;
			if (TypedEvidenceContract.Validate(missingRun).Count == 0)
				return UnitTestResult.Fail("Missing runId was accepted");

			TypedEvidenceEnvelope missingTarget = TypedEvidenceTestFixture.RemoteDigEnvelope();
			missingTarget.Target = null;
			if (TypedEvidenceContract.Validate(missingTarget).Count == 0)
				return UnitTestResult.Fail("Missing typed target was accepted");

			TypedEvidenceEnvelope wrongHash = TypedEvidenceTestFixture.RemoteDigEnvelope();
			wrongHash.StateHash = "sha256:" + new string('0', 64);
			return TypedEvidenceContract.Validate(wrongHash).Count == 0
				? UnitTestResult.Fail("A state hash unrelated to the canonical state was accepted")
				: UnitTestResult.Pass("Required facts and canonical state hash are fail-closed");
		}

		[UnitTest(name: "Typed evidence parser rejects legacy delimiter grammar", category: "Integration")]
		public static UnitTestResult LegacyGrammarIsRejected()
		{
			const string legacy =
				"[IntegrationEvidence] scenario=door;phase=final-state;revision=3;state=open";
			try
			{
				TypedEvidenceLogCodec.Parse(legacy);
				return UnitTestResult.Fail("Legacy arbitrary string-state grammar was accepted");
			}
			catch (FormatException)
			{
				return UnitTestResult.Pass("Only one-line typed JSON evidence is accepted");
			}
		}

		[UnitTest(name: "Integration evidence logger has no arbitrary string-state overload", category: "Integration")]
		public static UnitTestResult LoggerAcceptsOnlyTypedEnvelope()
		{
			MethodInfo[] methods = typeof(IntegrationScenarioEvidenceCore)
				.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.Where(method => method.Name == nameof(IntegrationScenarioEvidenceCore.Log))
				.ToArray();
			bool hasTypedOnly = methods.Length == 1 && methods[0].GetParameters().Length == 1
			                    && methods[0].GetParameters()[0].ParameterType == typeof(TypedEvidenceEnvelope);
			bool hasStringParameter = methods.SelectMany(method => method.GetParameters())
				.Any(parameter => parameter.ParameterType == typeof(string));
			return hasTypedOnly && !hasStringParameter
				? UnitTestResult.Pass("Evidence logging exposes only the typed envelope seam")
				: UnitTestResult.Fail("An arbitrary string-state evidence Log overload is public");
		}
	}
}

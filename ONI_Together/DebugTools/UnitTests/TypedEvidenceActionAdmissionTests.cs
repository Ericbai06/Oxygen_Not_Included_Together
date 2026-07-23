using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class TypedEvidenceActionAdmissionTests
	{
		private const string Prefix = "[IntegrationEvidence] ";
		private static readonly string[] AdmissionKeys =
		{
			"actionGeneration", "actionCorrelation", "actionSequence",
		};
		private static readonly string[] BaseKeys =
		{
			"schemaVersion", "runId", "dllHash", "scenario", "entryId", "role",
			"sessionEpoch", "connectionGeneration", "snapshotGeneration", "phase",
			"revisionDomain", "revision", "sequence", "target", "state", "stateHash",
		};

		[UnitTest(name: "Typed evidence: ordinary codec omits action admission",
			category: "Integration")]
		public static UnitTestResult OrdinaryCodecOmitsActionAdmission()
		{
			TypedEvidenceEnvelope ordinary = TypedEvidenceTestFixture.RemoteDigEnvelope();
			JObject json = Json(TypedEvidenceLogCodec.Serialize(ordinary));
			var failures = new List<string>();
			if (!SameKeys(json, BaseKeys))
				failures.Add("ordinary envelope key set is not the exact 16-key contract");
			string[] present = AdmissionKeys.Where(key => json[key] != null).ToArray();
			if (present.Length != 0)
				failures.Add("serialized action fields: " + string.Join(",", present));
			foreach (string key in AdmissionKeys) json.Remove(key);
			try
			{
				TypedEvidenceEnvelope parsed = TypedEvidenceLogCodec.Parse(Line(json));
				if (parsed.ActionGeneration != 0 || parsed.ActionSequence != 0
				    || !string.IsNullOrEmpty(parsed.ActionCorrelation))
					failures.Add("parse invented action admission");
			}
			catch (FormatException exception)
			{
				failures.Add("conditional envelope rejected: " + exception.Message);
			}
			return failures.Count == 0
				? UnitTestResult.Pass("Ordinary evidence round-trips without action admission")
				: UnitTestResult.Fail("Ordinary evidence " + string.Join("; ", failures));
		}

		[UnitTest(name: "Typed evidence: action codec requires exact admission",
			category: "Integration")]
		public static UnitTestResult ActionCodecRequiresExactAdmission()
		{
			TypedEvidenceEnvelope action = ActionEnvelope();
			JObject json = Json(TypedEvidenceLogCodec.Serialize(action));
			if (!SameKeys(json, BaseKeys.Concat(AdmissionKeys)))
				return UnitTestResult.Fail("Action envelope is not the exact 19-key contract");
			if (json.Value<long>("actionGeneration") != 7
			    || json.Value<string>("actionCorrelation") != "corr-action-7"
			    || json.Value<long>("actionSequence") != 11)
				return UnitTestResult.Fail("Action admission values were not serialized exactly");
			TypedEvidenceEnvelope parsed = TypedEvidenceLogCodec.Parse(Line(json));
			if (parsed.ActionGeneration != 7 || parsed.ActionCorrelation != "corr-action-7"
			    || parsed.ActionSequence != 11)
				return UnitTestResult.Fail("Action admission did not survive codec round-trip");
			foreach (string key in AdmissionKeys)
			{
				JObject missing = (JObject)json.DeepClone();
				missing.Remove(key);
				if (!ParseRejected(missing))
					return UnitTestResult.Fail("Action evidence accepted missing " + key);
			}
			JObject extra = (JObject)json.DeepClone();
			extra["unexpected"] = true;
			if (!ParseRejected(extra))
				return UnitTestResult.Fail("Action evidence accepted an extra top-level field");
			return UnitTestResult.Pass("Action admission fields are required and exact");
		}

		[UnitTest(name: "Typed evidence: partial action admission shapes reject",
			category: "Integration")]
		public static UnitTestResult PartialActionAdmissionShapesReject()
		{
			string[][] subsets =
			{
				new[] { AdmissionKeys[0] }, new[] { AdmissionKeys[1] },
				new[] { AdmissionKeys[2] }, new[] { AdmissionKeys[0], AdmissionKeys[1] },
				new[] { AdmissionKeys[0], AdmissionKeys[2] },
				new[] { AdmissionKeys[1], AdmissionKeys[2] },
			};
			JObject ordinary = Json(TypedEvidenceLogCodec.Serialize(
				TypedEvidenceTestFixture.RemoteDigEnvelope()));
			foreach (string[] subset in subsets)
			{
				JObject partial = (JObject)ordinary.DeepClone();
				foreach (string key in subset) partial[key] = AdmissionValue(key);
				if (!ParseRejected(partial))
					return UnitTestResult.Fail("Accepted partial admission: " +
						string.Join(",", subset));
			}
			return UnitTestResult.Pass("All six proper action-admission subsets reject");
		}

		[UnitTest(name: "Typed evidence: invalid action admission fails closed",
			category: "Integration")]
		public static UnitTestResult InvalidActionAdmissionFailsClosed()
		{
			TypedEvidenceEnvelope action = ActionEnvelope();
			action.ActionGeneration = 0;
			if (TypedEvidenceContract.Validate(action).Count == 0)
				return UnitTestResult.Fail("Action evidence accepted generation zero");
			action = ActionEnvelope();
			action.ActionCorrelation = "wrong|correlation";
			if (TypedEvidenceContract.Validate(action).Count == 0)
				return UnitTestResult.Fail("Action evidence accepted invalid correlation token");
			action = ActionEnvelope();
			action.ActionSequence = 0;
			return TypedEvidenceContract.Validate(action).Count != 0
				? UnitTestResult.Pass("Invalid generation, correlation, and sequence fail closed")
				: UnitTestResult.Fail("Action evidence accepted sequence zero");
		}

		[UnitTest(name: "Typed evidence: canonical state and envelope hashes are stable",
			category: "Integration")]
		public static UnitTestResult CanonicalHashesAreStable()
		{
			MethodInfo hash = typeof(TypedEvidenceContract).GetMethod(
				"ComputeEnvelopeHash", BindingFlags.Static | BindingFlags.Public |
				BindingFlags.NonPublic, null, new[] { typeof(TypedEvidenceEnvelope) }, null);
			if (hash == null || hash.ReturnType != typeof(string))
				return UnitTestResult.Fail("Canonical envelope hash API is missing");
			TypedEvidenceEnvelope original = ActionEnvelope();
			string serialized = TypedEvidenceLogCodec.Serialize(original);
			TypedEvidenceEnvelope parsed = TypedEvidenceLogCodec.Parse(serialized);
			TypedEvidenceEnvelope reordered = TypedEvidenceLogCodec.Parse(
				Line(ReverseObjects(Json(serialized))));
			string originalHash = (string)hash.Invoke(null, new object[] { original });
			string parsedHash = (string)hash.Invoke(null, new object[] { parsed });
			string reorderedHash = (string)hash.Invoke(null, new object[] { reordered });
			TypedEvidenceEnvelope changedState = TypedEvidenceLogCodec.Parse(serialized);
			((RemoteDigState)changedState.State).Progress = 0.75;
			changedState.StateHash = TypedEvidenceContract.ComputeStateHash(changedState.State);
			TypedEvidenceEnvelope changedEntry = TypedEvidenceLogCodec.Parse(serialized);
			changedEntry.EntryId = "sync:other-entry";
			string changedStateHash = (string)hash.Invoke(null, new object[] { changedState });
			string changedEntryHash = (string)hash.Invoke(null, new object[] { changedEntry });
			bool stable = original.StateHash == parsed.StateHash
			              && originalHash == parsedHash && originalHash == reorderedHash
			              && originalHash?.StartsWith("sha256:", StringComparison.Ordinal) == true
			              && original.StateHash != changedState.StateHash
			              && originalHash != changedStateHash && originalHash != changedEntryHash;
			return stable ? UnitTestResult.Pass("State and envelope hashes survive codec round-trip")
				: UnitTestResult.Fail("Canonical state or envelope hash changed after round-trip");
		}

		private static TypedEvidenceEnvelope ActionEnvelope()
		{
			TypedEvidenceEnvelope value = TypedEvidenceTestFixture.RemoteDigEnvelope();
			value.Role = "client";
			value.Phase = "client-apply";
			value.EntryId = "sync:client-action-apply";
			value.ActionGeneration = 7;
			value.ActionCorrelation = "corr-action-7";
			value.ActionSequence = 11;
			return value;
		}

		private static JObject Json(string line)
			=> JObject.Parse(line.Substring(Prefix.Length));

		private static string Line(JObject json)
			=> Prefix + json.ToString(Newtonsoft.Json.Formatting.None);

		private static bool SameKeys(JObject json, IEnumerable<string> expected)
			=> new HashSet<string>(json.Properties().Select(property => property.Name))
				.SetEquals(expected);

		private static JToken AdmissionValue(string key)
		{
			if (key == "actionGeneration") return 7L;
			if (key == "actionCorrelation") return "corr-action-7";
			return 11L;
		}

		private static JObject ReverseObjects(JObject source)
		{
			var result = new JObject();
			foreach (JProperty property in source.Properties().Reverse())
			{
				JToken value = property.Value is JObject child
					? ReverseObjects(child) : property.Value.DeepClone();
				result.Add(property.Name, value);
			}
			return result;
		}

		private static bool ParseRejected(JObject json)
		{
			try
			{
				TypedEvidenceLogCodec.Parse(Line(json));
				return false;
			}
			catch (FormatException)
			{
				return true;
			}
		}
	}
}

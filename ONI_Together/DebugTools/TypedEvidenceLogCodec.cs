#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace ONI_Together.DebugTools
{
	public static class TypedEvidenceLogCodec
	{
		private const string Prefix = "[IntegrationEvidence] ";
		private static readonly string[] BaseEnvelopeKeys =
		{
			"schemaVersion", "runId", "dllHash", "scenario", "entryId", "role",
			"sessionEpoch", "connectionGeneration", "snapshotGeneration", "phase",
			"revisionDomain", "revision", "sequence", "target", "state", "stateHash",
		};
		private static readonly string[] AdmissionKeys =
		{
			"actionGeneration", "actionCorrelation", "actionSequence",
		};
		private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
		{
			ContractResolver = new CamelCasePropertyNamesContractResolver(),
			Formatting = Formatting.None,
			MissingMemberHandling = MissingMemberHandling.Error,
			NullValueHandling = NullValueHandling.Include,
		};

		public static string Serialize(TypedEvidenceEnvelope evidence)
		{
			IReadOnlyList<string> errors = TypedEvidenceContract.Validate(evidence);
			if (errors.Count != 0)
				throw new ArgumentException(string.Join("; ", errors), nameof(evidence));
			return Prefix + JObject.FromObject(evidence, CreateSerializer()).ToString(Formatting.None);
		}

		public static TypedEvidenceEnvelope Parse(string line)
		{
			if (line == null) throw new ArgumentNullException(nameof(line));
			if (!line.StartsWith(Prefix + "{", StringComparison.Ordinal) || line.IndexOfAny(new[] { '\r', '\n' }) >= 0)
				throw new FormatException("Typed evidence must be one prefixed JSON object.");
			try
			{
				JObject json = JObject.Parse(line.Substring(Prefix.Length));
				RequireExactKeys(json);
				TypedEvidenceEnvelope result = ParseEnvelope(json);
				IReadOnlyList<string> errors = TypedEvidenceContract.Validate(result);
				if (errors.Count != 0) throw new FormatException(string.Join("; ", errors));
				return result;
			}
			catch (JsonException exception)
			{
				throw new FormatException("Typed evidence JSON is invalid.", exception);
			}
		}

		private static TypedEvidenceEnvelope ParseEnvelope(JObject json)
		{
			RequireTokenTypes(json);
			string scenario = json.Value<string>("scenario");
			EvidenceTypes types = TypedEvidenceContract.GetTypes(scenario);
			RequireObjectShape((JObject)json["target"], types.TargetType);
			RequireObjectShape((JObject)json["state"], types.StateType);
			return new TypedEvidenceEnvelope
			{
				SchemaVersion = json.Value<int>("schemaVersion"),
				RunId = json.Value<string>("runId"), DllHash = json.Value<string>("dllHash"),
				Scenario = scenario, EntryId = json.Value<string>("entryId"), Role = json.Value<string>("role"),
				SessionEpoch = json.Value<long>("sessionEpoch"), ConnectionGeneration = json.Value<long>("connectionGeneration"),
				SnapshotGeneration = json.Value<long>("snapshotGeneration"), Phase = json.Value<string>("phase"),
				RevisionDomain = json.Value<string>("revisionDomain"), Revision = json.Value<long>("revision"),
				Sequence = json.Value<long>("sequence"),
				ActionGeneration = json.Value<long?>("actionGeneration") ?? 0,
				ActionCorrelation = json.Value<string>("actionCorrelation") ?? string.Empty,
				ActionSequence = json.Value<long?>("actionSequence") ?? 0,
				Target = (ITypedEvidenceTarget)json["target"].ToObject(types.TargetType, CreateSerializer()),
				State = (ITypedEvidenceState)json["state"].ToObject(types.StateType, CreateSerializer()),
				StateHash = json.Value<string>("stateHash"),
			};
		}

		private static void RequireExactKeys(JObject json)
		{
			string[] actual = json.Properties().Select(property => property.Name).ToArray();
			bool hasAdmission = AdmissionKeys.Any(key => json[key] != null);
			string[] expected = hasAdmission
				? BaseEnvelopeKeys.Concat(AdmissionKeys).ToArray()
				: BaseEnvelopeKeys;
			if (actual.Length != expected.Length || expected.Except(actual, StringComparer.Ordinal).Any())
				throw new FormatException("Typed evidence envelope fields are not exact.");
		}

		private static void RequireTokenTypes(JObject json)
		{
			RequireType(json, "schemaVersion", JTokenType.Integer);
			foreach (string key in new[] { "runId", "dllHash", "scenario", "entryId", "role", "phase", "revisionDomain", "stateHash" })
				RequireType(json, key, JTokenType.String);
			foreach (string key in new[] { "sessionEpoch", "connectionGeneration", "snapshotGeneration", "revision", "sequence" })
				RequireType(json, key, JTokenType.Integer);
			if (json["actionGeneration"] != null)
			{
				RequireType(json, "actionGeneration", JTokenType.Integer);
				RequireType(json, "actionCorrelation", JTokenType.String);
				RequireType(json, "actionSequence", JTokenType.Integer);
			}
			RequireType(json, "target", JTokenType.Object);
			RequireType(json, "state", JTokenType.Object);
		}

		private static void RequireType(JObject json, string key, JTokenType type)
		{
			if (json[key] == null || json[key].Type != type)
				throw new FormatException("Typed evidence field has wrong type: " + key);
		}

		private static void RequireObjectShape(JObject value, Type type)
		{
			string[] expected = type.GetProperties().Select(property =>
			{
				var attribute = (JsonPropertyAttribute)Attribute.GetCustomAttribute(
					property, typeof(JsonPropertyAttribute));
				return attribute?.PropertyName ?? char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);
			}).ToArray();
			string[] actual = value.Properties().Select(property => property.Name).ToArray();
			if (actual.Length != expected.Length || expected.Except(actual, StringComparer.Ordinal).Any())
				throw new FormatException("Typed evidence target/state fields are not exact.");
		}

		private static JsonSerializer CreateSerializer() => JsonSerializer.Create(Settings);
	}
}
#endif

using System;
using System.Collections.Generic;
using System.Globalization;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal sealed class ScenarioActionCommand
	{
		private sealed class SelectorSchema
		{
			internal string Kind { get; }
			internal string[] Fields { get; }
			internal bool CleanupOnly { get; }

			internal SelectorSchema(string kind, params string[] fields)
				: this(kind, false, fields)
			{
			}

			internal SelectorSchema(string kind, bool cleanupOnly, params string[] fields)
			{
				Kind = kind;
				Fields = fields;
				CleanupOnly = cleanupOnly;
			}
		}

		private static readonly IReadOnlyDictionary<string, SelectorSchema> Schemas =
			new Dictionary<string, SelectorSchema>(StringComparer.Ordinal)
			{
				["remote-dig"] = new SelectorSchema("cell", "cell", "kind"),
				["priority"] = new SelectorSchema("netId", "kind", "netId"),
				["building-config"] = new SelectorSchema("netId", "kind", "netId"),
				["door"] = new SelectorSchema("netId", "kind", "netId"),
				["uproot"] = new SelectorSchema("netId", "kind", "netId"),
				["toggle"] = new SelectorSchema("netId", "kind", "netId"),
				["research"] = new SelectorSchema("tech", "kind", "techId"),
				["schedule"] = new SelectorSchema("schedule", "kind", "scheduleId"),
				["inventory"] = new SelectorSchema("inventory", "kind"),
				["storage"] = new SelectorSchema(
					"storage", "itemNetId", "kind", "storageNetId"),
				["pickup"] = new SelectorSchema(
					"pickup", "itemNetId", "kind", "targetCell"),
				["deconstruct"] = new SelectorSchema(
					"deconstruct", "buildingNetId", "kind", "targetCell"),
				["effect"] = new SelectorSchema("netId", "kind", "netId"),
				["chat"] = new SelectorSchema("sender", "kind", "sender"),
				["cursor"] = new SelectorSchema("player", "kind", "playerId"),
				["animation"] = new SelectorSchema("cell", "cell", "kind"),
				["motion"] = new SelectorSchema("netId", "kind", "netId"),
				["entity-lifecycle"] = new SelectorSchema("netId", "kind", "netId"),
				["dlc-runtime"] = new SelectorSchema(
					"dlc", "dlcFamily", "identity", "kind", "prefab"),
				["rocket"] = new SelectorSchema(
					"rocket", "kind", "padNetId", "rocketNetId"),
				["building-lifecycle"] = new SelectorSchema(
					"cell", true, "cell", "kind"),
				["reconnect-world-state"] = new SelectorSchema(
					"session", true, "kind", "sessionId"),
			};

		internal string RawCommand { get; }
		internal string Scenario { get; }
		internal bool IsCleanup { get; }
		internal string ActionProfile { get; }
		internal IReadOnlyDictionary<string, string> Selector { get; }

		private ScenarioActionCommand(
			string rawCommand,
			string scenario,
			bool isCleanup,
			string actionProfile,
			IReadOnlyDictionary<string, string> selector)
		{
			RawCommand = rawCommand;
			Scenario = scenario;
			IsCleanup = isCleanup;
			ActionProfile = actionProfile;
			Selector = selector;
		}

		internal static bool TryParse(string value, out ScenarioActionCommand command)
		{
			command = null;
			if (string.IsNullOrEmpty(value) || value.Length > 1024)
				return false;
			string[] parts = value.Split(':');
			if (parts.Length < 3 || !TryParsePrefix(parts[0], out bool cleanup)
			    || !Schemas.TryGetValue(parts[1], out SelectorSchema schema))
				return false;
			var selector = new Dictionary<string, string>(StringComparer.Ordinal);
			for (int index = 2; index < parts.Length; index++)
				if (!TryAddPair(parts[index], selector))
					return false;
			selector.TryGetValue("profile", out string profile);
			if (profile != null && !ScenarioActionProfileRegistry.IsAllowed(parts[1], profile))
				return false;
			selector.Remove("profile");
			if (schema.CleanupOnly && !cleanup || !MatchesSchema(selector, schema)
			    || !ValuesAreValid(selector))
				return false;
			command = new ScenarioActionCommand(value, parts[1], cleanup, profile, selector);
			return true;
		}

		internal bool TryGetInt(string key, out int value)
		{
			value = 0;
			return Selector.TryGetValue(key, out string raw)
			       && int.TryParse(raw, NumberStyles.AllowLeadingSign,
				       CultureInfo.InvariantCulture, out value);
		}

		internal bool TryGetUInt64(string key, out ulong value)
		{
			value = 0;
			return Selector.TryGetValue(key, out string raw)
			       && ulong.TryParse(raw, NumberStyles.None,
				       CultureInfo.InvariantCulture, out value);
		}

		private static bool TryParsePrefix(string value, out bool cleanup)
		{
			cleanup = false;
			if (value == "scenario-action")
				return true;
			if (value != "scenario-cleanup")
				return false;
			cleanup = true;
			return true;
		}

		private static bool TryAddPair(string part, IDictionary<string, string> selector)
		{
			int separator = part.IndexOf('=');
			if (separator <= 0 || separator != part.LastIndexOf('=')
			    || separator == part.Length - 1)
				return false;
			string key = part.Substring(0, separator);
			string value = part.Substring(separator + 1);
			return IsToken(key, 32) && IsToken(value, 128) && !selector.ContainsKey(key)
			       && Add(selector, key, value);
		}

		private static bool Add(IDictionary<string, string> selector, string key, string value)
		{
			selector.Add(key, value);
			return true;
		}

		private static bool MatchesSchema(
			IReadOnlyDictionary<string, string> selector,
			SelectorSchema schema)
		{
			if (selector.Count != schema.Fields.Length
			    || !selector.TryGetValue("kind", out string kind) || kind != schema.Kind)
				return false;
			foreach (string field in schema.Fields)
				if (!selector.ContainsKey(field))
					return false;
			return true;
		}

		private static bool ValuesAreValid(IReadOnlyDictionary<string, string> selector)
		{
			foreach (KeyValuePair<string, string> pair in selector)
			{
				if (pair.Key == "kind")
					continue;
				if (pair.Key == "sender" || pair.Key == "playerId")
				{
					if (!ulong.TryParse(pair.Value, NumberStyles.None,
						    CultureInfo.InvariantCulture, out ulong id) || id == 0)
						return false;
				}
				else if (pair.Key.EndsWith("NetId", StringComparison.Ordinal))
				{
					if (!int.TryParse(pair.Value, NumberStyles.AllowLeadingSign,
						    CultureInfo.InvariantCulture, out int id) || id == 0)
						return false;
				}
				else if (pair.Key == "cell" || pair.Key == "targetCell")
				{
					if (!int.TryParse(pair.Value, NumberStyles.None,
						    CultureInfo.InvariantCulture, out int cell) || cell < 0)
						return false;
				}
			}
			return true;
		}

		private static bool IsToken(string value, int maxLength)
		{
			if (string.IsNullOrEmpty(value) || value.Length > maxLength)
				return false;
			foreach (char character in value)
				if (!IsAsciiAlphaNumeric(character) && character != '-'
				    && character != '_' && character != '.')
					return false;
			return true;
		}

		private static bool IsAsciiAlphaNumeric(char value)
			=> value >= 'a' && value <= 'z' || value >= 'A' && value <= 'Z'
			   || value >= '0' && value <= '9';
	}
}
#endif

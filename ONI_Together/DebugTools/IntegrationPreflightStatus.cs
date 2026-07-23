#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ONI_Together.DebugTools
{
	public enum IntegrationTransportKind
	{
		Steam,
		NonSteam,
	}

	public sealed class IntegrationPreflightFacts
	{
		public string GameBuild { get; set; }
		public IntegrationTransportKind Transport { get; set; }
		public bool TransportReady { get; set; }
		public bool InSteamLobby { get; set; }
		public string SteamLobbyId { get; set; }
		public string NonSteamSessionIdentity { get; set; }
		public IEnumerable<string> ActiveDlcIds { get; set; }
		public int Protocol { get; set; }
		public string Role { get; set; }
		public IReadOnlyDictionary<string, string> StatusFields { get; set; }
	}

	public sealed class IntegrationPreflightStatus
	{
		public const string SupportedGameBuild = "U59-740622-S";
		private static readonly HashSet<string> ReservedFields = new(StringComparer.Ordinal)
		{
			"build", "sessionIdentity", "dlcFingerprint", "protocol", "role",
		};

		private readonly IReadOnlyList<KeyValuePair<string, string>> _fields;

		private IntegrationPreflightStatus(
			string build, string sessionIdentity, string dlcFingerprint,
			int protocol, string role, IReadOnlyDictionary<string, string> statusFields)
		{
			var fields = new List<KeyValuePair<string, string>>
			{
				new("build", build),
				new("sessionIdentity", sessionIdentity),
				new("dlcFingerprint", dlcFingerprint),
				new("protocol", protocol.ToString(CultureInfo.InvariantCulture)),
				new("role", role),
			};
			if (statusFields != null)
				fields.AddRange(statusFields.OrderBy(pair => pair.Key, StringComparer.Ordinal));
			_fields = fields;
		}

		public static bool TryCreate(
			IntegrationPreflightFacts facts,
			out IntegrationPreflightStatus status,
			out string error)
		{
			status = null;
			if (!TryValidate(facts, out string sessionIdentity, out error))
				return false;

			status = new IntegrationPreflightStatus(
				facts.GameBuild, sessionIdentity, ComputeDlcFingerprint(facts.ActiveDlcIds),
				facts.Protocol, facts.Role, facts.StatusFields);
			return true;
		}

		public string Format()
			=> string.Join(";", _fields.Select(pair => pair.Key + "=" + Escape(pair.Value)));

		internal static string Escape(string value)
		{
			if (value == null)
				return string.Empty;
			var result = new StringBuilder(value.Length);
			foreach (char character in value)
			{
				switch (character)
				{
					case '%': result.Append("%25"); break;
					case ';': result.Append("%3B"); break;
					case '=': result.Append("%3D"); break;
					case '\r': result.Append("%0D"); break;
					case '\n': result.Append("%0A"); break;
					default:
						if (char.IsControl(character))
							result.Append('%').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
						else
							result.Append(character);
						break;
				}
			}
			return result.ToString();
		}

		private static bool TryValidate(
			IntegrationPreflightFacts facts, out string sessionIdentity, out string error)
		{
			sessionIdentity = string.Empty;
			if (facts == null) return Fail("facts are required", out error);
			if (!string.Equals(facts.GameBuild, SupportedGameBuild, StringComparison.Ordinal))
				return Fail("build must be " + SupportedGameBuild, out error);
			if (!facts.TransportReady) return Fail("transport is not connected", out error);
			if (facts.Protocol != Networking.ProtocolCompatibility.CurrentProtocolVersion)
				return Fail("protocol is not supported", out error);
			if (facts.Role != "host" && facts.Role != "client")
				return Fail("role must be host or client", out error);
			if (facts.ActiveDlcIds == null) return Fail("DLC identity is missing", out error);
			if (!TryResolveSessionIdentity(facts, out sessionIdentity, out error)) return false;
			if (facts.StatusFields != null)
			{
				foreach (KeyValuePair<string, string> field in facts.StatusFields)
					if (!IsValidFieldName(field.Key) || ReservedFields.Contains(field.Key))
						return Fail("status field name is invalid or reserved", out error);
			}
			error = string.Empty;
			return true;
		}

		private static bool TryResolveSessionIdentity(
			IntegrationPreflightFacts facts, out string identity, out string error)
		{
			identity = string.Empty;
			if (facts.Transport == IntegrationTransportKind.Steam)
			{
				if (!facts.InSteamLobby
				    || !ulong.TryParse(facts.SteamLobbyId, NumberStyles.None,
					    CultureInfo.InvariantCulture, out ulong lobbyId) || lobbyId == 0)
					return Fail("Steam lobby identity is invalid", out error);
				identity = "steam:" + lobbyId.ToString(CultureInfo.InvariantCulture);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(facts.NonSteamSessionIdentity))
					return Fail("non-Steam session identity is missing", out error);
				identity = facts.NonSteamSessionIdentity;
			}
			error = string.Empty;
			return true;
		}

		private static string ComputeDlcFingerprint(IEnumerable<string> dlcIds)
		{
			string canonical = string.Join("\n", dlcIds
				.Where(id => !string.IsNullOrEmpty(id))
				.Distinct(StringComparer.Ordinal)
				.OrderBy(id => id, StringComparer.Ordinal));
			using SHA256 sha = SHA256.Create();
			byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
			return "sha256:" + string.Concat(digest.Select(value => value.ToString("x2")));
		}

		private static bool IsValidFieldName(string value)
			=> !string.IsNullOrEmpty(value) && char.IsLetter(value[0])
			   && value.All(character => char.IsLetterOrDigit(character));

		private static bool Fail(string message, out string error)
		{
			error = message;
			return false;
		}
	}
}
#endif

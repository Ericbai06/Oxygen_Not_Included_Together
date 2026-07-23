#if DEBUG
using System.Globalization;

namespace ONI_Together.Networking
{
	internal sealed class ScenarioActionAdmission
	{
		internal string Scenario;
		internal long Generation;
		internal string Correlation;
		internal long Sequence;

		internal ScenarioActionAdmission Copy(long? sequence = null)
			=> new()
			{
				Scenario = Scenario,
				Generation = Generation,
				Correlation = Correlation,
				Sequence = sequence ?? Sequence,
			};

		internal string ToMarker(char separator)
			=> Scenario + separator
			   + Generation.ToString(CultureInfo.InvariantCulture) + separator
			   + Correlation + separator
			   + Sequence.ToString(CultureInfo.InvariantCulture);

		internal static bool IsValidExpected(ScenarioActionAdmission value)
			=> value != null && IsToken(value.Scenario, 64)
			   && value.Generation > 0 && IsToken(value.Correlation, 128)
			   && value.Sequence >= 0;

		internal static bool TryParse(
			string marker, char separator, out ScenarioActionAdmission admission)
		{
			admission = null;
			string[] parts = marker?.Split(separator);
			if (parts?.Length != 4 || !IsToken(parts[0], 64)
			    || !long.TryParse(parts[1], NumberStyles.None,
				    CultureInfo.InvariantCulture, out long generation)
			    || generation <= 0 || !IsToken(parts[2], 128)
			    || !long.TryParse(parts[3], NumberStyles.None,
				    CultureInfo.InvariantCulture, out long sequence) || sequence <= 0)
				return false;
			admission = new ScenarioActionAdmission
			{
				Scenario = parts[0], Generation = generation,
				Correlation = parts[2], Sequence = sequence,
			};
			return true;
		}

		internal static bool TryParseArmCommand(
			string command, out ScenarioActionAdmission admission)
		{
			admission = null;
			string[] parts = command?.Split(':');
			if (parts?.Length != 4 || parts[0] != "scenario-arm"
			    || !TryPair(parts[1], "scenario", out string scenario)
			    || !TryPair(parts[2], "generation", out string generationText)
			    || !TryPair(parts[3], "correlation", out string correlation)
			    || !long.TryParse(generationText, NumberStyles.None,
				    CultureInfo.InvariantCulture, out long generation))
				return false;
			var candidate = new ScenarioActionAdmission
			{
				Scenario = scenario, Generation = generation,
				Correlation = correlation,
			};
			if (!IsValidExpected(candidate)) return false;
			admission = candidate;
			return true;
		}

		private static bool TryPair(
			string part, string expectedKey, out string value)
		{
			value = null;
			int separator = part?.IndexOf('=') ?? -1;
			if (separator <= 0 || separator != part.LastIndexOf('=')) return false;
			if (part.Substring(0, separator) != expectedKey) return false;
			value = part.Substring(separator + 1);
			return !string.IsNullOrEmpty(value);
		}

		private static bool IsToken(string value, int maximum)
		{
			if (string.IsNullOrEmpty(value) || value.Length > maximum)
				return false;
			foreach (char character in value)
				if (!char.IsLetterOrDigit(character)
				    && character != '-' && character != '_' && character != '.')
					return false;
			return true;
		}
	}
}
#endif

namespace ONI_Together.DebugTools.UnitTests
{
	internal enum ScenarioActionAdmissionState
	{
		Accepted,
		NotArmed,
		GenerationMismatch,
		CorrelationMismatch,
		InvalidSequence,
		Duplicate,
		OutOfOrder,
	}

	internal sealed class ScenarioActionExpectedAdmission
	{
		internal bool Armed { get; set; }
		internal long Generation { get; set; }
		internal string Correlation { get; set; }
		internal long LastAcceptedSequence { get; set; }
	}

	internal sealed class ScenarioActionAdmissionToken
	{
		internal long Generation { get; set; }
		internal string Correlation { get; set; }
		internal long Sequence { get; set; }
	}

	internal sealed class ScenarioActionAdmissionResult
	{
		internal ScenarioActionAdmissionState State { get; set; }
		internal long Generation { get; set; }
		internal string Correlation { get; set; }
		internal long Sequence { get; set; }
		internal bool Accepted => State == ScenarioActionAdmissionState.Accepted;
	}

	internal static class ScenarioActionReceiverAdmissionContract
	{
		internal static ScenarioActionAdmissionResult TryEnter(
			ScenarioActionExpectedAdmission expected,
			ScenarioActionAdmissionToken token)
		{
			if (expected?.Armed != true)
				return Rejected(ScenarioActionAdmissionState.NotArmed);
			if (token == null || token.Generation != expected.Generation)
				return Rejected(ScenarioActionAdmissionState.GenerationMismatch);
			if (token.Correlation != expected.Correlation)
				return Rejected(ScenarioActionAdmissionState.CorrelationMismatch);
			if (token.Sequence <= 0)
				return Rejected(ScenarioActionAdmissionState.InvalidSequence);
			if (token.Sequence == expected.LastAcceptedSequence)
				return Rejected(ScenarioActionAdmissionState.Duplicate);
			if (token.Sequence < expected.LastAcceptedSequence)
				return Rejected(ScenarioActionAdmissionState.OutOfOrder);
			expected.LastAcceptedSequence = token.Sequence;
			return new ScenarioActionAdmissionResult
			{
				State = ScenarioActionAdmissionState.Accepted,
				Generation = token.Generation,
				Correlation = token.Correlation,
				Sequence = token.Sequence,
			};
		}

		private static ScenarioActionAdmissionResult Rejected(
			ScenarioActionAdmissionState state)
			=> new() { State = state };
	}
}

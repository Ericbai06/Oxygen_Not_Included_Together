#if DEBUG
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.Networking
{
	internal sealed class ScenarioActionFlowEvidence
	{
		internal string Scenario;
		internal long Revision;
		internal ITypedEvidenceTarget Target;
		internal ITypedEvidenceState State;
		internal string EntryId;
		internal object Packet;
		internal ScenarioActionAdmission Admission;
	}

	internal static class ScenarioActionFlowContext
	{
		private sealed class Box { internal object Value; }
		private static readonly ConditionalWeakTable<object, Box> Values = new();

		internal static T Attach<T>(T owner, object value) where T : class
		{
			if (owner == null) return null;
			Values.Remove(owner);
			Values.Add(owner, new Box { Value = value });
			return owner;
		}

		internal static T Get<T>(object owner) where T : class
			=> owner != null && Values.TryGetValue(owner, out Box box)
				? box.Value as T : null;
	}

	internal static class ScenarioActionFlowTransport
	{
		internal static void Send<TPacket>(TPacket packet) where TPacket : class, IPacket
		{
			if (packet != null)
				PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
		}

		internal static void ObserveHost<TState>(TState state) where TState : class, ITypedEvidenceState
			=> Observe(state, "host-submit");

		internal static void ObserveClient<TState>(TState state) where TState : class, ITypedEvidenceState
			=> Observe(state, "client-apply");

		internal static void ObserveCleanup<TState>(TState state) where TState : class, ITypedEvidenceState
			=> Observe(state, "host-submit");

		private static void Observe<TState>(TState state, string phase)
			where TState : class, ITypedEvidenceState
		{
			ScenarioActionFlowEvidence evidence = ScenarioActionFlowContext.Get<ScenarioActionFlowEvidence>(state);
			if (evidence == null) return;
			bool clientApply = phase == "client-apply";
			ScenarioActionAdmission admission = evidence.Admission
				?? (clientApply
					? ScenarioActionReceiverGate.CurrentAccepted(evidence.Scenario)
					: ScenarioActionReceiverGate.CurrentExpected(evidence.Scenario));
			evidence.Admission = admission;
			IntegrationScenarioEvidenceCore.Log(TypedEvidenceRuntimeContext.Create(
				evidence.Scenario, phase, evidence.Revision,
				evidence.Target, evidence.State, evidence.EntryId,
				actionGeneration: admission?.Generation ?? 0,
				actionCorrelation: admission?.Correlation ?? string.Empty,
				actionSequence: admission?.Sequence ?? 0));
		}
	}

	internal static class ScenarioActionReceiverGate
	{
		private const char Separator = '|';
		private static readonly object Sync = new();
		private static readonly Dictionary<string, ScenarioActionAdmission> Expected = new();
		[System.ThreadStatic] private static ScenarioActionAdmission _accepted;

		internal static void ArmExpected(ScenarioActionAdmission admission)
		{
			if (!ScenarioActionAdmission.IsValidExpected(admission))
				throw new System.ArgumentException("Invalid scenario action admission.", nameof(admission));
			lock (Sync)
				Expected[admission.Scenario] = admission.Copy(sequence: 0);
			_accepted = null;
		}

		internal static bool IsArmed(string scenario)
		{
			lock (Sync) return Expected.ContainsKey(scenario);
		}

		internal static string Mark(string scenario)
		{
			lock (Sync)
			{
				if (!Expected.TryGetValue(scenario, out ScenarioActionAdmission expected))
					throw new System.InvalidOperationException("Scenario action admission is not armed.");
				expected.Sequence++;
				return expected.ToMarker(Separator);
			}
		}

		internal static bool TryEnter(string marker, string scenario)
			=> TryEnter(marker, scenario, out _);

		internal static bool TryEnter(
			string marker, string scenario, out ScenarioActionAdmission admission)
		{
			admission = null;
			_accepted = null;
			DispatchContext context = PacketHandler.CurrentContext;
			if (string.IsNullOrEmpty(marker) || MultiplayerSession.IsHost
			    || !context.SenderIsHost || !PacketHandler.IsCurrentDispatchContext(context))
				return false;
			if (!ScenarioActionAdmission.TryParse(marker, Separator, out var token)
			    || !string.Equals(token.Scenario, scenario, System.StringComparison.Ordinal))
				return false;
			lock (Sync)
			{
				if (!Expected.TryGetValue(scenario, out ScenarioActionAdmission expected)
				    || token.Generation != expected.Generation
				    || !string.Equals(token.Correlation, expected.Correlation,
					    System.StringComparison.Ordinal)
				    || token.Sequence <= expected.Sequence)
					return false;
				expected.Sequence = token.Sequence;
				admission = token.Copy();
				_accepted = admission;
				return true;
			}
		}

		internal static ScenarioActionAdmission CurrentAccepted(string scenario)
			=> _accepted?.Scenario == scenario ? _accepted.Copy() : null;

		internal static ScenarioActionAdmission CurrentExpected(string scenario)
		{
			lock (Sync)
				return Expected.TryGetValue(scenario, out ScenarioActionAdmission expected)
					? expected.Copy() : null;
		}
	}
}
#endif

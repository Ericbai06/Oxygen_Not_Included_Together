#if DEBUG
using System;
using System.Collections.Generic;

namespace ONI_Together.DebugTools
{
	internal sealed class FaultInjectionCommand
	{
		private const string FaultPrefix = "fault-inject:";
		private const string CleanPrefix = "fault-clean:";

		private FaultInjectionCommand(string caseId, bool cleanControl)
		{
			CaseId = caseId;
			IsCleanControl = cleanControl;
		}

		internal string CaseId { get; }
		internal bool IsCleanControl { get; }

		internal static bool TryParse(string value, out FaultInjectionCommand command)
		{
			command = null;
			if (!TryReadCaseId(value, out string caseId, out bool cleanControl)
			    || !FaultInjectionDriverRegistry.FaultCommands.ContainsKey(caseId))
				return false;
			command = new FaultInjectionCommand(caseId, cleanControl);
			return true;
		}

		private static bool TryReadCaseId(
			string value, out string caseId, out bool cleanControl)
		{
			caseId = null;
			cleanControl = false;
			if (value?.StartsWith(FaultPrefix, StringComparison.Ordinal) == true)
				caseId = value.Substring(FaultPrefix.Length);
			else if (value?.StartsWith(CleanPrefix, StringComparison.Ordinal) == true)
			{
				caseId = value.Substring(CleanPrefix.Length);
				cleanControl = true;
			}
			return !string.IsNullOrEmpty(caseId) && caseId.IndexOf(':') < 0;
		}
	}

	internal static class FaultInjectionDriverRegistry
	{
		internal static readonly IReadOnlyDictionary<string, string> FaultCommands =
			BuildCommands("fault-inject:");
		internal static readonly IReadOnlyDictionary<string, string> CleanControlCommands =
			BuildCommands("fault-clean:");

		internal static FaultInjectionReceipt Dispatch(FaultInjectionCommand command)
		{
			if (command == null)
				return FaultInjectionReceipt.Fail("dispatch", "invalid-fault-injection");
			if (command.IsCleanControl)
				return FaultUnityBindingRegistry.ExecuteCleanControl(command.CaseId);
			return FaultUnityBindingRegistry.ExecuteFault(command.CaseId);
		}

		private static IReadOnlyDictionary<string, string> BuildCommands(string prefix)
		{
			var commands = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (FaultUnityProductionBinding binding in FaultUnityBindingRegistry.Bindings)
				commands.Add(binding.CaseId, prefix + binding.CaseId);
			return commands;
		}
	}
}
#endif

using System;
using System.Collections.Generic;
using System.Threading;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	internal static class BuildMutationContext
	{
		private static readonly AsyncLocal<ScopeState> CurrentState = new();

		internal static BuildOperationId CurrentOperationId
			=> CurrentState.Value?.OperationId ?? default;

		internal static bool IsManaged
			=> CurrentState.Value != null;

		internal static IDisposable Enter(BuildOperationId operationId)
		{
			ScopeState previous = CurrentState.Value;
			CurrentState.Value = new ScopeState(operationId, previous);
			return new Scope(previous);
		}

		private sealed class ScopeState
		{
			internal readonly BuildOperationId OperationId;
			internal readonly ScopeState Parent;

			internal ScopeState(BuildOperationId operationId, ScopeState parent)
			{
				OperationId = operationId;
				Parent = parent;
			}
		}

		private sealed class Scope : IDisposable
		{
			private readonly ScopeState previous;
			private bool disposed;

			internal Scope(ScopeState previous)
			{
				this.previous = previous;
			}

			public void Dispose()
			{
				if (disposed)
					return;
				disposed = true;
				CurrentState.Value = previous;
			}
		}
	}

	internal static class BuildLifecycleRegistry
	{
		private static readonly Dictionary<int, BuildOperationId> Operations = new();

		internal static void Bind(int netId, BuildOperationId operationId)
		{
			if (netId != 0 && operationId.IsValid)
				Operations[netId] = operationId;
		}

		internal static bool TryGet(int netId, out BuildOperationId operationId)
			=> Operations.TryGetValue(netId, out operationId);

		internal static void Clear()
			=> Operations.Clear();
	}
}

using System;
using System.Collections.Generic;
using ONI_Together.Patches.ToolPatches.Build;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public sealed class BuildExecutionResult
	{
		public IReadOnlyList<PlacementOutcome> Placements { get; }
		public IReadOnlyList<UtilityEdge> Connections { get; }

		public BuildExecutionResult(
			IReadOnlyList<PlacementOutcome> placements,
			IReadOnlyList<UtilityEdge> connections)
		{
			Placements = placements ?? Array.Empty<PlacementOutcome>();
			Connections = connections ?? Array.Empty<UtilityEdge>();
		}
	}

	public interface IBuildRuntime
	{
		bool TryExecute(
			BuildRequest request,
			HostBuildPolicy policy,
			out BuildExecutionResult result,
			out BuildRejected rejection);

		ApplyResult Apply(BuildCommit commit);
	}

	public sealed class OniBuildRuntime : IBuildRuntime
	{
		public bool TryExecute(
			BuildRequest request,
			HostBuildPolicy policy,
			out BuildExecutionResult result,
			out BuildRejected rejection)
			=> BuildRuntimeAdapter.TryExecute(request, policy, out result, out rejection);

		public ApplyResult Apply(BuildCommit commit)
			=> BuildRuntimeAdapter.Apply(commit);
	}

	public static class BuildRuntimeProvider
	{
		private static IBuildRuntime current = new OniBuildRuntime();

		public static IBuildRuntime Current
		{
			get => current;
			set => current = value ?? throw new ArgumentNullException(nameof(value));
		}
	}

	public static class AuthoritativeBuildExecutor
	{
		private const int MaxRememberedOperations = 4096;
		private static readonly Dictionary<BuildOperationId, BuildResponse> Responses = new();
		private static readonly Queue<BuildOperationId> ResponseOrder = new();
		private static long sessionEpoch;

		public static bool Execute(
			BuildRequest request,
			HostBuildPolicy policy,
			out BuildCommit commit,
			out BuildRejected rejection)
		{
			commit = null;
			rejection = null;
			if (request == null || !request.OperationId.IsValid)
			{
				rejection = new BuildRejected(
					request?.OperationId ?? default,
					BuildRejectionReason.InvalidRequest,
					"build operation identity is invalid");
				return false;
			}
			if (sessionEpoch != 0 && request.OperationId.SessionEpoch != sessionEpoch)
			{
				rejection = RememberRejected(new BuildRejected(
					request.OperationId,
					BuildRejectionReason.StaleSession,
					"build operation belongs to an older session"));
				return false;
			}
			if (Responses.TryGetValue(request.OperationId, out BuildResponse prior))
			{
				commit = prior.Commit;
				rejection = prior.Rejection;
				return commit != null;
			}
		if (!BuildRequestValidator.TryValidate(request, out BuildRejected invalid))
		{
				rejection = RememberRejected(invalid);
				return false;
		}

		IBuildRuntime runtime = BuildRuntimeProvider.Current;
		using (BuildMutationContext.Enter(request.OperationId))
		{
			if (!runtime.TryExecute(request, policy, out BuildExecutionResult result,
				out rejection))
			{
				rejection ??= new BuildRejected(
					request.OperationId,
					BuildRejectionReason.PlacementFailed,
					"authoritative runtime rejected build");
				RememberRejected(rejection);
				return false;
			}
			commit = new BuildCommit(
				request,
				request.OperationId,
				result.Placements,
				result.Connections,
				new BuildRevision(NetworkIdentityRevision.Next()));
		}
		Remember(commit);
		return true;
	}

		public static void SetSessionEpoch(long value)
		{
			if (value <= 0 || value == sessionEpoch)
				return;
			sessionEpoch = value;
			Responses.Clear();
			ResponseOrder.Clear();
		}

		public static void Reset()
		{
			sessionEpoch = 0;
			Responses.Clear();
			ResponseOrder.Clear();
		}

		private static void Remember(BuildCommit value)
		{
			Responses[value.OperationId] = new BuildResponse(value, null);
			RememberOrder(value.OperationId);
		}

		private static BuildRejected RememberRejected(BuildRejected value)
		{
			if (value == null)
				return null;
			Responses[value.OperationId] = new BuildResponse(null, value);
			RememberOrder(value.OperationId);
			return value;
		}

		private static void RememberOrder(BuildOperationId operationId)
		{
			ResponseOrder.Enqueue(operationId);
			while (ResponseOrder.Count > MaxRememberedOperations)
			{
				BuildOperationId oldest = ResponseOrder.Dequeue();
				Responses.Remove(oldest);
			}
		}

		private readonly struct BuildResponse
		{
			internal readonly BuildCommit Commit;
			internal readonly BuildRejected Rejection;

			internal BuildResponse(BuildCommit commit, BuildRejected rejection)
			{
				Commit = commit;
				Rejection = rejection;
			}
		}
	}

	public static class BuildCommitApplier
	{
		private static readonly Dictionary<BuildOperationId, BuildRevision> Applied = new();

		public static ApplyResult Apply(BuildCommit commit)
		{
			if (commit == null || !commit.OperationId.IsValid || !commit.Revision.IsValid)
				return ApplyResult.Reject("build commit identity is invalid");
			if (Applied.TryGetValue(commit.OperationId, out BuildRevision current))
			{
				if (current.Value == commit.Revision.Value)
					return ApplyResult.Success(duplicate: true);
				if (commit.Revision.Value < current.Value)
					return ApplyResult.Reject("build commit revision is stale");
			}
			if (!BuildCommitValidator.TryValidate(commit, out string validationError))
				return ApplyResult.Reject(validationError);
			foreach (PlacementOutcome outcome in commit.Placements)
			{
				if (outcome == null || (outcome.Kind != BuildPlacementKind.Completed &&
					outcome.Kind != BuildPlacementKind.CompletedReplacement))
					continue;
				BuildRequest request = commit.Request;
				bool layerMatches = request != null && request.ObjectLayer >= 0;
				bool hasConstructable = request != null;
				bool prefabMatches = request != null &&
					BuildRequestValidator.IsBoundedId(request.PrefabId);
				bool cellMatches = request != null && ContainsCell(request.Geometry, outcome.Cell);
				if (!BuildLifecycleAdmission.CanComplete(layerMatches, outcome.HasIdentity,
					hasConstructable, prefabMatches, cellMatches))
					return ApplyResult.Reject("completed building lifecycle admission rejected");
			}
			ApplyResult result;
			using (BuildMutationContext.Enter(commit.OperationId))
				result = BuildRuntimeProvider.Current.Apply(commit);
			if (result.Applied)
				Applied[commit.OperationId] = commit.Revision;
			return result;
		}

		public static void Reset() => Applied.Clear();

		private static bool ContainsCell(BuildGeometry geometry, int cell)
		{
			if (geometry is BuildGeometry.SinglePlacement single)
				return single.Cell == cell;
			if (geometry is SinglePlacementGeometry singleGeometry)
				return singleGeometry.Cell == cell;
			IReadOnlyList<int> cells = geometry switch
			{
				BuildGeometry.UtilityPath utility => utility.Cells,
				UtilityPathGeometry utilityGeometry => utilityGeometry.Cells,
				_ => null
			};
			if (cells == null)
				return false;
			for (int index = 0; index < cells.Count; index++)
				if (cells[index] == cell)
					return true;
			return false;
		}
	}

	internal static class NetworkIdentityRevision
	{
		private static ulong next;

		internal static ulong Next()
		{
			next++;
			return next == 0 ? ++next : next;
		}
	}
}

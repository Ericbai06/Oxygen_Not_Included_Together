using System;
using System.Collections.Generic;
using System.Linq;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public readonly struct BuildOperationId : IEquatable<BuildOperationId>
	{
		public readonly long SessionEpoch;
		public readonly ulong SenderId;
		public readonly ulong Sequence;

		public BuildOperationId(long sessionEpoch, ulong senderId, ulong sequence)
		{
			SessionEpoch = sessionEpoch;
			SenderId = senderId;
			Sequence = sequence;
		}

		public bool IsValid => SessionEpoch > 0 && SenderId != 0 && Sequence != 0;

		public bool Equals(BuildOperationId other)
			=> SessionEpoch == other.SessionEpoch && SenderId == other.SenderId
			   && Sequence == other.Sequence;

		public override bool Equals(object obj)
			=> obj is BuildOperationId other && Equals(other);

		public override int GetHashCode()
			=> HashCode.Combine(SessionEpoch, SenderId, Sequence);

		public override string ToString()
			=> $"{SessionEpoch}:{SenderId}:{Sequence}";

		public static bool operator ==(BuildOperationId left, BuildOperationId right)
			=> left.Equals(right);

		public static bool operator !=(BuildOperationId left, BuildOperationId right)
			=> !left.Equals(right);
	}

	public abstract class BuildGeometry
	{
		protected BuildGeometry() { }

		public sealed class SinglePlacement : BuildGeometry
		{
			public int Cell { get; }
			public Orientation Orientation { get; }

			public SinglePlacement(int cell, Orientation orientation)
			{
				Cell = cell;
				Orientation = orientation;
			}
		}

		public sealed class UtilityPath : BuildGeometry
		{
			public IReadOnlyList<int> Cells { get; }

			public UtilityPath(IEnumerable<int> cells)
			{
				Cells = (cells ?? Enumerable.Empty<int>()).ToArray();
			}
		}
	}

	public sealed class SinglePlacementGeometry : BuildGeometry
	{
		public int Cell { get; }
		public Orientation Orientation { get; }

		public SinglePlacementGeometry(int cell, Orientation orientation)
		{
			Cell = cell;
			Orientation = orientation;
		}
	}

	public sealed class UtilityPathGeometry : BuildGeometry
	{
		public IReadOnlyList<int> Cells { get; }

		public UtilityPathGeometry(IEnumerable<int> cells)
		{
			Cells = (cells ?? Enumerable.Empty<int>()).ToArray();
		}
	}

	public sealed class BuildRequest
	{
		public BuildOperationId OperationId { get; }
		public string PrefabId { get; }
		public BuildGeometry Geometry { get; }
		public IReadOnlyList<string> MaterialTags { get; }
		public string FacadeId { get; }
		public int PriorityClass { get; }
		public int PriorityValue { get; }
		public int ObjectLayer { get; }

		public BuildRequest(
			BuildOperationId operationId,
			string prefabId,
			BuildGeometry geometry,
			IEnumerable<string> materialTags,
			string facadeId,
			int priorityClass,
			int priorityValue,
			int objectLayer)
		{
			OperationId = operationId;
			PrefabId = prefabId ?? string.Empty;
			Geometry = geometry;
			MaterialTags = (materialTags ?? Enumerable.Empty<string>()).ToArray();
			FacadeId = facadeId ?? string.Empty;
			PriorityClass = priorityClass;
			PriorityValue = priorityValue;
			ObjectLayer = objectLayer;
		}

		public bool IsUtility => Geometry is BuildGeometry.UtilityPath;
	}

	public enum BuildPlacementKind : byte
	{
		Queued = 1,
		Completed = 2,
		QueuedReplacement = 3,
		CompletedReplacement = 4
	}

	public sealed class PlacementOutcome
	{
		public int Cell { get; }
		public BuildPlacementKind Kind { get; }
		public int NetId { get; }
		public ulong LifecycleRevision { get; }

		public PlacementOutcome(
			int cell, BuildPlacementKind kind, int netId = 0, ulong lifecycleRevision = 0)
		{
			Cell = cell;
			Kind = kind;
			NetId = netId;
			LifecycleRevision = lifecycleRevision;
		}

		public bool HasIdentity => NetId != 0 && LifecycleRevision != 0;
	}

	public readonly struct UtilityEdge : IEquatable<UtilityEdge>
	{
		public readonly int FromCell;
		public readonly int ToCell;

		public UtilityEdge(int fromCell, int toCell)
		{
			FromCell = fromCell;
			ToCell = toCell;
		}

		public bool Equals(UtilityEdge other)
			=> FromCell == other.FromCell && ToCell == other.ToCell;

		public override bool Equals(object obj)
			=> obj is UtilityEdge other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(FromCell, ToCell);

		public static bool operator ==(UtilityEdge left, UtilityEdge right)
			=> left.Equals(right);

		public static bool operator !=(UtilityEdge left, UtilityEdge right)
			=> !left.Equals(right);
	}

	public readonly struct BuildRevision : IEquatable<BuildRevision>
	{
		public readonly ulong Value;

		public BuildRevision(ulong value) => Value = value;

		public bool IsValid => Value != 0;

		public bool Equals(BuildRevision other) => Value == other.Value;

		public override bool Equals(object obj)
			=> obj is BuildRevision other && Equals(other);

		public override int GetHashCode() => Value.GetHashCode();

		public static implicit operator ulong(BuildRevision revision) => revision.Value;
	}

	public sealed class BuildCommit
	{
		public BuildOperationId OperationId { get; }
		public BuildRequest Request { get; }
		public IReadOnlyList<PlacementOutcome> Placements { get; }
		public IReadOnlyList<UtilityEdge> Connections { get; }
		public BuildRevision Revision { get; }

		public BuildCommit(
			BuildOperationId operationId,
			IEnumerable<PlacementOutcome> placements,
			IEnumerable<UtilityEdge> connections,
			BuildRevision revision)
			: this(null, operationId, placements, connections, revision)
		{
		}

		public BuildCommit(
			BuildRequest request,
			BuildOperationId operationId,
			IEnumerable<PlacementOutcome> placements,
			IEnumerable<UtilityEdge> connections,
			BuildRevision revision)
		{
			Request = request;
			OperationId = operationId;
			Placements = (placements ?? Enumerable.Empty<PlacementOutcome>()).ToArray();
			Connections = (connections ?? Enumerable.Empty<UtilityEdge>()).Distinct().ToArray();
			Revision = revision;
		}
	}

	public enum BuildRejectionReason : byte
	{
		Unknown = 0,
		StaleSession = 1,
		DuplicateOperation = 2,
		InvalidRequest = 3,
		UnknownPrefab = 4,
		InvalidMaterial = 5,
		InvalidFacade = 6,
		InvalidGeometry = 7,
		Occupied = 8,
		PlacementFailed = 9,
		IdentityConflict = 10,
		RuntimeUnavailable = 11
	}

	public sealed class BuildRejected
	{
		public BuildOperationId OperationId { get; }
		public BuildRejectionReason Reason { get; }
		public string Message { get; }

		public BuildRejected(
			BuildOperationId operationId,
			BuildRejectionReason reason,
			string message)
		{
			OperationId = operationId;
			Reason = reason;
			Message = message ?? string.Empty;
		}
	}

	public readonly struct HostBuildPolicy
	{
		public readonly bool InstantBuild;

		public HostBuildPolicy(bool instantBuild) => InstantBuild = instantBuild;
	}

	public readonly struct ApplyResult
	{
		public bool Applied { get; }
		public bool Duplicate { get; }
		public string Error { get; }

		private ApplyResult(bool applied, bool duplicate, string error)
		{
			Applied = applied;
			Duplicate = duplicate;
			Error = error ?? string.Empty;
		}

		public static ApplyResult Success(bool duplicate = false)
			=> new(true, duplicate, string.Empty);

		public static ApplyResult Reject(string error)
			=> new(false, false, error);
	}
}

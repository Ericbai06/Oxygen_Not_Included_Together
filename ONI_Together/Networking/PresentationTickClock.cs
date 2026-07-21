using System;
using System.Threading;

namespace ONI_Together.Networking
{
	internal static class PresentationTickClock
	{
		internal const int TicksPerSecond = 5;
		private static long _localTick;
		private static long _hostOffset;
		private static long _lastEstimatedTick;
		private static ulong _lastWorldClockRevision;
		private static long _lastBaselineGeneration;

		internal static long CurrentTick
		{
			get
			{
				long local = Interlocked.Read(ref _localTick);
				if (!MultiplayerSession.IsClient)
					return local;
				return AdvanceEstimate(SaturatingAdd(
					local, Interlocked.Read(ref _hostOffset)));
			}
		}

		internal static void AdvanceLocalTick()
		{
			long local = Interlocked.Increment(ref _localTick);
			if (MultiplayerSession.IsClient)
				AdvanceEstimate(SaturatingAdd(
					local, Interlocked.Read(ref _hostOffset)));
		}

		internal static bool ObserveWorldClock(long hostTick, ulong revision)
		{
			if (hostTick < 0 || revision == 0 || revision <= _lastWorldClockRevision)
				return false;
			_lastWorldClockRevision = revision;
			ApplyHostTick(hostTick);
			return true;
		}

		internal static bool ObserveBaselineClock(long hostTick, long generation)
		{
			if (hostTick < 0 || generation <= 0 || generation <= _lastBaselineGeneration)
				return false;
			_lastBaselineGeneration = generation;
			ApplyHostTick(hostTick);
			return true;
		}

		internal static bool ResetCorrectionForBaseline(long hostTick, long generation)
		{
			if (hostTick < 0 || generation <= 0)
				return false;
			Interlocked.Exchange(ref _hostOffset, 0);
			_lastWorldClockRevision = 0;
			_lastBaselineGeneration = 0;
			return ObserveBaselineClock(hostTick, generation);
		}

		internal static uint DurationTicks(float seconds)
		{
			if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
				return 1;
			double ticks = Math.Ceiling(seconds * TicksPerSecond);
			return ticks >= uint.MaxValue ? uint.MaxValue : (uint)ticks;
		}

		internal static void ResetSessionState()
		{
			Interlocked.Exchange(ref _localTick, 0);
			Interlocked.Exchange(ref _hostOffset, 0);
			Interlocked.Exchange(ref _lastEstimatedTick, 0);
			_lastWorldClockRevision = 0;
			_lastBaselineGeneration = 0;
		}

		internal static void AdvanceLocalTickForTests() => AdvanceLocalTick();
		internal static bool ObserveWorldClockForTests(long tick, ulong revision)
			=> ObserveWorldClock(tick, revision);
		internal static bool ObserveBaselineClockForTests(long tick, long generation)
			=> ObserveBaselineClock(tick, generation);
		internal static long EstimatedHostTickForTests()
			=> AdvanceEstimate(SaturatingAdd(
				Interlocked.Read(ref _localTick), Interlocked.Read(ref _hostOffset)));

		private static void ApplyHostTick(long hostTick)
		{
			long local = Interlocked.Read(ref _localTick);
			long floor = AdvanceEstimate(hostTick);
			long desiredOffset = SaturatingSubtract(floor, local);
			Interlocked.Exchange(ref _hostOffset, desiredOffset);
		}

		private static long AdvanceEstimate(long candidate)
		{
			while (true)
			{
				long current = Interlocked.Read(ref _lastEstimatedTick);
				if (candidate <= current)
					return current;
				if (Interlocked.CompareExchange(
					    ref _lastEstimatedTick, candidate, current) == current)
					return candidate;
			}
		}

		private static long SaturatingAdd(long left, long right)
		{
			if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
			if (right < 0 && left < long.MinValue - right) return long.MinValue;
			return left + right;
		}

		private static long SaturatingSubtract(long left, long right)
		{
			if (right > 0 && left < long.MinValue + right) return long.MinValue;
			if (right < 0 && left > long.MaxValue + right) return long.MaxValue;
			return left - right;
		}
	}
}

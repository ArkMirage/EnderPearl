using System;
using System.Diagnostics;

namespace EnderPearl.Player
{
	/// <summary>
	/// Opt-in packet tracing for one player: the event-triggered trace window, the two direction
	/// sequence counters, and the session clock the trace lines are stamped with.
	///
	/// <para>Enabled by ENDERPEARL_LOG_PACKETS=1 (continuous) or ENDERPEARL_TRACE_MILLIS=&lt;ms&gt;
	/// (event-triggered window). Sequence methods are called while holding the owning
	/// <see cref="ProxyConnection"/>'s mutex; the window checks are lock-free by design because the
	/// relay reads them on every single packet.</para>
	/// </summary>
	public sealed class PacketTraceState
	{
		private static readonly bool LOG_PACKETS =
			Environment.GetEnvironmentVariable("ENDERPEARL_LOG_PACKETS") == "1";
		private static readonly int CONFIGURED_TRACE_MILLIS =
			int.TryParse(Environment.GetEnvironmentVariable("ENDERPEARL_TRACE_MILLIS"), out int traceMillis)
				? Math.Max(0, traceMillis)
				: 0;

		private readonly long createdAtNanos = NanoTime();
		private long traceUntilNanos;
		private long clientboundSequence;
		private long serverboundSequence;

		public static int ConfiguredTraceMillis => CONFIGURED_TRACE_MILLIS;

		public static bool IsConfigured => LOG_PACKETS || CONFIGURED_TRACE_MILLIS > 0;

		public static bool IsContinuous => LOG_PACKETS;

		public void TraceForMillis(long millis)
		{
			if (LOG_PACKETS || CONFIGURED_TRACE_MILLIS <= 0 || millis <= 0)
			{
				return;
			}
			traceUntilNanos = NanoTime() + millis * 1_000_000L;
		}

		public bool IsActive()
		{
			return LOG_PACKETS
				|| (CONFIGURED_TRACE_MILLIS > 0 && NanoTime() <= Volatile.Read(ref traceUntilNanos));
		}

		public long ElapsedMillis() => (NanoTime() - createdAtNanos) / 1_000_000L;

		public long NextClientboundSequence()
		{
			return ++clientboundSequence;
		}

		public long NextServerboundSequence()
		{
			return ++serverboundSequence;
		}

		public long ClientboundSequence() => clientboundSequence;

		public long ServerboundSequence() => serverboundSequence;

		/// <summary>Java's System.nanoTime: a monotonic nanosecond clock, independent of wall time.</summary>
		internal static long NanoTime()
		{
			return (long)(Stopwatch.GetTimestamp() * (double)1_000_000_000 / Stopwatch.Frequency);
		}
	}
}

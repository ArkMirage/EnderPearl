using System;
using System.Collections.Concurrent;

namespace EnderPearl.Backend
{
	/// <summary>
	/// The last sky each backend announced, keyed by backend name.
	///
	/// <para>Weather is a world property: everyone on a backend sees the same rain and thunder, so one
	/// record per backend is shared by every player on it - the same shape as <see
	/// cref="BackendBlockSchemes"/>. It exists because BDS is event-driven: it broadcasts a weather change
	/// and nothing else, so a client that switches in is never told the sky it is joining.</para>
	/// </summary>
	public static class BackendWeather
	{
		// LEVEL_EVENT_WORLD + n (Bedrock_v291): the wire EventId this codec writes verbatim, NOT the
		// cloudburst enum ordinals.
		public const int START_RAINING = 3001;
		public const int START_THUNDERSTORM = 3002;
		public const int STOP_RAINING = 3003;
		public const int STOP_THUNDERSTORM = 3004;

		private static readonly ConcurrentDictionary<string, bool> RainingByBackend = new(StringComparer.Ordinal);
		private static readonly ConcurrentDictionary<string, bool> ThunderingByBackend = new(StringComparer.Ordinal);

		/// <summary>Records the weather a backend just set; any other level event is not weather.</summary>
		public static void Observe(string backendName, int eventId)
		{
			switch (eventId)
			{
				case START_RAINING:
					RainingByBackend[backendName] = true;
					break;
				case STOP_RAINING:
					RainingByBackend[backendName] = false;
					break;
				case START_THUNDERSTORM:
					ThunderingByBackend[backendName] = true;
					break;
				case STOP_THUNDERSTORM:
					ThunderingByBackend[backendName] = false;
					break;
			}
		}

		/// <summary>
		/// The backend's sky, or false while it has never reported any weather - "never seen" stays
		/// distinguishable from "seen it clear", because the caller has to fall back differently.
		/// </summary>
		public static bool TryGet(string backendName, out bool raining, out bool thundering)
		{
			// Only one of the two has to have been seen: rain and thunder arrive as separate events, and a
			// backend that only ever rains never sends a thunder event at all.
			bool hasRain = RainingByBackend.TryGetValue(backendName, out raining);
			bool hasThunder = ThunderingByBackend.TryGetValue(backendName, out thundering);
			return hasRain || hasThunder;
		}
	}
}

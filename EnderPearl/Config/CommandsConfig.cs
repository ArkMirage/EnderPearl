using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace EnderPearl.Config
{
	/// <summary>
	/// Which of the proxy's own command names a backend is allowed to keep for itself.
	///
	/// <para>The proxy and a backend plugin routinely want the same name. A hub plugin's <c>/hub</c> is an
	/// in-world teleport to spawn and its <c>/server</c> opens a selector form; on a minigame backend
	/// with no such plugin the same two names have to mean the proxy's backend switch. Without this
	/// setting the proxy consumes both everywhere, the backend never sees the packet, and the client
	/// still renders the backend's description for a command the proxy answers — autocomplete from one
	/// server, behaviour from another.</para>
	///
	/// <para>A name listed for a backend is <em>forwarded</em> while the player is on that backend, and is
	/// left out of the command tree the proxy injects there, so the backend's own registration is the
	/// only one the client ever sees. Everywhere else the proxy keeps the name.</para>
	///
	/// <para><c>/proxy:hub</c> and <c>/proxy:server</c> reach the proxy's implementation whatever a
	/// backend does with the bare name. They are never forwarded and never injected into the command
	/// tree, so they do not appear in autocomplete, in <c>/help</c>, or in the client's parser — a
	/// player has to know the name to type it. Hidden is not the same as privileged:
	/// <see cref="EnderPearl.Backend.BackendCommandRouter"/> authorises the qualified form exactly as it
	/// authorises the bare one; the prefix decides <em>who handles the command</em>, never who may run it.</para>
	/// </summary>
	public sealed class CommandsConfig
	{
		public const string DEFAULT_QUALIFIER = "proxy:";

		private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

		/// <summary>Names every backend keeps, for backends with no list of their own.</summary>
		public IReadOnlySet<string> Passthrough { get; }

		/// <summary>Per-backend lists, which replace <see cref="Passthrough"/> rather than adding to it.</summary>
		public IReadOnlyDictionary<string, IReadOnlySet<string>> BackendPassthrough { get; }

		/// <summary>The prefix that forces proxy handling, or empty to disable it.</summary>
		public string Qualifier { get; }

		public CommandsConfig(
			IEnumerable<string>? passthrough,
			IReadOnlyDictionary<string, IEnumerable<string>?>? backendPassthrough,
			string? qualifier)
		{
			Passthrough = Lowercased(passthrough);
			Dictionary<string, IReadOnlySet<string>> normalized = new();
			if (backendPassthrough != null)
			{
				foreach (var pair in backendPassthrough)
				{
					normalized[Normalize(pair.Key)] = Lowercased(pair.Value);
				}
			}
			BackendPassthrough = normalized;
			Qualifier = Normalize(qualifier);
		}

		/// <summary>The proxy keeps every name on every backend, and the qualified form is available.</summary>
		public static CommandsConfig Defaults()
		{
			return new CommandsConfig(null, null, DEFAULT_QUALIFIER);
		}

		public static CommandsConfig From(JsonConfig config)
		{
			HashSet<string> passthrough = new HashSet<string>(
				ConfigValues.NormalizedList(config.GetStringList("commands.passthrough")));

			Dictionary<string, IEnumerable<string>?> backendPassthrough = new();
			foreach (KeyValuePair<string, JsonConfig> entry in config.Members("backends"))
			{
				// Has rather than a plain read, because an explicitly empty list is meaningful:
				// it means this backend passes nothing through even when commands.passthrough is set.
				if (entry.Value.Has("passthroughCommands"))
				{
					backendPassthrough[entry.Key] =
						ConfigValues.NormalizedList(entry.Value.GetStringList("passthroughCommands"));
				}
			}

			string qualifier = config.GetString("commands.qualifier", DEFAULT_QUALIFIER);
			return new CommandsConfig(passthrough, backendPassthrough, qualifier);
		}

		/// <summary>The <c>"commands"</c> section of the generated default configuration.</summary>
		public static JsonObject DefaultSection()
		{
			return new JsonObject
			{
				["passthrough"] = new JsonArray(),
				["qualifier"] = DEFAULT_QUALIFIER
			};
		}

		/// <summary>The names this backend keeps for itself.</summary>
		public IReadOnlySet<string> PassthroughFor(string? backendName)
		{
			return BackendPassthrough.TryGetValue(Normalize(backendName), out var configured)
				? configured
				: Passthrough;
		}

		/// <summary>Whether <paramref name="commandName"/> reaches the backend while the player is on <paramref name="backendName"/>.</summary>
		public bool IsPassthrough(string? backendName, string? commandName)
		{
			return PassthroughFor(backendName).Contains(Normalize(commandName));
		}

		/// <summary>
		/// Whether any backend passes anything through - used only to decide whether the startup
		/// diagnostics are worth printing.
		/// </summary>
		public bool IsEmpty()
		{
			return Passthrough.Count == 0 && BackendPassthrough.Values.All(set => set.Count == 0);
		}

		private static IReadOnlySet<string> Lowercased(IEnumerable<string>? values)
		{
			if (values == null)
			{
				return EmptySet;
			}
			HashSet<string> normalized = new();
			foreach (string value in values)
			{
				if (value != null && value.Trim().Length > 0)
				{
					normalized.Add(Normalize(value));
				}
			}
			return normalized;
		}

		private static string Normalize(string? value)
		{
			return value == null ? "" : value.Trim().ToLowerInvariant();
		}
	}
}

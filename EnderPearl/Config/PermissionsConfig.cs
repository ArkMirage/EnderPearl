using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EnderPearl.Config
{
	/// <summary>
	/// Who may run which proxy command.
	///
	/// <p>Deliberately two-level rather than a general permission system: a command is either open to
	/// everyone or restricted to administrators, and administrators are a flat list. Backends work the
	/// same way - an entry's <c>"adminOnly": true</c> keeps one out of reach of <c>/server</c>.</p>
	///
	/// <p><b>This is the proxy's only authorisation boundary, so it is checked on execution, not on
	/// display.</b></p>
	///
	/// <p>Administrators are matched by XUID <em>or</em> gamertag, both case-insensitively. Prefer XUIDs:
	/// both come from the client's Mojang-signed login chain and neither can be forged.</p>
	/// </summary>
	public sealed class PermissionsConfig
	{
		/// <summary>
		/// Commands restricted to administrators unless <c>permissions.adminCommands</c> says otherwise.
		/// <c>/glist</c> is included because it reports where every player on the network is.
		/// </summary>
		public static readonly IReadOnlySet<string> DEFAULT_ADMIN_COMMANDS = new HashSet<string> { "send", "alert", "glist", "perm" };

		/// <summary>
		/// Commands that stay administrator-only whatever the config says, because opening them hands
		/// over the proxy itself rather than one capability.
		/// </summary>
		public static readonly IReadOnlySet<string> ALWAYS_ADMIN = new HashSet<string> { "perm" };

		public IReadOnlySet<string> Admins { get; }

		public IReadOnlySet<string> AdminCommands { get; }

		public IReadOnlySet<string> AdminBackends { get; }

		public PermissionsConfig(
			IEnumerable<string>? admins,
			IEnumerable<string>? adminCommands,
			IEnumerable<string>? adminBackends)
		{
			Admins = Lowercased(admins);
			AdminCommands = Lowercased(adminCommands);
			AdminBackends = Lowercased(adminBackends);
		}

		/// <summary>No administrators, so every admin command is unavailable to everyone until one is configured.</summary>
		public static PermissionsConfig Defaults()
		{
			return new PermissionsConfig(Array.Empty<string>(), DEFAULT_ADMIN_COMMANDS, Array.Empty<string>());
		}

		public static PermissionsConfig From(JsonConfig config)
		{
			var admins = new List<string>(ConfigValues.NormalizedList(config.GetStringList("permissions.admins")));

			IEnumerable<string> adminCommands = config.Has("permissions.adminCommands")
				? ConfigValues.NormalizedList(config.GetStringList("permissions.adminCommands"))
				: new List<string>(DEFAULT_ADMIN_COMMANDS);

			var adminBackends = new List<string>();
			foreach (KeyValuePair<string, JsonConfig> entry in config.Members("backends"))
			{
				if (entry.Value.GetBool("adminOnly", false))
				{
					adminBackends.Add(entry.Key);
				}
			}
			return new PermissionsConfig(admins, adminCommands, adminBackends);
		}

		/// <summary>
		/// The <c>"permissions"</c> section of the generated default configuration. Per-backend
		/// <c>"adminOnly"</c> lives inside each backend entry under <c>"backends"</c> instead, and the
		/// runtime grant file (<c>permissions.json</c>, managed by <c>/perm</c>) stacks on top of all of it.
		/// </summary>
		public static JsonObject DefaultSection()
		{
			var adminCommands = new JsonArray();
			foreach (string command in SortedCopy(DEFAULT_ADMIN_COMMANDS))
			{
				adminCommands.Add(command);
			}
			return new JsonObject
			{
				["admins"] = new JsonArray(),
				["adminCommands"] = adminCommands
			};
		}

		private static List<string> SortedCopy(IEnumerable<string> values)
		{
			var sorted = new List<string>(values);
			sorted.Sort(StringComparer.Ordinal);
			return sorted;
		}

		public bool IsAdmin(string? xuid, string? displayName)
		{
			return Admins.Contains(Normalize(xuid)) || Admins.Contains(Normalize(displayName));
		}

		public bool IsAdminCommand(string commandName)
		{
			// ALWAYS_ADMIN wins over the configured list, in both directions: a config that omits
			// /perm would otherwise leave the command that grants permissions open to everyone, and
			// the first player to find it could make themselves an administrator. There is no
			// legitimate reason to open it, so it is not expressible.
			string normalized = Normalize(commandName);
			return ALWAYS_ADMIN.Contains(normalized) || AdminCommands.Contains(normalized);
		}

		/// <summary>Whether the player identified by xuid/displayName may run commandName.</summary>
		public bool Allows(string? xuid, string? displayName, string commandName)
		{
			return !IsAdminCommand(commandName) || IsAdmin(xuid, displayName);
		}

		public bool IsAdminBackend(string backendName) => AdminBackends.Contains(Normalize(backendName));

		/// <summary>
		/// Whether this player may send <em>themselves</em> to a backend - with <c>/server</c>,
		/// <c>/hub</c> or <c>/lobby</c>.
		///
		/// <p>Restricted backends are also hidden from the <c>/server</c> listing and from the command
		/// tree's backend enum, so a player has no way to learn one exists.</p>
		/// </summary>
		public bool MayJoinBackend(string? xuid, string? displayName, string backendName)
		{
			return !IsAdminBackend(backendName) || IsAdmin(xuid, displayName);
		}

		private static IReadOnlySet<string> Lowercased(IEnumerable<string>? values)
		{
			if (values == null)
			{
				return new HashSet<string>();
			}
			var normalized = new HashSet<string>();
			foreach (string? value in values)
			{
				if (!string.IsNullOrWhiteSpace(value))
				{
					normalized.Add(Normalize(value));
				}
			}
			return normalized;
		}

		private static string Normalize(string? value)
		{
			return value?.Trim().ToLowerInvariant() ?? "";
		}
	}
}

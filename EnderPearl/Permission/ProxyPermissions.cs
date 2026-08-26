using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnderPearl.Config;
using EnderPearl.Logging;

namespace EnderPearl.Permission
{
	/// <summary>
	/// Who may do what, as a live store rather than a restart.
	///
	/// <para><see cref="PermissionsConfig"/> remains the floor - whatever <c>config.json</c> says is always
	/// in force and cannot be revoked at runtime, so an operator locked out by a bad grant can always fix
	/// it by editing the file. Everything granted through <c>/perm</c> lives on top of that and is
	/// written to disk immediately, because the alternative is a permission that quietly disappears on
	/// the next restart.</para>
	///
	/// <h2>Nodes</h2>
	/// <list type="bullet">
	///   <item><c>admin</c> - everything, equivalent to being listed in <c>permissions.admins</c></item>
	///   <item><c>command.&lt;name&gt;</c> - one otherwise-restricted command, e.g. <c>command.send</c></item>
	///   <item><c>server.&lt;name&gt;</c> - one otherwise-restricted backend, e.g. <c>server.staff</c></item>
	/// </list>

	/// <h2>File</h2>
	/// <para><c>permissions.json</c> maps each subject to its granted nodes:</para>
	/// <pre>{ "aXuid123": ["admin"], "SomeGamertag": ["command.send", "server.staff"] }</pre>
	///
	/// <h2>Identity</h2>
	/// <para>A subject is an XUID or a gamertag, matched case-insensitively against either - the same rule
	/// the config list uses. XUIDs are the durable choice; a gamertag can be changed by its owner, and a
	/// released one can be claimed by someone else. Granting by name is supported because it is what an
	/// operator has to hand at the console, and because a player who has never connected has no XUID the
	/// proxy knows.</para>
	///
	/// <para>Reads happen on every command and from several event loops, so the map is guarded and handed
	/// out only as copies.</para>
	/// </summary>
	public sealed class ProxyPermissions
	{
		public const string ADMIN = "admin";
		public const string COMMAND_PREFIX = "command.";
		public const string SERVER_PREFIX = "server.";

		private static readonly JsonDocumentOptions DocumentOptions = new()
		{
			CommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true
		};

		private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

		private readonly PermissionsConfig config;
		private readonly string? file;
		private readonly object mutex = new();

		/// <summary>Subject -> nodes, sorted like Java's TreeMap of TreeSets so /perm listings are stable.</summary>
		private readonly SortedDictionary<string, SortedSet<string>> grants = new();

		internal ProxyPermissions(PermissionsConfig? config, string? file)
		{
			this.config = config ?? PermissionsConfig.Defaults();
			this.file = file;
		}

		/// <summary>An in-memory instance with nothing persisted, for tests and for the config-only path.</summary>
		public static ProxyPermissions InMemory(PermissionsConfig? config)
		{
			return new ProxyPermissions(config, null);
		}

		/// <summary>
		/// Reads the grant file, creating nothing if it is absent - an empty store is the correct state
		/// for a proxy that has never granted anything.
		/// </summary>
		public static ProxyPermissions Load(PermissionsConfig? config, string? permissionsPath)
		{
			ProxyPermissions permissions = new(config, permissionsPath);
			if (permissionsPath == null || !File.Exists(permissionsPath))
			{
				return permissions;
			}
			string json;
			try
			{
				json = File.ReadAllText(permissionsPath);
			}
			catch (IOException exception)
			{
				// Refusing to start would take the whole network down over a permissions file; running
				// with config-only permissions is the safer failure, as long as it is loud.
				Logger.Info($"WARNING: could not read {permissionsPath} ({exception.Message}); running with config permissions only.");
				return permissions;
			}
			JsonObject? root;
			try
			{
				root = JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject;
			}
			catch (JsonException exception)
			{
				Logger.Info($"WARNING: could not parse {permissionsPath} ({exception.Message}); running with config permissions only.");
				return permissions;
			}
			if (root == null)
			{
				return permissions;
			}
			foreach (KeyValuePair<string, JsonNode?> subject in root)
			{
				SortedSet<string> nodes = ParseNodes(subject.Value);
				if (nodes.Count > 0)
				{
					permissions.grants[Normalize(subject.Key)] = nodes;
				}
			}
			Logger.Info($"Loaded runtime permissions for {permissions.grants.Count} subject(s) from {permissionsPath}.");
			return permissions;
		}

		// ---------------------------------------------------------------- queries

		public bool IsAdmin(string? xuid, string? displayName)
		{
			return config.IsAdmin(xuid, displayName) || HasNode(xuid, displayName, ADMIN);
		}

		public bool IsAdminCommand(string commandName)
		{
			return config.IsAdminCommand(commandName);
		}

		public bool IsAdminBackend(string backendName)
		{
			return config.IsAdminBackend(backendName);
		}

		/// <summary>Whether this player may run <paramref name="commandName"/>.</summary>
		public bool Allows(string? xuid, string? displayName, string commandName)
		{
			if (!config.IsAdminCommand(commandName))
			{
				return true;
			}
			return IsAdmin(xuid, displayName)
				|| HasNode(xuid, displayName, COMMAND_PREFIX + Normalize(commandName));
		}

		/// <summary>
		/// Whether this player may send <em>themselves</em> to a backend. Deliberately not consulted by
		/// <c>/send</c>, failover or forced hosts - see <see cref="PermissionsConfig.MayJoinBackend"/>.
		/// </summary>
		public bool MayJoinBackend(string? xuid, string? displayName, string backendName)
		{
			if (!config.IsAdminBackend(backendName))
			{
				return true;
			}
			return IsAdmin(xuid, displayName)
				|| HasNode(xuid, displayName, SERVER_PREFIX + Normalize(backendName));
		}

		/// <summary>Nodes granted at runtime to this subject, not counting anything the config gives them.</summary>
		public IReadOnlySet<string> NodesOf(string subject)
		{
			lock (mutex)
			{
				return grants.TryGetValue(Normalize(subject), out SortedSet<string>? nodes)
					? new HashSet<string>(nodes)
					: new HashSet<string>();
			}
		}

		public LinkedHashMap<string, IReadOnlySet<string>> Subjects()
		{
			lock (mutex)
			{
				var copy = new LinkedHashMap<string, IReadOnlySet<string>>();
				foreach (KeyValuePair<string, SortedSet<string>> entry in grants)
				{
					copy.Add(entry.Key, new HashSet<string>(entry.Value));
				}
				return copy;
			}
		}

		/// <summary>What the config gives everyone matching, for <c>/perm info</c> to report alongside grants.</summary>
		public PermissionsConfig Config => config;

		// -------------------------------------------------------------- mutations

		/// <returns>false when the subject already had the node, so callers can say so</returns>
		public bool Grant(string subject, string node)
		{
			string key = Normalize(subject);
			string value = Normalize(node);
			RequireUsable(key, value);
			lock (mutex)
			{
				if (!grants.TryGetValue(key, out SortedSet<string>? nodes))
				{
					nodes = new SortedSet<string>();
					grants[key] = nodes;
				}
				bool added = nodes.Add(value);
				if (added)
				{
					Save();
				}
				return added;
			}
		}

		/// <returns>false when the subject did not have the node</returns>
		public bool Revoke(string subject, string node)
		{
			string key = Normalize(subject);
			string value = Normalize(node);
			lock (mutex)
			{
				if (!grants.TryGetValue(key, out SortedSet<string>? nodes) || !nodes.Remove(value))
				{
					return false;
				}
				if (nodes.Count == 0)
				{
					grants.Remove(key);
				}
				Save();
				return true;
			}
		}

		/// <summary>The nodes that make sense to grant, for autocomplete and for rejecting typos.</summary>
		public static List<string> KnownNodes(IEnumerable<string> commandNames, IEnumerable<string> backendNames)
		{
			var nodes = new List<string> { ADMIN };
			foreach (string command in commandNames)
			{
				nodes.Add(COMMAND_PREFIX + Normalize(command));
			}
			foreach (string backend in backendNames)
			{
				nodes.Add(SERVER_PREFIX + Normalize(backend));
			}
			return new List<string>(nodes);
		}

		// ---------------------------------------------------------------- interns

		private bool HasNode(string? xuid, string? displayName, string node)
		{
			string wanted = Normalize(node);
			lock (mutex)
			{
				return Contains(grants.TryGetValue(Normalize(xuid), out SortedSet<string>? byXuid) ? byXuid : null, wanted)
					|| Contains(grants.TryGetValue(Normalize(displayName), out SortedSet<string>? byName) ? byName : null, wanted);
			}
		}

		private static bool Contains(SortedSet<string>? nodes, string node)
		{
			// An admin grant answers for every node, so /perm set <player> admin needs no follow-up.
			return nodes != null && (nodes.Contains(node) || nodes.Contains(ADMIN));
		}

		private static void RequireUsable(string subject, string node)
		{
			if (subject.Length == 0)
			{
				throw new ArgumentException("subject cannot be blank");
			}
			if (node.Length == 0)
			{
				throw new ArgumentException("node cannot be blank");
			}
			// Any other characters are fine now that the store is JSON - a subject or node survives
			// the round trip whatever it contains.
		}

		private void Save()
		{
			if (file == null)
			{
				return;
			}
			try
			{
				var root = new JsonObject();
				foreach (KeyValuePair<string, SortedSet<string>> entry in grants)
				{
					var nodes = new JsonArray();
					foreach (string node in entry.Value)
					{
						nodes.Add(node);
					}
					root[entry.Key] = nodes;
				}
				string absolute = Path.GetFullPath(file);
				string? parent = Path.GetDirectoryName(absolute);
				if (!string.IsNullOrEmpty(parent))
				{
					Directory.CreateDirectory(parent);
				}
				// Written beside the target and moved into place: a half-written permissions file is
				// one that silently drops somebody's access on the next start.
				string temporary = Path.Combine(parent ?? "", Path.GetFileName(file) + ".tmp");
				File.WriteAllText(temporary, root.ToJsonString(WriteOptions) + "\n", new UTF8Encoding(false));
				File.Move(temporary, file, overwrite: true);
			}
			catch (IOException exception)
			{
				Logger.Info($"WARNING: could not write {file} ({exception.Message}); the change applies now but will be lost on restart.");
			}
		}

		/// <summary>A subject's granted-node array; entries that are not strings are skipped rather than fatal.</summary>
		private static SortedSet<string> ParseNodes(JsonNode? value)
		{
			var nodes = new SortedSet<string>();
			if (value is not JsonArray granted)
			{
				return nodes;
			}
			foreach (JsonNode? element in granted)
			{
				if (element is JsonValue scalar && scalar.TryGetValue<string>(out string? name))
				{
					string normalized = Normalize(name);
					if (normalized.Length > 0)
					{
						nodes.Add(normalized);
					}
				}
			}
			return nodes;
		}

		private static string Normalize(string? value)
		{
			return value?.Trim().ToLowerInvariant() ?? "";
		}
	}
}

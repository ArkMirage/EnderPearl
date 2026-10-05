using System;
using EnderPearl.Auth;
using EnderPearl.Backend;
using EnderPearl.Command;
using EnderPearl.Config;
using EnderPearl.Permission;
using EnderPearl.Player;

namespace EnderPearl.Server;

/// <summary>
/// The proxy-wide state every layer needs, built once and read from wherever it is used.
///
/// <para>These objects have exactly one instance per process and are consulted from the frontend, the backend
/// connector, the relay handlers and the command layer alike. Passing them explicitly meant every one of
/// those construction sites had to forward the same handful of references, so adding a single new consumer - a
/// command that needs the player roster, say - meant editing the assembly code in
/// <see cref="Frontend.BedrockProxyListener"/> and every hop between. Reading them from here instead keeps the
/// wiring about what an object owns, not about what it happens to need.</para>
///
/// <para>State only. Services - the backend connector, the switcher and failover, the command manager, the
/// console, the connection throttle - stay explicitly injected. Moving them here too would turn this into a
/// service locator, and an object that can reach any worker is an object whose dependencies can no longer be
/// read off its constructor.</para>
///
/// <para>Initialised once by <c>Program.Main</c>, after the logger is installed (a validation failure has to be
/// logged) and before the listener binds or the console accepts anything. Reading before that throws rather
/// than returning null, because a null from here would surface as a NullReferenceException somewhere unrelated,
/// at whatever moment the missing value was first touched.</para>
/// </summary>
public static class ProxyServer
{
	private static bool initialized;
	private static ProxyConfig? config;
	private static ConnectedPlayerRegistry? connectedPlayers;
	private static ProxyPermissions? permissions;
	private static ProxyPlayerEnum? playerEnum;
	private static BackendDirectory? backendDirectory;
	private static MojangMimicIdentity mimicIdentity = null!;

	/// <summary>The config as loaded and validated at startup; never reloaded.</summary>
	public static ProxyConfig Config => Ready(config, nameof(Config));

	/// <summary>Shorthand for the policy the connector, the switcher and every gate consults.</summary>
	public static ProxyPolicy Policy => Config.Policy;

	/// <summary>Shorthand for the command config: pass-through sets and the proxy's command qualifier.</summary>
	public static CommandsConfig Commands => Config.Policy.Commands;

	/// <summary>Everyone currently past login, keyed by XUID.</summary>
	public static ConnectedPlayerRegistry ConnectedPlayers => Ready(connectedPlayers, nameof(ConnectedPlayers));

	/// <summary>Runtime grants on top of the config's floor.</summary>
	public static ProxyPermissions Permissions => Ready(permissions, nameof(Permissions));

	/// <summary>The soft-enum writer for the roster, so a command can refresh autocomplete.</summary>
	public static ProxyPlayerEnum PlayerEnum => Ready(playerEnum, nameof(PlayerEnum));

	/// <summary>The configured backends, with the default and hub resolved.</summary>
	public static BackendDirectory BackendDirectory => Ready(backendDirectory, nameof(BackendDirectory));

	/// <summary>
	/// The proxy's own online-mimic signing identity. Always present: <c>Program.Main</c> loads one before the
	/// listener binds, so there is no plain-offline posture to fall back to.
	/// </summary>
	public static MojangMimicIdentity MimicIdentity
	{
		get
		{
			if (!initialized)
			{
				throw NotInitialized(nameof(MimicIdentity));
			}
			return mimicIdentity;
		}
	}

	/// <summary>
	/// Builds the proxy-wide state. Call once, from <c>Program.Main</c>, after <c>Logger.Install</c> and before
	/// the listener binds.
	///
	/// <para>The two constructor validations that used to run in the listener - max players, and the default/hub
	/// backend names existing - now run here. They are still inside Main's try/catch, so a bad config is still a
	/// logged fatal error rather than a stack trace; it just happens a moment earlier than the listener being
	/// constructed.</para>
	/// </summary>
	public static void Initialize(
		ProxyConfig config,
		ProxyPermissions permissions,
		MojangMimicIdentity mimicIdentity
	)
	{
		if (initialized)
		{
			throw new InvalidOperationException(
				"ProxyServer.Initialize ran twice. The proxy-wide state is built exactly once, in Program.Main;"
				+ " a second listener in the same process would silently share the first one's roster and backends."
			);
		}
		if (config == null)
		{
			throw new ArgumentNullException(nameof(config));
		}

		// Local variables throughout: reading the properties in here would trip the guard this method is about
		// to satisfy, because none of them are set until the end.
		ConnectedPlayerRegistry players = new ConnectedPlayerRegistry(config.MaxPlayers);
		// The config's own permissions are the floor and cannot be revoked at runtime; grants loaded from
		// permissions.json sit on top. With no file, ProxyPermissions.Load returns an empty store.
		ProxyPermissions grants = permissions;
		ProxyPlayerEnum roster = new ProxyPlayerEnum(grants);
		BackendDirectory backends = new BackendDirectory(
			config.Backends,
			config.Backend.Name,
			config.HubBackendName
		);

		ProxyServer.config = config;
		ProxyServer.connectedPlayers = players;
		ProxyServer.permissions = grants;
		ProxyServer.playerEnum = roster;
		ProxyServer.backendDirectory = backends;
		ProxyServer.mimicIdentity = mimicIdentity;
		initialized = true;
	}

	private static T Ready<T>(T? value, string member) where T : class
	{
		return value ?? throw NotInitialized(member);
	}

	private static InvalidOperationException NotInitialized(string member)
	{
		return new InvalidOperationException(
			$"ProxyServer.{member} was read before ProxyServer.Initialize ran. Proxy-wide state is built once,"
			+ " in Program.Main, before the listener binds and before the console accepts a command."
		);
	}
}

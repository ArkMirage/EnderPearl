namespace EnderPearl.Config;

/// <summary>
/// The policy half of the configuration - the settings that decide what the proxy <em>does</em>, as
/// opposed to the addresses and codecs it needs to run at all.
/// </summary>
public sealed class ProxyPolicy
{
	public required FailoverConfig Failover { get; init; }

	public required BackendSwitchConfig BackendSwitch { get; init; }

	public required PermissionsConfig Permissions { get; init; }

	public required SecurityConfig Security { get; init; }

	public required ForcedHostsConfig ForcedHosts { get; init; }

	public required JoinConfig Join { get; init; }

	public CommandsConfig Commands { get; init; } = CommandsConfig.Defaults();
}
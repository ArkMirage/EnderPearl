using System;

namespace EnderPearl.Config
{
	/// <summary>
	/// The policy half of the configuration - the settings that decide what the proxy <em>does</em>, as
	/// opposed to the addresses and codecs it needs to run at all.
	/// </summary>
	public sealed class ProxyPolicy
	{
		public FailoverConfig Failover { get; }

		public BackendSwitchConfig BackendSwitch { get; }

		public PermissionsConfig Permissions { get; }

		public SecurityConfig Security { get; }

		public ForcedHostsConfig ForcedHosts { get; }

		public JoinConfig Join { get; }

		public CommandsConfig Commands { get; }

		public ProxyPolicy(
			FailoverConfig failover,
			BackendSwitchConfig backendSwitch,
			PermissionsConfig permissions,
			SecurityConfig security,
			ForcedHostsConfig forcedHosts,
			JoinConfig join)
			: this(failover, backendSwitch, permissions, security, forcedHosts, join, CommandsConfig.Defaults())
		{
		}

		public ProxyPolicy(
			FailoverConfig failover,
			BackendSwitchConfig backendSwitch,
			PermissionsConfig permissions,
			SecurityConfig security,
			ForcedHostsConfig forcedHosts,
			JoinConfig join,
			CommandsConfig commands)
		{
			Failover = failover ?? throw new ArgumentNullException(nameof(failover));
			BackendSwitch = backendSwitch ?? throw new ArgumentNullException(nameof(backendSwitch));
			Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
			Security = security ?? throw new ArgumentNullException(nameof(security));
			ForcedHosts = forcedHosts ?? throw new ArgumentNullException(nameof(forcedHosts));
			Join = join ?? throw new ArgumentNullException(nameof(join));
			Commands = commands ?? throw new ArgumentNullException(nameof(commands));
		}

		public static ProxyPolicy Defaults()
		{
			return new ProxyPolicy(
				FailoverConfig.Disabled(),
				BackendSwitchConfig.Defaults(),
				PermissionsConfig.Defaults(),
				SecurityConfig.Defaults(),
				ForcedHostsConfig.Empty(),
				JoinConfig.Defaults(),
				CommandsConfig.Defaults()
			);
		}
	}
}

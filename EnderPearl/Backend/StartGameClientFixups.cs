using EnderPearl.Net;
using global::Protocol.Packets;

namespace EnderPearl.Backend
{
	/// <summary>
	/// Corrections applied to every backend StartGamePacket before it reaches the client.
	///
	/// <p>These are client-behaviour workarounds, not protocol translation, so they apply to
	/// same-protocol relaying just as much as to cross-protocol clients.</p>
	/// </summary>
	public struct StartGameClientFixups
	{
		public bool ForcedTickDeathSystems { get; }

		public bool EnabledCommands { get; }

		private StartGameClientFixups(bool forcedTickDeathSystems, bool enabledCommands)
		{
			ForcedTickDeathSystems = forcedTickDeathSystems;
			EnabledCommands = enabledCommands;
		}

		// Bisect switch: relay StartGame exactly as the backend sent it.
		// Enable with the AppContext switch "proxy.noStartGameFixups".
		private static readonly bool DISABLED = AppContext.TryGetSwitch("proxy.noStartGameFixups", out bool disabled) && disabled;

		public static StartGameClientFixups Apply(StartGamePacket startGame)
		{
			if (DISABLED)
			{
				return new StartGameClientFixups(false, false);
			}
			// tickDeathSystems is deliberately NOT corrected here: a client connected directly to the
			// same backend dies and respawns correctly with the backend's value, and overriding it only
			// makes the proxy diverge from a configuration known to work.
			bool forcedTickDeathSystems = false;

			// defaultPlayerPermission is deliberately NOT raised; per-player operator status is
			// synchronized separately from the backend's UpdateAbilities command permission.

			bool enabledCommands = startGame.Settings != null && !startGame.Settings.CommandsEnabled;
			if (enabledCommands)
			{
				startGame.Settings.CommandsEnabled = true;
				// The send path transmits a decoded packet's original wire bytes unless the cache is
				// cleared; without this the fixup silently never reaches the client.
				startGame.InvalidateWireCache();
			}

			return new StartGameClientFixups(forcedTickDeathSystems, enabledCommands);
		}
	}
}

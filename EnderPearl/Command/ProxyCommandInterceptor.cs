using System;
using System.Collections.Generic;
using EnderPearl.Config;
using global::Protocol.Packets;

namespace EnderPearl.Command
{
	/// <summary>
	/// Decides whether a command line the client sent is the proxy's to run or the backend's.
	///
	/// <para>One of these is built per backend connection, because the answer is per backend: a hub
	/// running a plugin that owns <c>/hub</c> and <c>/server</c> passes both through, while the same two
	/// names on a minigame backend are the proxy's. See <see cref="CommandsConfig"/> for the setting and
	/// for why the qualified form exists.</para>
	/// </summary>
	public sealed class ProxyCommandInterceptor
	{
		private readonly ProxyCommandRegistry registry;
		private readonly IReadOnlySet<string> passthrough;
		private readonly string qualifier;

		/// <summary>Keeps every command for the proxy, with the default qualified form still available.</summary>
		public ProxyCommandInterceptor(ProxyCommandRegistry registry)
			: this(registry, null, CommandsConfig.DEFAULT_QUALIFIER)
		{
		}

		/// <param name="passthrough">command names this backend has taken over, which are forwarded unchanged</param>
		/// <param name="qualifier">the prefix that forces proxy handling regardless, or empty to disable it</param>
		public ProxyCommandInterceptor(ProxyCommandRegistry registry, IEnumerable<string>? passthrough, string? qualifier)
		{
			this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
			if (passthrough == null)
			{
				this.passthrough = new HashSet<string>();
			}
			else
			{
				HashSet<string> copy = new(StringComparer.Ordinal);
				foreach (string name in passthrough)
				{
					copy.Add(name);
				}
				this.passthrough = copy;
			}
			this.qualifier = qualifier == null ? "" : qualifier.Trim().ToLowerInvariant();
		}

		public CommandInterception Intercept(CommandRequestPacket packet)
		{
			string commandLine = packet.Command;
			string name = ProxyCommandRegistry.CommandName(commandLine);

			// An empty qualifier disables the qualified form rather than making every command qualified,
			// which is what a StartsWith("") test would otherwise do.
			bool qualified = qualifier.Length > 0 && name.StartsWith(qualifier, StringComparison.Ordinal);
			string lookup = qualified ? name.Substring(qualifier.Length) : name;

			ProxyCommand? command = registry.Find(lookup);
			if (command == null)
			{
				// Includes a qualified name the proxy does not have: forwarding lets the backend give
				// its own "unknown command" rather than the proxy inventing one for a name it never
				// advertised.
				return new CommandInterception.Forward(commandLine);
			}
			if (!qualified && passthrough.Contains(lookup))
			{
				return new CommandInterception.Forward(commandLine);
			}
			// The original line is carried through unchanged, qualifier and all: CommandArguments cuts
			// at the first whitespace whatever the name is, so `/proxy:server skygen` yields the same
			// arguments as `/server skygen`.
			return new CommandInterception.Consumed(command, commandLine);
		}
	}
}

using EnderPearl.Backend;
using EnderPearl.Net;
using EnderPearl.Permission;
using global::Protocol.Packets;
using CommandPayloadTypes = global::Protocol.Types.AvailableCommandsPacketPayload;
using EnderPearl.Logging;

namespace EnderPearl.Command
{
	/// <summary>
	/// Adds the proxy's own commands to the <see cref="AvailableCommandsPacket"/> the backend sent, so
	/// they autocomplete like native ones.
	///
	/// <para>Real sessions use the four-argument constructor so an admin command never appears in a
	/// player's autocomplete, and the backend list is the one that player is allowed to see. Note
	/// that hiding is cosmetic — the client can send any command line it likes, so
	/// <c>BackendCommandRouter</c> checks again on execution.</para>
	/// </summary>
	public sealed class AvailableCommandsInjector
	{
		/// <summary>What vanilla names its free-text parameter, on <c>/me</c>, <c>/msg</c>, <c>/say</c> and friends.</summary>
		private const string FREE_TEXT_PARAM_NAME = "message";

		private static int freeTextTypeReported;

		private static readonly List<string> PERM_ACTIONS = new() { "set", "unset", "info", "list" };

		// A parameter's ParseSymbol is the raw wire symbol this codec passes through untouched. The
		// long-standing Bedrock flag bits say which table the rest of the value indexes.
		private const uint ARG_FLAG_VALID = 0x100000;
		private const uint ARG_FLAG_ENUM = 0x200000;
		private const uint ARG_FLAG_POSTFIX = 0x1000000;
		private const uint ARG_FLAG_SOFT_ENUM = 0x4000000;

		/// <summary>
		/// The wire id of the "string" parameter type on this protocol's table, under
		/// ARG_FLAG_VALID. Only used as the fallback for /send when no roster exists — everywhere else
		/// a symbol is borrowed from or registered against the packet itself.
		/// </summary>
		private const uint COMMAND_PARAM_STRING = ARG_FLAG_VALID | 56;

		/// <summary>The one flag the Java original set: CommandData.Flag.NOT_CHEAT, ordinal 7 → bit 128.</summary>
		private const ushort COMMAND_DATA_FLAG_NOT_CHEAT = 0x80;

		private readonly ProxyCommandRegistry registry;
		private readonly List<string> backendNames;
		private readonly Func<string, bool> visible;
		private readonly ProxyPlayerEnum? playerEnum;

		/// <summary>
		/// </summary>
		/// <param name="backendNames">the backends this session's player may switch themselves to; a restricted
		/// backend is left out so its existence is not advertised</param>
		/// <param name="visible">answers whether this session's player may use a command, by command name</param>
		/// <param name="playerEnum">supplies <c>/send</c>'s autocompletable player list, or null for none</param>
		public AvailableCommandsInjector(
			ProxyCommandRegistry registry,
			IEnumerable<string> backendNames,
			Func<string, bool>? visible,
			ProxyPlayerEnum? playerEnum
		)
		{
			this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
			this.backendNames = backendNames == null ? throw new ArgumentNullException(nameof(backendNames)) : new List<string>(backendNames);
			this.visible = visible ?? (_ => true);
			this.playerEnum = playerEnum;
		}

		public AvailableCommandsPacket Inject(AvailableCommandsPacket packet)
		{
			if (packet == null)
			{
				throw new ArgumentNullException(nameof(packet));
			}
			// Read the backend's own tree before adding ours, so the free-text type comes from a
			// command this client has already proved it can render.
			uint? freeTextType = FreeTextParamType(packet);

			HashSet<string> existing = new(StringComparer.Ordinal);
			foreach (CommandPayloadTypes.CommandData command in packet.Commands)
			{
				existing.Add(command.Name.ToLowerInvariant());
			}

			List<CommandPayloadTypes.CommandData> injected = new();
			foreach (ProxyCommand command in registry.Commands())
			{
				if (!visible(command.Name))
				{
					continue;
				}
				if (existing.Add(command.Name.ToLowerInvariant()))
				{
					injected.Add(ToCommandData(packet, command, freeTextType));
				}
			}
			packet.Commands.InsertRange(0, injected);
			if (injected.Count > 0)
			{
				// The send path transmits a decoded packet's original wire bytes unless the cache is
				// cleared; without this the injected commands silently never reach the client.
				packet.InvalidateWireCache();
			}
			return packet;
		}

		/// <summary>
		/// Borrows the wire symbol of a free-text parameter from a vanilla command in the backend's own
		/// command tree — <c>/me &lt;message&gt;</c>, <c>/msg &lt;target&gt; &lt;message&gt;</c>, and so on.
		///
		/// <para>Naming a parameter type constant instead would go through the codec's parameter type
		/// table, and <b>that table is not trustworthy on this protocol</b>. A relayed command never exposes
		/// it: relaying keeps the raw wire symbol and writes back whatever was read, so decode
		/// and encode cancel out even when the table is wrong. Only parameters the proxy invents actually
		/// ask the table for a number — which is why an <c>alert</c> declared with the <c>MESSAGE</c> type id
		/// <b>crashed the client outright</b> the moment it finished rendering the command name, while the
		/// structurally identical <c>STRING</c> parameter on <c>/send</c> was fine. Copying a type the backend
		/// just sent sidesteps the table entirely and keeps working across versions.</para>
		///
		/// <returns>the borrowed symbol, or null when the tree has none to borrow — the caller must then
		/// declare no parameter at all rather than fall back to a guess</returns>
		private static uint? FreeTextParamType(AvailableCommandsPacket packet)
		{
			foreach (CommandPayloadTypes.CommandData command in packet.Commands)
			{
				foreach (CommandPayloadTypes.OverloadData overload in command.Overloads)
				{
					foreach (CommandPayloadTypes.ParamData parameter in overload.ParameterData)
					{
						if (!FREE_TEXT_PARAM_NAME.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						// Postfix and enum/soft-enum parameters carry an index into a table of their own;
						// only a plain typed parameter is a symbol we can reuse.
						uint symbol = parameter.ParseSymbol;
						if ((symbol & (ARG_FLAG_POSTFIX | ARG_FLAG_ENUM | ARG_FLAG_SOFT_ENUM)) == 0)
						{
							ReportFreeTextType(command.Name, symbol);
							return symbol;
						}
					}
				}
			}
			ReportFreeTextType(null, null);
			return null;
		}

		/// <summary>Once per run: enough to diagnose a command tree, not enough to fill the log.</summary>
		private static void ReportFreeTextType(string? sourceCommand, uint? type)
		{
			if (System.Threading.Interlocked.CompareExchange(ref freeTextTypeReported, 1, 0) != 0)
			{
				return;
			}
			if (type == null)
			{
				Logger.Info("WARNING: the backend's command tree has no free-text parameter to copy, "
					+ "so /alert is advertised without one. Naming a parameter type directly is not an "
					+ "option here — the codec's type table for this protocol is unverified, and a wrong "
					+ "id crashes the client.");
				return;
			}
			if (ProxyConnection.IsPacketTracingConfigured() || ProxyConnection.ConfiguredPacketTraceMillis() > 0)
			{
				Logger.Info($"Sourced the /alert message parameter from the backend's /{sourceCommand}: {type.Value}.");
			}
		}

		private CommandPayloadTypes.CommandData ToCommandData(AvailableCommandsPacket packet, ProxyCommand command, uint? freeTextType)
		{
			return new CommandPayloadTypes.CommandData
			{
				Name = command.Name,
				Description = command.Description,
				Flags = COMMAND_DATA_FLAG_NOT_CHEAT,
				// ANY, not OPERATOR: the client hides commands above its own permission level, and
				// the proxy relays the backend's real level rather than pretending everyone is op.
				// Authorisation is the router's job, not the command tree's.
				PermissionLevel = global::Protocol.CommandPermissionLevel.Any,
				AliasEnum = -1,
				CommandDataChainedSubcommandIndexes = new List<uint>(),
				Overloads = OverloadsFor(packet, command, freeTextType)
			};
		}

		private List<CommandPayloadTypes.OverloadData> OverloadsFor(AvailableCommandsPacket packet, ProxyCommand command, uint? freeTextType)
		{
			switch (command.Name)
			{
				case "server":
				{
					return new List<CommandPayloadTypes.OverloadData>
					{
						new() { IsChaining = false, ParameterData = new List<CommandPayloadTypes.ParamData>() },
						new()
						{
							IsChaining = false,
							ParameterData = new List<CommandPayloadTypes.ParamData> { BackendNameParameter(packet, "name") }
						}
					};
				}
				case "send":
				{
					return new List<CommandPayloadTypes.OverloadData>
					{
						new()
						{
							IsChaining = false,
							ParameterData = new List<CommandPayloadTypes.ParamData>
							{
								PlayerParameter(packet, "player", false),
								BackendNameParameter(packet, "server")
							}
						}
					};
				}
				// Every parameter is an enum, so none of them touch the codec's parameter type table.
				// The trailing two are optional because `list` takes neither and `info` takes only the
				// player — one overload autocompletes all four forms without the client having to pick
				// between overloads as you type.
				case "perm":
				{
					return new List<CommandPayloadTypes.OverloadData>
					{
						new()
						{
							IsChaining = false,
							ParameterData = new List<CommandPayloadTypes.ParamData>
							{
								FixedEnumParameter(packet, "action", "ProxyPermActions", PERM_ACTIONS, false),
								PlayerParameter(packet, "player", true),
								FixedEnumParameter(packet, "node", "ProxyPermNodes", PermissionNodes(), true)
							}
						}
					};
				}
				case "alert":
				{
					return freeTextType == null
						? new List<CommandPayloadTypes.OverloadData>
						{
							new() { IsChaining = false, ParameterData = new List<CommandPayloadTypes.ParamData>() }
						}
						: new List<CommandPayloadTypes.OverloadData>
						{
							new()
							{
								IsChaining = false,
								ParameterData = new List<CommandPayloadTypes.ParamData>
								{
									FreeTextParameter("message", freeTextType.Value)
								}
							}
						};
				}
				default:
				{
					return new List<CommandPayloadTypes.OverloadData>
					{
						new() { IsChaining = false, ParameterData = new List<CommandPayloadTypes.ParamData>() }
					};
				}
			}
		}

		/// <summary>
		/// The network's player list, as a soft enum so it autocompletes and can be refreshed as people
		/// come and go.
		///
		/// <para>Not a target selector: the player being sent is usually on another backend, where the
		/// client has no entity to resolve the selector against and would reject the name as unknown.
		/// Falls back to a plain string when no roster is available, which still accepts a typed name —
		/// it just cannot suggest one.</para>
		/// </summary>
		private CommandPayloadTypes.ParamData PlayerParameter(AvailableCommandsPacket packet, string name, bool optional)
		{
			CommandPayloadTypes.ParamData parameter = new();
			parameter.Name = name;
			parameter.IsOptional = optional;
			if (playerEnum == null)
			{
				parameter.ParseSymbol = COMMAND_PARAM_STRING;
			}
			else
			{
				parameter.ParseSymbol = RegisterSoftEnum(packet);
			}
			return parameter;
		}

		/// <summary>
		/// Registers the roster once per packet and answers with its soft-enum reference symbol. Several
		/// invented parameters share one entry, and an entry the backend already supplied wins over
		/// registering a duplicate.
		/// </summary>
		private uint RegisterSoftEnum(AvailableCommandsPacket packet)
		{
			int index = packet.SoftEnums.FindIndex(entry => ProxyPlayerEnum.NAME.Equals(entry.EnumName, StringComparison.Ordinal));
			if (index < 0)
			{
				playerEnum!.InjectInto(packet);
				index = packet.SoftEnums.Count - 1;
			}
			// The Java serializer ORs ARG_FLAG_VALID into every enum symbol it writes
			// (AvailableCommandsSerializer_v291: soft = index | SOFT_ENUM | VALID, hard = index |
			// ENUM | VALID); this codec passes ParseSymbol through untouched, so the VALID bit has
			// to be part of the symbol we build here.
			return ARG_FLAG_SOFT_ENUM | ARG_FLAG_VALID | (uint)index;
		}

		/// <summary>
		/// A hard enum: a fixed set of values baked into the command tree. Unlike the soft player enum
		/// these never change during a session, so they need no follow-up update packet.
		/// </summary>
		private CommandPayloadTypes.ParamData FixedEnumParameter(
			AvailableCommandsPacket packet,
			string name,
			string enumName,
			List<string> values,
			bool optional
		)
		{
			CommandPayloadTypes.ParamData parameter = new();
			parameter.Name = name;
			parameter.IsOptional = optional;
			parameter.ParseSymbol = RegisterHardEnum(packet, enumName, values);
			return parameter;
		}

		/// <summary><c>admin</c>, plus one node per proxy command and per backend.</summary>
		private List<string> PermissionNodes()
		{
			List<string> commandNames = new();
			foreach (ProxyCommand command in registry.Commands())
			{
				commandNames.Add(command.Name);
			}
			return ProxyPermissions.KnownNodes(commandNames, backendNames);
		}

		/// <summary>A free-text parameter, typed by whatever the backend uses for one.</summary>
		private static CommandPayloadTypes.ParamData FreeTextParameter(string name, uint type)
		{
			CommandPayloadTypes.ParamData parameter = new();
			parameter.Name = name;
			parameter.IsOptional = false;
			parameter.ParseSymbol = type;
			return parameter;
		}

		private CommandPayloadTypes.ParamData BackendNameParameter(AvailableCommandsPacket packet, string name)
		{
			return FixedEnumParameter(packet, name, "ProxyBackends", backendNames, false);
		}

		/// <summary>
		/// Appends the values to the packet's shared string pool, adds an enum entry indexing them, and
		/// answers with the symbol that references the entry by index. Enums already present by name are
		/// reused rather than duplicated.
		/// </summary>
		private static uint RegisterHardEnum(AvailableCommandsPacket packet, string enumName, IReadOnlyList<string> values)
		{
			int index = packet.EnumData.FindIndex(entry => enumName.Equals(entry.Name, StringComparison.Ordinal));
			if (index >= 0)
			{
				return ARG_FLAG_ENUM | ARG_FLAG_VALID | (uint)index;
			}
			List<uint> valueIndices = new();
			foreach (string value in values)
			{
				int valueIndex = packet.EnumValues.IndexOf(value);
				if (valueIndex < 0)
				{
					packet.EnumValues.Add(value);
					valueIndex = packet.EnumValues.Count - 1;
				}
				valueIndices.Add((uint)valueIndex);
			}
			packet.EnumData.Add(new CommandPayloadTypes.EnumData
			{
				Name = enumName,
				Values = valueIndices
			});
			return ARG_FLAG_ENUM | ARG_FLAG_VALID | (uint)(packet.EnumData.Count - 1);
		}
	}
}

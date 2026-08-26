using EnderPearl.Permission;
using EnderPearl.Backend;
using EnderPearl.Session;
using global::Protocol.Packets;

namespace EnderPearl.Command
{
	/// <summary>
	/// The list of names <c>/send</c> autocompletes: everyone on the network, plus <c>all</c>.
	///
	/// <para>A <em>soft</em> enum, because the roster changes constantly and
	/// <see cref="AvailableCommandsPacket"/> is sent once when a player joins. Soft enums exist for exactly
	/// this 鈥?the client accepts an <see cref="UpdateSoftEnumPacket"/> afterwards, so the values can be
	/// replaced without rebuilding and resending the whole command tree.</para>
	///
	/// <para>Being an enum also keeps it clear of the parameter type table, which is not trustworthy
	/// on this protocol: an enum parameter's wire value indexes a table carried inside the packet itself.</para>
	/// </summary>
	public sealed class ProxyPlayerEnum
	{
		public const string NAME = "ProxyPlayers";

		/// <summary>Velocity's spelling, and what an admin reaches for when moving the whole network.</summary>
		public const string ALL = "all";

		private readonly ConnectedPlayerRegistry? connectedPlayers;
		private readonly ProxyPermissions permissions;

		public ProxyPlayerEnum(ConnectedPlayerRegistry? connectedPlayers, ProxyPermissions permissions)
		{
			this.connectedPlayers = connectedPlayers;
			this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
		}

		/// <summary>
		/// Adds this enum to a command tree being assembled: the current roster as one soft-enum entry.
		/// Callers reference it by its index into the packet's soft enum table.
		/// </summary>
		public void InjectInto(global::Protocol.Packets.AvailableCommandsPacket packet)
		{
			if (packet == null)
			{
				throw new ArgumentNullException(nameof(packet));
			}
			packet.SoftEnums.Add(new global::Protocol.Types.AvailableCommandsPacketPayload.SoftEnumData
			{
				EnumName = NAME,
				EnumOptions = Values()
			});
		}

		/// <summary>
		/// Replaces the enum on every client whose command tree contains it.
		///
		/// <para>Sent only to administrators: nobody else was given <c>/send</c>, so nobody else has a
		/// <c>ProxyPlayers</c> enum for the update to land in. It also means a player cannot learn who
		/// is online from a packet they were never meant to receive.</para>
		/// </summary>
		public void Broadcast()
		{
			if (connectedPlayers == null)
			{
				return;
			}
			List<string> options = Values();
			foreach (ProxyConnection connection in connectedPlayers.Connections())
			{
				if (!MayReceive(connection))
				{
					continue;
				}
				UpdateSoftEnumPacket packet = new();
				packet.EnumName = NAME;
				packet.Values = new List<string>(options);
				packet.UpdateType = global::Protocol.SoftEnumUpdateType.Replace;
				connection.Client().SendPacket(packet);
			}
		}

		private bool MayReceive(ProxyConnection connection)
		{
			if (!connection.Client().IsConnected || !connection.HasClientJoinedWorld())
			{
				return false;
			}
			return permissions.Allows(
				connection.ClientLogin.AuthData.Xuid,
				connection.ClientLogin.AuthData.DisplayName,
				"send"
			);
		}

		private List<string> Values()
		{
			// Insertion order matters for stable autocomplete; duplicate gamertags collapse onto one
			// value the way Java's LinkedHashMap did.
			List<string> values = new() { ALL };
			if (connectedPlayers == null)
			{
				return values;
			}
			foreach (ProxyConnection connection in connectedPlayers.Connections())
			{
				string name = connection.ClientLogin.AuthData.DisplayName;
				if (!string.IsNullOrWhiteSpace(name) && !values.Contains(name))
				{
					values.Add(name);
				}
			}
			return values;
		}
	}
}

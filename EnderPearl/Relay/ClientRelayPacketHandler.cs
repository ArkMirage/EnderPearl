using System;
using System.Collections.Generic;
using System.Globalization;
using EnderPearl.Command;
using EnderPearl.Core;
using global::Protocol.Packets;
using EnderPearl.Backend;
using EnderPearl.Player;
using PlayerActionType = global::Protocol.PlayerActionType;

namespace EnderPearl.Relay;

/// <summary>
/// The client-facing half of the relay: everything a connected player sends arrives here on its way to the
/// backend. Owns command interception, movement/chunk-radius bookkeeping, the client side of backend switch
/// resets, proxy resource-pack serving and the identity/normalization passes every forwarded packet goes
/// through.
///
/// <para>The backend-facing half is <see cref="BackendRelayPacketHandler"/>; anything rewritten here must be
/// rewritten back there.</para>
/// </summary>
public sealed partial class ClientRelayPacketHandler : PacketHandler
{
	private readonly ProxyConnection connection;
	private readonly EnderPearl.Command.ProxyCommandInterceptor commandInterceptor;
	private readonly BackendCommandRouter commandRouter;
	private PacketRewriter? rewriter;

	/// <summary>Every serverbound packet rewrite; stateless, so one lazy instance serves the session.</summary>
	private PacketRewriter Rewriter => rewriter ??= new PacketRewriter(connection);

	public ClientRelayPacketHandler(
		ProxyConnection connection,
		EnderPearl.Command.ProxyCommandInterceptor commandInterceptor,
		BackendCommandRouter commandRouter
	)
	{
		this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
		this.commandInterceptor = commandInterceptor ?? throw new ArgumentNullException(nameof(commandInterceptor));
		this.commandRouter = commandRouter ?? throw new ArgumentNullException(nameof(commandRouter));
	}

	/// <summary>
	/// The serverbound pipeline runs in a fixed order: gate, normalize, then route-or-forward.
	/// </summary>
	public override PacketSignal Handle(IPacket packet)
	{
		BackendSession? backend = connection.Backend();
		if (Gate(packet, backend))
		{
			return PacketSignal.Handled;
		}
		Normalize(packet);

		BackendSession? pendingBackend = PendingSwitchBackend();
		if (Route(packet, backend, pendingBackend))
		{
			return PacketSignal.Handled;
		}

		SendToBackend(backend, packet);
		return PacketSignal.Handled;
	}

	/// <summary>
	/// The gates a client packet can be stopped behind. Returns true when the packet was fully handled here.
	/// </summary>
	private bool Gate(IPacket packet, BackendSession? backend)
	{
		if (backend == null || !backend.IsConnected)
		{
			if (connection.IsFailingOver() || connection.IsJoinSequenceActive())
			{
				return true;
			}
			connection.Client.Disconnect("Backend is not connected");
			return true;
		}

		// Protocol 2168 has no serverbound aim-assist instruction packet, so there is no branch here.
		BackendSwitchReset? switchReset = connection.BackendSwitchResetRef();
		if (switchReset == null || !switchReset.IsActive())
		{
			return false;
		}
		if (packet is PlayerActionPacket action && action.Action == PlayerActionType.ChangeDimensionAck)
		{
			switchReset.HandleDimensionChangeSuccess(connection);
			return true;
		}
		if (packet is SetLocalPlayerAsInitializedPacket)
		{
			return true;
		}
		if (packet is ServerboundLoadingScreenPacket loadingScreen)
		{
			switchReset.HandleLoadingScreen(connection, loadingScreen);
			return true;
		}
		if (packet is SubChunkRequestPacket request)
		{
			switchReset.HandleTargetWorldRequest(connection, request.DimensionType?.Value ?? 0);
		}
		return false;
	}

	/// <summary>
	/// In-place edits every forwarded packet rides through: the diagnostic chunk-radius cap (with the
	/// requested radii remembered for the next switch) and the violation log line.
	/// </summary>
	private void Normalize(IPacket packet)
	{
		if (packet is PacketViolationWarningPacket violation)
		{
			Logger.Error(
				$"Client packet violation from {connection.Client.RemoteEndPoint}: type={violation.ViolationType} severity={violation.ViolationSeverity} " +
				$"packetId={violation.ViolationPacketId} message={violation.ViolationContext}.");
		}
		if (packet is RequestChunkRadiusPacket requestChunkRadius)
		{
			NormalizeChunkRadiusRequest(requestChunkRadius);
			connection.RememberChunkRadius(requestChunkRadius.ChunkRadius, requestChunkRadius.MaxChunkRadius);
		}
	}

	/// <summary>
	/// Packets that are answered or re-routed instead of relayed blind: the client's cache-status answer
	/// (always "no blob cache"), resource-pack responses that belong to the pending backend's handshake, and
	/// command requests the proxy may intercept. Returns true when fully handled.
	/// </summary>
	private bool Route(IPacket packet, BackendSession backend, BackendSession? pendingBackend)
	{
		if (packet is ClientCacheStatusPacket)
		{
			BackendSession targetBackend = pendingBackend == null ? backend : pendingBackend;
			targetBackend.SendPacket(new ClientCacheStatusPacket { IsCacheSupported = false });
			return true;
		}
		if (pendingBackend != null && IsBackendLoginResponse(packet))
		{
			SendToBackend(pendingBackend, packet);
			return true;
		}
		if (packet is CommandRequestPacket commandRequest)
		{
			if (IsClientSideCommandPreview(commandRequest))
			{
				return true;
			}
			CommandInterception interception = commandInterceptor.Intercept(commandRequest);
			if (interception is CommandInterception.Consumed consumed)
			{
				commandRouter.Execute(connection, consumed);
				return true;
			}
			SendToBackend(backend, commandRequest);
			return true;
		}
		return false;
	}

	/// <summary>
	/// Caps the chunk view distance the backend is asked for, from <c>-Dproxy.forceChunkRadius=2</c>. Zero
	/// (the default) leaves the client's request alone.
	/// </summary>
	private static readonly int FORCED_CHUNK_RADIUS = ReadIntProperty("proxy.forceChunkRadius", 0);

	private void NormalizeChunkRadiusRequest(RequestChunkRadiusPacket request)
	{
		if (FORCED_CHUNK_RADIUS > 0 && request.ChunkRadius > FORCED_CHUNK_RADIUS)
		{
			request.ChunkRadius = FORCED_CHUNK_RADIUS;
			if (request.MaxChunkRadius > FORCED_CHUNK_RADIUS)
			{
				request.MaxChunkRadius = (byte)Math.Min(FORCED_CHUNK_RADIUS, byte.MaxValue);
			}
		}
	}

	public override void OnDisconnected(string reason)
	{
		connection.CloseBackend(reason);
	}

	private static bool IsClientSideCommandPreview(CommandRequestPacket commandRequest)
	{
		string command = commandRequest.Command;
		if (command == null || command.Trim().Length == 0 || "/".Equals(command.Trim(), StringComparison.Ordinal))
		{
			return true;
		}
		return commandRequest.IsInternal;
	}

	private BackendSession? PendingSwitchBackend()
	{
		BackendSession? pendingBackend = connection.PendingBackend();
		if (!connection.IsSwitchingBackend() || pendingBackend == null || !pendingBackend.IsConnected)
		{
			return null;
		}
		return pendingBackend;
	}

	private static bool IsBackendLoginResponse(IPacket packet)
	{
		return packet is ResourcePackClientResponsePacket
			|| packet is ResourcePackChunkRequestPacket;
	}

	private void SendToBackend(BackendSession? backend, IPacket packet)
	{
		if (backend == null || !backend.IsConnected)
		{
			return;
		}
		// Sub-chunk requests belong to the client's session, so a backend that never advertised the
		// system must not receive them after a switch.
		if (backend.DropSubChunkRequests() && packet is SubChunkRequestPacket)
		{
			return;
		}
		if (packet is ContainerClosePacket clientContainerClose)
		{
			connection.ClientWorldState.TrackClientContainerClose(clientContainerClose.ContainerId);
		}
		Rewriter.RewriteServerbound(packet);
		backend.SendPacket(packet);
	}

	/// <summary>
	/// Java read tunables from system properties (<c>-Dproxy.forceChunkRadius</c>); environment variables
	/// play that role here.
	/// </summary>
	private static int ReadIntProperty(string name, int fallback)
	{
		string? raw = Environment.GetEnvironmentVariable(name);
		return raw != null && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
			? parsed
			: fallback;
	}
}

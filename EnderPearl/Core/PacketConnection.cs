using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Protocol.Packets;
using EnderPearl.Transport;
using EnderPearl.Core;

namespace EnderPearl.Core;

/// <summary>
/// One Bedrock protocol leg over a NetherNet connection: the framing, compression and encryption
/// plumbing (EnderPearl.Core.PacketSession), a read loop dispatching decoded packets to the current
/// handler, and send/close. This plays the role of the Java original's BedrockPeer + session pair.
///
/// <p>The two concrete sides are <c>EnderPearl.Frontend.ListenerSession</c> (client-facing) and
/// <c>EnderPearl.Backend.BackendSession</c> (backend-facing); they differ in disconnect semantics and
/// close behaviour only.</p>
/// </summary>
public abstract class PacketConnection : IDisposable
{
	private readonly object sendMutex = new();
	private volatile NetherNetConnection? conn;
	private volatile PacketHandler? handler;
	private volatile bool closed;
	private Thread? readThread;
	private string? disconnectReason;

	protected PacketConnection()
	{
		Session = new EnderPearl.Core.PacketSession
		{
			OnPackets = DispatchPackets
		};
	}

	public EnderPearl.Core.PacketSession Session { get; }

	public PacketHandler? Handler => handler;

	public bool IsConnected => !closed && conn is { IsConnected: true };

	/// <summary>The remote address of this leg, or null before a connection is attached.</summary>
	public IPEndPoint? RemoteEndPoint => conn?.RemoteEndPoint;

	public string? DisconnectReason => Volatile.Read(ref disconnectReason);

	/// <summary>Swaps the packet handler; used when the login phase hands over to the relay.</summary>
	public void SetPacketHandler(PacketHandler? newHandler)
	{
		handler = newHandler;
	}

	/// <summary>Replaces the handler with one that ignores everything (Java's discardInboundPackets).</summary>
	public void DiscardInboundPackets()
	{
		handler = PacketHandler.Discarding;
	}

	/// <summary>
	/// Attaches a live NetherNet connection without reading from it yet. Java installed each session's
	/// packet handler inside the Netty channel init, strictly before any inbound packet could be
	/// dispatched; splitting attach from StartReading reproduces that ordering - install the
	/// handler first, then call <see cref="StartReading"/>.
	/// </summary>
	public void Attach(NetherNetConnection connection)
	{
		ObjectDisposedException.ThrowIf(closed, this);
		conn = connection;
	}

	/// <summary>Starts the read loop. Requires a handler to be installed already.</summary>
	public void StartReading()
	{
		ObjectDisposedException.ThrowIf(closed, this);
		if (conn == null)
		{
			throw new InvalidOperationException("StartReading called before Attach");
		}

		if (handler == null)
		{
			throw new InvalidOperationException("StartReading called before a packet handler was installed");
		}

		NetherNetConnection connection = conn!;
		readThread = new Thread(ReadLoop)
		{
			Name = GetType().Name + "-" + connection.RemoteEndPoint,
			IsBackground = true
		};
		readThread.Start();
	}

	private void ReadLoop()
	{
		Exception? exitCause = null;
		try
		{
			var connection = conn!;
			while (!closed && connection.IsConnected && !connection.ContextToken.IsCancellationRequested)
			{
				byte[] data = connection.ReadPacket();
				if (data.Length > 0)
				{
					HandleDatagram(data);
				}
			}
		}
		catch (Exception exception) when (exception is ObjectDisposedException or SocketException
			                                  or OperationCanceledException)
		{
// The connection went away (peer disconnect notification, local Disconnect(), or a
// socket error); the finally block logs one accurate line. No logging here - this
// is the routine path for every clean close.
			exitCause = exception;
		}
		catch (Exception exception)
		{
			exitCause = exception;
			Logger.Error($"{GetType().Name} read loop exception on {conn?.RemoteEndPoint}: {exception}");
		}
		finally
		{
// Java surfaced the transport disconnect reason (TIMED_OUT, CLOSED_BY_REMOTE_PEER, ...) to
// the handler's onDisconnect; derive the closest equivalent from why this loop exited so
// failover logs and kick messages carry a real cause instead of "connection closed".
			string reason = disconnectReason ?? DescribeExit(exitCause);
// The transport is gone no matter who ended it. A peer-initiated close never runs
// CloseTransport, so without this `closed` stayed false and IsConnected kept claiming
// a dead session was alive - every "is the client still here" guard downstream lied,
// and a quitting player's backend legs reacted by starting failovers for a client
// that no longer existed.
			closed = true;
			if (disconnectReason == null)
			{
				MarkClosed(reason);
			}

			Logger.Info($"{GetType().Name} leg to {conn?.RemoteEndPoint} closed ({reason}).");
			OnTransportClosed();
		}
	}

	private string DescribeExit(Exception? exitCause)
	{
		switch (exitCause)
		{
			case null:
				return closed ? "closed locally" : "transport closed";
			case SocketException socket:
				return socket.SocketErrorCode switch
				{
					SocketError.TimedOut => "timed out",
					SocketError.ConnectionReset => "closed by remote peer",
					SocketError.ConnectionAborted => "connection aborted",
					SocketError.NetworkDown or SocketError.NetworkUnreachable => "network unreachable",
					_ => socket.Message
				};
			case OperationCanceledException:
			case ObjectDisposedException:
// Both arise from Conn.CloseImmediately(): a local Disconnect(), or the peer's
// transport disconnect notification completing the packet channel. One phrase for
// either direction of a clean close.
				return "disconnect notification";
			default:
				return closed ? "closed locally" : exitCause.GetType().Name;
		}
	}

	private void HandleDatagram(byte[] data)
	{
		try
		{
			var wrapper = new McbeWrapper();
			wrapper.Decode(data);
			Session.HandleMinecraftGamePacket(wrapper);
		}
		catch (Exception exception)
		{
			string dump = BitConverter.ToString(data, 0, Math.Min(data.Length, 48));
			Logger.Error(
				$"{GetType().Name} failed to decode an inbound batch from {conn?.RemoteEndPoint} ({data.Length} bytes): {exception.Message} | {dump}");
			OnBatchDecodeFailure(exception);
		}
	}

	private void DispatchPackets(List<global::Protocol.Packets.IPacket> packets)
	{
		PacketHandler? current = handler;
		if (current == null)
		{
			return;
		}

		foreach (IPacket packet in packets)
		{
			try
			{
				OnPacketReceived(packet);
				current.Handle(packet);
			}
			catch (Exception exception)
			{
				Logger.Error(
					$"{GetType().Name} handler threw for {packet.GetType().Name} from {conn?.RemoteEndPoint}: {exception}");
				OnHandlerFailure(packet, exception);
			}
		}
	}

	/// <summary>Per-packet receive hook (debug printing).</summary>
	protected virtual void OnPacketReceived(IPacket packet)
	{
	}

	/// <summary>Per-packet send hook (debug printing).</summary>
	protected virtual void OnPacketSent(IPacket packet)
	{
	}

	public void SendPacket(IPacket packet)
	{
		SendPackets(new List<IPacket> { packet });
	}

	public void SendPackets(IReadOnlyList<IPacket> packets)
	{
		if (!IsConnected)
		{
			return;
		}

		NetherNetConnection? connection = conn;
		if (connection == null)
		{
			return;
		}

		lock (sendMutex)
		{
			try
			{
				foreach (IPacket packet in packets)
				{
					OnPacketSent(packet);
				}

				byte[] wire = Session.PackPackets(new List<IPacket>(packets), Session.mCompressionAlgorithm).Encode()
					.ToArray();
				connection.Write(wire);
			}
			catch (Exception exception)
			{
				Logger.Error($"{GetType().Name} failed to send to {connection.RemoteEndPoint}: {exception}");
			}
		}
	}

	/// <summary>Closes the transport without any protocol-level goodbye.</summary>
	public void CloseTransport()
	{
		if (closed)
		{
			return;
		}

		closed = true;
		conn?.Close();
	}

	protected void MarkClosed(string? reason)
	{
		disconnectReason = reason;
	}

	public abstract void Dispose();

	/// <summary>An inbound batch could not be decoded; default tears the leg down like Netty's error handler.</summary>
	protected virtual void OnBatchDecodeFailure(Exception exception)
	{
		CloseTransport();
	}

	/// <summary>
	/// A handler threw while processing a packet. Java put Netty's LoggingExceptionHandler on both
	/// pipelines: it logs and then closes the context - a handler that cannot process a packet has
	/// left the session in an unknown state, so the leg goes down instead of the relay carrying on.
	/// </summary>
	protected virtual void OnHandlerFailure(IPacket packet, Exception exception)
	{
		CloseTransport();
	}

	protected virtual void OnTransportClosed()
	{
		PacketHandler? current = handler;
		try
		{
			current?.OnDisconnected(disconnectReason ?? "connection closed");
		}
		catch (Exception exception)
		{
			Logger.Error($"{GetType().Name} handler close-notification threw: {exception}");
		}
	}

	protected NetherNetConnection? Connection => conn;
}
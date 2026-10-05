using System.Net;
using NetherNet;

namespace EnderPearl.Transport;

/// <summary>
/// Adapts a <see cref="NetherNet.Conn"/> to the shape EnderPearl's packet plumbing expects from a transport:
/// IsConnected / RemoteEndPoint / ContextToken / ReadPacket / Write / Close.
/// </summary>
public sealed class NetherNetConnection
{
	private readonly Conn conn;
	private IPEndPoint? remoteEndPoint;
	private bool remoteEndPointResolved;

	public NetherNetConnection(Conn conn)
	{
		this.conn = conn ?? throw new ArgumentNullException(nameof(conn));
	}

	public Conn Inner => conn;

	public bool IsConnected => !conn.IsClosed;

	public CancellationToken ContextToken => conn.Context;

	public IPEndPoint? RemoteEndPoint
	{
		get
		{
			if (!remoteEndPointResolved)
			{
				remoteEndPoint = ResolveRemoteEndPoint();
				remoteEndPointResolved = true;
			}
			return remoteEndPoint;
		}
	}

	private IPEndPoint? ResolveRemoteEndPoint()
	{
		foreach (IceCandidate candidate in conn.RemoteAddr().Candidates)
		{
			if (IPAddress.TryParse(candidate.Address, out IPAddress? ip))
			{
				return new IPEndPoint(ip, candidate.Port);
			}
		}
		return null;
	}

	public byte[] ReadPacket()
	{
		try
		{
			return conn.ReadPacket();
		}
		catch (NetherNetException) when (conn.Context.IsCancellationRequested)
		{
			throw new OperationCanceledException(conn.Context);
		}
	}

	public void Write(byte[] data) => conn.Write(data);

	public void Close() => conn.Close();
}

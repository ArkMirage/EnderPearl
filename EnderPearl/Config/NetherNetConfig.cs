using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using NetherNet;

namespace EnderPearl.Config;

/// <summary>
/// ICE servers handed to NetherNet for the client-facing endpoint and for backend dial-outs.
///
/// <para>A proxy that only ever serves clients on its own LAN needs none of this: host candidates
/// already pair. A proxy reachable from the internet needs a STUN server so it can advertise a
/// reflexive (public) candidate, and a TURN server for the client pairs whose NAT refuses a direct
/// path.</para>
/// </summary>
public sealed class NetherNetConfig
{
	/// <summary>The STUN server used when <c>nethernet.stunServers</c> is absent from the config.</summary>
	public const string DefaultStunServer = "stun:stun.l.google.com:19302";

	public IReadOnlyList<string> StunServers { get; init; } = Array.Empty<string>();

	public IReadOnlyList<TurnServerConfig> TurnServers { get; init; } = Array.Empty<TurnServerConfig>();

	public NetherNet.Credentials ToCredentials()
	{
		var credentials = new NetherNet.Credentials();
		foreach (string url in StunServers)
		{
			credentials.IceServers.Add(new NetherNet.IceServer
			{
				Urls = new List<string> { url }
			});
		}
		foreach (TurnServerConfig turn in TurnServers)
		{
			credentials.IceServers.Add(new NetherNet.IceServer
			{
				Username = turn.Username,
				Password = turn.Password,
				Urls = new List<string>(turn.Urls)
			});
		}
		return credentials;
	}

	public static NetherNetConfig From(JsonConfig config)
	{
		var stun = new List<string>();
		foreach (string url in config.GetStringList("nethernet.stunServers"))
		{
			string trimmed = url.Trim();
			if (trimmed.Length > 0)
			{
				stun.Add(trimmed);
			}
		}
		if (stun.Count == 0 && !config.Has("nethernet.stunServers"))
		{
			stun.Add(DefaultStunServer);
		}

		var turn = new List<TurnServerConfig>();
		foreach (KeyValuePair<string, JsonConfig> entry in config.Members("nethernet.turnServers"))
		{
			var urls = new List<string>();
			foreach (string url in entry.Value.GetStringList("urls"))
			{
				string trimmed = url.Trim();
				if (trimmed.Length > 0)
				{
					urls.Add(trimmed);
				}
			}
			if (urls.Count == 0)
			{
				continue;
			}
			turn.Add(new TurnServerConfig
			{
				Urls = urls,
				Username = entry.Value.GetString("username", ""),
				Password = entry.Value.GetString("password", "")
			});
		}

		return new NetherNetConfig
		{
			StunServers = stun,
			TurnServers = turn
		};
	}

	/// <summary>The <c>"nethernet"</c> section of the generated default configuration.</summary>
	public static JsonObject DefaultSection()
	{
		return new JsonObject
		{
			["stunServers"] = new JsonArray(DefaultStunServer),
			["turnServers"] = new JsonObject()
		};
	}
}

public sealed class TurnServerConfig
{
	public IReadOnlyList<string> Urls { get; init; } = Array.Empty<string>();

	public string Username { get; init; } = "";

	public string Password { get; init; } = "";
}
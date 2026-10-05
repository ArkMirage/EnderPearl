using System;
using System.Collections.Generic;
using EnderPearl.Backend;
using EnderPearl.Config;
using EnderPearl.Core;

namespace EnderPearl.Player;
/// <summary>
/// The player's three move-locks: an explicit backend switch (/server), a failover walking the
/// fallback chain, and the join sequence trying backends before the player first reaches a world.
/// Each can be claimed by exactly one driver at a time; the claim methods answer the losers.
///
/// <para>Not thread-safe by design: every method must be called while holding the owning
/// <see cref="ProxyConnection"/>'s mutex. The SwitchStart/FailoverStart result enums stay declared
/// on <see cref="ProxyConnection"/> so the public API callers already read is unchanged.</para>
/// </summary>
public sealed class PlayerSwitchState
{
	private const long FAILOVER_EPISODE_WINDOW_MILLIS = 30_000;
	private const int MAX_FAILOVERS_PER_EPISODE = 3;
	/// <summary>Comfortably longer than a full /server retry sequence.</summary>
	private const long SWITCH_LOCK_MAX_MILLIS = 120_000;

	private bool backendSwitchInProgress;
	private string? backendSwitchTarget;
	private long backendSwitchStartedAtMillis;
	private bool failoverInProgress;
	private long lastFailoverStartedAtMillis;
	private int failoversInEpisode;
	private bool joinSequenceActive;
	private List<BackendConfig> remainingJoinCandidates = new();
	private long joinAttemptId;
	private long lastHandledJoinAttemptId = -1;
	private bool clientJoinedWorld;

	/// <summary>
	/// Claims the right to move this player to another backend.
	///
	/// <p>The lock is released by whoever took it, but a stuck one strands the player on "already
	/// connecting" with no way out short of reconnecting. SWITCH_LOCK_MAX_MILLIS bounds that.</p>
	/// </summary>
	public ProxyConnection.SwitchStart BeginBackendSwitch(string name)
	{
		if (backendSwitchInProgress)
		{
			long heldForMillis = CurrentTimeMillis() - backendSwitchStartedAtMillis;
			if (heldForMillis < SWITCH_LOCK_MAX_MILLIS)
			{
				return ProxyConnection.SwitchStart.ALREADY_SWITCHING;
			}
			Logger.Info(
				$"Backend switch to {backendSwitchTarget} has been in progress for {heldForMillis}ms with no outcome; taking the switch over for {name}.");
		}
		backendSwitchInProgress = true;
		backendSwitchTarget = name;
		backendSwitchStartedAtMillis = CurrentTimeMillis();
		return ProxyConnection.SwitchStart.STARTED;
	}

	/// <summary>Releases the switch lock (also called when a backend took over and finished it).</summary>
	public void FinishBackendSwitch()
	{
		backendSwitchInProgress = false;
		backendSwitchTarget = null;
	}

	/// <summary>Clears the switch lock as part of a backend handoff; caller already holds the mutex.</summary>
	public void EndSwitchLocked()
	{
		backendSwitchInProgress = false;
		backendSwitchTarget = null;
	}

	public string? BackendSwitchTarget() => backendSwitchTarget;

	public bool IsSwitchInProgress() => backendSwitchInProgress;

	/// <summary>
	/// Marks the start of a failover: the backend the player was on has gone away and the proxy is
	/// walking the fallback chain looking for one that will take them.
	///
	/// <p>Hops that keep happening inside FAILOVER_EPISODE_WINDOW_MILLIS are counted as one episode
	/// and capped.</p>
	/// </summary>
	public ProxyConnection.FailoverStart BeginFailover()
	{
		if (failoverInProgress)
		{
			return ProxyConnection.FailoverStart.ALREADY_RUNNING;
		}
		long now = CurrentTimeMillis();
		failoversInEpisode = now - lastFailoverStartedAtMillis <= FAILOVER_EPISODE_WINDOW_MILLIS
			? failoversInEpisode + 1
			: 1;
		lastFailoverStartedAtMillis = now;
		if (failoversInEpisode > MAX_FAILOVERS_PER_EPISODE)
		{
			return ProxyConnection.FailoverStart.TOO_MANY;
		}
		failoverInProgress = true;
		return ProxyConnection.FailoverStart.STARTED;
	}

	public void FinishFailover()
	{
		failoverInProgress = false;
	}

	public bool IsFailingOver() => failoverInProgress;

	/// <summary>
	/// Starts the join sequence: the ordered backends to try before giving up on a player who has
	/// not reached a world yet.
	/// </summary>
	public void BeginJoinSequence(List<BackendConfig> candidates)
	{
		joinSequenceActive = true;
		remainingJoinCandidates = new List<BackendConfig>(candidates);
	}

	public bool IsJoinSequenceActive() => joinSequenceActive;

	public void EndJoinSequence()
	{
		joinSequenceActive = false;
		remainingJoinCandidates.Clear();
	}

	public BackendConfig? NextJoinCandidate()
	{
		if (remainingJoinCandidates.Count == 0)
		{
			return null;
		}
		BackendConfig candidate = remainingJoinCandidates[0];
		remainingJoinCandidates.RemoveAt(0);
		return candidate;
	}

	/// <summary>Numbers the current attempt, so a failure of an earlier one cannot end a later one.</summary>
	public void BeginJoinAttempt()
	{
		joinAttemptId++;
	}

	/// <summary>
	/// Claims the right to react to the current attempt's failure. One dead backend surfaces on
	/// several paths at once; the first caller acts, the rest are told the failure is already handled.
	/// </summary>
	public bool ClaimJoinFailure()
	{
		if (lastHandledJoinAttemptId == joinAttemptId)
		{
			return false;
		}
		lastHandledJoinAttemptId = joinAttemptId;
		return true;
	}

	/// <summary>
	/// Records that the client has been handed a StartGame and is in a world. Deliberately not reset
	/// by a backend swap: once a client is in a world it stays in one across every subsequent switch.
	/// </summary>
	public void MarkClientJoinedWorld()
	{
		clientJoinedWorld = true;
		joinSequenceActive = false;
		remainingJoinCandidates.Clear();
	}

	public bool HasClientJoinedWorld() => clientJoinedWorld;

	private static long CurrentTimeMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

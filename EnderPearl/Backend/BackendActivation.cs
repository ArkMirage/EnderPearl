using System;

namespace EnderPearl.Backend;
/// <summary>
/// What the caller of a backend connect wants to hear about as the handshake progresses.
///
/// <para>Subclasses decide the outcome policy: the plain join lands the player or walks the join
/// try-list, the switch completes a pending handoff future, and the guarded variant wraps another
/// activation to restore session state on failure.</para>
/// </summary>
public abstract class BackendActivation
{
	/// <summary>The encryption handshake completed and the relay is installed.</summary>
	public virtual void OnReady(BackendSession backend)
	{
	}

	/// <summary>The target's StartGame has arrived.</summary>
	public virtual void OnStartGame(BackendSession backend)
	{
	}

	public abstract void OnFailure(BackendSession? backend, Exception exception);
}

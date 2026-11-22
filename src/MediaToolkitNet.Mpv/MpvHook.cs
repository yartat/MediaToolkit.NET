#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

namespace MediaToolkitNet.Mpv;

/// <summary>
/// Runs when mpv reaches a hook: a point where it waits for you before it goes on. It runs on the player's event
/// thread, so no other event of this player is delivered meanwhile and an <c>await</c> on the player's own
/// asynchronous commands inside it never finishes; call <see cref="MpvHook.Defer"/> to do that kind of work.
/// </summary>
/// <param name="hook">The hook that was reached.</param>
public delegate void MpvHookHandler(MpvHook hook);

/// <summary>
/// One run of a hook. mpv holds the file it is loading (or closing) until every handler of the hook has let it go:
/// when the handler returns, or, after <see cref="Defer"/>, when <see cref="Continue"/> is called.
/// </summary>
public sealed class MpvHook
{
    private readonly MpvPlayer _player;
    private int _continued;

    internal MpvHook(MpvPlayer player, string name, ulong id)
    {
        _player = player;
        Name = name;
        Id = id;
    }

    /// <summary>The hook's name, for example <c>on_load</c>.</summary>
    public string Name { get; }

    /// <summary>The player that reached the hook, for reading and setting properties while mpv waits.</summary>
    public MpvPlayer Player => _player;

    internal ulong Id { get; }

    internal bool Deferred { get; private set; }

    /// <summary>
    /// Keeps mpv waiting after the handler returns, so the work can go on elsewhere, on another thread or after an
    /// <c>await</c>. Call <see cref="Continue"/> when it is done: mpv does nothing else with this file until then.
    /// </summary>
    public void Defer() => Deferred = true;

    /// <summary>
    /// Lets mpv go on. Does nothing the second time, and nothing when the player has been disposed. Call it from any
    /// thread once the hook was deferred; a handler that was not deferred does not need it.
    /// </summary>
    public void Continue()
    {
        if (Interlocked.Exchange(ref _continued, 1) == 0)
        {
            _player.ContinueHook(Id);
        }
    }
}

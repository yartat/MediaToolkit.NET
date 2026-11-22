#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

namespace MediaToolkitNet.Mpv;

/// <summary>
/// Called when a property mpv is watching changes, and once at the start with its current value. It runs on the
/// player's event thread, so no other event of that player is delivered meanwhile, and a handler that throws is
/// reported through <see cref="MpvPlayer.Failed"/>.
/// </summary>
/// <param name="name">The property.</param>
/// <param name="value">The new value, or <see langword="null"/> when the property has none at the moment.</param>
public delegate void MpvPropertyChanged(string name, MpvNode? value);

/// <summary>One argument of an mpv command, as <c>command-list</c> describes it.</summary>
/// <param name="Name">The argument's name, which a command given with named arguments takes.</param>
/// <param name="Type">mpv's name of its type, for example <c>String</c>, <c>Integer</c>, <c>Flags</c>.</param>
/// <param name="IsOptional">Whether it can be left out.</param>
/// <param name="Default">The default of an optional argument, when mpv says one (0.42).</param>
/// <param name="Choices">The values a choice argument accepts, when mpv says them (0.42).</param>
public sealed record MpvCommandArgument(
    string Name,
    string Type,
    bool IsOptional,
    MpvNode? Default,
    IReadOnlyList<string> Choices);

/// <summary>One command of mpv, as <c>command-list</c> describes it.</summary>
/// <param name="Name">The command's name.</param>
/// <param name="Arguments">Its arguments, in order.</param>
/// <param name="IsVariadic">Whether it takes any number of arguments after those.</param>
public sealed record MpvCommandInfo(string Name, IReadOnlyList<MpvCommandArgument> Arguments, bool IsVariadic)
{
    internal static MpvCommandInfo From(MpvNode node) => new(
        node["name"]?.AsString() ?? string.Empty,
        [.. (node["args"]?.Items ?? []).Select(a => new MpvCommandArgument(
            a["name"]?.AsString() ?? string.Empty,
            a["type"]?.AsString() ?? string.Empty,
            a["optional"]?.AsFlag() ?? false,
            a["default"],
            [.. (a["choices"]?.Items ?? []).Select(c => c.AsString()).OfType<string>()]))],
        node["vararg"]?.AsFlag() ?? false);
}

/// <summary>A watch on a property, from <see cref="MpvPlayer.ObserveProperty"/>. Disposing it stops the watch.</summary>
public sealed class MpvObservation : IDisposable
{
    private readonly MpvPlayer _player;
    private int _disposed;

    internal MpvObservation(MpvPlayer player, ulong id)
    {
        _player = player;
        Id = id;
    }

    internal ulong Id { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _player.Unobserve(Id);
        }
    }
}

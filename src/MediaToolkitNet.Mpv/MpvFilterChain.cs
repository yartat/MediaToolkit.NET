namespace MediaToolkitNet.Mpv;

/// <summary>
/// One of mpv's two filter chains, <c>vf</c> for video and <c>af</c> for audio.
/// </summary>
/// <remarks>
/// <para>
/// This is what mpv has in place of a filter graph the caller assembles: mpv
/// builds and rebuilds the graph itself, and the chain is set as a string. The
/// filters are mpv's own — <c>format</c>, <c>scale</c>, <c>sub</c> and so on —
/// plus anything libavfilter offers through <c>lavfi</c>, so
/// <see cref="Lavfi"/> turns a chain written for <c>FFmpegFilterGraph</c> into
/// something mpv accepts.
/// </para>
/// <para>
/// mpv applies a change immediately and re-creates the chain on the next frame,
/// so a filter that cannot be initialised fails here rather than silently.
/// </para>
/// </remarks>
public sealed class MpvFilterChain
{
    private readonly MpvPlayer _player;

    internal MpvFilterChain(MpvPlayer player, string property)
    {
        _player = player;
        Property = property;
    }

    /// <summary>The mpv property this chain lives in: <c>vf</c> or <c>af</c>.</summary>
    public string Property { get; }

    /// <summary>The chain as mpv currently has it, or <see langword="null"/> when it is empty.</summary>
    public string? Current => _player.GetPropertyString(Property);

    /// <summary>Replaces the whole chain.</summary>
    /// <param name="chain">An mpv filter chain, or an empty string to clear it.</param>
    public void Set(string chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        _player.Command(Property, "set", chain);
    }

    /// <summary>Appends one filter to the end of the chain.</summary>
    public void Add(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        _player.Command(Property, "add", filter);
    }

    /// <summary>Removes a filter that was added earlier.</summary>
    /// <param name="filter">The same string it was added with, or its index in the chain.</param>
    public void Remove(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        _player.Command(Property, "del", filter);
    }

    /// <summary>Turns a filter off without removing it, or back on.</summary>
    public void Toggle(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        _player.Command(Property, "toggle", filter);
    }

    /// <summary>Removes every filter from the chain.</summary>
    public void Clear() => _player.Command(Property, "clr");

    /// <summary>
    /// Wraps a libavfilter chain in the <c>lavfi</c> filter, which is how mpv
    /// takes one.
    /// </summary>
    /// <param name="chain">A chain in FFmpeg syntax, for example <c>scale=640:-1,hflip</c>.</param>
    /// <returns>Returns the mpv filter string, <c>lavfi=[...]</c>.</returns>
    public static string Lavfi(string chain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chain);
        return $"lavfi=[{chain}]";
    }

    /// <inheritdoc />
    public override string ToString() => $"{Property}={Current ?? string.Empty}";
}

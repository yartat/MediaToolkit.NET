#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Text;

namespace MediaToolkitNet.Abstractions.Frames;

/// <summary>One subtitle event: a piece of text and when it is on screen.</summary>
/// <remarks>
/// Unlike <see cref="VideoFrame"/> and <see cref="AudioFrame"/>, a cue is an
/// ordinary record rather than a borrowed view: it is a few hundred bytes of
/// text, and copying it costs nothing worth avoiding.
/// </remarks>
/// <param name="Start">When the text appears, measured from the start of the stream.</param>
/// <param name="End">When it goes away.</param>
/// <param name="Text">The text, with line breaks as <c>\n</c> and styling removed.</param>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text)
{
    /// <summary>
    /// The event as ASS writes it, styling included, when the source had one:
    /// every FFmpeg text decoder produces ASS, whatever format it reads.
    /// </summary>
    public string? Ass { get; init; }

    /// <summary>How long the text is on screen.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>
    /// The text of an ASS event line, which after eight commas of layer, style,
    /// name, margins and effect holds the text, with <c>\N</c> for a line break
    /// and <c>{\...}</c> overrides for styling.
    /// </summary>
    public static string TextOfAssEvent(string assEvent)
    {
        ArgumentNullException.ThrowIfNull(assEvent);

        var start = 0;
        for (var commas = 0; commas < 8; commas++)
        {
            var next = assEvent.IndexOf(',', start);
            if (next < 0)
            {
                // Not an event line after all; take it as plain text.
                start = 0;
                break;
            }

            start = next + 1;
        }

        var builder = new StringBuilder(assEvent.Length - start);
        for (var i = start; i < assEvent.Length; i++)
        {
            var c = assEvent[i];
            if (c == '{' && assEvent.IndexOf('}', i) is var close && close > i)
            {
                i = close;
            }
            else if (c == '\\' && i + 1 < assEvent.Length && assEvent[i + 1] is 'N' or 'n')
            {
                builder.Append('\n');
                i++;
            }
            else if (c == '\\' && i + 1 < assEvent.Length && assEvent[i + 1] == 'h')
            {
                builder.Append(' ');
                i++;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

namespace MediaToolkitNet.Abstractions;

/// <summary>
/// Raises events from threads the library owns. A subscriber's exception on
/// such a thread, or inside a native callback, ends the process, so it stops here.
/// </summary>
public static class SafeEvents
{
    /// <summary>Raises a <c>Failed</c> event; an exception thrown by a subscriber is swallowed.</summary>
    /// <param name="handler">The event, which may have no subscribers.</param>
    /// <param name="sender">The object raising it.</param>
    /// <param name="error">What failed.</param>
    public static void RaiseFailed(EventHandler<MediaToolkitNetException>? handler, object sender, MediaToolkitNetException error)
    {
        try
        {
            handler?.Invoke(sender, error);
        }
        catch (Exception)
        {
            // Nothing can be done about a subscriber that throws from the failure handler.
        }
    }
}

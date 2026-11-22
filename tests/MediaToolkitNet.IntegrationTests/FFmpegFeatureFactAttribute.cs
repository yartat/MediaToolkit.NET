#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.FFmpeg;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// A fact that is skipped unless FFmpeg is loadable and the libraries have the
/// named <see cref="FFmpegFeatures"/> property: FFmpeg 8.x, 9.0 and 9.1 each
/// added API that older series, and the FFmpeg of a Linux distribution, lack.
/// </summary>
public sealed class FFmpegFeatureFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="FFmpegFeatureFactAttribute"/> class.</summary>
    /// <param name="feature">The name of a bool property of <see cref="FFmpegFeatures"/>.</param>
    public FFmpegFeatureFactAttribute(string feature)
    {
        if (FFmpegEnvironment.SkipReason is { } reason)
        {
            Skip = reason;
            return;
        }

        var property = typeof(FFmpegFeatures).GetProperty(feature)
            ?? throw new ArgumentException($"FFmpegFeatures has no property {feature}.", nameof(feature));
        if (property.GetValue(null) is not true)
        {
            Skip = $"These FFmpeg libraries ({FFmpegEnvironment.Versions}) do not have {feature}.";
        }
    }
}

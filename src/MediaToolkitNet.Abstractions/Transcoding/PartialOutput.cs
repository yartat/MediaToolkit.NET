#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

namespace MediaToolkitNet.Abstractions.Transcoding;

/// <summary>
/// A file being written beside the one it will become. The job writes
/// <see cref="Path"/>; <see cref="Commit"/> renames it over the real output, and
/// disposing without a commit deletes it. A file that already exists at the
/// output, or a link to an input, is therefore never opened for writing.
/// </summary>
/// <remarks>
/// The partial file has the output's extension, which is how muxers choose a
/// format. An output that is not a plain file path, such as <c>pipe:1</c> or a
/// URL, is used as it is and <see cref="Commit"/> does nothing.
/// </remarks>
public sealed class PartialOutput : IDisposable
{
    private readonly string _target;
    private readonly bool _isFile;
    private bool _committed;

    private PartialOutput(string target, string path, bool isFile)
    {
        _target = target;
        Path = path;
        _isFile = isFile;
    }

    /// <summary>Where the job should write.</summary>
    public string Path { get; }

    /// <summary>Chooses the partial path for an output.</summary>
    /// <param name="output">The output the request names.</param>
    public static PartialOutput Begin(string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        if (output.Contains("://", StringComparison.Ordinal) || output.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase))
        {
            return new PartialOutput(output, output, isFile: false);
        }

        var full = System.IO.Path.GetFullPath(output);
        var directory = System.IO.Path.GetDirectoryName(full)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(full);
        var partial = System.IO.Path.Combine(
            directory, $".{name}.partial-{Guid.NewGuid():N}{System.IO.Path.GetExtension(full)}");
        return new PartialOutput(full, partial, isFile: true);
    }

    /// <summary>Makes the partial file the output, replacing whatever is there.</summary>
    public void Commit()
    {
        if (!_isFile)
        {
            return;
        }

        // A rename replaces the directory entry, so a link at the output is replaced, not followed.
        File.Move(Path, _target, overwrite: true);
        _committed = true;
    }

    /// <summary>Deletes the partial file unless it was committed.</summary>
    public void Dispose()
    {
        if (!_isFile || _committed)
        {
            return;
        }

        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The job's own error matters more than a leftover temporary file.
        }
    }
}

#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Interop;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>AUDIT.md SEC-2 and SEC-3: where native libraries may be loaded from.</summary>
public sealed class NativeSearchPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mtn-search-" + Guid.NewGuid().ToString("n"));

    public NativeSearchPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AnAncestorFolderThatIsNotTheRepositoryIsNotSearched()
    {
        // <root>/src/FFMpeg/<platform> exists, but <root> is not a checkout.
        var planted = Directory.CreateDirectory(Path.Combine(_root, "src", "FFMpeg", NativeSearchPaths.PlatformFolder));
        var app = Directory.CreateDirectory(Path.Combine(_root, "Program Files", "Vendor", "App"));

        NativeSearchPaths.Resolve(app.FullName).Should().NotContain(planted.FullName);
    }

    [Fact]
    public void TheCheckedInFolderIsStillFoundFromInsideTheRepository()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "src", "FFMpeg", NativeSearchPaths.PlatformFolder));
        File.WriteAllText(Path.Combine(_root, "MediaToolkit.NET.slnx"), "<Solution />");
        var bin = Directory.CreateDirectory(Path.Combine(_root, "tests", "X", "bin", "Debug", "net10.0"));

        NativeSearchPaths.Resolve(bin.FullName).Should().Contain(folder.FullName);
    }

    [Fact]
    public void RelativeAndEmptyPathEntriesAreNotLoadedFrom()
    {
        var absolute = Path.GetFullPath(_root);
        var variable = string.Join(Path.PathSeparator, "", "relative" + Path.DirectorySeparatorChar + "dir", ".", absolute);

        NativeSearchPaths.PathEntries(variable).Should().Equal(absolute);
    }
}

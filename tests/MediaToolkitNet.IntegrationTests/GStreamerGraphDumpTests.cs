#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// The Graphviz dump of a pipeline. The names, the size and the property values asked about are ones the test chose, so
/// finding them in the text means the dump is of this pipeline.
/// </summary>
[SupportedOSPlatform("linux")]
public class GStreamerGraphDumpTests
{
    private const string Description =
        "videotestsrc name=camera num-buffers=37 ! videoconvert ! video/x-raw,format=BGRA,width=66,height=50 ! fakesink name=screen";

    [GStreamerOnlyFact]
    public void TheDumpNamesTheElementsTheirLinksAndTheNegotiatedFormat()
    {
        using var pipeline = GStreamerPipeline.Parse(Description);
        pipeline.Pause();

        var dot = pipeline.ToDot();

        dot.Should().StartWith("digraph pipeline {").And.EndWith("}\n");
        dot.Should().Contain("camera").And.Contain("screen").And.Contain("GstVideoTestSrc").And.Contain("->");
        dot.Should().Contain("video/x-raw").And.Contain("width: 66").And.Contain("height: 50");
        dot.Should().Contain("num-buffers=37", "a property that is not its default is shown");
    }

    [GStreamerOnlyFact]
    public void TheDetailsAskedForDecideWhatIsInTheDump()
    {
        using var pipeline = GStreamerPipeline.Parse(Description);
        pipeline.Pause();

        var brief = pipeline.ToDot(GstGraphDetails.MediaType);
        var bare = pipeline.ToDot(GstGraphDetails.None);

        brief.Should().Contain("video/x-raw").And.NotContain("width: 66").And.NotContain("num-buffers=37");
        bare.Should().Contain("camera").And.NotContain("video/x-raw").And.NotContain("num-buffers=37");
    }

    [GStreamerOnlyFact]
    public void APipelineThatHasNotStartedCanBeDumpedAndADisposedOneCannot()
    {
        var pipeline = GStreamerPipeline.Parse(Description);

        pipeline.ToDot().Should().Contain("camera").And.NotContain("width: 66", "nothing is negotiated before it starts");

        pipeline.Dispose();
        var act = () => pipeline.ToDot();
        act.Should().Throw<ObjectDisposedException>();
    }
}

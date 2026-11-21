#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Mpv;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>AUDIT.md BUG-12 and SEC-4.</summary>
public class AuditMpvQuotingTests
{
    [Fact]
    public void ALavfiChainWithABracketInsideIsLengthPrefixed()
    {
        const string chain = "drawtext=text='a]b'";

        MpvFilterChain.Lavfi(chain).Should().Be($"lavfi=graph=%{chain.Length}%{chain}");
    }

    [Fact]
    public void ThePlayerDoesNotRunYtdlOrScriptsByDefault()
    {
        using var player = new MpvPlayer();

        player.Options.Should().Contain("ytdl", "no").And.Contain("load-scripts", "no");
    }
}

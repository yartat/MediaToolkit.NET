#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.FFmpeg.Reading;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// The other side of the network policy and of the interrupt callback: an
/// input that is allowed still opens and reads through the pre-opened path.
/// </summary>
public class AuditNetworkTests
{
    [FFmpegFact]
    public void ANetworkInputIsReadWhenTheCallerAllowsIt()
    {
        using var media = new TestMedia();
        var clip = media.PathTo("clip.ts");
        FfmpegProgram.Run($"-f lavfi -i testsrc=d=1:s=160x120:r=25 -c:v mpeg2video -f mpegts \"{clip}\"");
        var bytes = File.ReadAllBytes(clip);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => Serve(listener, bytes));

        try
        {
            using var reader = FFmpegMediaReader.Open(
                $"http://127.0.0.1:{port}/clip.ts",
                new MediaReaderOptions
                {
                    DemuxerOptions = new Dictionary<string, string> { ["protocol_whitelist"] = "file,http,tcp,crypto" },
                });

            var frames = 0;
            while (reader.Video!.TryReadNext((in VideoFrame _) => frames++))
            {
            }

            frames.Should().BeGreaterThan(20);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Serve(TcpListener listener, byte[] bytes)
    {
        try
        {
            while (true)
            {
                using var client = listener.AcceptTcpClient();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var request = string.Empty;
                while (!request.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    request += Encoding.ASCII.GetString(buffer, 0, read);
                }

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                stream.Write(header);
                stream.Write(bytes);
            }
        }
        catch (Exception)
        {
            // The listener is stopped when the test ends.
        }
    }
}

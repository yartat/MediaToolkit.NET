#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>Where a WebRTC connection is, <c>GstWebRTCPeerConnectionState</c>.</summary>
public enum GStreamerWebRtcState
{
    /// <summary>Nothing has been tried.</summary>
    New = 0,

    /// <summary>The two sides are looking for a way to each other.</summary>
    Connecting = 1,

    /// <summary>Media can flow.</summary>
    Connected = 2,

    /// <summary>The way was lost, perhaps for a moment.</summary>
    Disconnected = 3,

    /// <summary>No way was found.</summary>
    Failed = 4,

    /// <summary>The connection was closed.</summary>
    Closed = 5,
}

/// <summary>A way the other side might be reached, to hand over as it is found.</summary>
/// <param name="MediaLine">Which media section of the description it belongs to.</param>
/// <param name="Candidate">The candidate in SDP form.</param>
public readonly record struct GStreamerIceCandidate(int MediaLine, string Candidate);

/// <summary>A stream the other side sends, as RTP on a new pad.</summary>
[SupportedOSPlatform("linux")]
public sealed class GStreamerWebRtcStream
{
    private readonly GStreamerWebRtcPeer _peer;

    internal GStreamerWebRtcStream(GStreamerWebRtcPeer peer, string pad)
    {
        _peer = peer;
        Pad = pad;
    }

    /// <summary>The pad of <c>webrtcbin</c> that carries it.</summary>
    public string Pad { get; }

    /// <summary>
    /// Takes the stream into a part of a pipeline, built from a description that starts with the depayloader, such as
    /// <c>rtpvp8depay ! vp8dec ! appsink name=picture async=false</c>.
    /// </summary>
    /// <returns>The bin that was made; the pipeline owns it.</returns>
    public GStreamerElement ReceiveInto(string description)
    {
        var bin = _peer.Pipeline.AddBin(description);
        _peer.Bin.Link(bin, Pad, "sink");
        bin.SyncStateWithPipeline();
        return bin;
    }
}

/// <summary>
/// One end of a WebRTC connection, with <c>webrtcbin</c>: what it sends and receives are parts of its
/// <see cref="Pipeline"/>, and the offer, the answer and the candidates are text for the application to carry across,
/// or <see cref="ExchangeAsync"/> carries them the way WHIP and WHEP do.
/// </summary>
/// <remarks>
/// <para>
/// Add what it sends and receives, <see cref="Start"/>, then either side makes an offer. Events are raised on threads of
/// GStreamer's.
/// </para>
/// <para>
/// WebRTC media is always encrypted, but who is at the other end is only as trustworthy as the way the descriptions got
/// there: carry them over a channel you trust. Data channels are not offered.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class GStreamerWebRtcPeer : IDisposable
{
    private const int Offer = 1;
    private const int Answer = 3;
    private const int GatheringComplete = 2;
    private const int Replied = 2;
    private const int ReceiveOnly = 3;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _negotiationNeeded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<nint> _waiting = [];
    private bool _sends;
    private volatile bool _disposed;

    /// <summary>Creates a peer.</summary>
    /// <param name="stunServer">
    /// A STUN server such as <c>stun://stun.example.org:3478</c>, to be found from outside the local network; null for none.
    /// </param>
    /// <exception cref="NotSupportedException"><c>webrtcbin</c> or its ICE plugin is not installed.</exception>
    public GStreamerWebRtcPeer(string? stunServer = null)
    {
        if (!IsAvailable)
        {
            throw new NotSupportedException("WebRTC needs webrtcbin (gst-plugins-bad) and the nice plugin (gstreamer1.0-nice).");
        }

        Pipeline = GStreamerPipeline.Create();
        Bin = Pipeline.Add("webrtcbin", "webrtc");
        Bin.Set("bundle-policy", "max-bundle");
        if (stunServer is not null)
        {
            Bin.SetValue("stun-server", stunServer);
        }

        Bin.Failed += (_, e) => SafeEvents.RaiseFailed(Failed, this, e);
        Bin.Connect("on-negotiation-needed", _ => _negotiationNeeded.TrySetResult());
        Bin.Connect("on-ice-candidate", args =>
            IceCandidate?.Invoke(this, new GStreamerIceCandidate((int)args.Get<uint>(0), args.Get<string>(1))));
        Bin.Connect("pad-added", args =>
        {
            if (args.NameOf(0) is { } pad && pad.StartsWith("src_", StringComparison.Ordinal))
            {
                StreamReceived?.Invoke(new GStreamerWebRtcStream(this, pad));
            }
        });
        Pipeline.Bus.MessageReceived += (_, message) =>
        {
            if (message.Type == GstMessageType.Error && !_disposed)
            {
                SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(
                    GstConstants.BackendName, $"{message.Text} ({message.Debug})", 0));
            }
        };
    }

    /// <summary>True when GStreamer here can make a WebRTC connection.</summary>
    public static bool IsAvailable =>
        OperatingSystem.IsLinux() && GstWebRtc.IsAvailable && GStreamerElements.Exists("webrtcbin") && GStreamerElements.Exists("nicesrc");

    /// <summary>The pipeline of this peer.</summary>
    public GStreamerPipeline Pipeline { get; }

    /// <summary>Where the connection is.</summary>
    public GStreamerWebRtcState ConnectionState => (GStreamerWebRtcState)Bin.Get<int>("connection-state");

    /// <summary>Raised for each candidate this side finds; give it to the other side's <see cref="AddIceCandidate"/>.</summary>
    public event EventHandler<GStreamerIceCandidate>? IceCandidate;

    /// <summary>Raised for each stream the other side sends; take it with <see cref="GStreamerWebRtcStream.ReceiveInto"/>.</summary>
    public event Action<GStreamerWebRtcStream>? StreamReceived;

    /// <summary>Raised when the pipeline fails or a handler throws.</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    internal GStreamerElement Bin { get; }

    /// <summary>Adds something to send, before <see cref="Start"/>.</summary>
    /// <param name="description">
    /// A part of a pipeline ending in an RTP payloader and its caps, such as
    /// <c>... ! rtpvp8pay ! application/x-rtp,media=video,encoding-name=VP8,payload=96</c>.
    /// </param>
    public void AddSender(string description)
    {
        // A description cannot end in caps, which is how a sender's naturally does, so it is given an element to end in.
        var bin = Pipeline.AddBin(description + " ! identity");
        bin.Link(Bin, "src", "sink_%u");
        _sends = true;
    }

    /// <summary>
    /// Says this side wants to receive something it does not send, so that its offer asks for it, as a WHEP viewer's does.
    /// </summary>
    /// <param name="rtpCaps">Such as <c>application/x-rtp,media=video,encoding-name=VP8,payload=96,clock-rate=90000</c>.</param>
    public void AddReceiver(string rtpCaps) =>
        Bin.EmitDiscardingResult("add-transceiver", ReceiveOnly, GStreamerCaps.Parse(rtpCaps));

    /// <summary>Starts the pipeline.</summary>
    public void Start() => Pipeline.Play();

    /// <summary>Makes an offer and takes it as this side's description.</summary>
    /// <returns>The offer in SDP, for the other side's <see cref="AcceptOfferAsync"/>.</returns>
    public async Task<string> CreateOfferAsync(CancellationToken cancellation = default)
    {
        if (_sends)
        {
            // The senders' formats have to be known before an offer can name them.
            await Task.WhenAny(_negotiationNeeded.Task, Task.Delay(Patience, cancellation)).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }

        var offer = await CreateAsync("create-offer", "offer").ConfigureAwait(false);
        await SetAsync("set-local-description", Offer, offer).ConfigureAwait(false);
        return offer;
    }

    /// <summary>Takes the other side's offer and answers it.</summary>
    /// <returns>The answer in SDP, for the other side's <see cref="AcceptAnswerAsync"/>.</returns>
    /// <exception cref="MediaToolkitNetException">The offer is not usable.</exception>
    public async Task<string> AcceptOfferAsync(string offer)
    {
        await SetAsync("set-remote-description", Offer, offer).ConfigureAwait(false);
        var answer = await CreateAsync("create-answer", "answer").ConfigureAwait(false);
        await SetAsync("set-local-description", Answer, answer).ConfigureAwait(false);
        return answer;
    }

    /// <summary>Takes the answer to this side's offer.</summary>
    /// <exception cref="MediaToolkitNetException">The answer is not usable.</exception>
    public Task AcceptAnswerAsync(string answer) => SetAsync("set-remote-description", Answer, answer);

    /// <summary>Adds a candidate the other side found; both descriptions should be set first.</summary>
    public void AddIceCandidate(GStreamerIceCandidate candidate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Bin.Emit("add-ice-candidate", (uint)candidate.MediaLine, candidate.Candidate);
    }

    /// <summary>
    /// This side's description with every candidate in it, once they have all been gathered: for a signalling that
    /// cannot pass them one by one.
    /// </summary>
    /// <exception cref="MediaToolkitNetException">There is no description, or gathering did not finish in time.</exception>
    public async Task<string> GetCompleteDescriptionAsync(CancellationToken cancellation = default)
    {
        var clock = Stopwatch.StartNew();
        while (Bin.Get<int>("ice-gathering-state") != GatheringComplete)
        {
            if (clock.Elapsed > Patience)
            {
                throw Refused("the candidates were not gathered in time.");
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }

        return LocalDescription() ?? throw Refused("there is no local description yet.");
    }

    /// <summary>
    /// Carries the descriptions over HTTP as WHIP (publishing) and WHEP (playing) do: the offer, with its candidates, is
    /// posted to <paramref name="endpoint"/> and the answer comes back.
    /// </summary>
    /// <param name="endpoint">The WHIP or WHEP URL; use <c>https</c> when a token goes with it.</param>
    /// <param name="bearerToken">Sent as <c>Authorization: Bearer</c>, or null.</param>
    /// <param name="client">The HTTP client to use, or null for one of this call's own.</param>
    /// <param name="cancellation">Cancels the exchange.</param>
    /// <returns>
    /// The session's URL when the endpoint names one; end the session by sending <c>DELETE</c> to it.
    /// </returns>
    /// <exception cref="MediaToolkitNetException">The endpoint refused, or its answer is not usable.</exception>
    public async Task<Uri?> ExchangeAsync(
        Uri endpoint, string? bearerToken = null, HttpClient? client = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        await CreateOfferAsync(cancellation).ConfigureAwait(false);
        var offer = await GetCompleteDescriptionAsync(cancellation).ConfigureAwait(false);

        using var own = client is null ? new HttpClient() : null;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(offer)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/sdp");
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await (client ?? own!).SendAsync(request, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Refused($"the endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await AcceptAnswerAsync(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        return response.Headers.Location is { } location ? new Uri(endpoint, location) : null;
    }

    /// <inheritdoc />
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _negotiationNeeded.TrySetResult(); // whoever waits for it goes on, to find the peer disposed
        lock (_waiting)
        {
            foreach (var promise in _waiting)
            {
                GstWebRtc.PromiseInterrupt((void*)promise);
            }
        }

        Pipeline.Dispose();
    }

    private static MediaToolkitNetException Refused(string message) => new(GstConstants.BackendName, message, 0);

    // Emits a signal that answers through a promise and gives what read the reply made of it.
    private unsafe Task<string> AskAsync(Action<nint> emit, Func<nint, string> read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var promise = (nint)GstWebRtc.PromiseNew();
        lock (_waiting)
        {
            _waiting.Add(promise);
        }

        try
        {
            emit(promise);
        }
        catch
        {
            lock (_waiting)
            {
                _waiting.Remove(promise);
            }

            Gst.gst_mini_object_unref((void*)promise);
            throw;
        }

        return Task.Run(() =>
        {
            try
            {
                var result = GstWebRtc.PromiseWait((void*)promise);
                if (result != Replied)
                {
                    throw Refused("webrtcbin gave no reply.");
                }

                return read((nint)GstWebRtc.PromiseGetReply((void*)promise));
            }
            finally
            {
                lock (_waiting)
                {
                    _waiting.Remove(promise);
                }

                Gst.gst_mini_object_unref((void*)promise);
            }
        });
    }

    private unsafe Task<string> CreateAsync(string signal, string field) => AskAsync(
        promise => Bin.Emit(signal, null, promise),
        reply =>
        {
            Span<byte> scratch = stackalloc byte[16];
            using var name = new Utf8Scoped(field, scratch);
            var value = reply == 0 ? null : Gst.gst_structure_get_value((void*)reply, name.Pointer);
            if (value is null)
            {
                throw Refused($"no {field} could be made: {Text(reply)}");
            }

            return TextOf((byte*)Gst.g_value_get_boxed(value));
        });

    private unsafe Task SetAsync(string signal, int type, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        // A description begins with its version line; the parser accepts much that does not, and finds nothing in it.
        if (!description.StartsWith("v=", StringComparison.Ordinal))
        {
            return Task.FromException(Refused("this is not a session description."));
        }

        var bytes = Encoding.UTF8.GetBytes(description);
        void* sdp = null;
        GstWebRtc.SdpNew(&sdp);
        fixed (byte* data = bytes)
        {
            if (GstWebRtc.SdpParse(data, (uint)bytes.Length, sdp) != 0)
            {
                GstWebRtc.SdpFree(sdp);
                return Task.FromException(Refused("this is not a session description."));
            }
        }

        // The description owns the message from here, and the signal takes a copy of the description.
        var native = (nint)GstWebRtc.DescriptionNew(type, sdp);
        try
        {
            return AskAsync(
                promise => Bin.Emit(signal, native, promise),
                reply =>
                {
                    var text = Text(reply);
                    return text.Contains("error", StringComparison.OrdinalIgnoreCase)
                        ? throw Refused($"the description was not accepted: {text}")
                        : string.Empty;
                });
        }
        finally
        {
            GstWebRtc.DescriptionFree((void*)native);
        }
    }

    private static unsafe string Text(nint reply) => reply == 0 ? string.Empty : Gst.TakeString(Gst.gst_structure_to_string((void*)reply));

    private static unsafe string TextOf(byte* description) =>
        Gst.TakeString(GstWebRtc.SdpAsText(*(void**)(description + GstWebRtc.DescriptionSdp)));

    private unsafe string? LocalDescription()
    {
        var storage = stackalloc byte[GstSignals.ValueSize];
        new Span<byte>(storage, GstSignals.ValueSize).Clear();
        Span<byte> scratch = stackalloc byte[32];
        using var name = new Utf8Scoped("local-description", scratch);
        var spec = Gst.g_object_class_find_property(GstLayout.ClassOf(Bin.Handle), name.Pointer);
        Gst.g_value_init(storage, *(nuint*)Gst.g_param_spec_get_default_value(spec));
        try
        {
            Gst.g_object_get_property(Bin.Handle, name.Pointer, storage);
            var description = (byte*)Gst.g_value_get_boxed(storage);
            return description is null ? null : TextOf(description);
        }
        finally
        {
            Gst.g_value_unset(storage);
        }
    }
}

using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// Lists capture devices through libavdevice.
/// </summary>
/// <remarks>
/// libavdevice is a thin shim over the platform stacks, so the device names it
/// returns are the ones FFmpeg itself expects: pass them straight to
/// <see cref="FFmpegDemuxer.Open"/> together with the matching demuxer name.
/// The dedicated platform backends generally report richer information.
/// </remarks>
public sealed unsafe class FFmpegDeviceEnumerator : IMediaDeviceEnumerator
{
    // AVDeviceInfoList: { AVDeviceInfo **devices; int nb_devices; int default_device; }
    private const int ListDevices = 0;
    private const int ListCount = 8;
    private const int ListDefault = 12;

    // AVDeviceInfo: { char *device_name; char *device_description; enum AVMediaType *media_types; int nb_media_types; }
    private const int InfoName = 0;
    private const int InfoDescription = 8;

    /// <inheritdoc />
    public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        FFmpegLibraries.EnsureLoaded();
        if (FFmpegLibraries.AvDevice is null)
        {
            return [];
        }

        var result = new List<MediaDevice>();
        foreach (var (demuxer, deviceKind) in PlatformDemuxers())
        {
            if ((kind & deviceKind) == 0)
            {
                continue;
            }

            Collect(demuxer, deviceKind, result);
        }

        return result;
    }

    /// <inheritdoc />
    public MediaDevice? GetDefault(MediaDeviceKind kind)
    {
        foreach (var device in Enumerate(kind))
        {
            if (device.IsDefault)
            {
                return device;
            }
        }

        return Enumerate(kind).FirstOrDefault();
    }

    /// <summary>Demuxer names libavdevice exposes on the current platform, with what they capture.</summary>
    public static IEnumerable<(string Demuxer, MediaDeviceKind Kind)> PlatformDemuxers()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ("dshow", MediaDeviceKind.VideoCapture | MediaDeviceKind.AudioCapture);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return ("avfoundation", MediaDeviceKind.VideoCapture | MediaDeviceKind.AudioCapture);
        }
        else
        {
            yield return ("video4linux2", MediaDeviceKind.VideoCapture);
            yield return ("alsa", MediaDeviceKind.AudioCapture);
            yield return ("pulse", MediaDeviceKind.AudioCapture);
        }
    }

    private static void Collect(string demuxerName, MediaDeviceKind kind, List<MediaDevice> result)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var name = new Utf8Scoped(demuxerName, scratch);

        var demuxer = AV.av_find_input_format(name.Pointer);
        if (demuxer is null)
        {
            return;
        }

        void* list = null;
        if (AV.avdevice_list_input_sources(demuxer, null, null, &list) < 0 || list is null)
        {
            // Several device demuxers do not implement enumeration at all; that
            // is expected and not an error worth surfacing.
            return;
        }

        try
        {
            var raw = (byte*)list;
            var count = *(int*)(raw + ListCount);
            var defaultIndex = *(int*)(raw + ListDefault);
            var devices = *(byte***)(raw + ListDevices);

            for (var i = 0; i < count; i++)
            {
                var info = devices[i];
                var id = Interop.Utf8.ToManagedOrEmpty(*(byte**)(info + InfoName));
                var description = Interop.Utf8.ToManaged(*(byte**)(info + InfoDescription));

                result.Add(new MediaDevice(
                    id,
                    string.IsNullOrWhiteSpace(description) ? id : description!,
                    kind,
                    $"{FFmpegLibraries.BackendName}/{demuxerName}",
                    i == defaultIndex));
            }
        }
        finally
        {
            AV.avdevice_free_list_devices(&list);
        }
    }
}

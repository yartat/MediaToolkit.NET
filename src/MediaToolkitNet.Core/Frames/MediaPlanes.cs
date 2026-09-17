using System.Runtime.CompilerServices;

namespace MediaToolkitNet.Core.Frames;

/// <summary>
/// Fixed-size storage for the plane pointers of a single frame. Eight entries
/// match AV_NUM_DATA_POINTERS, which is the largest plane count any backend in
/// this repository reports.
/// </summary>
[InlineArray(MediaPlanes.MaxPlanes)]
public struct PlanePointers
{
    private nint _element0;
}

/// <summary>Fixed-size storage for the per-plane strides of a single frame.</summary>
[InlineArray(MediaPlanes.MaxPlanes)]
public struct PlaneStrides
{
    private int _element0;
}

/// <summary>Plane-related constants shared by the frame types.</summary>
public static class MediaPlanes
{
    /// <summary>Maximum number of planes a frame may expose.</summary>
    public const int MaxPlanes = 8;
}

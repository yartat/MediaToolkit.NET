using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Interop.Com;

namespace MediaToolkitNet.Windows.Native;

/// <summary>
/// Vtable wrappers for the DirectShow interfaces.
/// </summary>
/// <remarks>
/// <para>
/// Every slot constant below carries the inheritance chain that produced it.
/// A wrong number does not raise anything: it calls the neighbouring method with
/// the arguments of the intended one, so the count is written down rather than
/// remembered.
/// </para>
/// <para>
/// DirectShow inherits deeply. <c>IMediaControl</c> and <c>IMediaEvent</c> are
/// dual interfaces, so four <c>IDispatch</c> methods sit between
/// <c>IUnknown</c> and their own; <c>IBaseFilter</c> arrives through
/// <c>IMediaFilter : IPersist</c>, which adds seven.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static unsafe class DsNative
{
    // IFilterGraph : IUnknown            -> own methods start at 3
    private const int FilterGraphAddFilter = 3;
    private const int FilterGraphRemoveFilter = 4;
    private const int FilterGraphEnumFilters = 5;
    private const int FilterGraphFindFilterByName = 6;
    private const int FilterGraphConnectDirect = 7;
    private const int FilterGraphReconnect = 8;
    private const int FilterGraphDisconnect = 9;
    private const int FilterGraphSetDefaultSyncSource = 10;

    // IGraphBuilder : IFilterGraph       -> 3 + 8 = 11
    private const int GraphBuilderConnect = 11;
    private const int GraphBuilderRender = 12;
    private const int GraphBuilderRenderFile = 13;
    private const int GraphBuilderAddSourceFilter = 14;
    private const int GraphBuilderSetLogFile = 15;
    private const int GraphBuilderAbort = 16;
    private const int GraphBuilderShouldOperationContinue = 17;

    // IFilterGraph2 : IGraphBuilder      -> 11 + 7 = 18
    private const int FilterGraph2AddSourceFilterForMoniker = 18;
    private const int FilterGraph2ReconnectEx = 19;
    private const int FilterGraph2RenderEx = 20;

    // IMediaControl : IDispatch : IUnknown -> 3 + 4 = 7
    private const int MediaControlRun = 7;
    private const int MediaControlPause = 8;
    private const int MediaControlStop = 9;
    private const int MediaControlGetState = 10;
    private const int MediaControlStopWhenReady = 15;

    // IMediaEvent : IDispatch : IUnknown -> 3 + 4 = 7
    private const int MediaEventGetEventHandle = 7;
    private const int MediaEventGetEvent = 8;
    private const int MediaEventWaitForCompletion = 9;
    private const int MediaEventFreeEventParams = 12;

    // IMediaSeeking : IUnknown           -> own methods start at 3
    private const int MediaSeekingGetCapabilities = 3;
    private const int MediaSeekingIsFormatSupported = 5;
    private const int MediaSeekingSetTimeFormat = 9;
    private const int MediaSeekingGetDuration = 10;
    private const int MediaSeekingGetCurrentPosition = 12;
    private const int MediaSeekingSetPositions = 14;
    private const int MediaSeekingSetRate = 17;
    private const int MediaSeekingGetRate = 18;

    // IBaseFilter : IMediaFilter : IPersist : IUnknown
    //   IPersist adds GetClassID (3); IMediaFilter adds six (4..9) -> own start at 10
    private const int PersistGetClassId = 3;
    private const int MediaFilterStop = 4;
    private const int MediaFilterPause = 5;
    private const int MediaFilterRun = 6;
    private const int MediaFilterGetState = 7;
    private const int BaseFilterEnumPins = 10;
    private const int BaseFilterFindPin = 11;
    private const int BaseFilterQueryFilterInfo = 12;
    private const int BaseFilterJoinFilterGraph = 13;
    private const int BaseFilterQueryVendorInfo = 14;

    // IPin : IUnknown                    -> own methods start at 3
    private const int PinConnect = 3;
    private const int PinReceiveConnection = 4;
    private const int PinDisconnect = 5;
    private const int PinConnectedTo = 6;
    private const int PinConnectionMediaType = 7;
    private const int PinQueryPinInfo = 8;
    private const int PinQueryDirection = 9;
    private const int PinQueryId = 10;
    private const int PinQueryAccept = 11;
    private const int PinEnumMediaTypes = 12;

    // IEnumPins / IEnumFilters / IEnumMediaTypes : IUnknown -> Next at 3
    private const int EnumNext = 3;
    private const int EnumReset = 5;

    // IFileSourceFilter / IFileSinkFilter : IUnknown -> own methods start at 3
    private const int FileSourceLoad = 3;
    private const int FileSourceGetCurFile = 4;
    private const int FileSinkSetFileName = 3;

    // ICaptureGraphBuilder2 : IUnknown   -> own methods start at 3
    private const int CaptureBuilderSetFiltergraph = 3;
    private const int CaptureBuilderSetOutputFileName = 5;
    private const int CaptureBuilderFindInterface = 6;
    private const int CaptureBuilderRenderStream = 7;

    // ISampleGrabber : IUnknown          -> own methods start at 3
    private const int SampleGrabberSetOneShot = 3;
    private const int SampleGrabberSetMediaType = 4;
    private const int SampleGrabberGetConnectedMediaType = 5;
    private const int SampleGrabberSetBufferSamples = 6;
    private const int SampleGrabberGetCurrentBuffer = 7;
    private const int SampleGrabberSetCallback = 9;

    // IMediaSample : IUnknown            -> own methods start at 3
    private const int MediaSampleGetPointer = 3;
    private const int MediaSampleGetSize = 4;
    private const int MediaSampleGetTime = 5;
    private const int MediaSampleGetActualDataLength = 11;
    private const int MediaSampleGetMediaType = 13;

    // ------------------------------------------------------------ IFilterGraph

    /// <summary>IFilterGraph::AddFilter.</summary>
    public static int AddFilter(void* graph, void* filter, string? name)
    {
        fixed (char* pName = name)
        {
            return ((delegate* unmanaged[Stdcall]<void*, void*, char*, int>)
                Com.Slot(graph, FilterGraphAddFilter))(graph, filter, pName);
        }
    }

    /// <summary>IFilterGraph::RemoveFilter.</summary>
    public static int RemoveFilter(void* graph, void* filter) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(graph, FilterGraphRemoveFilter))(graph, filter);

    /// <summary>IFilterGraph::EnumFilters.</summary>
    public static int EnumFilters(void* graph, out void* enumerator)
    {
        fixed (void** p = &enumerator)
        {
            return ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(graph, FilterGraphEnumFilters))(graph, p);
        }
    }

    /// <summary>IFilterGraph::FindFilterByName.</summary>
    public static int FindFilterByName(void* graph, string name, out void* filter)
    {
        fixed (char* pName = name)
        fixed (void** p = &filter)
        {
            return ((delegate* unmanaged[Stdcall]<void*, char*, void**, int>)
                Com.Slot(graph, FilterGraphFindFilterByName))(graph, pName, p);
        }
    }

    /// <summary>IFilterGraph::ConnectDirect, which refuses to insert intermediate filters.</summary>
    public static int ConnectDirect(void* graph, void* output, void* input, AmMediaType* mediaType) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void*, AmMediaType*, int>)
            Com.Slot(graph, FilterGraphConnectDirect))(graph, output, input, mediaType);

    /// <summary>IFilterGraph::Disconnect. Call it on both pins of a connection.</summary>
    public static int Disconnect(void* graph, void* pin) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(graph, FilterGraphDisconnect))(graph, pin);

    /// <summary>IFilterGraph::SetDefaultSyncSource.</summary>
    public static int SetDefaultSyncSource(void* graph) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(graph, FilterGraphSetDefaultSyncSource))(graph);

    // ----------------------------------------------------------- IGraphBuilder

    /// <summary>IGraphBuilder::Connect, the intelligent connect that may add filters.</summary>
    public static int Connect(void* graph, void* output, void* input) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, void*, int>)Com.Slot(graph, GraphBuilderConnect))(graph, output, input);

    /// <summary>IGraphBuilder::Render, which builds everything downstream of a pin.</summary>
    public static int Render(void* graph, void* outputPin) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(graph, GraphBuilderRender))(graph, outputPin);

    /// <summary>IGraphBuilder::RenderFile.</summary>
    public static int RenderFile(void* graph, string file)
    {
        fixed (char* pFile = file)
        {
            return ((delegate* unmanaged[Stdcall]<void*, char*, char*, int>)
                Com.Slot(graph, GraphBuilderRenderFile))(graph, pFile, null);
        }
    }

    /// <summary>IGraphBuilder::AddSourceFilter.</summary>
    public static int AddSourceFilter(void* graph, string file, string? name, out void* filter)
    {
        fixed (char* pFile = file)
        fixed (char* pName = name)
        fixed (void** p = &filter)
        {
            return ((delegate* unmanaged[Stdcall]<void*, char*, char*, void**, int>)
                Com.Slot(graph, GraphBuilderAddSourceFilter))(graph, pFile, pName, p);
        }
    }

    /// <summary>IGraphBuilder::Abort, which asks a long render to give up.</summary>
    public static int Abort(void* graph) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(graph, GraphBuilderAbort))(graph);

    // ---------------------------------------------------------- IMediaControl

    /// <summary>IMediaControl::Run.</summary>
    public static int Run(void* control) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(control, MediaControlRun))(control);

    /// <summary>IMediaControl::Pause.</summary>
    public static int Pause(void* control) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(control, MediaControlPause))(control);

    /// <summary>IMediaControl::Stop.</summary>
    public static int Stop(void* control) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(control, MediaControlStop))(control);

    /// <summary>IMediaControl::StopWhenReady, which flushes before stopping.</summary>
    public static int StopWhenReady(void* control) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(control, MediaControlStopWhenReady))(control);

    /// <summary>IMediaControl::GetState. The state is an OAFilterState, a long.</summary>
    public static int GetState(void* control, int timeoutMs, out int state)
    {
        fixed (int* p = &state)
        {
            return ((delegate* unmanaged[Stdcall]<void*, int, int*, int>)
                Com.Slot(control, MediaControlGetState))(control, timeoutMs, p);
        }
    }

    // ------------------------------------------------------------ IMediaEvent

    /// <summary>IMediaEvent::GetEvent.</summary>
    public static int GetEvent(void* mediaEvent, out int code, out nint param1, out nint param2, int timeoutMs)
    {
        fixed (int* pCode = &code)
        fixed (nint* p1 = &param1)
        fixed (nint* p2 = &param2)
        {
            return ((delegate* unmanaged[Stdcall]<void*, int*, nint*, nint*, int, int>)
                Com.Slot(mediaEvent, MediaEventGetEvent))(mediaEvent, pCode, p1, p2, timeoutMs);
        }
    }

    /// <summary>IMediaEvent::WaitForCompletion.</summary>
    public static int WaitForCompletion(void* mediaEvent, int timeoutMs, out int eventCode)
    {
        fixed (int* p = &eventCode)
        {
            return ((delegate* unmanaged[Stdcall]<void*, int, int*, int>)
                Com.Slot(mediaEvent, MediaEventWaitForCompletion))(mediaEvent, timeoutMs, p);
        }
    }

    /// <summary>IMediaEvent::FreeEventParams. Every event taken from GetEvent must be freed.</summary>
    public static int FreeEventParams(void* mediaEvent, int code, nint param1, nint param2) =>
        ((delegate* unmanaged[Stdcall]<void*, int, nint, nint, int>)
            Com.Slot(mediaEvent, MediaEventFreeEventParams))(mediaEvent, code, param1, param2);

    // ---------------------------------------------------------- IMediaSeeking

    /// <summary>IMediaSeeking::GetCapabilities.</summary>
    public static int GetSeekingCapabilities(void* seeking, out uint capabilities)
    {
        fixed (uint* p = &capabilities)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)
                Com.Slot(seeking, MediaSeekingGetCapabilities))(seeking, p);
        }
    }

    /// <summary>IMediaSeeking::IsFormatSupported.</summary>
    public static int IsFormatSupported(void* seeking, in Guid format)
    {
        fixed (Guid* p = &format)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, int>)
                Com.Slot(seeking, MediaSeekingIsFormatSupported))(seeking, p);
        }
    }

    /// <summary>IMediaSeeking::SetTimeFormat.</summary>
    public static int SetTimeFormat(void* seeking, in Guid format)
    {
        fixed (Guid* p = &format)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, int>)
                Com.Slot(seeking, MediaSeekingSetTimeFormat))(seeking, p);
        }
    }

    /// <summary>IMediaSeeking::GetDuration, in 100-nanosecond units.</summary>
    public static int GetDuration(void* seeking, out long duration)
    {
        fixed (long* p = &duration)
        {
            return ((delegate* unmanaged[Stdcall]<void*, long*, int>)
                Com.Slot(seeking, MediaSeekingGetDuration))(seeking, p);
        }
    }

    /// <summary>IMediaSeeking::GetCurrentPosition, in 100-nanosecond units.</summary>
    public static int GetCurrentPosition(void* seeking, out long position)
    {
        fixed (long* p = &position)
        {
            return ((delegate* unmanaged[Stdcall]<void*, long*, int>)
                Com.Slot(seeking, MediaSeekingGetCurrentPosition))(seeking, p);
        }
    }

    /// <summary>IMediaSeeking::SetPositions.</summary>
    public static int SetPositions(void* seeking, ref long current, uint currentFlags, ref long stop, uint stopFlags)
    {
        fixed (long* pCurrent = &current)
        fixed (long* pStop = &stop)
        {
            return ((delegate* unmanaged[Stdcall]<void*, long*, uint, long*, uint, int>)
                Com.Slot(seeking, MediaSeekingSetPositions))(seeking, pCurrent, currentFlags, pStop, stopFlags);
        }
    }

    /// <summary>IMediaSeeking::SetRate, where 1.0 is normal speed.</summary>
    public static int SetRate(void* seeking, double rate) =>
        ((delegate* unmanaged[Stdcall]<void*, double, int>)Com.Slot(seeking, MediaSeekingSetRate))(seeking, rate);

    /// <summary>IMediaSeeking::GetRate.</summary>
    public static int GetRate(void* seeking, out double rate)
    {
        fixed (double* p = &rate)
        {
            return ((delegate* unmanaged[Stdcall]<void*, double*, int>)Com.Slot(seeking, MediaSeekingGetRate))(seeking, p);
        }
    }

    // ------------------------------------------------------------ IBaseFilter

    /// <summary>IPersist::GetClassID, which every filter implements.</summary>
    public static int GetClassId(void* filter, out Guid classId)
    {
        fixed (Guid* p = &classId)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, int>)Com.Slot(filter, PersistGetClassId))(filter, p);
        }
    }

    /// <summary>IMediaFilter::Stop, on a single filter rather than the graph.</summary>
    public static int StopFilter(void* filter) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(filter, MediaFilterStop))(filter);

    /// <summary>IMediaFilter::Pause, on a single filter.</summary>
    public static int PauseFilter(void* filter) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(filter, MediaFilterPause))(filter);

    /// <summary>IMediaFilter::Run, on a single filter.</summary>
    public static int RunFilter(void* filter, long start) =>
        ((delegate* unmanaged[Stdcall]<void*, long, int>)Com.Slot(filter, MediaFilterRun))(filter, start);

    /// <summary>IMediaFilter::GetState.</summary>
    public static int GetFilterState(void* filter, uint timeoutMs, out FilterState state)
    {
        fixed (FilterState* p = &state)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint, FilterState*, int>)
                Com.Slot(filter, MediaFilterGetState))(filter, timeoutMs, p);
        }
    }

    /// <summary>IBaseFilter::EnumPins.</summary>
    public static int EnumPins(void* filter, out void* enumerator)
    {
        fixed (void** p = &enumerator)
        {
            return ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(filter, BaseFilterEnumPins))(filter, p);
        }
    }

    /// <summary>IBaseFilter::FindPin, by the pin's identifier rather than its name.</summary>
    public static int FindPin(void* filter, string id, out void* pin)
    {
        fixed (char* pId = id)
        fixed (void** p = &pin)
        {
            return ((delegate* unmanaged[Stdcall]<void*, char*, void**, int>)
                Com.Slot(filter, BaseFilterFindPin))(filter, pId, p);
        }
    }

    /// <summary>IBaseFilter::QueryFilterInfo. The graph in the result must be released.</summary>
    public static int QueryFilterInfo(void* filter, out FilterInfo info)
    {
        // Zeroed first: on failure the callee may not write, and the caller
        // releases the Graph pointer out of this struct.
        info = default;
        fixed (FilterInfo* p = &info)
        {
            return ((delegate* unmanaged[Stdcall]<void*, FilterInfo*, int>)
                Com.Slot(filter, BaseFilterQueryFilterInfo))(filter, p);
        }
    }

    /// <summary>IBaseFilter::QueryVendorInfo. Returns a task-allocated string or nothing.</summary>
    public static string? QueryVendorInfo(void* filter)
    {
        char* text;
        var hr = ((delegate* unmanaged[Stdcall]<void*, char**, int>)
            Com.Slot(filter, BaseFilterQueryVendorInfo))(filter, &text);
        if (HResult.Failed(hr) || text is null)
        {
            return null;
        }

        try
        {
            return new string(text);
        }
        finally
        {
            Ole32.CoTaskMemFree(text);
        }
    }

    // ------------------------------------------------------------------- IPin

    /// <summary>IPin::Connect, negotiating a media type.</summary>
    public static int ConnectPin(void* output, void* input, AmMediaType* mediaType) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, AmMediaType*, int>)
            Com.Slot(output, PinConnect))(output, input, mediaType);

    /// <summary>IPin::Disconnect.</summary>
    public static int DisconnectPin(void* pin) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(pin, PinDisconnect))(pin);

    /// <summary>IPin::ConnectedTo. Returns VFW_E_NOT_CONNECTED when the pin is free.</summary>
    public static int ConnectedTo(void* pin, out void* other)
    {
        fixed (void** p = &other)
        {
            return ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(pin, PinConnectedTo))(pin, p);
        }
    }

    /// <summary>IPin::ConnectionMediaType. The result owns memory; free it with FreeMediaType.</summary>
    public static int ConnectionMediaType(void* pin, out AmMediaType mediaType)
    {
        mediaType = default;
        fixed (AmMediaType* p = &mediaType)
        {
            return ((delegate* unmanaged[Stdcall]<void*, AmMediaType*, int>)
                Com.Slot(pin, PinConnectionMediaType))(pin, p);
        }
    }

    /// <summary>IPin::QueryPinInfo. The filter in the result must be released.</summary>
    public static int QueryPinInfo(void* pin, out PinInfo info)
    {
        info = default;
        fixed (PinInfo* p = &info)
        {
            return ((delegate* unmanaged[Stdcall]<void*, PinInfo*, int>)Com.Slot(pin, PinQueryPinInfo))(pin, p);
        }
    }

    /// <summary>IPin::QueryDirection.</summary>
    public static int QueryDirection(void* pin, out PinDirection direction)
    {
        fixed (PinDirection* p = &direction)
        {
            return ((delegate* unmanaged[Stdcall]<void*, PinDirection*, int>)
                Com.Slot(pin, PinQueryDirection))(pin, p);
        }
    }

    /// <summary>IPin::QueryId. Returns a task-allocated string.</summary>
    public static string? QueryId(void* pin)
    {
        char* text;
        var hr = ((delegate* unmanaged[Stdcall]<void*, char**, int>)Com.Slot(pin, PinQueryId))(pin, &text);
        if (HResult.Failed(hr) || text is null)
        {
            return null;
        }

        try
        {
            return new string(text);
        }
        finally
        {
            Ole32.CoTaskMemFree(text);
        }
    }

    /// <summary>IPin::EnumMediaTypes.</summary>
    public static int EnumMediaTypes(void* pin, out void* enumerator)
    {
        fixed (void** p = &enumerator)
        {
            return ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(pin, PinEnumMediaTypes))(pin, p);
        }
    }

    // ----------------------------------------------------------- enumerators

    /// <summary>IEnumPins::Next / IEnumFilters::Next / IEnumMediaTypes::Next, one item at a time.</summary>
    public static int EnumeratorNext(void* enumerator, out void* item)
    {
        uint fetched;
        fixed (void** p = &item)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint, void**, uint*, int>)
                Com.Slot(enumerator, EnumNext))(enumerator, 1, p, &fetched);
        }
    }

    /// <summary>Resets an enumerator to its first item.</summary>
    public static int EnumeratorReset(void* enumerator) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(enumerator, EnumReset))(enumerator);

    // ----------------------------------------------------- file source / sink

    /// <summary>IFileSourceFilter::Load.</summary>
    public static int LoadFile(void* fileSource, string path, AmMediaType* mediaType)
    {
        fixed (char* p = path)
        {
            return ((delegate* unmanaged[Stdcall]<void*, char*, AmMediaType*, int>)
                Com.Slot(fileSource, FileSourceLoad))(fileSource, p, mediaType);
        }
    }

    /// <summary>IFileSourceFilter::GetCurFile.</summary>
    public static string? GetCurrentFile(void* fileSource)
    {
        char* text;
        var mediaType = default(AmMediaType);
        var hr = ((delegate* unmanaged[Stdcall]<void*, char**, AmMediaType*, int>)
            Com.Slot(fileSource, FileSourceGetCurFile))(fileSource, &text, &mediaType);

        FreeMediaTypeContents(&mediaType);
        if (HResult.Failed(hr) || text is null)
        {
            return null;
        }

        try
        {
            return new string(text);
        }
        finally
        {
            Ole32.CoTaskMemFree(text);
        }
    }

    /// <summary>IFileSinkFilter::SetFileName.</summary>
    public static int SetFileName(void* fileSink, string path, AmMediaType* mediaType)
    {
        fixed (char* p = path)
        {
            return ((delegate* unmanaged[Stdcall]<void*, char*, AmMediaType*, int>)
                Com.Slot(fileSink, FileSinkSetFileName))(fileSink, p, mediaType);
        }
    }

    // --------------------------------------------------- ICaptureGraphBuilder2

    /// <summary>ICaptureGraphBuilder2::SetFiltergraph.</summary>
    public static int SetFiltergraph(void* builder, void* graph) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int>)
            Com.Slot(builder, CaptureBuilderSetFiltergraph))(builder, graph);

    /// <summary>ICaptureGraphBuilder2::RenderStream, the call that wires a capture branch.</summary>
    public static int RenderStream(
        void* builder, Guid* category, Guid* mediaType, void* source, void* intermediate, void* sink) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, void*, void*, void*, int>)
            Com.Slot(builder, CaptureBuilderRenderStream))(builder, category, mediaType, source, intermediate, sink);

    /// <summary>ICaptureGraphBuilder2::FindInterface, which searches a branch for an interface.</summary>
    public static int FindInterface(
        void* builder, Guid* category, Guid* mediaType, void* filter, in Guid iid, out void* result)
    {
        fixed (Guid* pIid = &iid)
        fixed (void** p = &result)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, void*, Guid*, void**, int>)
                Com.Slot(builder, CaptureBuilderFindInterface))(builder, category, mediaType, filter, pIid, p);
        }
    }

    /// <summary>ICaptureGraphBuilder2::SetOutputFileName.</summary>
    public static int SetOutputFileName(
        void* builder, in Guid type, string path, out void* muxer, out void* sink)
    {
        fixed (Guid* pType = &type)
        fixed (char* pPath = path)
        fixed (void** pMuxer = &muxer)
        fixed (void** pSink = &sink)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, char*, void**, void**, int>)
                Com.Slot(builder, CaptureBuilderSetOutputFileName))(builder, pType, pPath, pMuxer, pSink);
        }
    }

    // ---------------------------------------------------------- ISampleGrabber

    /// <summary>ISampleGrabber::SetMediaType.</summary>
    public static int SetGrabberMediaType(void* grabber, AmMediaType* mediaType) =>
        ((delegate* unmanaged[Stdcall]<void*, AmMediaType*, int>)
            Com.Slot(grabber, SampleGrabberSetMediaType))(grabber, mediaType);

    /// <summary>ISampleGrabber::GetConnectedMediaType.</summary>
    public static int GetGrabberMediaType(void* grabber, out AmMediaType mediaType)
    {
        mediaType = default;
        fixed (AmMediaType* p = &mediaType)
        {
            return ((delegate* unmanaged[Stdcall]<void*, AmMediaType*, int>)
                Com.Slot(grabber, SampleGrabberGetConnectedMediaType))(grabber, p);
        }
    }

    /// <summary>ISampleGrabber::SetOneShot.</summary>
    public static int SetOneShot(void* grabber, bool oneShot) =>
        ((delegate* unmanaged[Stdcall]<void*, int, int>)
            Com.Slot(grabber, SampleGrabberSetOneShot))(grabber, oneShot ? 1 : 0);

    /// <summary>ISampleGrabber::SetBufferSamples, which keeps a copy for GetCurrentBuffer.</summary>
    public static int SetBufferSamples(void* grabber, bool buffer) =>
        ((delegate* unmanaged[Stdcall]<void*, int, int>)
            Com.Slot(grabber, SampleGrabberSetBufferSamples))(grabber, buffer ? 1 : 0);

    /// <summary>ISampleGrabber::GetCurrentBuffer.</summary>
    public static int GetCurrentBuffer(void* grabber, ref int size, byte* buffer)
    {
        fixed (int* p = &size)
        {
            return ((delegate* unmanaged[Stdcall]<void*, int*, byte*, int>)
                Com.Slot(grabber, SampleGrabberGetCurrentBuffer))(grabber, p, buffer);
        }
    }

    /// <summary>
    /// ISampleGrabber::SetCallback. <paramref name="kind"/> is 0 for SampleCB and
    /// 1 for BufferCB.
    /// </summary>
    public static int SetCallback(void* grabber, void* callback, int kind) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int, int>)
            Com.Slot(grabber, SampleGrabberSetCallback))(grabber, callback, kind);

    // ------------------------------------------------------------ IMediaSample

    /// <summary>IMediaSample::GetPointer.</summary>
    public static int GetSamplePointer(void* sample, out byte* data)
    {
        fixed (byte** p = &data)
        {
            return ((delegate* unmanaged[Stdcall]<void*, byte**, int>)
                Com.Slot(sample, MediaSampleGetPointer))(sample, p);
        }
    }

    /// <summary>IMediaSample::GetSize, the capacity of the buffer.</summary>
    public static int GetSampleSize(void* sample) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(sample, MediaSampleGetSize))(sample);

    /// <summary>IMediaSample::GetActualDataLength, the bytes actually valid.</summary>
    public static int GetSampleLength(void* sample) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(sample, MediaSampleGetActualDataLength))(sample);

    /// <summary>
    /// IMediaSample::GetMediaType. Returns S_OK and an allocated media type only
    /// when the format changed since the previous sample, S_FALSE otherwise.
    /// </summary>
    public static int GetSampleMediaType(void* sample, out AmMediaType* mediaType)
    {
        fixed (AmMediaType** p = &mediaType)
        {
            return ((delegate* unmanaged[Stdcall]<void*, AmMediaType**, int>)
                Com.Slot(sample, MediaSampleGetMediaType))(sample, p);
        }
    }

    /// <summary>IMediaSample::GetTime, in 100-nanosecond units.</summary>
    public static int GetSampleTime(void* sample, out long start, out long end)
    {
        fixed (long* pStart = &start)
        fixed (long* pEnd = &end)
        {
            return ((delegate* unmanaged[Stdcall]<void*, long*, long*, int>)
                Com.Slot(sample, MediaSampleGetTime))(sample, pStart, pEnd);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Releases what an <see cref="AmMediaType"/> owns, without freeing the
    /// structure itself. Use it for a media type filled in place.
    /// </summary>
    public static void FreeMediaTypeContents(AmMediaType* mediaType)
    {
        if (mediaType is null)
        {
            return;
        }

        if (mediaType->FormatBlock is not null)
        {
            Ole32.CoTaskMemFree(mediaType->FormatBlock);
            mediaType->FormatBlock = null;
            mediaType->FormatSize = 0;
        }

        if (mediaType->Unknown is not null)
        {
            Com.Release(mediaType->Unknown);
            mediaType->Unknown = null;
        }
    }

    /// <summary>Releases a media type that DirectShow allocated on the task heap.</summary>
    public static void FreeMediaType(AmMediaType* mediaType)
    {
        if (mediaType is null)
        {
            return;
        }

        FreeMediaTypeContents(mediaType);
        Ole32.CoTaskMemFree(mediaType);
    }

    /// <summary>Reads a fixed WCHAR buffer such as PIN_INFO::achName.</summary>
    public static string ReadFixedString(char* buffer, int capacity)
    {
        var span = new ReadOnlySpan<char>(buffer, capacity);
        var end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }
}

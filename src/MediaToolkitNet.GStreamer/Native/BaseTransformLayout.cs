#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>
/// What deriving from <c>GstBaseTransform</c> needs: the functions, and where the two methods that are replaced sit in
/// its class, checked against the class GStreamer built.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class BaseTransformLayout
{
    // After the element class come two gbooleans, then the methods in this order, then the padding.
    private const int Booleans = 8;
    private const int Methods = 22;
    private const int Reserved = 18;
    private const int SetCapsMethod = 3;
    private const int TransformInPlaceMethod = 19;

    // The methods GstBaseTransform's own class_init fills in, and those it leaves empty for a subclass.
    private static readonly int[] Filled = [0, 1, 2, 4, 5, 7, 8, 12, 13, 14, 15, 20, 21];
    private static readonly int[] Empty = [3, 18, 19];

    private static readonly Lock Gate = new();
    private static BaseTransformLayout? _instance;

    public nuint Parent;
    public uint ClassSize;
    public uint InstanceSize;
    public int SetCapsSlot;
    public int TransformSlot;

    public delegate* unmanaged[Cdecl]<nuint, byte*, uint, delegate* unmanaged[Cdecl]<void*, void*, void>, uint, void*, int, nuint> Register;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, nuint, int> ElementRegister;
    public delegate* unmanaged[Cdecl]<void*, byte*, byte*, byte*, byte*, void> SetMetadata;
    public delegate* unmanaged[Cdecl]<byte*, int, int, void*, void*> PadTemplateNew;
    public delegate* unmanaged[Cdecl]<void*, void*, void> AddPadTemplate;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, void> WeakRef;

    public static BaseTransformLayout Require()
    {
        lock (Gate)
        {
            return _instance ??= Load();
        }
    }

    private static BaseTransformLayout Load()
    {
        if (!NativeModule.TryLoad(["libgstbase-1.0.so.0", "libgstbase-1.0.so"], out var library))
        {
            throw new NotSupportedException("libgstbase-1.0 is not installed.");
        }

        var query = (delegate* unmanaged[Cdecl]<nuint, byte*, void>)Gst.GObject.GetExport("g_type_query");
        var classRef = (delegate* unmanaged[Cdecl]<nuint, void*>)Gst.GObject.GetExport("g_type_class_ref");
        var classUnref = (delegate* unmanaged[Cdecl]<void*, void>)Gst.GObject.GetExport("g_type_class_unref");

        var layout = new BaseTransformLayout
        {
            Parent = ((delegate* unmanaged[Cdecl]<nuint>)library.GetExport("gst_base_transform_get_type"))(),
            Register = (delegate* unmanaged[Cdecl]<nuint, byte*, uint, delegate* unmanaged[Cdecl]<void*, void*, void>, uint, void*, int, nuint>)
                Gst.GObject.GetExport("g_type_register_static_simple"),
            ElementRegister = (delegate* unmanaged[Cdecl]<void*, byte*, uint, nuint, int>)Gst.Core.GetExport("gst_element_register"),
            SetMetadata = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, byte*, byte*, void>)Gst.Core.GetExport("gst_element_class_set_metadata"),
            PadTemplateNew = (delegate* unmanaged[Cdecl]<byte*, int, int, void*, void*>)Gst.Core.GetExport("gst_pad_template_new"),
            AddPadTemplate = (delegate* unmanaged[Cdecl]<void*, void*, void>)Gst.Core.GetExport("gst_element_class_add_pad_template"),
            WeakRef = (delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, void>)
                Gst.GObject.GetExport("g_object_weak_ref"),
        };

        // GTypeQuery: type, name, class_size, instance_size.
        var result = stackalloc byte[24];
        query(Gst.gst_element_get_type(), result);
        var elementClass = (int)*(uint*)(result + 16);
        query(layout.Parent, result);
        layout.ClassSize = *(uint*)(result + 16);
        layout.InstanceSize = *(uint*)(result + 20);

        var methods = elementClass + Booleans;
        layout.SetCapsSlot = methods + (SetCapsMethod * sizeof(void*));
        layout.TransformSlot = methods + (TransformInPlaceMethod * sizeof(void*));

        if (layout.ClassSize != methods + ((Methods + Reserved) * sizeof(void*)))
        {
            throw Mismatch($"the class is {layout.ClassSize} bytes");
        }

        var klass = (byte*)classRef(layout.Parent);
        try
        {
            // passthrough_on_same_caps is FALSE and transform_ip_on_passthrough TRUE in the base class.
            if (*(int*)(klass + elementClass) != 0 || *(int*)(klass + elementClass + 4) != 1)
            {
                throw Mismatch("the flags are not where they should be");
            }

            foreach (var method in Filled)
            {
                if (*(void**)(klass + methods + (method * sizeof(void*))) is null)
                {
                    throw Mismatch($"method {method} is empty");
                }
            }

            foreach (var method in Empty)
            {
                if (*(void**)(klass + methods + (method * sizeof(void*))) is not null)
                {
                    throw Mismatch($"method {method} is filled");
                }
            }
        }
        finally
        {
            classUnref(klass);
        }

        return layout;
    }

    private static MediaToolkitNetException Mismatch(string what) => new(
        GstConstants.BackendName, $"GstBaseTransformClass is not laid out as expected: {what}.", 0);
}

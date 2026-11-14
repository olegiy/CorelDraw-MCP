using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CorelMcp.Worker.Operations;
using TYPEATTR = System.Runtime.InteropServices.ComTypes.TYPEATTR;

namespace CorelMcp.Worker.Interop;

// Corel's export wrapper exposes filter properties through its default IDispatch,
// but does not QI the filter-specific IID. Validate its runtime type info first,
// then use only these fixed, compiled property bindings. No name-based Invoke.
[ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IAIExportOptions
{
    [DispId(11)] bool TextAsCurves { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
}
[ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IEPSExportOptions
{
    [DispId(14)] bool TextAsCurves { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
}
[ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IHPGLExportOptions { [DispId(24)] short PlotterUnits { get; set; } }
[ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IJpgExportOptions { [DispId(13)] int Compression { get; set; } }

internal static class ExportFilterTypes
{
    public static void Require(VGCore.ExportFilter filter, string expectedGuid)
    {
        ITypeInfo? info = null;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(((CorelMcp.IMetadataDispatch)filter).GetTypeInfo(0, 0, out info));
            info.GetTypeAttr(out attributes);
            var actual = Marshal.PtrToStructure<TYPEATTR>(attributes);
            if (actual.guid != new Guid(expectedGuid))
                throw new OperationFailure("FILTER_UNSUPPORTED", "Тип фильтра не совпадает с проверенным SDK-интерфейсом: " + actual.guid);
        }
        finally
        {
            if (attributes != IntPtr.Zero) info!.ReleaseTypeAttr(attributes);
            if (info is not null) Marshal.ReleaseComObject(info);
        }
    }
}

// Build-time only, executed by Windows PowerShell 5.1. Never shipped in the worker.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TYPELIBATTR = System.Runtime.InteropServices.ComTypes.TYPELIBATTR;
using SYSKIND = System.Runtime.InteropServices.ComTypes.SYSKIND;

public sealed class WorkerInteropGenerator : ITypeLibImporterNotifySink
{
    private readonly string directory;
    private readonly Dictionary<Guid, Assembly> dependencies = new Dictionary<Guid, Assembly>();
    public readonly List<string> Warnings = new List<string>();
    private WorkerInteropGenerator(string directory) { this.directory = directory; }

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(string file, int regKind, out ITypeLib library);

    public static object[] Generate(string source, string outputDirectory)
    {
        ITypeLib library;
        LoadTypeLibEx(source, 2 /* REGKIND_NONE: never register a typelib */, out library);
        try
        {
            IntPtr pointer;
            library.GetLibAttr(out pointer);
            TYPELIBATTR attributes;
            try { attributes = (TYPELIBATTR)Marshal.PtrToStructure(pointer, typeof(TYPELIBATTR)); }
            finally { library.ReleaseTLibAttr(pointer); }
            if (attributes.guid != new Guid("95E23C91-BC5A-49F3-8CD1-1FC515597048") || attributes.wMajorVerNum != 27)
                throw new InvalidOperationException("Ожидалась официальная type library VGCore 27.");
            if (attributes.syskind != SYSKIND.SYS_WIN64)
                throw new InvalidOperationException("Ожидалась type library VGCore для Windows x64.");
            Directory.CreateDirectory(outputDirectory);
            var importer = new WorkerInteropGenerator(outputDirectory);
            importer.Convert(library, "VGCore.Interop.dll", "VGCore");
            return new object[] { attributes.guid.ToString(), (int)attributes.wMajorVerNum,
                (int)attributes.wMinorVerNum, attributes.syskind.ToString(), importer.Warnings.ToArray() };
        }
        finally { Marshal.ReleaseComObject(library); }
    }

    private Assembly Convert(ITypeLib library, string file, string namespaceName)
    {
        var converter = new TypeLibConverter();
        var assembly = converter.ConvertTypeLibToAssembly(library, Path.Combine(directory, file),
            TypeLibImporterFlags.ImportAsX64, this, null, null, namespaceName, null);
        assembly.Save(file);
        return assembly;
    }

    public void ReportEvent(ImporterEventKind kind, int code, string message)
    {
        if (kind != ImporterEventKind.NOTIF_TYPECONVERTED)
            Warnings.Add(kind + ":" + code + ": " + message);
    }

    public Assembly ResolveRef(object value)
    {
        var library = (ITypeLib)value;
        IntPtr pointer;
        library.GetLibAttr(out pointer);
        TYPELIBATTR attributes;
        try { attributes = (TYPELIBATTR)Marshal.PtrToStructure(pointer, typeof(TYPELIBATTR)); }
        finally { library.ReleaseTLibAttr(pointer); }
        Assembly existing;
        if (dependencies.TryGetValue(attributes.guid, out existing)) return existing;
        // Only the standard OLE library is an allowed dependency of this generated adapter.
        if (attributes.guid != new Guid("00020430-0000-0000-C000-000000000046"))
            throw new InvalidOperationException("Непроверенная зависимость VGCore: " + attributes.guid);
        var result = Convert(library, "stdole.Interop.dll", "stdole");
        dependencies.Add(attributes.guid, result);
        return result;
    }
}

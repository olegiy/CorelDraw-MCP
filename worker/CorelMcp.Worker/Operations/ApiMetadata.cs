#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TYPEDESC = System.Runtime.InteropServices.ComTypes.TYPEDESC;
using TYPEATTR = System.Runtime.InteropServices.ComTypes.TYPEATTR;
using TYPELIBATTR = System.Runtime.InteropServices.ComTypes.TYPELIBATTR;
using TYPEKIND = System.Runtime.InteropServices.ComTypes.TYPEKIND;
using FUNCDESC = System.Runtime.InteropServices.ComTypes.FUNCDESC;
using ELEMDESC = System.Runtime.InteropServices.ComTypes.ELEMDESC;
using PARAMDESC = System.Runtime.InteropServices.ComTypes.PARAMDESC;
using PARAMFLAG = System.Runtime.InteropServices.ComTypes.PARAMFLAG;
using FUNCFLAGS = System.Runtime.InteropServices.ComTypes.FUNCFLAGS;
using INVOKEKIND = System.Runtime.InteropServices.ComTypes.INVOKEKIND;
using VARDESC = System.Runtime.InteropServices.ComTypes.VARDESC;
using VARKIND = System.Runtime.InteropServices.ComTypes.VARKIND;
using VARFLAGS = System.Runtime.InteropServices.ComTypes.VARFLAGS;
using IMPLTYPEFLAGS = System.Runtime.InteropServices.ComTypes.IMPLTYPEFLAGS;

namespace CorelMcp
{
    // Only IDispatch's metadata slots are exposed. No Invoke or property chain exists here.
    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMetadataDispatch
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, int lcid, out ITypeInfo info);
    }

    public sealed class ApiMetadataSession
    {
        private sealed class Snapshot
        {
            public Dictionary<string, object> Library;
            public List<Dictionary<string, object>> Types = new List<Dictionary<string, object>>();
            public Dictionary<string, int> Indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<Dictionary<string, object>>> Members = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        }
        private readonly Dictionary<string, Snapshot> cache = new Dictionary<string, Snapshot>();
        public void Clear() { cache.Clear(); }
        private static Dictionary<string, object> Row(params object[] pairs)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) row.Add((string)pairs[i], pairs[i + 1]);
            return row;
        }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        private static string DocName(ITypeInfo info, int member)
        {
            string name, doc, help; int context;
            info.GetDocumentation(member, out name, out doc, out context, out help);
            return name;
        }
        private static string TypeName(ITypeInfo info, TYPEDESC type, int depth)
        {
            if (depth > 16) return "recursive";
            VarEnum value = (VarEnum)type.vt;
            if (value == VarEnum.VT_PTR || value == VarEnum.VT_SAFEARRAY)
            {
                TYPEDESC child = (TYPEDESC)Marshal.PtrToStructure(type.lpValue, typeof(TYPEDESC));
                return TypeName(info, child, depth + 1) + (value == VarEnum.VT_PTR ? "*" : "[]");
            }
            if (value == VarEnum.VT_USERDEFINED)
            {
                ITypeInfo referenced = null;
                try { info.GetRefTypeInfo(unchecked((int)type.lpValue.ToInt64()), out referenced); return DocName(referenced, -1); }
                finally { Release(referenced); }
            }
            if (value == VarEnum.VT_CARRAY)
            {
                TYPEDESC child = (TYPEDESC)Marshal.PtrToStructure(type.lpValue, typeof(TYPEDESC));
                return TypeName(info, child, depth + 1) + "[fixed]";
            }
            return value.ToString().Replace("VT_", "");
        }
        private static object SafeVariant(IntPtr pointer)
        {
            object value = Marshal.GetObjectForNativeVariant(pointer);
            if (value == null || value == DBNull.Value) return null;
            if (Marshal.IsComObject(value)) { Release(value); return "[COM constant]"; }
            if (value is DateTime) return ((DateTime)value).ToString("o", CultureInfo.InvariantCulture);
            if (value is string || value is bool || value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal || value is ushort || value is uint || value is ulong || value is sbyte) return value;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        private static ITypeInfo AliasInfo(ITypeInfo info, TYPEATTR type)
        {
            if (type.typekind != TYPEKIND.TKIND_ALIAS || (VarEnum)type.tdescAlias.vt != VarEnum.VT_USERDEFINED) return null;
            ITypeInfo target; info.GetRefTypeInfo(unchecked((int)type.tdescAlias.lpValue.ToInt64()), out target); return target;
        }
        private static bool IsEnumeration(ITypeInfo info, int depth)
        {
            if (depth > 16) throw new InvalidOperationException("Слишком глубокая цепочка псевдонимов API.");
            IntPtr attributes = IntPtr.Zero; ITypeInfo target = null;
            try
            {
                info.GetTypeAttr(out attributes); TYPEATTR type = (TYPEATTR)Marshal.PtrToStructure(attributes, typeof(TYPEATTR));
                if (type.typekind == TYPEKIND.TKIND_ENUM) return true;
                target = AliasInfo(info, type); return target != null && IsEnumeration(target, depth + 1);
            }
            finally { Release(target); if (attributes != IntPtr.Zero) info.ReleaseTypeAttr(attributes); }
        }
        private static List<Dictionary<string, object>> ReadMembers(ITypeInfo info) { return ReadMembers(info, 0); }
        private static List<Dictionary<string, object>> ReadMembers(ITypeInfo info, int depth)
        {
            if (depth > 16) throw new InvalidOperationException("Слишком глубокая цепочка типов API.");
            IntPtr attributes = IntPtr.Zero;
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            try
            {
                info.GetTypeAttr(out attributes);
                TYPEATTR type = (TYPEATTR)Marshal.PtrToStructure(attributes, typeof(TYPEATTR));
                ITypeInfo alias = AliasInfo(info, type);
                if (alias != null) { try { return ReadMembers(alias, depth + 1); } finally { Release(alias); } }
                if (type.typekind == TYPEKIND.TKIND_COCLASS)
                {
                    // Reflect the default incoming interface; event/source interfaces are excluded.
                    for (int i = 0; i < type.cImplTypes; i++)
                    {
                        IMPLTYPEFLAGS flags; info.GetImplTypeFlags(i, out flags);
                        if ((flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) == 0 || (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0) continue;
                        int reference; ITypeInfo inherited = null;
                        try { info.GetRefTypeOfImplType(i, out reference); info.GetRefTypeInfo(reference, out inherited); return ReadMembers(inherited, depth + 1); }
                        finally { Release(inherited); }
                    }
                }
                for (int i = 0; i < type.cFuncs; i++)
                {
                    IntPtr descriptor = IntPtr.Zero;
                    try
                    {
                        info.GetFuncDesc(i, out descriptor);
                        FUNCDESC func = (FUNCDESC)Marshal.PtrToStructure(descriptor, typeof(FUNCDESC));
                        string[] names = new string[func.cParams + 1]; int found;
                        info.GetNames(func.memid, names, names.Length, out found);
                        List<Dictionary<string, object>> parameters = new List<Dictionary<string, object>>();
                        for (int p = 0; p < func.cParams; p++)
                        {
                            ELEMDESC element = (ELEMDESC)Marshal.PtrToStructure(IntPtr.Add(func.lprgelemdescParam, p * Marshal.SizeOf(typeof(ELEMDESC))), typeof(ELEMDESC));
                            PARAMDESC param = element.desc.paramdesc;
                            PARAMFLAG flags = param.wParamFlags;
                            bool hasDefault = (flags & PARAMFLAG.PARAMFLAG_FHASDEFAULT) != 0 && param.lpVarValue != IntPtr.Zero;
                            parameters.Add(Row("name", p + 1 < found ? names[p + 1] : "arg" + (p + 1), "type", TypeName(info, element.tdesc, 0),
                                "flags", flags.ToString(), "in", (flags & PARAMFLAG.PARAMFLAG_FIN) != 0, "out", (flags & PARAMFLAG.PARAMFLAG_FOUT) != 0,
                                "optional", (flags & PARAMFLAG.PARAMFLAG_FOPT) != 0 || (func.cParamsOpt > 0 && p >= func.cParams - func.cParamsOpt),
                                "retval", (flags & PARAMFLAG.PARAMFLAG_FRETVAL) != 0, "has_default", hasDefault, "default", hasDefault ? SafeVariant(IntPtr.Add(param.lpVarValue, 8)) : null));
                        }
                        rows.Add(Row("name", found > 0 ? names[0] : DocName(info, func.memid), "member_id", func.memid, "kind", func.invkind.ToString(),
                            "return_type", TypeName(info, func.elemdescFunc.tdesc, 0), "flags", ((FUNCFLAGS)func.wFuncFlags).ToString(), "parameters", parameters,
                            "optional_parameter_count", func.cParamsOpt, "readonly", func.invkind == INVOKEKIND.INVOKE_PROPERTYGET));
                    }
                    finally { if (descriptor != IntPtr.Zero) info.ReleaseFuncDesc(descriptor); }
                }
                // A getter is read-only only when this type has no matching property setter.
                foreach (Dictionary<string, object> row in rows)
                    if ((string)row["kind"] == INVOKEKIND.INVOKE_PROPERTYGET.ToString())
                        foreach (Dictionary<string, object> candidate in rows)
                            if ((int)candidate["member_id"] == (int)row["member_id"] && ((string)candidate["kind"] == INVOKEKIND.INVOKE_PROPERTYPUT.ToString() || (string)candidate["kind"] == INVOKEKIND.INVOKE_PROPERTYPUTREF.ToString())) row["readonly"] = false;
                for (int i = 0; i < type.cVars; i++)
                {
                    IntPtr descriptor = IntPtr.Zero;
                    try
                    {
                        info.GetVarDesc(i, out descriptor);
                        VARDESC variable = (VARDESC)Marshal.PtrToStructure(descriptor, typeof(VARDESC));
                        rows.Add(Row("name", DocName(info, variable.memid), "member_id", variable.memid, "kind", variable.varkind.ToString(),
                            "type", TypeName(info, variable.elemdescVar.tdesc, 0), "flags", ((VARFLAGS)variable.wVarFlags).ToString(),
                            "readonly", variable.varkind == VARKIND.VAR_CONST || (((VARFLAGS)variable.wVarFlags & VARFLAGS.VARFLAG_FREADONLY) != 0),
                            "value", variable.varkind == VARKIND.VAR_CONST ? SafeVariant(variable.desc.lpvarValue) : null));
                    }
                    finally { if (descriptor != IntPtr.Zero) info.ReleaseVarDesc(descriptor); }
                }
                if (type.typekind == TYPEKIND.TKIND_INTERFACE || type.typekind == TYPEKIND.TKIND_DISPATCH)
                {
                    for (int i = 0; i < type.cImplTypes; i++)
                    {
                        int reference; ITypeInfo inherited = null; IMPLTYPEFLAGS flags;
                        try
                        {
                            info.GetImplTypeFlags(i, out flags);
                            if ((flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0) continue;
                            info.GetRefTypeOfImplType(i, out reference); info.GetRefTypeInfo(reference, out inherited);
                            foreach (Dictionary<string, object> candidate in ReadMembers(inherited, depth + 1))
                            {
                                bool exists = rows.Exists(delegate(Dictionary<string, object> row) { return Object.Equals(row["member_id"], candidate["member_id"]) && Object.Equals(row["kind"], candidate["kind"]) && Object.Equals(row["name"], candidate["name"]); });
                                if (!exists) rows.Add(candidate);
                            }
                        }
                        finally { Release(inherited); }
                    }
                }
            }
            finally { if (attributes != IntPtr.Zero) info.ReleaseTypeAttr(attributes); }
            return rows;
        }
        public Dictionary<string, object> Describe(object application, string kind, string name, string filter, int offset, int limit)
        {
            ITypeInfo root = null; ITypeLib library = null; IntPtr attributes = IntPtr.Zero;
            try
            {
                IMetadataDispatch dispatch = (IMetadataDispatch)application;
                Marshal.ThrowExceptionForHR(dispatch.GetTypeInfo(0, 0, out root));
                int containingIndex; root.GetContainingTypeLib(out library, out containingIndex);
                library.GetLibAttr(out attributes);
                TYPELIBATTR attr = (TYPELIBATTR)Marshal.PtrToStructure(attributes, typeof(TYPELIBATTR));
                string key = attr.guid.ToString("D") + ":" + attr.wMajorVerNum + ":" + attr.wMinorVerNum + ":" + attr.lcid;
                Snapshot snapshot;
                if (!cache.TryGetValue(key, out snapshot))
                {
                    snapshot = new Snapshot(); string libName, doc, help; int context;
                    library.GetDocumentation(-1, out libName, out doc, out context, out help);
                    snapshot.Library = Row("guid", attr.guid.ToString("D"), "major", attr.wMajorVerNum, "minor", attr.wMinorVerNum, "lcid", attr.lcid, "name", libName, "flags", attr.wLibFlags.ToString());
                    for (int i = 0; i < library.GetTypeInfoCount(); i++)
                    {
                        ITypeInfo info = null; IntPtr typeAttributes = IntPtr.Zero;
                        try
                        {
                            library.GetTypeInfo(i, out info); info.GetTypeAttr(out typeAttributes);
                            TYPEATTR type = (TYPEATTR)Marshal.PtrToStructure(typeAttributes, typeof(TYPEATTR));
                            string typeName = DocName(info, -1);
                            List<Dictionary<string, object>> interfaces = new List<Dictionary<string, object>>();
                            for (int implemented = 0; implemented < type.cImplTypes; implemented++)
                            {
                                int reference; ITypeInfo inherited = null; IMPLTYPEFLAGS flags;
                                try
                                {
                                    info.GetImplTypeFlags(implemented, out flags); info.GetRefTypeOfImplType(implemented, out reference); info.GetRefTypeInfo(reference, out inherited);
                                    interfaces.Add(Row("name", DocName(inherited, -1), "flags", flags.ToString()));
                                }
                                finally { Release(inherited); }
                            }
                            snapshot.Types.Add(Row("name", typeName, "kind", type.typekind.ToString(), "guid", type.guid.ToString("D"), "flags", type.wTypeFlags.ToString(), "member_count", type.cFuncs + type.cVars, "interfaces", interfaces));
                            if (!snapshot.Indexes.ContainsKey(typeName)) snapshot.Indexes.Add(typeName, i);
                        }
                        finally { if (typeAttributes != IntPtr.Zero) info.ReleaseTypeAttr(typeAttributes); Release(info); }
                    }
                    cache.Add(key, snapshot);
                }
                List<Dictionary<string, object>> source = snapshot.Types;
                if (kind != "types")
                {
                    int index;
                    if (!snapshot.Indexes.TryGetValue(name, out index)) { Exception missing = new InvalidOperationException("Тип API не найден: " + name); missing.Data["code"] = "API_TYPE_NOT_FOUND"; throw missing; }
                    if (kind == "enum")
                    {
                        ITypeInfo enumInfo = null;
                        try
                        {
                            library.GetTypeInfo(index, out enumInfo);
                            if (!IsEnumeration(enumInfo, 0)) { Exception invalid = new InvalidOperationException("Тип API не является перечислением: " + name); invalid.Data["code"] = "API_NOT_ENUM"; throw invalid; }
                        }
                        finally { Release(enumInfo); }
                    }
                    if (!snapshot.Members.TryGetValue(name, out source))
                    {
                        ITypeInfo info = null;
                        try { library.GetTypeInfo(index, out info); source = ReadMembers(info); snapshot.Members.Add(name, source); }
                        finally { Release(info); }
                    }
                }
                List<Dictionary<string, object>> matches = source.FindAll(delegate(Dictionary<string, object> row) { return ((string)row["name"]).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 && (kind != "types" || name.Length == 0 || String.Equals((string)row["name"], name, StringComparison.OrdinalIgnoreCase)); });
                int count = Math.Min(limit, Math.Max(0, matches.Count - offset));
                return Row("kind", kind, "name", name.Length == 0 ? null : name, "items", matches.GetRange(Math.Min(offset, matches.Count), count), "total", matches.Count, "offset", offset, "limit", limit,
                    "next_offset", offset + count < matches.Count ? (object)(offset + count) : null, "library", snapshot.Library,
                    "corel_version", attr.wMajorVerNum + "." + attr.wMinorVerNum, "corel_version_source", "type_library");
            }
            finally { if (attributes != IntPtr.Zero) library.ReleaseTLibAttr(attributes); Release(library); Release(root); }
        }
    }
}

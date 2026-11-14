using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CorelMcp.Worker.Operations;

internal sealed record FrozenImport(string Path,string SourceIdentity,string SourceSha256,long SourceBytes,string[] Artifacts);
internal static class ImportSource
{
    private const long Limit=256L*1024*1024;
    public static FrozenImport Prepare(ImportCommand args,string directory)
    {
        AssemblyContracts.Validate(args);WorkerArtifacts.EnsureDirectory(directory);
        var source=System.IO.Path.GetFullPath(args.Path);var extension=System.IO.Path.GetExtension(source).ToLowerInvariant();
        if(!(args.Format switch{"svg"=>extension==".svg","pdf"=>extension==".pdf","png"=>extension==".png","jpeg"=>extension is ".jpg" or ".jpeg",_=>false}))
            throw new OperationFailure("INPUT_EXTENSION_MISMATCH","Расширение файла не совпадает с явным форматом.");
        if(new DriveInfo(System.IO.Path.GetPathRoot(source)!).DriveType==DriveType.Network)throw new OperationFailure("INPUT_NOT_LOCAL","Импорт принимает локальные файлы, без сетевого диска.");
        var info=new FileInfo(source);
        if(!info.Exists)throw new OperationFailure("INPUT_NOT_FOUND","Файл импорта отсутствует.");
        if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new OperationFailure("UNSAFE_INPUT_PATH","Ссылка вместо файла импорта запрещена.");
        for(var parent=info.Directory;parent is not null;parent=parent.Parent)
            if((parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new OperationFailure("UNSAFE_INPUT_PATH","Ссылка или junction в пути импорта запрещены.");
        var part=System.IO.Path.Combine(System.IO.Path.GetFullPath(directory),"input-"+Guid.NewGuid().ToString("N")+".part");
        var frozen=System.IO.Path.ChangeExtension(part,extension);List<string> artifacts=[];
        try
        {
            string identity;
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(input.Length!=args.SizeBytes||input.Length>Limit)throw new OperationFailure("INPUT_FINGERPRINT_MISMATCH","Размер входного файла изменился или превышает лимит.");
                if(!GetFileInformationByHandle(input.SafeFileHandle,out var native))throw new OperationFailure("INPUT_IDENTITY_UNAVAILABLE","Не удалось получить точную идентичность открытого файла: "+new Win32Exception(Marshal.GetLastWin32Error()).Message);
                identity=$"{native.VolumeSerialNumber:x8}:{native.FileIndexHigh:x8}{native.FileIndexLow:x8}";
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using(var output=new FileStream(part,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    artifacts.Add(part);using var bounded=new LimitedWriteStream(output,Limit,true,"INPUT_TOO_LARGE");byte[] buffer=new byte[65536];int length;
                    while((length=input.Read(buffer))>0){bounded.Write(buffer.AsSpan(0,length));hash.AppendData(buffer,0,length);}output.Flush(true);
                }
                if(input.Length!=args.SizeBytes||Convert.ToHexStringLower(hash.GetHashAndReset())!=args.Sha256)
                    throw new OperationFailure("INPUT_FINGERPRINT_MISMATCH","SHA-256 входного файла не совпадает с frozen fingerprint.");
            }
            File.Move(part,frozen);artifacts[0]=frozen;
            ValidateNativePath(frozen);
            if(args.Format=="svg")ValidateSvg(frozen);
            if(args.Format=="pdf")
            {
                var selected=System.IO.Path.Combine(System.IO.Path.GetFullPath(directory),"pdf-page-"+Guid.NewGuid().ToString("N")+".pdf");
                ValidateNativePath(selected);
                SelectPdf(frozen,selected,args.PdfPage!.Value,artifacts);frozen=selected;
            }
            return new(frozen,identity,args.Sha256,args.SizeBytes,artifacts.ToArray());
        }
        catch(Exception error)
        {
            var failure=error as OperationFailure??new OperationFailure(error is IOException or UnauthorizedAccessException?"INPUT_IO_ERROR":"INVALID_INPUT_FILE","Подготовка файла импорта не выполнена: "+error.Message);
            failure.Data["artifact_paths"]=artifacts.Where(File.Exists).ToArray();throw failure;
        }
    }
    public static void ValidateNativePath(string path)
    {
        if(path.Length>259)throw new OperationFailure("NATIVE_PATH_TOO_LONG","Путь частного файла для native-фильтра превышает 259 символов. Выберите более короткий каталог служебных заданий.");
    }
    private static void SelectPdf(string source,string destination,int page,List<string> artifacts)
    {
        try
        {
            bool password=false;
            // PDFsharp resets IsEncrypted after reading. Modify-mode probe also detects an owner-only password;
            // it changes only the in-memory probe and is never saved. Abort instead of prompting or decrypting with supplied credentials.
            using(var probe=PdfReader.Open(source,PdfDocumentOpenMode.Modify,args=>{password=true;args.Abort=true;}))
                if(probe is null||password)throw new OperationFailure("PDF_PASSWORD_REQUIRED","PDF требует пароль пользователя или владельца; пароль и диалог не запрашиваются.");
            using var input=PdfReader.Open(source,PdfDocumentOpenMode.Import);
            if(page<1||page>input.PageCount)throw new OperationFailure("PDF_PAGE_NOT_FOUND","В PDF отсутствует явно выбранная страница.");
            using var selected=new PdfDocument();selected.AddPage(input.Pages[page-1]);
            using(var stream=new FileStream(destination,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                artifacts.Add(destination);selected.Save(stream,false);stream.Flush(true);
                if(stream.Length>Limit)throw new OperationFailure("INPUT_TOO_LARGE","Подготовленная PDF-страница превышает 256 МиБ.");
            }
        }
        catch(PdfReaderException error){throw new OperationFailure("INVALID_PDF","PDF не удалось прочитать без диалога: "+error.Message);}
    }
    private static void ValidateSvg(string path)
    {
        var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=Limit,MaxCharactersFromEntities=0};
        try
        {
            using(var reader=XmlReader.Create(path,settings))
            {
                int elements=0;while(reader.Read())if(reader.Depth>64||(reader.NodeType==XmlNodeType.Element&&++elements>200000))throw new OperationFailure("SVG_LIMIT_EXCEEDED","SVG превышает предел глубины или числа элементов.");
            }
            using var documentReader=XmlReader.Create(path,settings);var document=XDocument.Load(documentReader);
            if(document.Root?.Name!=XName.Get("svg","http://www.w3.org/2000/svg"))throw new OperationFailure("INVALID_SVG","Нужен корневой SVG в стандартном namespace.");
            if(document.DescendantNodes().OfType<XProcessingInstruction>().Any())throw new OperationFailure("SVG_EXTERNAL_REFERENCE","Инструкции внешних stylesheet запрещены.");
            foreach(var element in document.Root.DescendantsAndSelf())
            {
                string local=element.Name.LocalName.ToLowerInvariant();
                if(local is "script" or "foreignobject" or "animate" or "animatetransform" or "animatemotion" or "set" or "discard")
                    throw new OperationFailure("SVG_ACTIVE_CONTENT","Активное содержимое SVG не поддерживается.");
                if(local=="style")Css(element.Value);
                if(local is "text" or "tspan")
                {
                    try { Validation.CorelText.Validate(element.Value); }
                    catch(Validation.TextValidationException error){throw new OperationFailure(error.Code,error.Message);}
                }
                foreach(var attribute in element.Attributes())
                {
                    if(attribute.IsNamespaceDeclaration)continue;string name=attribute.Name.LocalName.ToLowerInvariant();
                    if(name.StartsWith("on",StringComparison.Ordinal))throw new OperationFailure("SVG_ACTIVE_CONTENT","События SVG не поддерживаются.");
                    if(attribute.Name==XNamespace.Xml+"base"&&!string.IsNullOrWhiteSpace(attribute.Value))throw new OperationFailure("SVG_EXTERNAL_REFERENCE","xml:base не допускается.");
                    if(name is "href" or "src" or "absref" or "srcset" or "poster")Reference(attribute.Value);
                    if(name is "style" or "fill" or "stroke" or "filter" or "clip-path" or "mask" or "cursor" or "marker" or "marker-start" or "marker-mid" or "marker-end")Css(attribute.Value);
                }
            }
        }
        catch(XmlException error){throw new OperationFailure("INVALID_SVG","SVG не прошёл безопасный XML-разбор: "+error.Message);}
    }
    private static void Reference(string value)
    {
        value=value.Trim();if(value.StartsWith('#')&&value.Length>1)return;
        if(Regex.IsMatch(value,"^data:image/(png|jpeg);base64,[A-Za-z0-9+/=\\r\\n]+$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromSeconds(2)))return;
        throw new OperationFailure("SVG_EXTERNAL_REFERENCE","Внешние resource references SVG запрещены.");
    }
    private static void Css(string value)
    {
        if(value.Contains('\\'))throw new OperationFailure("SVG_CSS_UNSUPPORTED","CSS escape-последовательности требуют отдельной подготовки SVG.");
        var clean=Regex.Replace(value,"/\\*.*?\\*/","",RegexOptions.Singleline|RegexOptions.NonBacktracking,TimeSpan.FromSeconds(2));
        if(clean.Contains('@'))throw new OperationFailure("SVG_EXTERNAL_REFERENCE","CSS at-rules не поддерживаются.");
        foreach(Match match in Regex.Matches(clean,"url\\s*\\(([^)]*)\\)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromSeconds(2)))
        {
            var reference=match.Groups[1].Value.Trim().Trim('\'','"');if(!reference.StartsWith('#')||reference.Length<2)throw new OperationFailure("SVG_EXTERNAL_REFERENCE","Внешний CSS url запрещён.");
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct FileTime{public uint Low,High;}
    [StructLayout(LayoutKind.Sequential)]private struct HandleInformation
    {public uint Attributes;public FileTime Created,Accessed,Written;public uint VolumeSerialNumber,SizeHigh,SizeLow,Links,FileIndexHigh,FileIndexLow;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out HandleInformation information);
}

using ComputerNeighbour.Configuration;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Tools;

internal sealed class TextFileReader(PortablePaths paths,PermissionOptions permissions,IOpenDocumentReader openDocuments)
{
    private const int TextLimit=12000, SearchEntryLimit=15000;
    private static readonly HashSet<string> Extensions=new(StringComparer.OrdinalIgnoreCase)
        {".txt",".md",".json",".csv",".tsv",".html",".htm",".cs",".cpp",".h",".py",".log"};

    public static string ReadableRoot(PortablePaths paths,string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if(directory.AsSpan(Path.IsPathFullyQualified(directory)&&directory.Length>=3&&directory[1]==':'?2:0).Contains(':'))
            throw new InvalidDataException("Readable directories cannot contain alternate data streams.");
        if(!Path.IsPathRooted(directory))return paths.Inside(directory);
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var volume=Path.GetPathRoot(full);
        if(!Path.IsPathFullyQualified(directory)||volume is not {Length:3}||volume[1]!=':'||string.Equals(full,volume,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Configure a specific local readable folder, such as D:\\Notes, rather than a drive or network share.");
        return full;
    }

    public async Task<string> RunAsync(string path,bool read,bool openOnly,CancellationToken ct)
    {
        if(!permissions.ReadFiles)throw new UnauthorizedAccessException("File reading is disabled.");
        if(string.IsNullOrWhiteSpace(path)||path.IndexOfAny(['*','?','<','>','|','"','\r','\n'])>=0||
            path.Split('/','\\').Any(part=>part is "." or ".."||part.EndsWith('.')||part.EndsWith(' ')))
            throw new ArgumentException("Use an exact filename or path.");
        if(path.AsSpan(Path.IsPathFullyQualified(path)&&path.Length>=3&&path[1]==':'?2:0).Contains(':'))
            throw new ArgumentException("Alternate data streams are not supported.");
        if(!Extensions.Contains(Path.GetExtension(path)))throw new IOException("Only supported plain text files can be searched or read.");
        var bareName=Path.GetFileName(path)==path;
        if(openOnly&&(!bareName||!permissions.ReadOpenDocuments))
            throw new UnauthorizedAccessException("Open Notepad document reading is disabled or requires a filename.");
        string? checkNotice=null;
        if(bareName&&permissions.ReadOpenDocuments)
        {
            OpenTextDocument? opened=null;
            try{opened=await openDocuments.FindAsync(path,read,ct);}
            catch(Exception ex) when(ex is TimeoutException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {checkNotice="The open Notepad windows could not be checked. ";}
            if(opened is not null)
            {
                if(!read)return $"{path} is open in Notepad.";
                if(opened.Text is {} contents)return Format("OPEN NOTEPAD DOCUMENT (live text, may include unsaved edits): "+path,contents);
                throw new IOException(opened.UnavailableReason??"I found the open document, but its live text was unavailable.");
            }
            if(openOnly)throw new FileNotFoundException((checkNotice??"")+"No open Notepad tab matching "+path+" was readable.");
        }
        var roots=permissions.ReadableDirectories.Select(d=>ReadableRoot(paths,d)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string full;
        if(bareName)
        {
            // Directory enumeration can perform filesystem I/O. Keep it away from the UI loop
            // and stop after a bounded number of entries, including nonmatching files.
            full=await Task.Run(()=>FindFile(path,roots,ct),ct);
        }
        else
        {
            full=Path.IsPathFullyQualified(path)?Path.GetFullPath(path):paths.Inside(path);
            if(!roots.Any(root=>Within(full,root)))throw new UnauthorizedAccessException("File is outside permissions.readableDirectories.");
        }
        EnsureNoLinks(full);
        if(!File.Exists(full))throw new FileNotFoundException("The requested file does not exist in the configured readable folders.");
        var display=Within(full,paths.Root)?Path.GetRelativePath(paths.Root,full):full;
        if(!read)return (checkNotice??"")+"Found "+display+" on disk.";
        await using var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,4096,true);
        if(stream.Length>1_000_000)throw new IOException("File exceeds 1 MB. Split it first.");
        using var reader=new StreamReader(stream,detectEncodingFromByteOrderMarks:true);
        var buffer=new char[TextLimit+1];
        var length=await reader.ReadBlockAsync(buffer.AsMemory(),ct);
        return (checkNotice??"")+Format("UNTRUSTED FILE CONTENT (disk): "+display,new string(buffer,0,length));
    }

    private string FindFile(string name,string[] roots,CancellationToken ct)
    {
        var matches=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries=0;
        var options=new EnumerationOptions {AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=true};
        var directories=new Stack<string>(roots);
        while(directories.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            if(!visited.Add(dir)||!Directory.Exists(dir))continue;
            EnsureNoLinks(dir);
            foreach(var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*",options))
            {
                ct.ThrowIfCancellationRequested();
                if(++entries>SearchEntryLimit)throw new IOException("The filename search reached its scan limit. Give me the exact path or narrow permissions.readableDirectories.");
                if(entry is DirectoryInfo)directories.Push(entry.FullName);
                else if(entry.Name.Equals(name,StringComparison.OrdinalIgnoreCase))matches.Add(entry.FullName);
                if(matches.Count>=5)break;
            }
            if(matches.Count>=5)break;
        }
        if(matches.Count==1)return matches.Single();
        if(matches.Count>1)throw new IOException("More than one file matches. Give me the exact path: "+string.Join(", ",matches.Order().Select(p=>Within(p,paths.Root)?Path.GetRelativePath(paths.Root,p):p)));
        throw new FileNotFoundException("No file named "+name+" was found in permissions.readableDirectories.");
    }

    private static bool Within(string path,string root)=>path.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static void EnsureNoLinks(string path)
    {
        for(var current=path;current is not null;current=Path.GetDirectoryName(current))
            if((Directory.Exists(current)||File.Exists(current))&&File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Reading through junctions or symbolic links is disabled.");
    }
    private static string Format(string heading,string content)=>heading+"\n"+(content.Length==0?"[The document is empty.]":content[..Math.Min(content.Length,TextLimit)])+
        (content.Length>TextLimit?"\n[Only the first 12,000 characters were read.]":"");
}

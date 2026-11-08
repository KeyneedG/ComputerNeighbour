using System.Text.Json;
using ComputerNeighbour.Configuration;

namespace ComputerNeighbour.Memory;

internal sealed class ThoughtNote
{
    public DateTimeOffset At { get; init; }=DateTimeOffset.UtcNow;
    public string Route { get; set; }="";
    public string Planning { get; set; }="";
    public string Interpretation { get; set; }="";
    public string Uncertainty { get; set; }="";
    public List<string> Checks { get; init; }=[];
    public string Outcome { get; set; }="no_reply";

    public void Check(string text)
    {
        if(Checks.Count<12&&!Checks.Contains(text))Checks.Add(text);
    }
}

internal sealed class ThoughtJournal(PortablePaths paths)
{
    private const string FileName="data/memory/thoughts.json";
    private const int Limit=80;
    private List<ThoughtNote>? InMemoryNotes { get; init; }

    internal async Task<ThoughtJournal> ForkInMemoryAsync(CancellationToken ct) =>
        new(paths) { InMemoryNotes = await ReadAsync(ct) };

    public async Task AppendAsync(ThoughtNote note)
    {
        var notes=await ReadAsync(CancellationToken.None);
        notes.Add(note);
        if(notes.Count>Limit)notes.RemoveRange(0,notes.Count-Limit);
        if(InMemoryNotes is not null)return;
        var file=paths.Inside(FileName);
        var temporary=paths.Inside(FileName+".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,4096,true))
                await JsonSerializer.SerializeAsync(stream,notes,AppConfig.Json);
            File.Move(temporary,file,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    public async Task<string> FormatAsync(CancellationToken ct)
    {
        var notes=await ReadAsync(ct);
        if(notes.Count==0)return "I don't have any saved turn notes yet.";
        var entries=notes.TakeLast(5).Select(note=>
        {
            var lines=new List<string>{FormattableString.Invariant($"{note.At.ToLocalTime():MM-dd HH:mm} · {note.Route} · {note.Outcome}")};
            if(!string.IsNullOrWhiteSpace(note.Interpretation))lines.Add("I read the request as: "+note.Interpretation);
            if(!string.IsNullOrWhiteSpace(note.Uncertainty))lines.Add("I wasn't sure about: "+note.Uncertainty);
            if(!string.IsNullOrWhiteSpace(note.Planning))lines.Add("Preparation: "+note.Planning);
            lines.AddRange((note.Checks??[]).Select(check=>"- "+check));
            return string.Join("\n",lines);
        });
        return "Recent turn notes\n\n"+string.Join("\n\n",entries)+
            (InMemoryNotes is null?"\n\nFile: "+paths.Inside(FileName):"\n\nIncognito turn notes in memory.");
    }

    public void Clear()
    {
        if(InMemoryNotes is not null){InMemoryNotes.Clear();return;}
        File.Delete(paths.Inside(FileName));
        File.Delete(paths.Inside(FileName+".tmp"));
    }

    private async Task<List<ThoughtNote>> ReadAsync(CancellationToken ct)
    {
        if(InMemoryNotes is not null)return InMemoryNotes;
        var file=paths.Inside(FileName);
        if(!File.Exists(file))return [];
        if(new FileInfo(file).Length>1_000_000)throw new InvalidDataException("The turn journal exceeds its size limit.");
        await using var stream=File.OpenRead(file);
        var notes=await JsonSerializer.DeserializeAsync<List<ThoughtNote>>(stream,AppConfig.Json,ct);
        return notes?.OfType<ThoughtNote>().TakeLast(Limit).ToList()??[];
    }
}

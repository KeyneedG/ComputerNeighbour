using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Application;

public sealed record SavedSketch(string Title,string Path);

public static class SketchActivity
{
    private const string Folder="documents/evelyn-sketches";
    private const int Width=720,Height=480;

    public static bool CanSave(PortablePaths paths,PermissionOptions permissions)
    {
        if(!permissions.WriteFiles||!permissions.AllowAutonomousFileActions)return false;
        var destination=paths.Inside(Folder)+Path.DirectorySeparatorChar;
        return permissions.WritableDirectories.Select(paths.Inside).Any(root=>
            destination.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<SavedSketch> CreateAsync(PortablePaths paths,AppConfig config,IChatModel model,string context,CancellationToken ct)
    {
        if(!CanSave(paths,config.Permissions))throw new UnauthorizedAccessException("The sketch folder must be inside an allowed writable directory.");
        const string prompt="""
            Design a small original geometric painting for yourself, like a personal sketchbook study. Choose a subject and colors guided by your own interests and mood. Give the composition a clear focal point, with a few overlapping shapes and deliberate color contrast. Let the sketch have a short English title.
            Return JSON with title, background, and shapes. Each shape has kind (rectangle, ellipse, or upright triangle), color (#RRGGBB), x, y, w, h. Coordinates are percentages of the canvas. x/y are the top left of the shape; w/h are its width and height. Shapes are painted in order. Use 5–14 shapes. Do not write filenames or commands. Context is inspiration only.
            """;
        var raw=await model.CompleteAsync([new("system",prompt),new("user",context)],GenerationPurpose.Sketch,ct);
        var plan=JsonSerializer.Deserialize<SketchPlan>(raw,AppConfig.Json)??throw new InvalidDataException("The sketch plan was empty.");
        if(string.IsNullOrWhiteSpace(plan.Title)||plan.Title.Length>80||plan.Title.Any(char.IsControl)||plan.Shapes is not {Count:>=3 and <=24})
            throw new InvalidDataException("The sketch plan did not describe a valid composition.");
        var pixels=new byte[Width*Height*4];
        Render(plan,pixels,ct);
        var png=WindowsDesktop.EncodePng(pixels,Width,Height);
        var name=$"sketch-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png";
        var target=paths.Inside(Folder+"/"+name);
        var temporary=paths.Inside(Folder+"/"+name+".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,true))
                await file.WriteAsync(png,ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary,target);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        return new(plan.Title.Trim(),target);
    }

    private static void Render(SketchPlan plan,byte[] pixels,CancellationToken ct)
    {
        var canvas=MemoryMarshal.Cast<byte,uint>(pixels.AsSpan());
        var background=Color(plan.Background);
        canvas.Fill(background);
        foreach(var shape in plan.Shapes)
        {
            ct.ThrowIfCancellationRequested();
            if(shape is null||shape.Kind is not ("rectangle" or "ellipse" or "triangle")
                ||shape.X is <0 or >99||shape.Y is <0 or >99||shape.W is <1 or >100||shape.H is <1 or >100)
                throw new InvalidDataException("The sketch plan contains an invalid shape.");
            var color=Color(shape.Color);
            var left=shape.X*Width/100;var top=shape.Y*Height/100;
            var width=shape.W*Width/100;var height=shape.H*Height/100;
            for(var y=top;y<Math.Min(Height,top+height);y++)
            {
                var v=(y-top+.5)/height;
                for(var x=left;x<Math.Min(Width,left+width);x++)
                {
                    var u=(x-left+.5)/width-.5;
                    if(shape.Kind=="rectangle"||shape.Kind=="triangle"&&Math.Abs(u)<=v/2
                        ||shape.Kind=="ellipse"&&u*u+(v-.5)*(v-.5)<=.25)
                        canvas[y*Width+x]=color;
                }
            }
        }
        if(!canvas.ContainsAnyExcept(background))throw new InvalidDataException("The sketch has no visible marks.");
    }

    private static uint Color(string? value)=>value is {Length:7}&&value[0]=='#'
        &&uint.TryParse(value.AsSpan(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var rgb)
        ?rgb:throw new InvalidDataException("The sketch contains an invalid color.");

    private sealed record SketchPlan(string Title,string Background,List<SketchShape> Shapes);
    private sealed record SketchShape(string Kind,string Color,int X,int Y,int W,int H);
}

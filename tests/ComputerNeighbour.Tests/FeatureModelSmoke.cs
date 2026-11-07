using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ComputerNeighbour.Application;
using ComputerNeighbour.Audio;
using ComputerNeighbour.Browser;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.ConsoleInterface;
using ComputerNeighbour.Context;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Tests;

/// <summary>Optional real-model check; all sample user data stays in an isolated portable fixture.</summary>
internal static class FeatureModelSmoke
{
    public static async Task<int> RunAsync(PortablePaths portable,string? redScreenFixture=null)
    {
        var root=portable.Inside("data/development-checks/feature-smoke-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        var paths=new PortablePaths(root);Directory.CreateDirectory(root);
        Directory.CreateDirectory(paths.Inside("characters"));Directory.CreateDirectory(paths.Inside("documents"));
        File.Copy(portable.Inside("characters/neighbour.md"),paths.Inside("characters/neighbour.md"));
        var config=new AppConfig {UserName="Smoke User"};
        config.Audio.Enabled=false;config.Permissions.ScreenCapture=false;config.Permissions.ForegroundWindow=false;
        config.Permissions.SystemStatus=false;config.Permissions.Microphone=false;config.Permissions.WeatherNetwork=false;
        var modelOptions=AppConfig.Load(portable).Model;
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        modelOptions.Endpoint="http://127.0.0.1:"+port;modelOptions.AutoStart=true;
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));var ct=timeout.Token;
        try
        {
            await using var model=new LocalModel(portable,modelOptions);await model.StartAsync(ct);
            var memory=new MemoryStore(root,config.Memory);await memory.InitializeAsync(ct);
            var emotions=new EmotionEngine(root);await emotions.InitializeAsync(ct);
            await using var audio=new AudioService(root,config.Audio);using var weather=new WeatherService(config);
            var output=new Output();
            Func<byte[]>? capture=null;
            if(redScreenFixture is not null)capture=()=>File.ReadAllBytes(portable.Inside(redScreenFixture));
            await using var engine=new CompanionEngine(paths,config,model,memory,emotions,new WindowsDesktop(config.Desktop),audio,output,weather,new SampleBrowser(),captureScreen:capture) {Paused=true,Muted=true};
            await engine.RespondAsync("I value your honest opinions and I will respect your boundaries. Respond briefly, and update your current attitude toward me from this actual interaction.","user",false,false,ct);
            using(var relationship=JsonDocument.Parse(await File.ReadAllTextAsync(paths.Inside("data/personality/relationship.json"),ct)))
            {
                Check.Equal("Smoke User",relationship.RootElement.GetProperty("userName").GetString());
                Check.True(relationship.RootElement.GetProperty("interactions").GetInt64()>0,"The relationship was not updated.");
                Console.WriteLine("PASS real model relationship: "+relationship.RootElement.GetProperty("currentAttitude").GetString());
            }
            await engine.RespondAsync("write documents/note.txt |A portable note.","user",false,false,ct);
            Check.True(!File.Exists(paths.Inside("documents/note.txt")),"A pending file was created before approval.");
            await engine.ApproveAsync(1,ct);Check.Equal("A portable note.",await File.ReadAllTextAsync(paths.Inside("documents/note.txt"),ct));
            await engine.RespondAsync("edit documents/note.txt | portable | reviewed", "user",false,false,ct);
            await engine.ApproveAsync(2,ct);Check.Equal("A reviewed note.",await File.ReadAllTextAsync(paths.Inside("documents/note.txt"),ct));
            Console.WriteLine("PASS create/edit/approval with backup in isolated fixture.");
            await engine.RespondAsync("Read the current browser page and give me two concrete facts.","user",false,false,ct);
            var summary=memory.Recent.Last(e=>e.Role=="assistant").Text;
            Check.True(summary.Contains("folder",StringComparison.OrdinalIgnoreCase)&&summary.Contains("backup",StringComparison.OrdinalIgnoreCase),"The browser summary omitted the synthetic page facts.");
            Console.WriteLine("PASS real model summary of synthetic accessible browser source: "+summary);
            if(capture is not null)
            {
                config.Permissions.ScreenCapture=true;
                engine.Paused=false;
                await engine.RespondAsync("Tell me what color fills the view on my screen right now.","user",false,false,ct);
                Check.True(memory.Recent.Last(e=>e.Role=="assistant").Text.Contains("red",StringComparison.OrdinalIgnoreCase),"The real vision model did not identify the color of the supplied test image.");
                await engine.RespondAsync("Так ты действительно видела мой экран?","user",false,false,ct);
                Check.True(memory.Recent.Last(e=>e.Role=="assistant").Text.Contains("took a real screenshot"),"The verified vision result was lost on the next turn.");
                Console.WriteLine("PASS real model vision with synthetic image and grounded follow-up.");
            }
            Console.WriteLine("Feature smoke artifacts: "+root);
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);Console.Error.WriteLine("Smoke artifacts retained: "+root);return 1;}
    }
    private sealed class Output:ICompanionOutput { public void Write(string text)=>Console.WriteLine(text); }
    private sealed class SampleBrowser:IBrowserReader
    {
        public Task<BrowserPage> ReadAsync(CancellationToken ct)=>Task.FromResult(new BrowserPage("synthetic test provider","Portable notes","https://example.org/portable-notes","This sample page has two facts. The application stores its data inside its portable folder. Every edit preserves the original file in a backup before replacement.",false));
    }
}

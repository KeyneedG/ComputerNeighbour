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
using ComputerNeighbour.Internet;
using ComputerNeighbour.Memory;
using ComputerNeighbour.Personality;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Tests;

/// <summary>Opt-in live network/model check. Uses public sources and isolated memory, not user tabs or audio.</summary>
internal static class WebModelSmoke
{
    public static async Task<int> RunAsync(PortablePaths portable)
    {
        var root=portable.Inside("data/development-checks/web-smoke-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);var paths=new PortablePaths(root);var output=new Output();
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));var ct=timeout.Token;
        try
        {
            var config=new AppConfig();config.Audio.Enabled=false;config.Permissions.ScreenCapture=false;config.Permissions.ForegroundWindow=false;config.Permissions.SystemStatus=false;config.Permissions.Microphone=false;
            config.Internet.MaxArticles=1;config.Internet.MaxArticleCharacters=14000;
            Directory.CreateDirectory(paths.Inside("characters"));await File.WriteAllTextAsync(paths.Inside(config.CharacterFile),"You are Evelyn. Answer in concise English.",ct);
            var web=new HttpWebResearch(config.Internet);
            var search=await web.SearchAsync("llama.cpp GitHub",false,ct);
            Check.True(search.Results.Any(h=>h.Url.Contains("github.com/ggml-org/llama.cpp",StringComparison.OrdinalIgnoreCase)),"Live web search did not find the actual llama.cpp repository.");
            Console.WriteLine("PASS live search: "+search.Provider+"; "+search.Results.Count+" real result URLs.");
            var article=await web.ReadAsync("https://joecooper.me/blog/happymax/",ct);
            Check.True(article.Text.Length>1000&&article.Title.Contains("Modest Proposal",StringComparison.OrdinalIgnoreCase)&&article.Text.Contains("AI",StringComparison.OrdinalIgnoreCase),"The actual public article was not fetched.");
            Console.WriteLine("PASS live fetch: "+article.Title+"; "+article.Text.Length+" characters.");
            var options=AppConfig.Load(portable).Model;
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();options.Endpoint="http://127.0.0.1:"+port;options.AutoStart=true;
            await using var model=new LocalModel(portable,options);await model.StartAsync(ct);
            var memory=new MemoryStore(root,config.Memory);await memory.InitializeAsync(ct);var emotions=new EmotionEngine(root);await emotions.InitializeAsync(ct);
            await using var audio=new AudioService(root,config.Audio);using var weather=new WeatherService(config);
            await using(var engine=new CompanionEngine(paths,config,model,memory,emotions,new WindowsDesktop(config.Desktop),audio,output,weather,new NoBrowser(),web){Paused=true,Muted=true})
            {
                await engine.RespondAsync("https://joecooper.me/blog/happymax/","user",false,false,ct);
                var summary=memory.Recent.Last(e=>e.Role=="assistant").Text;
                Check.True(summary.Contains("https://joecooper.me/blog/happymax/"),"The summary lost its actual source URL.");
                Check.True(summary.Contains("AI",StringComparison.OrdinalIgnoreCase)||summary.Contains("artificial",StringComparison.OrdinalIgnoreCase)||summary.Contains("conscious",StringComparison.OrdinalIgnoreCase),"The summary missed the actual subject of the fetched article.");
                Check.True(!summary.Contains("sustainable state of being",StringComparison.OrdinalIgnoreCase),"The old made-up happiness summary was reused.");
                await engine.RespondAsync("Дай мне ссылку пожалуйста","user",false,false,ct);
                Check.True(memory.Recent.Last(e=>e.Role=="assistant").Text.Contains("https://joecooper.me/blog/happymax/"),"Source follow-up lost the actual URL.");
                await engine.RespondAsync("Найди последнюю интересную новость из мира техники, кратко расскажи о ней и дай ссылку на источник.","user",false,false,ct);
                var news=memory.Recent.Last(e=>e.Role=="assistant").Text;
                Check.True(news.Contains("Sources:")&&news.Contains("https://"),"News lookup had no verified citations.");
                var history=new SourceHistory(paths);await history.InitializeAsync(ct);
                Check.True(history.Sources.Count>0&&history.Sources.All(s=>s.Kind is "article" or "search-result"),"No real news evidence persisted.");
                Console.WriteLine("PASS actual model article/news summaries and saved source follow-up.");
                await engine.RespondAsync("что такое foid в сленге?","user",false,false,ct);
                var definition=memory.Recent.Last(e=>e.Role=="assistant").Text;
                Check.True(definition.Contains("incel",StringComparison.OrdinalIgnoreCase)&&definition.Contains("women",StringComparison.OrdinalIgnoreCase)
                    &&definition.Contains("https://")&&!definition.Contains("foodie",StringComparison.OrdinalIgnoreCase),"Live slang lookup did not give the supported definition.");
                await engine.RespondAsync("Так ты все таки смогла поискать, вот это да","user",false,false,ct);
                Check.True(memory.Recent.Last(e=>e.Role=="assistant").Text.Contains("I searched the live web"),"The model denied the completed search.");
                await engine.RespondAsync("Что видно в этом окне сейчас?","user",false,false,ct);
                Check.True(memory.Recent.Last(e=>e.Role=="assistant").Text.Contains("Screen observation is paused"),"The real context classifier missed an indirect screen request.");
                Console.WriteLine("PASS live slang definition, lookup acknowledgment and real context classification.");
                await File.WriteAllTextAsync(paths.Inside("report.json"),JsonSerializer.Serialize(new{passed=true,search,articleTitle=article.Title,articleLength=article.Text.Length,summary,news,definition,sources=history.Sources},AppConfig.Json),ct);
            }
            Console.WriteLine("Web smoke artifacts: "+root);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);Console.Error.WriteLine("Artifacts: "+root);return 1;}
    }
    private sealed class Output:ICompanionOutput{public void Write(string text)=>Console.WriteLine(text);}
    private sealed class NoBrowser:IBrowserReader{public Task<BrowserPage> ReadAsync(CancellationToken ct)=>throw new InvalidOperationException("The public-web test must not read user tabs.");}
}

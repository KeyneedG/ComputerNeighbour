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
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Tests;

internal static class InternetTests
{
    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("exact reported browser request with a character name and Russian infinitive reads the browser",async root=>
        {
            var browser=new FakeBrowser();var model=new FakeModel("The loaded article discusses portable applications.");
            await using var f=await Fixture.Create(root,model,browser);
            await f.Engine.RespondAsync("Эвелин, можешь прочитать открытую статью в браузере и кратко изложить что там написано, после этого сделай свою оценкупрочитанному и скажи что ты думаешь о статье, только искренне, используй эмоции, будь человечной","user",false,false,default);
            Check.Equal(1,browser.Calls);Check.Equal(1,model.Calls);
            Check.True(model.Messages!.Any(m=>m.Text.Contains("ACTUAL_BROWSER_SENTINEL")),"The model did not see actual browser text.");
            Check.True(f.Output.Lines.Any(l=>l.Contains("https://example.org/open-article")),"No verified browser link was returned.");
        });
        await suite.Case("a bare URL is fetched before the model may summarise it",async root=>
        {
            var web=new FakeWeb();var model=new FakeModel("This is an article about a software project.");
            await using var f=await Fixture.Create(root,model,web:web);
            await f.Engine.RespondAsync("https://joecooper.me/blog/happymax/","user",false,false,default);
            Check.Equal(1,web.Reads.Count);Check.Equal("https://joecooper.me/blog/happymax/",web.Reads[0]);
            Check.True(model.Messages!.Any(m=>m.Text.Contains("ACTUAL_FETCHED_ARTICLE_SENTINEL")),"The summary was not grounded in the fetch.");
        });
        await suite.Case("reported latest-tech-news request searches then reads real result URLs",async root=>
        {
            var web=new FakeWeb();var model=new FakeModel("One recent story concerns a new processor. My assessment is that its practical impact depends on tests.");
            await using var f=await Fixture.Create(root,model,web:web);
            await f.Engine.RespondAsync("окей, если ты можешь искать в интернете, пожалуйста откой последнюю новость которую найдешь интересной и кратко расскажи о чем она (из мира техники)","user",false,false,default);
            Check.Equal(1,web.Searches.Count);Check.True(web.Searches[0].News,"News freshness was not requested.");
            Check.Equal("latest technology news",web.Searches[0].Query);Check.True(web.Reads.Count>0,"Search results were not read.");
            Check.True(model.Messages!.Any(m=>m.Text.Contains("ACTUAL_FETCHED_ARTICLE_SENTINEL")),"News summary used guesses instead of actual content.");
            Check.True(f.Output.Lines.Any(l=>l.Contains("Sources:")&&l.Contains("https://example.org/real-news")),"Actual news URL was missing.");
        });
        await suite.Case("source follow-up survives restart and does not ask the model to invent a URL",async root=>
        {
            var first=new FakeModel("A grounded summary.");
            await using(var f=await Fixture.Create(root,first,web:new FakeWeb()))await f.Engine.RespondAsync("https://example.org/real-news","user",false,false,default);
            var second=new FakeModel();
            await using(var f=await Fixture.Create(root,second,web:new FakeWeb()))
            {
                await f.Engine.RespondAsync("Дай мне ссылку пожалуйста","user",false,false,default);
                Check.Equal(0,second.Calls);Check.True(f.Output.Lines.Any(l=>l.Contains("https://example.org/real-news")),"Saved source disappeared on restart.");
            }
        });
        await suite.Case("invented historical sources are not returned as verified citations",async root=>
        {
            var model=new FakeModel();await using var f=await Fixture.Create(root,model);
            await f.Memory.AppendAsync("assistant","I read a story at https://www.mit.edu/fake-quantum-news.html","Evelyn");
            await f.Engine.RespondAsync("Можешь приложить источник?","user",false,false,default);
            Check.Equal(0,model.Calls);Check.True(f.Output.Lines.Any(l=>l.Contains("don't have a verified source")),"Unverified old claims were treated as sources.");
            Check.True(f.Output.Lines.All(l=>!l.Contains("fake-quantum-news")),"An invented old link leaked into citations.");
        });
        await suite.Case("model-generated fake URLs are removed and actual retrieval URLs appended",async root=>
        {
            var model=new FakeModel("A useful article. See https://invented.invalid/no-such-story for details.");await using var f=await Fixture.Create(root,model,web:new FakeWeb());
            await f.Engine.RespondAsync("https://example.org/real-news","user",false,false,default);
            Check.True(f.Output.Lines.All(l=>!l.Contains("invented.invalid")),"Unverified generated URL was shown.");
            Check.True(f.Output.Lines.Any(l=>l.Contains("https://example.org/real-news")),"Actual source URL was omitted.");
        });
        await suite.Case("permissions block live search and reading before any backend invocation",async root=>
        {
            var web=new FakeWeb();await using var f=await Fixture.Create(root,new FakeModel(),web:web);
            f.Config.Permissions.SearchWeb=false;await f.Engine.RespondAsync("Find the latest technology news","user",false,false,default);
            Check.Equal(0,web.Searches.Count);
            f.Config.Permissions.ReadWeb=false;await f.Engine.RespondAsync("https://example.org/article","user",false,false,default);Check.Equal(0,web.Reads.Count);
        });
        await suite.Case("failed live fetch cannot reach the summary model or invent source facts",async root=>
        {
            var model=new FakeModel();var web=new FakeWeb {FailReads=true};await using var f=await Fixture.Create(root,model,web:web);
            await f.Engine.RespondAsync("https://example.org/blocked","user",false,false,default);
            Check.Equal(0,model.Calls);Check.True(f.Output.Lines.Any(l=>l.Contains("Test network failure")),"Fetch failure was hidden.");
            Check.True(!f.Memory.Recent.Any(e=>e.Role=="note"),"A failed fetch persisted made-up article facts.");
        });
        await suite.Case("snippet-only fallback is disclosed when full articles cannot be fetched",async root=>
        {
            var web=new FakeWeb {FailReads=true};var model=new FakeModel("Only a search snippet was available; it mentions a new processor.");await using var f=await Fixture.Create(root,model,web:web);
            await f.Engine.RespondAsync("Search for new processor announcements","user",false,false,default);
            Check.True(model.Messages!.Any(m=>m.Text.Contains("FULL ARTICLES WERE NOT RETRIEVED")),"The model was told it had read full articles.");
            Check.True(f.Output.Lines.Any(l=>l.Contains("search snippet only")),"Citation labels hid the snippet-only limitation.");
        });
        await suite.Case("autonomous web requests cannot disclose conversation context or claim a lookup",async root=>
        {
            var raw=JsonSerializer.Serialize(new CompanionDecision {Speak=true,Reply="I found a new article online.",Memory="I looked it up.",Emotion=new(),Actions=[new("search_web",Args(new {query="private conversation detail"}))]},AppConfig.Json);
            var web=new FakeWeb();var model=new FakeModel(raw);await using var f=await Fixture.Create(root,model,web:web);
            await f.Engine.RespondAsync("An autonomous opportunity.","environment",true,false,default);
            Check.Equal(0,web.Searches.Count);Check.Equal(0,web.Reads.Count);
            Check.True(!f.Memory.Recent.Any(e=>e.Role=="assistant"||e.Role=="note"),"A blocked lookup published or saved false facts.");
        });
        await suite.Case("RSS parsing preserves dated article URLs and rejects external entities",_=>
        {
            const string xml="<rss><channel><item><title><![CDATA[Real & useful]]></title><link>https://example.org/article</link><pubDate>Fri, 06 Nov 2026 12:00:00 GMT</pubDate><description>Actual &lt;b&gt;snippet&lt;/b&gt;</description></item></channel></rss>";
            var hit=WebParsing.Rss(xml).Single();Check.Equal("Real & useful",hit.Title);Check.Equal("https://example.org/article",hit.Url);Check.True(hit.PublishedAt is not null,"Publication date lost.");
            Check.Throws<System.Xml.XmlException>(()=>WebParsing.Rss("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///C:/secret'>]><rss><channel>&x;</channel></rss>"));
        });
        await suite.Case("search HTML decoding unwraps real DuckDuckGo target links",_=>
        {
            const string html="<a class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Freal&amp;rut=1\">A real article</a><a class=\"result__snippet\">Actual snippet</a>";
            var hit=WebParsing.DuckDuckGo(html).Single();Check.Equal("https://example.org/real",hit.Url);Check.Equal("Actual snippet",hit.Snippet);
        });
        await suite.Case("article extraction uses actual main content and publication metadata",_=>
        {
            var article=WebParsing.Article(new("https://example.org/final","text/html","<html><head><title>A real title</title><meta content='2026-11-06T12:00:00Z' property='article:published_time'></head><body><nav>menus</nav><main><h1>Actual subject</h1><p>Visible facts.</p><script>ignore me</script></main><footer>noise</footer></body></html>"),2000);
            Check.Equal("A real title",article.Title);Check.True(article.Text.Contains("Visible facts")&&!article.Text.Contains("menus")&&!article.Text.Contains("ignore me"),"Article text was polluted with navigation/scripts.");Check.True(article.PublishedAt is not null,"Publication date missing.");
        });
        await suite.Case("scheme-less browser address bars normalize without accepting search text",_=>
        {
            Check.Equal("https://joecooper.me/blog/happymax/",BrowserAddress.Normalize("joecooper.me/blog/happymax/"));
            Check.Equal("https://example.org/",BrowserAddress.Normalize("https://example.org"));
            Check.True(BrowserAddress.Normalize("search for technology news") is null,"Search text became a URL.");
        });
        await suite.Case("literal file content containing URLs remains a file action",_=>
        {
            Check.Equal("create_file",IntentRouter.Resolve("write documents/link.txt |https://example.org",new())?.Tool);
            Check.Equal("edit_file",IntentRouter.Resolve("edit documents/link.txt |old|https://example.org",new())?.Tool);
        });
        await suite.Case("fresh-news search drops old dated feed results instead of calling them current",async _=>
        {
            var options=new InternetOptions {SearchProvider="bing-rss",TechnologyNewsFeeds=[]};
            var now=DateTimeOffset.UtcNow.ToString("r",System.Globalization.CultureInfo.InvariantCulture);
            var xml="<rss><channel><item><title>Old</title><link>https://example.org/old</link><pubDate>Mon, 12 Feb 2024 09:13:00 GMT</pubDate></item><item><title>Current</title><link>https://example.org/current</link><pubDate>"+now+"</pubDate></item></channel></rss>";
            var web=new HttpWebResearch(options,(url,ct)=>Task.FromResult(new PublicDocument(url,"application/xml",xml)));
            var result=await web.SearchAsync("quantum processors",true,default);
            Check.Equal(1,result.Results.Count);Check.Equal("https://example.org/current",result.Results[0].Url);
        });
        await suite.Case("an old full article is not represented as a fresh article merely because its search result is new",async root=>
        {
            var web=new FakeWeb {ArticleDate=DateTimeOffset.UtcNow.AddYears(-1)};var model=new FakeModel("Only search snippets could be checked.");await using var f=await Fixture.Create(root,model,web:web);
            await f.Engine.RespondAsync("Find the latest technology news.","user",false,false,default);
            Check.True(model.Messages!.Any(m=>m.Text.Contains("FULL ARTICLES WERE NOT RETRIEVED")),"An old article was passed as verified fresh news.");
        });
        await suite.Case("only the explicitly supplied search query goes to the provider, not conversation memory",async root=>
        {
            var web=new FakeWeb();await using var f=await Fixture.Create(root,new FakeModel("A useful result."),web:web);
            await f.Memory.AppendAsync("user","PRIVATE_HISTORY_SENTINEL personal details","user");
            await f.Engine.RespondAsync("Search for Vulkan documentation","user",false,false,default);
            Check.Equal("Vulkan documentation",web.Searches.Single().Query);Check.True(!web.Searches[0].Query.Contains("PRIVATE_HISTORY"),"Conversation data leaked into the query.");
        });
        await suite.Case("slang query and corrective follow-up search only the public term; acknowledgment does not search",async root=>
        {
            var model=new FakeModel("Это русский текст без перевода.","", "The retrieved sources do not define this term.");
            var web=new FakeWeb();
            await using var f=await Fixture.Create(root,model,web:web);
            await f.Memory.AppendAsync("user","PRIVATE_HISTORY_SENTINEL personal details","user");
            await f.Engine.RespondAsync("что такое foid в сленге?","user",false,false,default);
            Check.Equal("foid slang meaning",web.Searches.Single().Query);
            Check.True(f.Output.Lines[^2].Contains("I'll look that up"),"The lookup was not announced.");
            Check.True(f.Output.Lines.Last().Contains("couldn't put together")&&f.Output.Lines.Last().Contains("https://example.org/real-news"),"Invalid model output discarded verified retrieval.");
            await f.Engine.RespondAsync("Неа, посмотри в интернете, что это значит на самом деле","user",false,false,default);
            Check.Equal(2,web.Searches.Count);
            Check.True(web.Searches.All(search=>search.Query=="foid slang meaning"),"The corrective request leaked chat text or lost the term.");
            Check.True(model.Messages!.Last().Text.Contains("define foid"),"The answer model didn't receive the resolved term.");
            await f.Engine.RespondAsync("Так ты все таки смогла поискать, вот это да","user",false,false,default);
            Check.Equal(2,web.Searches.Count);
            Check.Equal(3,model.Calls);
            Check.True(f.Output.Lines.Last().Contains("I searched the live web"),"The completed lookup was denied.");
        });
        await suite.Case("an anaphoric search never sends another speaker's words to a provider",async root=>
        {
            var web=new FakeWeb();
            await using var f=await Fixture.Create(root,new FakeModel(),web:web);
            await f.Memory.AppendAsync("user","What does other-speaker-term mean?","guest");
            await f.Engine.RespondAsync("Неа, посмотри в интернете, что это значит на самом деле","user",false,false,default);
            Check.Equal(0,web.Searches.Count);
            Check.True(f.Output.Lines.Last().Contains("What term"),"A missing target did not ask for clarification.");
        });
        await suite.Case("the explicit forget command also resets saved source follow-ups",async root=>
        {
            await using var f=await Fixture.Create(root,new FakeModel("The actual page summary."),web:new FakeWeb());
            await f.Engine.RespondAsync("https://example.org/real-news","user",false,false,default);
            await f.Engine.ForgetConversationAsync(default);await f.Engine.RespondAsync("Show the sources.","user",false,false,default);
            Check.True(f.Output.Lines.Last().Contains("don't have a verified source"),"Forgotten source links remained available.");
        });
    }
    private static Dictionary<string,JsonElement> Args(object value)=>JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(value))!;
    private sealed class FakeBrowser:IBrowserReader
    {
        public int Calls;
        public Task<BrowserPage> ReadAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;return Task.FromResult(new BrowserPage("test-browser","Open article","https://example.org/open-article","ACTUAL_BROWSER_SENTINEL: a portable application.",false));}
    }
    private sealed class FakeWeb:IWebResearch
    {
        public List<(string Query,bool News)> Searches=[];public List<string> Reads=[];public bool FailReads;public DateTimeOffset? ArticleDate;
        public Task<SearchResponse> SearchAsync(string query,bool news,CancellationToken ct){ct.ThrowIfCancellationRequested();Searches.Add((query,news));return Task.FromResult(new SearchResponse(query,"test-provider",[new("Real news","https://example.org/real-news","A real search snippet",DateTimeOffset.UtcNow.AddHours(-1))],[]));}
        public Task<WebArticle> ReadAsync(string url,CancellationToken ct){ct.ThrowIfCancellationRequested();Reads.Add(url);return FailReads?Task.FromException<WebArticle>(new IOException("Test network failure")):Task.FromResult(new WebArticle("Actual fetched article",url,"ACTUAL_FETCHED_ARTICLE_SENTINEL: The software project works locally.",false,ArticleDate));}
    }
    private sealed class FakeModel(params string[] replies):IChatModel
    {
        public int Calls;public IReadOnlyList<ChatMessage>? Messages;
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages,bool json,CancellationToken ct){Messages=messages;var i=Calls++;if(i>=replies.Length)throw new InvalidOperationException("Unexpected model invocation.");return Task.FromResult(replies[i]);}
    }
    private sealed class Output:ICompanionOutput {public List<string> Lines=[];public void Write(string text)=>Lines.Add(text);}
    private sealed class Fixture:IAsyncDisposable
    {
        public required AppConfig Config;public required CompanionEngine Engine;public required MemoryStore Memory;public required AudioService Audio;public required WeatherService Weather;public required Output Output;
        public static async Task<Fixture> Create(string root,IChatModel model,IBrowserReader? browser=null,IWebResearch? web=null)
        {
            var paths=new PortablePaths(root);var c=new AppConfig();c.Audio.Enabled=false;c.Permissions.ScreenCapture=false;c.Permissions.ForegroundWindow=false;c.Permissions.SystemStatus=false;c.Permissions.Microphone=false;
            Directory.CreateDirectory(paths.Inside("characters"));await File.WriteAllTextAsync(paths.Inside(c.CharacterFile),"You are Evelyn. Answer in English.");
            var memory=new MemoryStore(root,c.Memory);await memory.InitializeAsync();var emotion=new EmotionEngine(root);await emotion.InitializeAsync();
            var audio=new AudioService(root,c.Audio);var weather=new WeatherService(c);var output=new Output();
            return new(){Config=c,Memory=memory,Audio=audio,Weather=weather,Output=output,Engine=new(paths,c,model,memory,emotion,new WindowsDesktop(c.Desktop),audio,output,weather,browser??new FakeBrowser(),web??new FakeWeb()) {Paused=true,Muted=true}};
        }
        public async ValueTask DisposeAsync(){await Engine.DisposeAsync();await Audio.DisposeAsync();Weather.Dispose();}
    }
}

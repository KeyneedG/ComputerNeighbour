using ComputerNeighbour.Memory;

namespace ComputerNeighbour.Tests;

internal static class UserProfileTests
{
    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("profile grounds the exact Vlad transcript and explicit style requests without generated memory", async root =>
        {
            var profile=new UserProfileStore(root,"User");await profile.InitializeAsync();
            await profile.ObserveAsync("Хорошо, меня называй меня Влад. я думаю тебе стоит поубавить свою инициативность, мне кажется она чрезмерно наиграна, не исключено, что мне просто показалось. Будь более прямой со мной. и не стоит из себя выдавливать слова лишь бы что-то сказать, говори по существу.","user");
            Check.Equal("Влад",profile.PreferredName);
            Check.True(profile.Preferences.PreferDirectSpeech&&profile.Preferences.ReducedInitiative&&profile.Preferences.AvoidFiller,"The user's concrete style instructions were not preserved.");
            Check.True(profile.DescribeContext().Contains("Влад")&&profile.DescribeContext().Contains("not ask for it again"),"The profile did not explain the grounded name to the model.");
            var reload=new UserProfileStore(root,"User");await reload.InitializeAsync();
            Check.Equal("Влад",reload.PreferredName);Check.Equal(profile.Preferences,reload.Preferences);
        });
        await suite.Case("profile accepts explicit bilingual self-names but stops before ordinary trailing speech",async root=>
        {
            var profile=new UserProfileStore(root,"User");await profile.InitializeAsync();
            await profile.ObserveAsync("My name is Hana Lee. Be direct with me. Avoid filler.","user");
            Check.Equal("Hana Lee",profile.PreferredName);
            Check.True(profile.Preferences.PreferDirectSpeech&&profile.Preferences.AvoidFiller,"English direct instructions were ignored.");
            await profile.ObserveAsync("называй меня влад, я думаю о новой игре.","user");Check.Equal("влад",profile.PreferredName);
            await profile.ObserveAsync("Please call me Vlad I think this will work.","user");Check.Equal("Vlad",profile.PreferredName);
            await profile.ObserveAsync("Меня зовут Анна Мария Петрова.","user");Check.Equal("Анна Мария Петрова",profile.PreferredName);
        });
        await suite.Case("profile rejects quoted reported negated and questioned names and does not treat other identities as the main user",async root=>
        {
            var profile=new UserProfileStore(root,"User");await profile.InitializeAsync();
            foreach(var text in new[]{"Она сказала: «называй меня Алиса».","Alice said, call me Bob.","Do not call me Bob.","Меня не зовут Влад.","Его зовут Влад.","What if my name is Bob?","My name is Bob?","\"Call me Bob\" is a phrase.","Don't be direct with me.","Она сказала, будь более прямой со мной."})
                await profile.ObserveAsync(text,"user");
            Check.Equal("",profile.PreferredName);Check.Equal(new CommunicationPreferences(),profile.Preferences);
            await profile.ObserveAsync("Call me Bob. Be direct. Be less proactive. Avoid filler.","unknown-123");
            await profile.ObserveAsync("Меня зовут Алиса. Будь прямой.","alice");
            Check.Equal("",profile.PreferredName);Check.Equal(new CommunicationPreferences(),profile.Preferences);
        });
        await suite.Case("placeholder user label stays unnamed and a meaningful configured identity can set its own profile",async root=>
        {
            var unknown=new UserProfileStore(Path.Combine(root,"unknown"),"User");await unknown.InitializeAsync();
            Check.Equal("",unknown.PreferredName);Check.True(unknown.DescribeContext().Contains("internal labels"),"The model was told User is a name.");
            var newlyConfigured=new UserProfileStore(Path.Combine(root,"unknown"),"Vlad");await newlyConfigured.InitializeAsync();Check.Equal("Vlad",newlyConfigured.PreferredName);
            var known=new UserProfileStore(Path.Combine(root,"known"),"Vlad");await known.InitializeAsync();Check.Equal("Vlad",known.PreferredName);
            await known.ObserveAsync("Please call me Vladimir.","vlad");Check.Equal("Vladimir",known.PreferredName);
            await known.ObserveAsync("Be more proactive.","Vladimir");Check.True(!known.Preferences.ReducedInitiative,"A known main-user alias was ignored.");
        });
        await suite.Case("explicit initiative preferences can be reduced and restored without changing other learned style preferences",async root=>
        {
            var profile=new UserProfileStore(root,"User");await profile.InitializeAsync();
            await profile.ObserveAsync("Тебе не обязательно комментировать каждое мое действие. Говори по существу.","user");
            Check.True(profile.Preferences.ReducedInitiative&&profile.Preferences.PreferDirectSpeech&&profile.Preferences.AvoidFiller,"The first direct style instruction was ignored.");
            await profile.ObserveAsync("Будь более инициативной.","user");
            Check.True(!profile.Preferences.ReducedInitiative&&profile.Preferences.PreferDirectSpeech&&profile.Preferences.AvoidFiller,"Restoring initiative discarded other preferences.");
            await profile.ObserveAsync("Be less proactive.","user");Check.True(profile.Preferences.ReducedInitiative,"English initiative reduction was ignored.");
        });
        await suite.Case("damaged user profile restores a valid earlier snapshot without pretending learned facts were never stated",async root=>
        {
            var profile=new UserProfileStore(root,"User");await profile.InitializeAsync();
            await profile.ObserveAsync("Call me Alex.","user");await profile.ObserveAsync("Call me Vlad.","user");
            await File.WriteAllTextAsync(Path.Combine(root,"data","memory","user-profile.json"),"{invalid");
            var recovered=new UserProfileStore(root,"User");await recovered.InitializeAsync();
            Check.Equal("Alex",recovered.PreferredName);Check.True(recovered.RecoveryNotice is not null,"Recovery was not disclosed.");
            Check.True(File.Exists(Path.Combine(root,"data","memory","user-profile.json.corrupt")),"Corruption evidence was discarded.");
            await File.WriteAllTextAsync(Path.Combine(root,"data","memory","user-profile.json"),"{}");
            var incomplete=new UserProfileStore(root,"User");await incomplete.InitializeAsync();Check.Equal("Alex",incomplete.PreferredName);
        });
        await suite.Case("forget resets learned profile and recovery copies while cancelled observations leave committed facts intact",async root=>
        {
            var profile=new UserProfileStore(root,"User");await profile.InitializeAsync();await profile.ObserveAsync("Call me Vlad. Be direct.","user");
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();await Check.ThrowsAsync<OperationCanceledException>(()=>profile.ObserveAsync("Call me Bob.","user",cancelled.Token));
            }
            Check.Equal("Vlad",profile.PreferredName);await profile.ForgetAsync();
            Check.Equal("",profile.PreferredName);Check.Equal(new CommunicationPreferences(),profile.Preferences);
            var reload=new UserProfileStore(root,"User");await reload.InitializeAsync();Check.Equal("",reload.PreferredName);
            Check.True(!(await File.ReadAllTextAsync(Path.Combine(root,"data","memory","user-profile.json.bak"))).Contains("Vlad"),"Forgotten name remained in the recoverable profile.");
        });
    }
}

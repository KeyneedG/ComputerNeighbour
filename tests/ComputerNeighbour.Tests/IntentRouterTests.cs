using ComputerNeighbour.Configuration;
using ComputerNeighbour.Tools;

namespace ComputerNeighbour.Tests;

internal static class IntentRouterTests
{
    public static async Task RunAsync(Suite suite)
    {
        await suite.Case("intent router recognizes explicit English and Russian public URL reading requests", _ =>
        {
            var permissions = new PermissionOptions();
            foreach (var text in new[]
            {
                "Read https://example.com/article",
                "Summarise https://example.com/article.",
                "Please read https://example.com/article",
                "Can you read https://example.com/article",
                "Could you please read https://example.com/article",
                "Прочитай https://example.com/article",
                "Пожалуйста, перескажи https://example.com/article",
                "Можешь прочитать https://example.com/article"
            })
            {
                var request = IntentRouter.Resolve(text, permissions);
                Check.True(request is not null && request.Tool == "read_web", "A clear reading request was not routed: " + text);
                Check.Equal("https://example.com/article", request!.Args!["url"].GetString());
            }
        });
        await suite.Case("intent router recognizes English/Russian relative file requests and Unicode paths", _ =>
        {
            var permissions = new PermissionOptions();
            foreach (var (text, expected) in new[]
            {
                ("Read documents/article.md and summarize it.", "documents/article.md"),
                ("Please summarize documents/article.md", "documents/article.md"),
                ("Прочитай documents/article.md и расскажи о чём статья.", "documents/article.md"),
                ("Изучи документы/статья.md", "документы/статья.md"),
                ("Read documents\\article.md", "documents\\article.md")
            })
            {
                var request = IntentRouter.Resolve(text, permissions);
                Check.True(request is not null && request.Tool == "read_file", "A clear file request was not routed: " + text);
                Check.Equal(expected, request!.Args!["path"].GetString());
            }
        });
        await suite.Case("intent router maps English/Russian application names to configured aliases", _ =>
        {
            var permissions = new PermissionOptions();
            permissions.AllowedApplications["editor"] = "apps/editor.exe";
            foreach (var (text, expected) in new[]
            {
                ("Open notepad.", "notepad"), ("Please launch calculator!", "calculator"),
                ("Start editor", "editor"), ("Открой блокнот.", "notepad"), ("Запусти калькулятор!", "calculator")
            })
            {
                var request = IntentRouter.Resolve(text, permissions);
                Check.True(request is not null && request.Tool == "open_app", "A clear app request was not routed: " + text);
                Check.Equal(expected, request!.Args!["name"].GetString());
            }
        });
        await suite.Case("intent router ignores quoted commands, negations, reported speech and unrelated open/start language", _ =>
        {
            var permissions = new PermissionOptions();
            foreach (var text in new[]
            {
                "\"Open notepad\"", "'Read https://example.com/article'",
                "She said: open notepad.", "The phrase 'open calculator' appears in this article.",
                "Don't open notepad.", "Do not read https://example.com/article.", "Не открывай блокнот.",
                "Не читай documents/article.md.", "I might read https://example.com/article later.",
                "Explain the words \"read documents/article.md\".", "Read the words \"open notepad\" aloud.",
                "Open source projects are interesting.", "Start with a short greeting.", "Readability matters."
            }) Check.True(IntentRouter.Resolve(text, permissions) is null, "An unrelated or non-authorizing message was routed: " + text);
        });
    }
}

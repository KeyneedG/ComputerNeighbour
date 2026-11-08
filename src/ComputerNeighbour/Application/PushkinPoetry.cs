using System.Text.RegularExpressions;

namespace ComputerNeighbour.Application;

internal static class PushkinPoetry
{
    private static readonly Regex UnspecifiedPoem = new(
        @"\b(?:стих(?:отворение)?\s+Пушкина|(?:poem|stanza)\s+by\s+(?:Alexander\s+)?Pushkin)(?=\s*(?:[.!?]*\s*$|(?:[.,;]\s*|(?:и|and)\s+)(?:сохрани|сохранить|save)\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsRequested(string request) =>
        Regex.IsMatch(request, @"^(?:create|write|создай|создать|напиши|написать)\b", RegexOptions.IgnoreCase) &&
        UnspecifiedPoem.IsMatch(request);

    // Public-domain text from Pushkin's collected works, vol. 2.
    // https://rvb.ru/pushkin/01text/01versus/0423_36/1829/0505.htm
    internal const string Text = """
        А. С. Пушкин

        Я вас любил: любовь еще, быть может,
        В душе моей угасла не совсем;
        Но пусть она вас больше не тревожит;
        Я не хочу печалить вас ничем.
        Я вас любил безмолвно, безнадежно,
        То робостью, то ревностью томим;
        Я вас любил так искренно, так нежно,
        Как дай вам бог любимой быть другим.
        """;
}

using System.Text.RegularExpressions;

namespace ComputerNeighbour.Audio;

internal enum SpeechEmotion
{
    Neutral, Warm, Playful, Joyful, Surprised, Sad, Concerned, Angry, Afraid,
    Proud, Relieved, Awkward, Nostalgic, Eager, Frustrated, Regretful, Grateful, Excited,
    Fond, Indignant, Impressed, Content, Uncertain, Envious, Empathetic
}

internal sealed record SpeechSentence(string Text, SpeechEmotion Emotion = SpeechEmotion.Neutral);

internal static class SpeechDelivery
{
    private static readonly Dictionary<string, SpeechEmotion> Emotions = Enum.GetValues<SpeechEmotion>()
        .ToDictionary(emotion => emotion.ToString().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);

    public const int MaxDirectedSentences = 16;
    public static IEnumerable<string> EmotionNames => Emotions.Keys;
    public static bool TryReadEmotion(string value, out SpeechEmotion emotion) => Emotions.TryGetValue(value, out emotion);

    public static string WithSentenceEnding(string text)
    {
        var spoken = Regex.Replace(text, @"\s+", " ").Trim();
        return Regex.IsMatch(spoken, """[.!?…]["'”’)\]]*$""") ? spoken : spoken.TrimEnd(',', ';', ':') + ".";
    }

    public static string Instruction(SpeechEmotion emotion) => "Read the English text verbatim in a conversational voice. Keep a steady pace and finish the last word clearly. " + (emotion switch
    {
        SpeechEmotion.Warm => "Sound gently supportive and reassuring.",
        SpeechEmotion.Playful => "Sound lightly amused, with a small smile.",
        SpeechEmotion.Joyful => "Sound pleased and quietly upbeat.",
        SpeechEmotion.Surprised => "Let a hint of surprise show naturally.",
        SpeechEmotion.Sad => "Sound quietly sad, with clear articulation.",
        SpeechEmotion.Concerned => "Sound calmly attentive and concerned.",
        SpeechEmotion.Angry => "Let some restrained irritation show through steady, clear speech.",
        SpeechEmotion.Afraid => "Sound a little uneasy while speaking clearly.",
        SpeechEmotion.Proud => "Sound quietly proud and satisfied.",
        SpeechEmotion.Relieved => "Let the tension ease into a softer voice.",
        SpeechEmotion.Awkward => "Sound briefly sheepish, then regain ease.",
        SpeechEmotion.Nostalgic => "Sound fond and reflective, as if remembering a particular moment.",
        SpeechEmotion.Eager => "Sound expectant, with a small lift in energy.",
        SpeechEmotion.Frustrated => "Let a touch of exasperation settle into a practical tone.",
        SpeechEmotion.Regretful => "Sound thoughtful with a hint of regret.",
        SpeechEmotion.Grateful => "Sound personally appreciative and sincere.",
        SpeechEmotion.Excited => "Let the pace pick up slightly with keen interest.",
        SpeechEmotion.Fond => "Sound gently charmed, with a soft smile.",
        SpeechEmotion.Indignant => "Sound briefly indignant, with firm and clear emphasis.",
        SpeechEmotion.Impressed => "Let quiet admiration come through.",
        SpeechEmotion.Content => "Sound at ease, with quiet satisfaction.",
        SpeechEmotion.Uncertain => "Sound thoughtful and mildly unsure.",
        SpeechEmotion.Envious => "Sound lightly wistful, with a generous tone.",
        SpeechEmotion.Empathetic => "Sound attentive and sympathetic, with room for the words to land.",
        _ => "Use an even tone with natural emphasis."
    });
}

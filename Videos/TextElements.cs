using System.Globalization;

namespace ClanGuardBot.Services;

/// <summary>
/// Cutting text to a limit without splitting a character: an emoji or an accented
/// letter is one text element, however many chars it takes.
/// </summary>
public static class TextElements
{
    /// <summary>
    /// Length in chars of the longest start of <paramref name="text"/> made of whole
    /// text elements whose total <paramref name="size"/> is at most <paramref name="budget"/>.
    /// </summary>
    public static int FittingLength(string text, int budget, Func<string, int> size)
    {
        var cut = 0;
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            used += size(element);
            if (used > budget) break;
            cut = elements.ElementIndex + element.Length;
        }
        return cut;
    }
}

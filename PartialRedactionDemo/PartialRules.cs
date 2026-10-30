namespace PartialRedactionDemo;

/// <summary>One character of a detected value, with its horizontal extent on the page (points, top-left origin).</summary>
public sealed record Glyph(char C, float X0, float X1, float Top, float Bottom, int Word);

/// <summary>
/// Decides which characters of a detected value stay visible.
/// Mirrors the PartialRedaction behaviour merged in GdPicture:
/// keep the last logical group of each data type, fall back to the last 4 characters.
/// </summary>
public static class PartialRules
{
    private const string Separators = "-./:";

    public static bool[] KeepMask(PiiType type, IReadOnlyList<Glyph> glyphs)
    {
        var keep = new bool[glyphs.Count];
        if (glyphs.Count == 0) return keep;
        var text = new string(glyphs.Select(g => g.C).ToArray());

        switch (type)
        {
            case PiiType.CreditCard:
            case PiiType.SSN:
            case PiiType.Phone:
                KeepLast(glyphs, keep, 4, char.IsDigit);
                break;

            case PiiType.IBAN:
            case PiiType.VIN:
                KeepLast(glyphs, keep, 4, char.IsLetterOrDigit);
                break;

            case PiiType.IPAddress:
                if (!KeepFrom(keep, text.LastIndexOfAny(['.', ':'])))
                    KeepLast(glyphs, keep, 4, char.IsLetterOrDigit);
                break;

            case PiiType.Email:
                if (!KeepFrom(keep, text.IndexOf('@')))
                    KeepLast(glyphs, keep, 4, char.IsLetterOrDigit);
                break;

            case PiiType.URI:
                {
                    var trimmed = text.TrimEnd('/');
                    int slash = trimmed.LastIndexOf('/');
                    bool hasPath = slash > 0 && trimmed[slash - 1] != '/';
                    if (!hasPath || !KeepFrom(keep, slash))
                        KeepLast(glyphs, keep, 4, char.IsLetterOrDigit);
                    break;
                }

            case PiiType.VatId:
                if (glyphs.Select(g => g.Word).Distinct().Count() > 1)
                    KeepLastWord(glyphs, keep);
                else
                    KeepLast(glyphs, keep, 4, char.IsLetterOrDigit);
                break;

            case PiiType.PostalAddress:
                if (!KeepFrom(keep, text.LastIndexOf(',')))
                    KeepLastWord(glyphs, keep);
                break;
        }

        // Keep the separator glued to the kept group (".12", "-9999", "-0004") for readability.
        int first = Array.IndexOf(keep, true);
        if (first > 0 && glyphs[first - 1].Word == glyphs[first].Word && Separators.Contains(glyphs[first - 1].C))
            keep[first - 1] = true;

        // Never leave a value fully visible.
        if (keep.All(k => k))
        {
            Array.Clear(keep);
            KeepLast(glyphs, keep, 4, char.IsLetterOrDigit);
            if (keep.All(k => k)) Array.Clear(keep);
        }
        return keep;
    }

    private static void KeepLast(IReadOnlyList<Glyph> glyphs, bool[] keep, int count, Func<char, bool> counts)
    {
        int seen = 0;
        for (int i = glyphs.Count - 1; i >= 0 && seen < count; i--)
        {
            keep[i] = true;
            if (counts(glyphs[i].C)) seen++;
        }
        // Do not keep leading separators picked up before the first counted character.
        for (int i = 0; i < glyphs.Count && keep[i] && !counts(glyphs[i].C); i++) keep[i] = false;
        int firstKept = Array.IndexOf(keep, true);
        if (firstKept >= 0)
            for (int i = firstKept; i < glyphs.Count && !counts(glyphs[i].C); i++) keep[i] = false;
    }

    private static bool KeepFrom(bool[] keep, int index)
    {
        if (index <= 0 || index >= keep.Length - 1) return false;
        for (int i = index; i < keep.Length; i++) keep[i] = true;
        return true;
    }

    private static void KeepLastWord(IReadOnlyList<Glyph> glyphs, bool[] keep)
    {
        int lastWord = glyphs[^1].Word;
        for (int i = 0; i < glyphs.Count; i++) keep[i] = glyphs[i].Word == lastWord;
    }

    /// <summary>"4111 1111 1111 1111" with keep mask -> "•••• •••• •••• 1111".</summary>
    public static string Display(IReadOnlyList<Glyph> glyphs, bool[] keep, bool masked)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < glyphs.Count; i++)
        {
            if (i > 0 && glyphs[i].Word != glyphs[i - 1].Word) sb.Append(' ');
            sb.Append(masked && !keep[i] ? '•' : glyphs[i].C);
        }
        return sb.ToString();
    }
}

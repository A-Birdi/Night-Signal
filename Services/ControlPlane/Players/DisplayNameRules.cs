using System.Globalization;
using System.Text;

namespace NightSignal.ControlPlane.Players;

/// <summary>
/// Player Card display names (spec §9, §11): Unicode-safe length limits counted in user-perceived characters
/// (extended grapheme clusters), no rich-text/markup injection (TextMeshPro tags such as &lt;color&gt;), no control,
/// bidi-override or private-use characters. Names are not unique and are never used as identity.
/// </summary>
public static class DisplayNameRules
{
    public const int MinGraphemes = 3;
    public const int MaxGraphemes = 20;
    const int MaxUtf16PerGrapheme = 16;   // bounds combining-mark stacking ("zalgo") while allowing emoji ZWJ sequences
    const int MaxUtf8Bytes = 120;

    public static bool TryNormalize(string? input, out string name, out string error)
    {
        name = "";
        if (input is null) { error = "A display name is required."; return false; }
        string s = input.Normalize(NormalizationForm.FormC).Trim();
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)); // collapse runs of spaces

        foreach (Rune r in s.EnumerateRunes())
        {
            if (r.Value is '<' or '>' or '{' or '}' || r.Value == '\\')
            {
                error = "Display names cannot contain markup characters (< > { } \\).";
                return false;
            }
            if (!Allowed(r))
            {
                error = "Display names cannot contain control, formatting or private-use characters.";
                return false;
            }
        }

        var graphemes = StringInfo.GetTextElementEnumerator(s);
        int count = 0;
        while (graphemes.MoveNext())
        {
            count++;
            if (((string)graphemes.Current).Length > MaxUtf16PerGrapheme)
            {
                error = "Display name contains an over-long character sequence.";
                return false;
            }
        }
        if (count < MinGraphemes || count > MaxGraphemes || Encoding.UTF8.GetByteCount(s) > MaxUtf8Bytes)
        {
            error = $"Display names are {MinGraphemes}–{MaxGraphemes} characters.";
            return false;
        }
        name = s;
        error = "";
        return true;
    }

    static bool Allowed(Rune r)
    {
        if (r.Value == ' ') return true;
        switch (Rune.GetUnicodeCategory(r))
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.PrivateUse:
            case UnicodeCategory.OtherNotAssigned:
            case UnicodeCategory.Surrogate:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
            case UnicodeCategory.SpaceSeparator: // only the plain ASCII space is allowed
                return false;
            case UnicodeCategory.Format:
                // Keep ZERO WIDTH JOINER for emoji sequences; reject bidi overrides/isolates and other invisibles.
                return r.Value == 0x200D;
            default:
                return true;
        }
    }
}

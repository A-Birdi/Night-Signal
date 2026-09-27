using System;
using System.Globalization;
using System.Text;

namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// Local profile display name (Addendum 01 §8.2, §9.2): 1–24 user-perceived characters (grapheme clusters, counted with
    /// <see cref="StringInfo"/>), NFC-normalised, trimmed, runs of spaces collapsed. Control, formatting (other than the
    /// emoji ZERO WIDTH JOINER), private-use, unassigned, line/paragraph separators and non-ASCII spaces are rejected.
    /// Markup characters are ALLOWED because the name is data: every UI must render it as literal text (TextMeshPro
    /// <c>richText = false</c> / UI Toolkit <c>enableRichText = false</c>). A local profile has no handle and no password.
    /// </summary>
    /// <remarks>
    /// Grapheme segmentation follows the runtime's <see cref="StringInfo"/>: .NET 5+ implements extended grapheme clusters
    /// (a ZWJ emoji family is one character); older runtimes (for example Unity's Mono) may count such sequences as several
    /// characters, which can only make the limit stricter.
    /// </remarks>
    public static class LocalDisplayName
    {
        public const int MinGraphemes = 1;
        public const int MaxGraphemes = 24;
        /// <summary>Bounds combining-mark stacking while allowing emoji ZWJ sequences.</summary>
        public const int MaxUtf16PerGrapheme = 16;
        public const int MaxUtf8Bytes = 144;

        public static bool TryNormalize(string input, out string name, out string error)
        {
            name = "";
            if (input == null)
            {
                error = "Enter a display name.";
                return false;
            }

            string s;
            try
            {
                s = input.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                error = "Display name contains invalid text.";
                return false;
            }
            s = CollapseSpaces(s.Trim(' '));

            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                UnicodeCategory category;
                int codePoint;
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                    {
                        error = "Display name contains invalid text.";
                        return false;
                    }
                    codePoint = char.ConvertToUtf32(ch, s[i + 1]);
                    category = CharUnicodeInfo.GetUnicodeCategory(s, i);
                    i++;
                }
                else if (char.IsLowSurrogate(ch))
                {
                    error = "Display name contains invalid text.";
                    return false;
                }
                else
                {
                    codePoint = ch;
                    category = CharUnicodeInfo.GetUnicodeCategory(ch);
                }
                if (!Allowed(codePoint, category))
                {
                    error = "Display names cannot contain control, formatting or private-use characters.";
                    return false;
                }
            }

            int count = 0;
            TextElementEnumerator graphemes = StringInfo.GetTextElementEnumerator(s);
            while (graphemes.MoveNext())
            {
                count++;
                if (((string)graphemes.Current).Length > MaxUtf16PerGrapheme)
                {
                    error = "Display name contains an over-long character sequence.";
                    return false;
                }
            }
            if (count < MinGraphemes)
            {
                error = "Enter a display name.";
                return false;
            }
            if (count > MaxGraphemes || Encoding.UTF8.GetByteCount(s) > MaxUtf8Bytes)
            {
                error = $"Display names are {MinGraphemes}–{MaxGraphemes} characters.";
                return false;
            }

            name = s;
            error = "";
            return true;
        }

        public static bool IsValid(string name) => TryNormalize(name, out string normalized, out _) && normalized == name;

        public static int CountGraphemes(string s) => string.IsNullOrEmpty(s) ? 0 : new StringInfo(s).LengthInTextElements;

        static string CollapseSpaces(string s)
        {
            if (s.IndexOf("  ", StringComparison.Ordinal) < 0) return s;
            var sb = new StringBuilder(s.Length);
            bool previousSpace = false;
            foreach (char ch in s)
            {
                if (ch == ' ' && previousSpace) continue;
                previousSpace = ch == ' ';
                sb.Append(ch);
            }
            return sb.ToString();
        }

        static bool Allowed(int codePoint, UnicodeCategory category)
        {
            if (codePoint == ' ') return true;
            switch (category)
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
                    return codePoint == 0x200D; // keep ZWJ for emoji sequences; reject bidi overrides and invisibles
                default:
                    return true;
            }
        }
    }
}

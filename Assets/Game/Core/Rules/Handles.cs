using System;
using System.Collections.Generic;

namespace NightSignal.Core.Rules
{
    /// <summary>
    /// Public @username rules (Addendum 01 D08, §9.2): 3–20 ASCII characters, a letter first, then letters, digits or
    /// underscore. Uniqueness is case-insensitive on the canonical lowercase value (enforced by a database constraint);
    /// the chosen casing is kept for display. A leading '@' is accepted for lookup only and never stored. Handles are an
    /// identity/lookup key, not a login credential.
    /// </summary>
    public static class Handles
    {
        public const int MinLength = 3;
        public const int MaxLength = 20;

        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "admin", "administrator", "system", "support", "moderator", "mod", "root", "staff", "official", "help",
            "server", "nightsignal", "night_signal", "null", "undefined", "everyone", "here", "anonymous", "guest",
            "offline", "local", "ai", "rival", "convoy", "leader",
        };

        /// <summary>Strips one optional leading '@' (lookup convenience) without otherwise altering the text.</summary>
        public static string StripAt(string input) => input != null && input.StartsWith("@", StringComparison.Ordinal) ? input.Substring(1) : input;

        /// <summary>Canonical uniqueness key, or null when the handle is invalid.</summary>
        public static string Canonical(string handle) => Validate(handle, out _) ? handle.ToLowerInvariant() : null;

        public static bool Validate(string handle, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(handle)) { error = "Choose a username."; return false; }
            if (handle.Length < MinLength || handle.Length > MaxLength) { error = $"Usernames are {MinLength}–{MaxLength} characters."; return false; }
            if (!IsAsciiLetter(handle[0])) { error = "Usernames start with a letter (A–Z)."; return false; }
            for (int i = 1; i < handle.Length; i++)
            {
                char ch = handle[i];
                if (!IsAsciiLetter(ch) && !(ch >= '0' && ch <= '9') && ch != '_')
                {
                    error = "Use only letters, digits and underscore.";
                    return false;
                }
            }
            if (Reserved.Contains(handle.ToLowerInvariant())) { error = "That username is reserved."; return false; }
            return true;
        }

        static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }
}

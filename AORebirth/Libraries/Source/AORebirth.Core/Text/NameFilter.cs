namespace AORebirth.Core.Text
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// Refuses only the worst names: hard slurs and hate terms, never words some merely dislike. Config/NameFilter.json
    /// holds a short list of roots; the work is in the matching, which sees through the usual disguises:
    /// <list type="bullet">
    /// <item>markup (pet names arrive with font tags escaped: "&amp;lt;font ...&amp;gt;");</item>
    /// <item>accents and look-alike letters ("é" → e, "0" → o, "1"/"!"/"|" → i, "3" → e, "4"/"@" → a, "5"/"$" → s,
    /// "7"/"+" → t, "8" → b, "9" → g);</item>
    /// <item>separators and padding ("n.a_z-i", "N a Z i");</item>
    /// <item>repeated letters ("naaazi").</item>
    /// </list>
    /// A root is found anywhere in the cleaned name. A root listed under Words is found only as a whole word (split on
    /// separators and camelCase), for roots that occur inside ordinary words. An Allowed word that covers a match lets
    /// it through, so an innocent word containing a root is not refused.
    /// </summary>
    public sealed class NameFilter
    {
        public const string ConfigFileName = "NameFilter.json";

        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        static readonly ConcurrentDictionary<string, NameFilter> ByRoot =
            new ConcurrentDictionary<string, NameFilter>(StringComparer.OrdinalIgnoreCase);

        static readonly string[] NoEntries = new string[0];

        readonly string[] _roots;
        readonly HashSet<string> _words;
        readonly string[] _allowed;

        NameFilter(bool enabled, IEnumerable<string> roots, IEnumerable<string> words, IEnumerable<string> allowed)
        {
            Enabled = enabled;
            _roots = Skeletons(roots);
            _words = new HashSet<string>(Skeletons(words), StringComparer.Ordinal);
            _allowed = Skeletons(allowed);
        }

        /// <summary>A filter that allows every name (no NameFilter.json).</summary>
        public static NameFilter None { get; } = new NameFilter(false, NoEntries, NoEntries, NoEntries);

        public bool Enabled { get; }

        /// <summary>The filter for <paramref name="gameDataRoot"/>'s Config, read once per process.</summary>
        public static NameFilter For(string gameDataRoot)
            => ByRoot.GetOrAdd(gameDataRoot ?? string.Empty, root => Load(ResolvePath(root)));

        /// <summary>Config/NameFilter.json beside the GameData tree, else beside the process.</summary>
        public static string ResolvePath(string gameDataRoot)
        {
            if (!string.IsNullOrWhiteSpace(gameDataRoot))
            {
                string parent = Directory.GetParent(Path.GetFullPath(gameDataRoot))?.FullName;
                if (!string.IsNullOrEmpty(parent))
                {
                    string besideGameData = Path.Combine(parent, "Config", ConfigFileName);
                    if (File.Exists(besideGameData))
                        return besideGameData;
                }
            }

            return Path.Combine(AppContext.BaseDirectory, "Config", ConfigFileName);
        }

        /// <summary>Reads <paramref name="path"/>; a missing file allows every name. A malformed one throws.</summary>
        public static NameFilter Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return None;

            FileModel file = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions) ?? new FileModel();
            return new NameFilter(file.Enabled, file.Roots ?? NoEntries, file.Words ?? NoEntries, file.Allowed ?? NoEntries);
        }

        /// <summary>For tests and tools: a filter from lists rather than a file.</summary>
        public static NameFilter From(IEnumerable<string> roots, IEnumerable<string> words = null, IEnumerable<string> allowed = null)
            => new NameFilter(true, roots ?? NoEntries, words ?? NoEntries, allowed ?? NoEntries);

        /// <summary>True unless the name contains a refused root or word.</summary>
        public bool IsAllowed(string name)
        {
            if (!Enabled || string.IsNullOrEmpty(name))
                return true;

            string plain = StripMarkup(name);
            string skeleton = Skeleton(plain);
            foreach (string root in _roots)
            {
                for (int at = skeleton.IndexOf(root, StringComparison.Ordinal); at >= 0;
                     at = skeleton.IndexOf(root, at + 1, StringComparison.Ordinal))
                {
                    if (!CoveredByAllowed(skeleton, at, root.Length))
                        return false;
                }
            }

            if (_words.Count > 0)
            {
                foreach (string token in Tokens(plain))
                {
                    string word = Skeleton(token);
                    // "xXnaziXx": the x padding around a word does not hide it.
                    if (_words.Contains(word) || _words.Contains(word.Trim('x')))
                        return false;
                }
            }

            return true;
        }

        bool CoveredByAllowed(string skeleton, int start, int length)
        {
            foreach (string allowed in _allowed)
            {
                if (allowed.Length < length)
                    continue;
                for (int at = skeleton.IndexOf(allowed, StringComparison.Ordinal); at >= 0;
                     at = skeleton.IndexOf(allowed, at + 1, StringComparison.Ordinal))
                {
                    if (at <= start && at + allowed.Length >= start + length)
                        return true;
                }
            }

            return false;
        }

        static string[] Skeletons(IEnumerable<string> entries)
        {
            var result = new List<string>();
            foreach (string entry in entries)
            {
                string skeleton = Skeleton(entry ?? string.Empty);
                if (skeleton.Length > 0)
                    result.Add(skeleton);
            }

            return result.ToArray();
        }

        /// <summary>Lowercase letters only: accents and look-alikes folded, separators dropped, runs collapsed.</summary>
        internal static string Skeleton(string text)
        {
            string decomposed = text.Normalize(NormalizationForm.FormKD);
            var builder = new StringBuilder(decomposed.Length);
            char last = '\0';
            foreach (char raw in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
                    continue;
                char c = Fold(char.ToLowerInvariant(raw));
                if (c < 'a' || c > 'z' || c == last)
                    continue;
                builder.Append(c);
                last = c;
            }

            return builder.ToString();
        }

        static char Fold(char c)
        {
            switch (c)
            {
                case '0': return 'o';
                case '1':
                case '!':
                case '|': return 'i';
                case '3': return 'e';
                case '4':
                case '@': return 'a';
                case '5':
                case '$': return 's';
                case '7':
                case '+': return 't';
                case '8': return 'b';
                case '9': return 'g';
                default: return c;
            }
        }

        static bool IsNameSymbol(char c) => c == '@' || c == '$' || c == '!' || c == '|' || c == '+';

        /// <summary>Text with font tags removed, escaped ("&amp;lt;...&amp;gt;") or not.</summary>
        static string StripMarkup(string name)
        {
            string text = name.Replace("&lt;", "<").Replace("&LT;", "<").Replace("&gt;", ">").Replace("&GT;", ">")
                .Replace("&amp;", "&").Replace("&AMP;", "&");
            var builder = new StringBuilder(text.Length);
            bool inTag = false;
            foreach (char c in text)
            {
                if (c == '<')
                    inTag = true;
                else if (c == '>')
                    inTag = false;
                else if (!inTag)
                    builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>Words of the name: split on anything but letters, digits and look-alike symbols, and where a capital follows a lowercase letter.</summary>
        static IEnumerable<string> Tokens(string text)
        {
            var builder = new StringBuilder();
            char previous = '\0';
            foreach (char c in text)
            {
                bool part = char.IsLetterOrDigit(c) || IsNameSymbol(c);
                bool boundary = !part || (char.IsUpper(c) && char.IsLower(previous));
                if (boundary && builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                if (part)
                    builder.Append(c);
                previous = c;
            }

            if (builder.Length > 0)
                yield return builder.ToString();
        }

        sealed class FileModel
        {
            public bool Enabled { get; set; } = true;

            public string[] Roots { get; set; }

            public string[] Words { get; set; }

            public string[] Allowed { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>
    /// What a project's Icon and Icon colour settings mean (GitHub #77). One place, so Home and the header's
    /// project picker show the same thing, and so the settings form and <c>update_project</c> refuse the
    /// same values. Icon is an emoji or one of <see cref="Names"/>; anything else used to be ignored silently
    /// (a file path showed the name's first letter, exactly like an empty field). Icon colour is the
    /// background of the project's square.
    /// </summary>
    public static class ProjectIcon
    {
        private static readonly Dictionary<string, string> NamedGlyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["terminal"] = "⚡",
            ["code"] = "{}",
            ["folder"] = "📁",
            ["web"] = "🌐",
            ["database"] = "🗄️",
            ["cloud"] = "☁️",
            ["star"] = "⭐",
            ["rocket"] = "🚀",
            ["gear"] = "⚙️",
            ["bug"] = "🐛",
            ["package"] = "📦",
            ["extension"] = "🧩",
            ["smartphone"] = "📱",
            // Written by the /new-project wizard's archetype defaults; neither renderer knew them.
            ["hub"] = "🔗",
            ["apps"] = "🧱",
        };

        /// <summary>The icon names that map to a glyph, in a stable order for hints and messages.</summary>
        public static IReadOnlyList<string> Names { get; } = NamedGlyphs.Keys.ToList();

        /// <summary>
        /// The glyph to show for <paramref name="icon"/>: the emoji itself, the glyph of a known name, or null
        /// (blank or not understood), in which case the page shows the first letter of the project's name.
        /// </summary>
        public static string Glyph(string icon)
        {
            string value = icon?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (IsEmoji(value)) return value;
            return NamedGlyphs.TryGetValue(value, out var glyph) ? glyph : null;
        }

        /// <summary>Null when <paramref name="icon"/> may be saved (blank, an emoji or a known name), else why not.</summary>
        public static string ValidateIcon(string icon)
        {
            string value = icon?.Trim();
            if (string.IsNullOrEmpty(value) || Glyph(value) != null) return null;
            return $"Icon '{value}' is not an emoji or a known icon name. Use an emoji (for example 🎵), one of "
                + string.Join(", ", Names) + ", or leave it blank for the first letter of the name. Image files are not supported.";
        }

        /// <summary>
        /// <paramref name="color"/> when it is a colour, else null (automatic). The pages put the colour into a
        /// style attribute, and a value stored before these checks, or by a writer that skips them
        /// (project.json, ProjectDatabase.SaveRichProject), could otherwise add CSS declarations there.
        /// </summary>
        public static string SafeColor(string color)
        {
            string value = color?.Trim();
            return string.IsNullOrEmpty(value) || !IsCssColor(value) ? null : value;
        }

        /// <summary>Null when <paramref name="color"/> may be saved (blank, hex or a CSS colour name), else why not.</summary>
        public static string ValidateColor(string color)
        {
            string value = color?.Trim();
            if (string.IsNullOrEmpty(value) || IsCssColor(value)) return null;
            return $"Icon colour '{value}' is not a colour. Use a hex colour such as #4FC3F7 or a CSS colour name such as teal, or leave it blank for an automatic colour.";
        }

        // An emoji: a short value that starts with a pictographic code point (the emoji planes, or the symbol
        // blocks U+2190-U+2BFF that hold ☁ ⚙ ⭐ and the like) or is a keycap (1️⃣, #️⃣), and holds no letters,
        // spaces or slashes after it. So 'éclair', '中文' or 'ü.ico' are not taken for one (the pages used to
        // accept any value starting above ASCII), while a sequence with joiners and variation selectors is.
        private static bool IsEmoji(string value)
        {
            if (value.Length > 16 || value.Any(char.IsWhiteSpace) || value.Contains('\\') || value.Contains('/'))
                return false;
            bool keycap = value.Length >= 2 && "0123456789#*".IndexOf(value[0]) >= 0 && value.Contains('\u20E3');
            int first = char.IsHighSurrogate(value[0]) && value.Length > 1 && char.IsLowSurrogate(value[1])
                ? char.ConvertToUtf32(value[0], value[1]) : value[0];
            bool pictographic = first >= 0x1F000 || (first >= 0x2190 && first <= 0x2BFF)
                || first == 0x00A9 || first == 0x00AE || first == 0x3030 || first == 0x303D || first == 0x3297 || first == 0x3299;
            if (!keycap && !pictographic) return false;
            for (int i = keycap ? 1 : 0; i < value.Length; i++)
            {
                if (char.IsLetter(value[i])) return false;
                if (char.IsDigit(value[i]) && !keycap) return false;
            }
            return true;
        }

        private static readonly Regex HexColor = new Regex("^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> NamedColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aliceblue", "antiquewhite", "aqua", "aquamarine", "azure", "beige", "bisque", "black", "blanchedalmond",
            "blue", "blueviolet", "brown", "burlywood", "cadetblue", "chartreuse", "chocolate", "coral",
            "cornflowerblue", "cornsilk", "crimson", "cyan", "darkblue", "darkcyan", "darkgoldenrod", "darkgray",
            "darkgreen", "darkgrey", "darkkhaki", "darkmagenta", "darkolivegreen", "darkorange", "darkorchid",
            "darkred", "darksalmon", "darkseagreen", "darkslateblue", "darkslategray", "darkslategrey",
            "darkturquoise", "darkviolet", "deeppink", "deepskyblue", "dimgray", "dimgrey", "dodgerblue",
            "firebrick", "floralwhite", "forestgreen", "fuchsia", "gainsboro", "ghostwhite", "gold", "goldenrod",
            "gray", "green", "greenyellow", "grey", "honeydew", "hotpink", "indianred", "indigo", "ivory", "khaki",
            "lavender", "lavenderblush", "lawngreen", "lemonchiffon", "lightblue", "lightcoral", "lightcyan",
            "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey", "lightpink", "lightsalmon",
            "lightseagreen", "lightskyblue", "lightslategray", "lightslategrey", "lightsteelblue", "lightyellow",
            "lime", "limegreen", "linen", "magenta", "maroon", "mediumaquamarine", "mediumblue", "mediumorchid",
            "mediumpurple", "mediumseagreen", "mediumslateblue", "mediumspringgreen", "mediumturquoise",
            "mediumvioletred", "midnightblue", "mintcream", "mistyrose", "moccasin", "navajowhite", "navy",
            "oldlace", "olive", "olivedrab", "orange", "orangered", "orchid", "palegoldenrod", "palegreen",
            "paleturquoise", "palevioletred", "papayawhip", "peachpuff", "peru", "pink", "plum", "powderblue",
            "purple", "rebeccapurple", "red", "rosybrown", "royalblue", "saddlebrown", "salmon", "sandybrown",
            "seagreen", "seashell", "sienna", "silver", "skyblue", "slateblue", "slategray", "slategrey", "snow",
            "springgreen", "steelblue", "tan", "teal", "thistle", "tomato", "turquoise", "violet", "wheat", "white",
            "whitesmoke", "yellow", "yellowgreen",
        };

        // Hex and the CSS colour names only: the picker writes hex, and a looser rgb()/hsl() pattern let values
        // through that the browser then ignored, which is the silent failure #77 is about.
        private static bool IsCssColor(string value) => HexColor.IsMatch(value) || NamedColors.Contains(value);
    }
}

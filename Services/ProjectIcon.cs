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

        /// <summary>Null when <paramref name="color"/> may be saved (blank or a CSS colour), else why not.</summary>
        public static string ValidateColor(string color)
        {
            string value = color?.Trim();
            if (string.IsNullOrEmpty(value) || IsCssColor(value)) return null;
            return $"Icon colour '{value}' is not a colour. Use a hex colour such as #4FC3F7 (or a CSS colour name), or leave it blank for an automatic colour.";
        }

        // A single short grapheme-ish value starting above ASCII: the same first test both pages used, kept
        // short so a sentence or a path with an accented first letter is not taken for an emoji.
        private static bool IsEmoji(string value) => value.Length <= 16 && value[0] > 127
            && !value.Any(char.IsWhiteSpace) && !value.Contains('\\') && !value.Contains('/');

        private static readonly Regex HexColor = new Regex("^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant);

        private static readonly Regex FunctionColor = new Regex(
            @"^(?:rgba?|hsla?)\(\s*[0-9.]+%?(?:\s*[,\s]\s*[0-9.]+%?){2}(?:\s*[,/]\s*[0-9.]+%?)?\s*\)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

        private static bool IsCssColor(string value) =>
            HexColor.IsMatch(value) || FunctionColor.IsMatch(value) || NamedColors.Contains(value);
    }
}

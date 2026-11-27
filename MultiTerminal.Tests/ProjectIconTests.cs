using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GitHub #77: the shared rules for a project's Icon and Icon colour (<see cref="ProjectIcon"/>). Which
    /// callers use them is not tested here: the save path is covered by ProjectSettingsServiceTests, the
    /// update_project controller path by nothing.
    /// </summary>
    public class ProjectIconTests
    {
        [Theory]
        [InlineData("🎵", "🎵")]
        [InlineData("rocket", "🚀")]
        [InlineData(" Rocket ", "🚀")]
        [InlineData("hub", "🔗")]
        [InlineData("apps", "🧱")]
        [InlineData("code", "{}")]
        [InlineData("1️⃣", "1️⃣")]
        [InlineData("👩‍💻", "👩‍💻")]
        [InlineData("☁️", "☁️")]
        public void An_emoji_or_a_known_name_has_a_glyph(string icon, string expected) =>
            Assert.Equal(expected, ProjectIcon.Glyph(icon));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(@"M:\MusicManager\MusicManager.ico")]
        [InlineData("notaname")]
        [InlineData("é/x.png")]
        [InlineData("🎵 and some words")]
        [InlineData("éclair")]
        [InlineData("ü.ico")]
        [InlineData("中文")]
        [InlineData("🎵abc")]
        [InlineData("1")]
        public void Anything_else_has_no_glyph(string icon) => Assert.Null(ProjectIcon.Glyph(icon));

        [Fact]
        public void An_image_path_is_refused_with_the_reason()
        {
            string error = ProjectIcon.ValidateIcon(@"M:\MusicManager\MusicManager.ico");
            Assert.NotNull(error);
            Assert.Contains("Image files are not supported", error);
            Assert.Contains("rocket", error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("🎵")]
        [InlineData("database")]
        public void Blank_emoji_and_known_names_may_be_saved(string icon) => Assert.Null(ProjectIcon.ValidateIcon(icon));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("#4FC3F7")]
        [InlineData("#abc")]
        [InlineData("#4FC3F780")]
        [InlineData("RebeccaPurple")]
        public void Colours_may_be_saved(string color) => Assert.Null(ProjectIcon.ValidateColor(color));

        [Theory]
        [InlineData("4FC3F7")]
        [InlineData("#12")]
        [InlineData("#GGGGGG")]
        [InlineData("blurple")]
        [InlineData("url(file://x)")]
        [InlineData("red; background:url(x)")]
        [InlineData("rgb(300,0,0)")]
        public void Non_colours_are_refused(string color) => Assert.NotNull(ProjectIcon.ValidateColor(color));

        [Theory]
        [InlineData("#4FC3F7", "#4FC3F7")]
        [InlineData(" teal ", "teal")]
        [InlineData("red; background:url(x)", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Only_a_colour_reaches_the_pages(string stored, string expected) =>
            Assert.Equal(expected, ProjectIcon.SafeColor(stored));

        /// <summary>
        /// The icon names are typed out in the form's hint and in update_project's description; each must list
        /// every name <see cref="ProjectIcon.Names"/> knows. Falsified: adding a name to the map without the
        /// hint turned this red.
        /// </summary>
        [Fact]
        public void Every_icon_name_is_listed_in_the_form_hint_and_the_tool_description()
        {
            string root = TestRepoRoot();
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "ProjectSettings", "project-settings.html"));
            string tools = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "mcp", "index.js"));
            string hint = System.Text.RegularExpressions.Regex.Match(form, @"An emoji \(🎵\), or one of: ([^.]+)\.").Groups[1].Value;
            string desc = System.Text.RegularExpressions.Regex.Match(tools, @"a known icon name \(([^)]+)\)").Groups[1].Value;
            Assert.True(hint.Length > 20 && desc.Length > 20, "The hint or the tool description was not found.");
            foreach (var name in ProjectIcon.Names)
            {
                Assert.Contains(" " + name, " " + hint.Replace(",", " "));
                Assert.Contains(" " + name, " " + desc.Replace(",", " "));
            }
        }

        private static string TestRepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "MultiTerminal.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}

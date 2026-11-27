using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GitHub #77: what a project's Icon and Icon colour settings accept and show. Home and the header's
    /// project picker both show <see cref="ProjectIcon.Glyph"/>, and the settings form and update_project both
    /// refuse what <see cref="ProjectIcon.ValidateIcon"/> / <see cref="ProjectIcon.ValidateColor"/> refuse.
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
        [InlineData("rgb(10, 20, 30)")]
        [InlineData("rgba(10,20,30,0.5)")]
        [InlineData("hsl(200 50% 40%)")]
        [InlineData("RebeccaPurple")]
        public void Colours_may_be_saved(string color) => Assert.Null(ProjectIcon.ValidateColor(color));

        [Theory]
        [InlineData("4FC3F7")]
        [InlineData("#12")]
        [InlineData("#GGGGGG")]
        [InlineData("blurple")]
        [InlineData("url(file://x)")]
        [InlineData("red; background:url(x)")]
        public void Non_colours_are_refused(string color) => Assert.NotNull(ProjectIcon.ValidateColor(color));
    }
}

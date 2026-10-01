using MultiTerminal.Dialogs;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The save rule for the Settings dialog's masked secret boxes (task 3f0f6983). The dialog's
    /// focus handling only makes a bad value rarer; this rule is what guarantees the placeholder
    /// is never written back as, or into, a stored secret.
    /// </summary>
    public class SecretBoxRulesTests
    {
        private const string Placeholder = "••••••••";

        [Theory]
        [InlineData(false, "real-key", false)]                       // untouched box: never written
        [InlineData(true, "real-key", true)]
        [InlineData(true, "", true)]                                 // emptied box: clears the secret
        [InlineData(true, "••••••••", false)]                        // bare placeholder
        [InlineData(true, "••••••••real-key", false)]                // pasted after the dots
        [InlineData(true, "real••••••••-key", false)]                // pasted into the middle
        public void Secret_box_never_saves_the_placeholder(bool dirty, string value, bool expected)
        {
            Assert.Equal(expected, SecretBoxRules.ShouldWrite(dirty, value, Placeholder));
        }
    }
}

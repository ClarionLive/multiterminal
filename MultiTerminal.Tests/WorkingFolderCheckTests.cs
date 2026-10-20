using System;
using System.Collections.Generic;
using MultiTerminal.Dialogs;
using Xunit;
using Check = MultiTerminal.Dialogs.IdentityPickerDialog.WorkingFolderCheck;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GH #44: the Launch Claude dialog offers to create a working folder that does not exist yet,
    /// but only for a fully qualified path on a local drive that exists. Everything else keeps the
    /// "Please select a valid working folder" error. The directory probe is injected, so these facts
    /// touch no disk. What they do NOT cover: the dialog's Yes/No prompt and the CreateDirectory call.
    /// </summary>
    public sealed class WorkingFolderCheckTests
    {
        // Only the C: drive and C:\existing exist.
        private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\", @"C:\existing",
        };

        private static Check Classify(string folder) =>
            IdentityPickerDialog.ClassifyWorkingFolder(folder, Existing.Contains);

        [Fact]
        public void An_existing_folder_is_accepted_as_is()
        {
            Assert.Equal(Check.Exists, Classify(@"C:\existing"));
        }

        [Theory]
        [InlineData(@"C:\new")]
        [InlineData(@"C:\existing\new\deeper")]
        public void A_missing_folder_on_an_existing_drive_is_offered_for_creation(string folder)
        {
            Assert.Equal(Check.CanCreate, Classify(folder));
        }

        [Fact]
        public void A_missing_folder_on_a_drive_that_does_not_exist_is_invalid()
        {
            Assert.Equal(Check.Invalid, Classify(@"Q:\new"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("relative\\folder")]
        [InlineData(@"\rooted-but-no-drive")]
        [InlineData(@"C:drive-relative")]
        public void Blank_or_not_fully_qualified_paths_are_invalid(string folder)
        {
            Assert.Equal(Check.Invalid, Classify(folder));
        }

        [Fact]
        public void A_missing_unc_folder_is_never_offered_for_creation()
        {
            // The share's root is reported as existing, so only the UNC rule can make this Invalid.
            var withShare = new HashSet<string>(Existing, StringComparer.OrdinalIgnoreCase) { @"\\server\share\" };
            Assert.Equal(Check.Invalid,
                IdentityPickerDialog.ClassifyWorkingFolder(@"\\server\share\new", withShare.Contains));
        }
    }
}

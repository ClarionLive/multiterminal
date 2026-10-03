using System.Collections.Generic;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 9f95ab0c: the pure "is there already a project at this folder?" check behind New Project.
    /// The broker-level behaviour (refuse / adopt without overwriting) is in
    /// <see cref="NewProjectExistingFolderBrokerTests"/>.
    /// </summary>
    public class ExistingProjectDetectorTests
    {
        private const string Folder = @"H:\Projects\TestB";

        private static Project Json(string id, string name) => new Project { Id = id, Name = name, Path = Folder };

        [Fact]
        public void ProjectJson_only_is_detected()
        {
            var match = ExistingProjectDetector.Detect(Folder, Json("ac8a793b", "TestB"), new List<(string, string, string)>());

            Assert.Equal(ExistingProjectSource.ProjectJson, match.Source);
            Assert.Equal("ac8a793b", match.ProjectId);
            Assert.Equal("TestB", match.ProjectName);
        }

        // The case the old project.json-only check could not see: a database row pointing at the folder,
        // reached through a trailing separator and a different letter case.
        [Theory]
        [InlineData(@"H:\Projects\TestB\")]
        [InlineData(@"h:\projects\testb")]
        [InlineData(@"H:/Projects/TestB/")]
        [InlineData(@"  H:\PROJECTS\TestB\\  ")]
        public void DatabaseRow_only_is_detected_through_separator_and_case_variants(string picked)
        {
            var rows = new List<(string, string, string)> { ("d08313df", "Testing", Folder), ("ffff0000", "Other", @"H:\Projects\TestBB") };

            var match = ExistingProjectDetector.Detect(picked, null, rows);

            Assert.Equal(ExistingProjectSource.DatabaseRow, match.Source);
            Assert.Equal("d08313df", match.ProjectId);
            Assert.Equal("Testing", match.ProjectName);
            Assert.Equal(new[] { "d08313df" }, match.DatabaseIds);
        }

        [Fact]
        public void Sibling_folder_with_a_shared_prefix_is_not_a_match()
        {
            var rows = new List<(string, string, string)> { ("ffff0000", "Other", @"H:\Projects\TestBB") };

            Assert.False(ExistingProjectDetector.Detect(Folder, null, rows).Exists);
        }

        [Fact]
        public void Both_prefers_the_json_id_and_the_database_name()
        {
            var rows = new List<(string, string, string)> { ("ac8a793b", "TestB (renamed in DB)", Folder + @"\") };

            var match = ExistingProjectDetector.Detect(Folder, Json("ac8a793b", "TestB"), rows);

            Assert.Equal(ExistingProjectSource.Both, match.Source);
            Assert.Equal("ac8a793b", match.ProjectId);
            Assert.Equal("TestB (renamed in DB)", match.ProjectName);
        }

        [Fact]
        public void Same_folder_database_pair_resolves_to_the_lowest_id_every_time()
        {
            var rows = new List<(string, string, string)> { ("d08313df", "Testing", Folder), ("25760f44", "mt-stress-fixture", Folder) };

            var match = ExistingProjectDetector.Detect(Folder, null, rows);

            Assert.Equal("25760f44", match.ProjectId);
            Assert.Equal(2, match.DatabaseIds.Count);
        }

        [Fact]
        public void Drive_root_keeps_its_separator()
        {
            Assert.Equal(@"C:\", ExistingProjectDetector.NormalizePath(@"C:\"));
            Assert.True(ExistingProjectDetector.PathsEqual(@"C:\", @"c:/"));
        }

        [Fact]
        public void Free_folder_creates_without_asking()
        {
            bool asked = false;

            var decision = ExistingProjectDetector.Decide(ExistingProjectMatch.None, _ => { asked = true; return NewProjectFolderDecision.OpenExisting; });

            Assert.Equal(NewProjectFolderDecision.CreateNew, decision);
            Assert.False(asked);
        }

        // No second project on one folder (Owner decision): whatever the prompt answers, an occupied
        // folder never yields CreateNew.
        [Theory]
        [InlineData(NewProjectFolderDecision.OpenExisting, NewProjectFolderDecision.OpenExisting)]
        [InlineData(NewProjectFolderDecision.RenameExisting, NewProjectFolderDecision.RenameExisting)]
        [InlineData(NewProjectFolderDecision.ChooseDifferentFolder, NewProjectFolderDecision.ChooseDifferentFolder)]
        [InlineData(NewProjectFolderDecision.Cancel, NewProjectFolderDecision.Cancel)]
        [InlineData(NewProjectFolderDecision.CreateNew, NewProjectFolderDecision.Cancel)]
        public void Occupied_folder_always_asks_and_never_creates(NewProjectFolderDecision answer, NewProjectFolderDecision expected)
        {
            var occupied = ExistingProjectDetector.Detect(Folder, Json("ac8a793b", "TestB"), null);
            bool asked = false;

            var decision = ExistingProjectDetector.Decide(occupied, _ => { asked = true; return answer; });

            Assert.True(asked);
            Assert.Equal(expected, decision);
        }

        // Pipeline Run 1: a copied/cloned/moved folder (or a hostile file) whose project.json carries the
        // id of a project registered at ANOTHER folder. Open would launch that folder; Rename would rename it.
        [Fact]
        public void Json_id_registered_at_another_folder_is_reported_and_not_openable()
        {
            var rows = new List<(string, string, string)> { ("ac8a793b", "TestB", @"H:\Projects\Original") };

            var match = ExistingProjectDetector.Detect(Folder, Json("ac8a793b", "TestB"), rows);

            Assert.True(match.Exists);
            Assert.Equal(ExistingProjectProblem.IdRegisteredElsewhere, match.Problem);
            Assert.Equal(@"H:\Projects\Original", match.OtherPath);
            Assert.False(match.CanOpenOrRename);
            Assert.Contains("copy of project 'TestB'", ExistingProjectDetector.DescribeProblem(match));
        }

        [Fact]
        public void Json_id_registered_at_this_folder_via_a_variant_path_is_openable()
        {
            var rows = new List<(string, string, string)> { ("ac8a793b", "TestB", @"h:\projects\testb\") };

            var match = ExistingProjectDetector.Detect(Folder, Json("ac8a793b", "TestB"), rows);

            Assert.Equal(ExistingProjectProblem.None, match.Problem);
            Assert.True(match.CanOpenOrRename);
        }

        // A project.json that exists but does not parse (or has no id) still occupies the folder.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Unreadable_project_json_occupies_the_folder(string id)
        {
            var parsed = id == null ? null : Json(id, "NoId");

            var match = ExistingProjectDetector.Detect(Folder, projectFileExists: true, parsed, new List<(string, string, string)>());

            Assert.True(match.Exists);
            Assert.Equal(ExistingProjectProblem.UnreadableProjectFile, match.Problem);
            Assert.False(match.CanOpenOrRename);
        }

        [Fact]
        public void Json_id_differing_from_this_folders_row_is_a_conflict()
        {
            var rows = new List<(string, string, string)> { ("d08313df", "Testing", Folder) };

            var match = ExistingProjectDetector.Detect(Folder, Json("ac8a793b", "TestB"), rows);

            Assert.Equal(ExistingProjectProblem.IdConflict, match.Problem);
            Assert.Equal(new[] { "d08313df" }, match.DatabaseIds);
            Assert.False(match.CanOpenOrRename);
        }

        // Whatever the prompt answers, a problem folder never yields Open or Rename.
        [Theory]
        [InlineData(NewProjectFolderDecision.OpenExisting, NewProjectFolderDecision.Cancel)]
        [InlineData(NewProjectFolderDecision.RenameExisting, NewProjectFolderDecision.Cancel)]
        [InlineData(NewProjectFolderDecision.ChooseDifferentFolder, NewProjectFolderDecision.ChooseDifferentFolder)]
        [InlineData(NewProjectFolderDecision.Cancel, NewProjectFolderDecision.Cancel)]
        public void Problem_folder_never_opens_or_renames(NewProjectFolderDecision answer, NewProjectFolderDecision expected)
        {
            var rows = new List<(string, string, string)> { ("ac8a793b", "TestB", @"H:\Projects\Original") };
            var copied = ExistingProjectDetector.Detect(Folder, Json("ac8a793b", "TestB"), rows);

            Assert.Equal(expected, ExistingProjectDetector.Decide(copied, _ => answer));
        }
    }
}

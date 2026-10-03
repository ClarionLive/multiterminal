using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MultiTerminal.Dialogs;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;
using FileProject = MultiTerminal.Models.Project;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 9f95ab0c, pipeline Run 2 (folds in 2e297688): the Project Manager's New Project goes through
    /// the guarded broker path; occupancy that cannot be determined fails closed; a failed project.json
    /// write rolls the create back; a row written behind the broker's back can be renamed after Open;
    /// one folder reached by two spellings is not "a copy". Real MessageBroker + ProjectService on temp
    /// SQLite and temp folders.
    /// </summary>
    public sealed class NewProjectRun2Tests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;
        private readonly string _root;
        private readonly string _folder;
        private readonly Func<string, string> _originalResolver;

        public NewProjectRun2Tests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_np2_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_np2_msg_{stamp}.db");
            _root = Path.Combine(Path.GetTempPath(), $"mt_np2_dir_{stamp}");
            _folder = Path.Combine(_root, "TestB");
            Directory.CreateDirectory(_folder);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
            _originalResolver = ExistingProjectDetector.FinalPathResolver;
        }

        public void Dispose()
        {
            ExistingProjectDetector.FinalPathResolver = _originalResolver;
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _dbPath, _msgDbPath })
            {
                foreach (var f in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            GC.SuppressFinalize(this);
        }

        private static FileProject SeedTestB(ProjectService service, string folder)
        {
            var p = FileProject.Create("TestB", folder);
            p.Id = "ac8a793b";
            p.Description = "The original description";
            service.SaveProject(p);
            return p;
        }

        private static ConcurrentDictionary<string, MultiTerminal.MCPServer.Models.Project> BrokerCache(MessageBroker broker)
            => (ConcurrentDictionary<string, MultiTerminal.MCPServer.Models.Project>)typeof(MessageBroker)
                .GetField("_projects", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(broker);

        // ---- Project Manager door (2e297688) ----

        [Fact]
        public void Project_manager_create_on_an_occupied_folder_is_refused_and_writes_nothing()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            SeedTestB(service, _folder);
            string jsonPath = Path.Combine(_folder, ".claude", "project.json");
            byte[] before = File.ReadAllBytes(jsonPath);
            using var broker = new MessageBroker { ProjectService = service };
            var dialogProject = new FileProject { Id = "dialog-guid", Name = "TestC", Path = _folder };

            string refusal = ProjectManagerDialog.CreateThroughBroker(broker, dialogProject);

            Assert.NotNull(refusal);
            Assert.Contains("ac8a793b", refusal);
            Assert.Equal("dialog-guid", dialogProject.Id);
            Assert.Equal(before, File.ReadAllBytes(jsonPath));
            Assert.Single(db.GetAllProjects());
            Assert.Equal("TestB", db.GetRichProject("ac8a793b").Name);
        }

        [Fact]
        public void Project_manager_create_on_a_free_folder_creates_one_project_with_the_brokers_id()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            var dialogProject = new FileProject { Id = "dialog-guid", Name = "Fresh", Path = _folder };

            string refusal = ProjectManagerDialog.CreateThroughBroker(broker, dialogProject);

            Assert.Null(refusal);
            Assert.NotEqual("dialog-guid", dialogProject.Id);
            Assert.Equal(dialogProject.Id, service.LoadProject(_folder).Id);
            Assert.Single(db.GetAllProjects());
        }

        [Fact]
        public void Project_manager_create_without_a_broker_refuses()
        {
            var dialogProject = new FileProject { Id = "dialog-guid", Name = "Fresh", Path = _folder };

            Assert.NotNull(ProjectManagerDialog.CreateThroughBroker(null, dialogProject));
            Assert.False(File.Exists(Path.Combine(_folder, ".claude", "project.json")));
        }

        // ---- Indeterminate occupancy ----

        [Fact]
        public void Indeterminate_project_file_is_refused_by_every_create_path()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service, ProjectFileProbe = _ => ProjectFileState.Indeterminate };

            var create = broker.CreateProject("TestC", null, "test", _folder);
            var reuse = broker.CreateProject("TestC", null, "test", _folder, allowReuseExisting: true);
            var open = broker.RegisterExistingProject(_folder, "test");

            Assert.False(create.Success);
            Assert.True(create.FolderOccupied);
            Assert.Contains("can't read the .claude folder", create.Error);
            Assert.False(reuse.Success);
            Assert.False(open.Success);
            Assert.Empty(db.GetAllProjects());
            Assert.False(File.Exists(Path.Combine(_folder, ".claude", "project.json")));
        }

        // The real probe against a real access-denied .claude folder (a Deny ListDirectory ACE for the
        // current user). File.Exists alone answers false here, exactly like an empty folder.
        [Fact]
        public void Probe_reports_indeterminate_for_an_unlistable_claude_folder()
        {
            string claude = Path.Combine(_folder, ".claude");
            Directory.CreateDirectory(claude);
            var dir = new DirectoryInfo(claude);
            var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory, AccessControlType.Deny);
            var acl = dir.GetAccessControl();
            acl.AddAccessRule(deny);
            dir.SetAccessControl(acl);
            try
            {
                Assert.False(File.Exists(Path.Combine(claude, "project.json")));
                Assert.Equal(ProjectFileState.Indeterminate, ExistingProjectDetector.ProbeProjectFile(_folder));
            }
            finally
            {
                acl = dir.GetAccessControl();
                acl.RemoveAccessRule(deny);
                dir.SetAccessControl(acl);
            }
        }

        [Fact]
        public void Probe_confirms_absent_and_present()
        {
            Assert.Equal(ProjectFileState.Absent, ExistingProjectDetector.ProbeProjectFile(Path.Combine(_root, "does-not-exist")));
            Assert.Equal(ProjectFileState.Absent, ExistingProjectDetector.ProbeProjectFile(_folder));
            Directory.CreateDirectory(Path.Combine(_folder, ".claude"));
            Assert.Equal(ProjectFileState.Absent, ExistingProjectDetector.ProbeProjectFile(_folder));
            File.WriteAllText(Path.Combine(_folder, ".claude", "project.json"), "{}");
            Assert.Equal(ProjectFileState.Present, ExistingProjectDetector.ProbeProjectFile(_folder));
        }

        [Fact]
        public void Indeterminate_is_occupied_and_not_openable()
        {
            var match = ExistingProjectDetector.Detect(_folder, ProjectFileState.Indeterminate, null, new List<(string, string, string)>());

            Assert.True(match.Exists);
            Assert.Equal(ExistingProjectProblem.IndeterminateProjectFile, match.Problem);
            Assert.False(match.CanOpenOrRename);
        }

        // ---- Failed project.json write rolls the create back ----

        // A FILE named ".claude" makes ProjectService.SaveProject's CreateDirectory throw after the row insert.
        [Fact]
        public void Failed_project_json_write_rolls_back_the_row_and_the_cache()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            string blocker = Path.Combine(_folder, ".claude");
            File.WriteAllText(blocker, "not a directory");
            using var broker = new MessageBroker { ProjectService = service };
            int cachedBefore = BrokerCache(broker).Count;

            var result = broker.CreateProject("Doomed", null, "test", _folder);

            Assert.False(result.Success);
            Assert.Contains("not created", result.Error);
            Assert.Empty(db.GetAllProjects());
            Assert.Equal(cachedBefore, BrokerCache(broker).Count);
            Assert.Equal("not a directory", File.ReadAllText(blocker));
        }

        // ---- Open, then Rename, a row written behind the broker's back ----

        [Fact]
        public void Row_written_after_the_broker_started_can_be_opened_then_renamed()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            // Written behind the broker (as the start screen or EditProjectDialog do): DB row only.
            var row = FileProject.Create("Behind", _folder);
            row.Id = "b0b0b0b0";
            db.SaveRichProject(row);

            var open = broker.RegisterExistingProject(_folder, "test");
            var rename = broker.UpdateProject("b0b0b0b0", "Renamed", null, "test");

            Assert.True(open.Success, open.Error);
            Assert.True(rename.Success, rename.Error);
            Assert.Equal("Renamed", db.GetRichProject("b0b0b0b0").Name);
        }

        // ---- Auto-registration of a discovered folder ----

        [Fact]
        public void Auto_registration_skips_a_copy_whose_id_belongs_to_another_folder()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            SeedTestB(service, _folder);
            string copy = Path.Combine(_root, "TestB-copy");
            Directory.CreateDirectory(Path.Combine(copy, ".claude"));
            File.Copy(Path.Combine(_folder, ".claude", "project.json"), Path.Combine(copy, ".claude", "project.json"));

            bool registered = service.AutoRegisterDiscoveredProject(copy, service.LoadProject(copy));

            Assert.False(registered);
            Assert.True(ExistingProjectDetector.PathsEqual(_folder, db.GetRichProject("ac8a793b").Path));
        }

        [Fact]
        public void Auto_registration_registers_an_unregistered_folder_under_its_own_id()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            Directory.CreateDirectory(Path.Combine(_folder, ".claude"));
            File.WriteAllText(Path.Combine(_folder, ".claude", "project.json"), "{\"id\": \"c0ffee00\", \"name\": \"Found\"}");

            Assert.True(service.AutoRegisterDiscoveredProject(_folder, service.LoadProject(_folder)));
            Assert.Equal("Found", db.GetRichProject("c0ffee00").Name);
        }

        // ---- One folder, two spellings ----

        // A directory junction needs no privilege. Detect must see the folder's own project.json, reached
        // through the junction, as the folder's own, not as "a copy" of the project registered at the target.
        [Fact]
        public void Junction_to_the_registered_folder_is_not_a_copy()
        {
            string link = Path.Combine(_root, "TestB-link");
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_folder}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            mklink.WaitForExit(10000);
            Assert.True(Directory.Exists(link), "junction was not created");
            try
            {
                var json = new FileProject { Id = "ac8a793b", Name = "TestB", Path = link };
                var rows = new List<(string, string, string)> { ("ac8a793b", "TestB", _folder) };

                var match = ExistingProjectDetector.Detect(link, json, rows);

                Assert.Equal(ExistingProjectProblem.None, match.Problem);
                Assert.True(match.CanOpenOrRename);
            }
            finally
            {
                Directory.Delete(link); // removes the junction, not the target
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
        private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
        private static extern uint GetLongPathName(string shortPath, [Out] char[] longPath, uint length);

        // 8.3 short name vs long name. NOT evidence for FinalPathResolver: Path.GetFullPath already expands
        // short names on .NET, so this stays green with the resolver removed (falsification H7, Run 2; the
        // spellings were confirmed to differ on the dev box). It pins the end-to-end property only. Where a
        // volume generates no 8.3 names the two spellings are identical and the check is trivially true.
        [Fact]
        public void Short_and_long_spellings_of_one_folder_are_equal()
        {
            string longDir = Path.Combine(_root, "A Long Folder Name With Spaces");
            Directory.CreateDirectory(longDir);
            var buffer = new char[1024];
            string longForm = new string(buffer, 0, (int)GetLongPathName(longDir, buffer, (uint)buffer.Length));
            string shortForm = new string(buffer, 0, (int)GetShortPathName(longDir, buffer, (uint)buffer.Length));

            Assert.True(ExistingProjectDetector.PathsEqual(shortForm, longForm), $"'{shortForm}' vs '{longForm}'");
        }

        // Pins that the comparison goes through FinalPathResolver at all (the junction test above pins that
        // the production resolver follows links).
        [Fact]
        public void Resolver_is_consulted_for_existing_folders()
        {
            string alias = Path.Combine(_root, "Alias");
            Directory.CreateDirectory(alias);
            ExistingProjectDetector.FinalPathResolver = p => string.Equals(p, alias, StringComparison.OrdinalIgnoreCase) ? _folder : null;

            Assert.True(ExistingProjectDetector.PathsEqual(alias, _folder));
        }
    }
}

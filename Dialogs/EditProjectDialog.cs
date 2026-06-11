using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog for creating or editing a project.
    /// Supports legacy mode (Project object) and rich mode (projectId + ProjectDatabase).
    /// Rich mode exposes all 23 project columns across 6 tabs and editable association DataGridViews.
    /// </summary>
    public partial class EditProjectDialog : Form
    {
        private readonly TerminalTheme _theme;
        private readonly bool _isEditMode;
        private readonly ProjectDatabase _projectDb;
        private readonly ProjectContextService _contextService;

        // Loaded association data (for diff-save)
        private List<ProjectAgent> _originalAgents = new List<ProjectAgent>();
        private List<ProjectMcpServer> _originalMcpServers = new List<ProjectMcpServer>();
        private List<ProjectSpecialistAgent> _originalSpecialists = new List<ProjectSpecialistAgent>();
        private List<ProjectPath> _originalPaths = new List<ProjectPath>();
        private List<ProjectPromptEntry> _originalPrompts = new List<ProjectPromptEntry>();
        private List<ProjectSkill> _originalSkills = new List<ProjectSkill>();

        private Project _workingProject;

        // Controls — added to Controls collection via tab pages / parent form and auto-disposed by base Form.Dispose().
#pragma warning disable CA2213
        // Tab 1 - General
        private TextBox nameTextBox;
        private TextBox descriptionTextBox;
        private ComboBox projectTypeComboBox;
        private TextBox currentVersionTextBox;
        private TextBox changeLogTextBox;
        private TextBox iconTextBox;
        private TextBox iconColorTextBox;
        private CheckBox isPinnedCheckBox;

        // Tab 2 - Paths & Commands
        private TextBox pathTextBox;
        private Button pathBrowseButton;
        private TextBox sourcePathTextBox;
        private Button sourcePathBrowseButton;
        private TextBox deployPathTextBox;
        private Button deployPathBrowseButton;
        private TextBox buildOutputPathTextBox;
        private Button buildOutputPathBrowseButton;
        private TextBox buildCommandTextBox;
        private TextBox deployCommandTextBox;
        private TextBox launchCommandTextBox;

        // Tab 3 - Git
        private TextBox gitRepoUrlTextBox;
        private TextBox gitDefaultBranchTextBox;
        private CheckBox gitAutoCommitCheckBox;
        private ComboBox sourceControlAccountComboBox;

        // Tab 4 - Agents
        private DataGridView agentsGridView;
        private Button addAgentButton;
        private Button removeAgentButton;
        private DataGridView specialistsGridView;
        private Button addSpecialistButton;
        private Button removeSpecialistButton;

        // Tab 5 - MCP & Skills
        private DataGridView mcpGridView;
        private Button addMcpButton;
        private Button removeMcpButton;
        private DataGridView skillsGridView;
        private Button addSkillButton;
        private Button removeSkillButton;

        // Tab 6 - Prompts
        private DataGridView promptsGridView;
        private Button addPromptButton;
        private Button removePromptButton;

        private Button saveButton;
        private Button cancelButton;
        private TabControl tabControl;
#pragma warning restore CA2213

        // Track which tabs the user has visited to avoid reading stale textbox values
        private readonly HashSet<int> _visitedTabs = new HashSet<int> { 0 };

        // Legacy properties for backward compatibility with ProjectManagerDialog
        public string ProjectName
        {
            get => nameTextBox?.Text?.Trim() ?? string.Empty;
            set { if (nameTextBox != null) nameTextBox.Text = value; }
        }

        public string ProjectPath
        {
            get => pathTextBox?.Text?.Trim() ?? string.Empty;
            set { if (pathTextBox != null) pathTextBox.Text = value; }
        }

        public string Description
        {
            get => descriptionTextBox?.Text?.Trim() ?? string.Empty;
            set { if (descriptionTextBox != null) descriptionTextBox.Text = value; }
        }

        public string ChangeLog
        {
            get => changeLogTextBox?.Text ?? string.Empty;
            set { if (changeLogTextBox != null) changeLogTextBox.Text = value; }
        }

        public bool IsEditMode => _isEditMode;

        /// <summary>Gets the resulting Project after OK is clicked (rich mode only).</summary>
        public Project ResultProject { get; private set; }

        // ── Legacy constructor: create new project ────────────────────────────
        public EditProjectDialog(TerminalTheme theme)
        {
            _theme = theme ?? TerminalTheme.Dark;
            _isEditMode = false;
            _projectDb = null;
            _contextService = null;
            InitializeComponent();
            ApplyTheme();
            Text = "New Project";
            saveButton.Text = "Create";
        }

        // ── Legacy constructor: edit existing project ─────────────────────────
        public EditProjectDialog(Project project, TerminalTheme theme)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            _theme = theme ?? TerminalTheme.Dark;
            _isEditMode = true;
            _projectDb = null;
            _contextService = null;
            _workingProject = project;
            InitializeComponent();
            ApplyTheme();
            Text = "Edit Project";
            saveButton.Text = "Save";
            PopulateFromProject(project);
            pathTextBox.ReadOnly = true;
            pathBrowseButton.Enabled = false;
        }

        // ── Rich constructor: edit by projectId (full 6-tab experience) ───────
        public EditProjectDialog(string projectId, ProjectDatabase projectDb, TerminalTheme theme)
        {
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentNullException(nameof(projectId));
            _projectDb = projectDb ?? throw new ArgumentNullException(nameof(projectDb));
            _theme = theme ?? TerminalTheme.Dark;
            _isEditMode = true;
            _contextService = new ProjectContextService(_projectDb);
            InitializeComponent();
            ApplyTheme();
            Text = "Edit Project";
            saveButton.Text = "Save";
            LoadFromDatabase(projectId);
        }

        // ── Rich constructor: create new project with database ────────────────
        public EditProjectDialog(ProjectDatabase projectDb, TerminalTheme theme)
        {
            _projectDb = projectDb ?? throw new ArgumentNullException(nameof(projectDb));
            _theme = theme ?? TerminalTheme.Dark;
            _isEditMode = false;
            _contextService = new ProjectContextService(_projectDb);
            InitializeComponent();
            ApplyTheme();
            Text = "New Project";
            saveButton.Text = "Create";
        }

        // ── Rich constructor overloads without explicit theme (defaults to Dark) ─

        /// <summary>Creates a new project with database support, using the default dark theme.</summary>
        public EditProjectDialog(ProjectDatabase projectDb)
            : this(projectDb, TerminalTheme.Dark) { }

        /// <summary>Edits a project by ID with database support, using the default dark theme.</summary>
        public EditProjectDialog(string projectId, ProjectDatabase projectDb)
            : this(projectId, projectDb, TerminalTheme.Dark) { }

        // ─────────────────────────────────────────────────────────────────────
        // Data loading
        // ─────────────────────────────────────────────────────────────────────

        private void LoadFromDatabase(string projectId)
        {
            var context = _contextService?.GetProjectContext(projectId);
            if (context?.Project == null)
            {
                MessageBox.Show("Project not found: " + projectId, "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _workingProject = context.Project;
            PopulateFromProject(context.Project);
            _originalAgents = context.Agents ?? new List<ProjectAgent>();
            _originalMcpServers = context.McpServers ?? new List<ProjectMcpServer>();
            _originalSpecialists = context.SpecialistAgents ?? new List<ProjectSpecialistAgent>();
            _originalPaths = context.Paths ?? new List<ProjectPath>();
            _originalPrompts = context.Prompts ?? new List<ProjectPromptEntry>();
            _originalSkills = context.Skills ?? new List<ProjectSkill>();
            PopulateAgentsGrid(context.Agents);
            PopulateSpecialistsGrid(context.SpecialistAgents);
            PopulateMcpGrid(context.McpServers);
            PopulateSkillsGrid(context.Skills);
            PopulatePromptsGrid(context.Prompts);
        }

        private void PopulateFromProject(Project project)
        {
            nameTextBox.Text = project.Name ?? string.Empty;
            pathTextBox.Text = project.Path ?? string.Empty;
            sourcePathTextBox.Text = project.SourcePath ?? string.Empty;
            deployPathTextBox.Text = project.DeployPath ?? string.Empty;
            buildOutputPathTextBox.Text = project.BuildOutputPath ?? string.Empty;
            buildCommandTextBox.Text = project.BuildCommand ?? string.Empty;
            deployCommandTextBox.Text = project.DeployCommand ?? string.Empty;
            launchCommandTextBox.Text = project.LaunchCommand ?? string.Empty;
            descriptionTextBox.Text = project.Description ?? string.Empty;
            changeLogTextBox.Text = project.ChangeLog ?? string.Empty;
            currentVersionTextBox.Text = project.CurrentVersion ?? "0.1.0";
            iconTextBox.Text = project.Icon ?? string.Empty;
            iconColorTextBox.Text = project.IconColor ?? string.Empty;
            isPinnedCheckBox.Checked = project.IsPinned;
            gitRepoUrlTextBox.Text = project.GitRepoUrl ?? string.Empty;
            gitDefaultBranchTextBox.Text = project.GitDefaultBranch ?? string.Empty;
            gitAutoCommitCheckBox.Checked = project.GitAutoCommit;
            SelectSourceControlAccount(project.SourceControlAccountId);
            int typeIdx = projectTypeComboBox.FindStringExact(project.ProjectType ?? string.Empty);
            projectTypeComboBox.SelectedIndex = typeIdx >= 0 ? typeIdx : 0;
        }

        /// <summary>
        /// Selects the combo item whose account id matches, falling back to "(None)".
        /// </summary>
        private void SelectSourceControlAccount(string accountId)
        {
            for (int i = 0; i < sourceControlAccountComboBox.Items.Count; i++)
            {
                if (sourceControlAccountComboBox.Items[i] is SourceAccountItem item &&
                    string.Equals(item.Id, accountId, StringComparison.OrdinalIgnoreCase))
                {
                    sourceControlAccountComboBox.SelectedIndex = i;
                    return;
                }
            }
            sourceControlAccountComboBox.SelectedIndex = 0; // (None)
        }

        private void PopulateAgentsGrid(List<ProjectAgent> agents)
        {
            agentsGridView.Rows.Clear();
            if (agents == null) return;
            foreach (var a in agents)
            {
                int r = agentsGridView.Rows.Add(a.AgentName, a.Role, a.PreferredModel);
                agentsGridView.Rows[r].Tag = a.Id;
            }
        }

        private void PopulateSpecialistsGrid(List<ProjectSpecialistAgent> specialists)
        {
            specialistsGridView.Rows.Clear();
            if (specialists == null) return;
            foreach (var s in specialists)
            {
                int r = specialistsGridView.Rows.Add(s.AgentType, s.IsEnabled, s.CustomPrompt);
                specialistsGridView.Rows[r].Tag = s.Id;
            }
        }

        private void PopulateMcpGrid(List<ProjectMcpServer> servers)
        {
            mcpGridView.Rows.Clear();
            if (servers == null) return;
            foreach (var s in servers)
            {
                int r = mcpGridView.Rows.Add(s.ServerName, s.IsEnabled);
                mcpGridView.Rows[r].Tag = s.Id;
            }
        }

        private void PopulateSkillsGrid(List<ProjectSkill> skills)
        {
            skillsGridView.Rows.Clear();
            if (skills == null) return;
            foreach (var s in skills)
            {
                int r = skillsGridView.Rows.Add(s.SkillName, s.IsEnabled);
                skillsGridView.Rows[r].Tag = s.Id;
            }
        }

        private void PopulatePromptsGrid(List<ProjectPromptEntry> prompts)
        {
            promptsGridView.Rows.Clear();
            if (prompts == null) return;
            foreach (var p in prompts)
            {
                int r = promptsGridView.Rows.Add(p.PromptType, p.PromptText, p.DisplayOrder);
                promptsGridView.Rows[r].Tag = p.Id;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Browse buttons
        // ─────────────────────────────────────────────────────────────────────

        private void BrowseFolderInto(TextBox target, string description)
        {
            using var dlg = new FolderBrowserDialog { Description = description, ShowNewFolderButton = true };
            if (!string.IsNullOrEmpty(target.Text) && Directory.Exists(target.Text))
                dlg.SelectedPath = target.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                target.Text = dlg.SelectedPath;
                if (target == pathTextBox && string.IsNullOrWhiteSpace(nameTextBox.Text))
                    nameTextBox.Text = System.IO.Path.GetFileName(dlg.SelectedPath);
            }
        }

        private void PathBrowseButton_Click(object sender, EventArgs e) => BrowseFolderInto(pathTextBox, "Select project folder");
        private void SourcePathBrowseButton_Click(object sender, EventArgs e) => BrowseFolderInto(sourcePathTextBox, "Select source folder");
        private void DeployPathBrowseButton_Click(object sender, EventArgs e) => BrowseFolderInto(deployPathTextBox, "Select deploy folder");
        private void BuildOutputPathBrowseButton_Click(object sender, EventArgs e) => BrowseFolderInto(buildOutputPathTextBox, "Select build output folder");

        // ─────────────────────────────────────────────────────────────────────
        // Grid row add/remove handlers
        // ─────────────────────────────────────────────────────────────────────

        private void AddAgentButton_Click(object sender, EventArgs e)
        {
            int r = agentsGridView.Rows.Add("NewAgent", "", "");
            agentsGridView.CurrentCell = agentsGridView.Rows[r].Cells[0];
            agentsGridView.BeginEdit(true);
        }

        private void RemoveAgentButton_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in agentsGridView.SelectedRows)
                if (!row.IsNewRow) agentsGridView.Rows.Remove(row);
        }

        private void AddSpecialistButton_Click(object sender, EventArgs e)
        {
            int r = specialistsGridView.Rows.Add("new-specialist", true, "");
            specialistsGridView.CurrentCell = specialistsGridView.Rows[r].Cells[0];
            specialistsGridView.BeginEdit(true);
        }

        private void RemoveSpecialistButton_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in specialistsGridView.SelectedRows)
                if (!row.IsNewRow) specialistsGridView.Rows.Remove(row);
        }

        private void AddMcpButton_Click(object sender, EventArgs e)
        {
            int r = mcpGridView.Rows.Add("new-server", true);
            mcpGridView.CurrentCell = mcpGridView.Rows[r].Cells[0];
            mcpGridView.BeginEdit(true);
        }

        private void RemoveMcpButton_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in mcpGridView.SelectedRows)
                if (!row.IsNewRow) mcpGridView.Rows.Remove(row);
        }

        private void AddSkillButton_Click(object sender, EventArgs e)
        {
            int r = skillsGridView.Rows.Add("new-skill", true);
            skillsGridView.CurrentCell = skillsGridView.Rows[r].Cells[0];
            skillsGridView.BeginEdit(true);
        }

        private void RemoveSkillButton_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in skillsGridView.SelectedRows)
                if (!row.IsNewRow) skillsGridView.Rows.Remove(row);
        }

        private void AddPromptButton_Click(object sender, EventArgs e)
        {
            int r = promptsGridView.Rows.Add("context", "", 0);
            promptsGridView.CurrentCell = promptsGridView.Rows[r].Cells[0];
            promptsGridView.BeginEdit(true);
        }

        private void RemovePromptButton_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in promptsGridView.SelectedRows)
                if (!row.IsNewRow) promptsGridView.Rows.Remove(row);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Save / Cancel
        // ─────────────────────────────────────────────────────────────────────

        private void SaveButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(nameTextBox.Text))
            {
                MessageBox.Show("Please enter a project name.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                nameTextBox.Focus();
                return;
            }

            // Legacy create mode requires a valid path
            if (_projectDb == null && !_isEditMode)
            {
                if (string.IsNullOrWhiteSpace(pathTextBox.Text))
                {
                    MessageBox.Show("Please select a project path.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    pathTextBox.Focus();
                    return;
                }
                if (!Directory.Exists(pathTextBox.Text))
                {
                    MessageBox.Show("The specified path does not exist.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    pathTextBox.Focus();
                    return;
                }
            }

            BuildResultProject();

            if (_projectDb != null)
            {
                try { SaveToDatabase(); }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to save project: " + ex.Message, "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Build and save result
        // ─────────────────────────────────────────────────────────────────────

        private void BuildResultProject()
        {
            if (_workingProject == null)
                _workingProject = new Project { Id = Guid.NewGuid().ToString(), CreatedAt = DateTime.UtcNow };

            // Tab 0 - General (always visited, it's the default selected tab)
            _workingProject.Name = nameTextBox.Text.Trim();
            _workingProject.Description = NullIfEmpty(descriptionTextBox.Text.Trim());
            _workingProject.ChangeLog = NullIfEmpty(changeLogTextBox.Text);
            _workingProject.CurrentVersion = string.IsNullOrWhiteSpace(currentVersionTextBox.Text)
                ? "0.1.0"
                : currentVersionTextBox.Text.Trim();
            _workingProject.Icon = NullIfEmpty(iconTextBox.Text.Trim());
            _workingProject.IconColor = NullIfEmpty(iconColorTextBox.Text.Trim());
            _workingProject.IsPinned = isPinnedCheckBox.Checked;
            string selType = projectTypeComboBox.SelectedItem as string;
            _workingProject.ProjectType = string.IsNullOrWhiteSpace(selType) ? null : selType;

            // Tab 1 - Paths & Commands (only read if user visited this tab)
            if (_visitedTabs.Contains(1))
            {
                _workingProject.Path = pathTextBox.Text.Trim();
                _workingProject.SourcePath = string.IsNullOrWhiteSpace(sourcePathTextBox.Text)
                    ? pathTextBox.Text.Trim()
                    : sourcePathTextBox.Text.Trim();
                _workingProject.DeployPath = NullIfEmpty(deployPathTextBox.Text.Trim());
                _workingProject.BuildOutputPath = NullIfEmpty(buildOutputPathTextBox.Text.Trim());
                _workingProject.BuildCommand = NullIfEmpty(buildCommandTextBox.Text.Trim());
                _workingProject.DeployCommand = NullIfEmpty(deployCommandTextBox.Text.Trim());
                _workingProject.LaunchCommand = NullIfEmpty(launchCommandTextBox.Text.Trim());
            }

            // Tab 2 - Git (only read if user visited this tab)
            if (_visitedTabs.Contains(2))
            {
                _workingProject.GitRepoUrl = NullIfEmpty(gitRepoUrlTextBox.Text.Trim());
                _workingProject.GitDefaultBranch = NullIfEmpty(gitDefaultBranchTextBox.Text.Trim());
                _workingProject.GitAutoCommit = gitAutoCommitCheckBox.Checked;
                _workingProject.SourceControlAccountId =
                    (sourceControlAccountComboBox.SelectedItem as SourceAccountItem)?.Id;
            }

            // Tabs 3-5 (Agents, MCP & Skills, Prompts) use DataGridViews which
            // store values in managed memory — no handle-creation dependency.

            _workingProject.UpdatedAt = DateTime.UtcNow;
            ResultProject = _workingProject;
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private void SaveToDatabase()
        {
            if (ResultProject == null) BuildResultProject();
            _projectDb.SaveRichProject(ResultProject);
            string pid = ResultProject.Id;
            // SaveRichProject COALESCEs source_control_account_id (so stale project.json re-saves
            // can't wipe it), which means a "(None)" clear here would otherwise be ignored. Apply
            // the binding — set OR clear — out-of-band via the dedicated setter so this dialog is
            // the authoritative writer of the project's account selection.
            _projectDb.SetSourceControlAccount(pid, ResultProject.SourceControlAccountId);
            SaveAgentsFromGrid(pid);
            SaveSpecialistsFromGrid(pid);
            SaveMcpFromGrid(pid);
            SaveSkillsFromGrid(pid);
            SavePromptsFromGrid(pid);
        }

        private void SaveAgentsFromGrid(string pid)
        {
            var names = new HashSet<string>();
            foreach (DataGridViewRow row in agentsGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string n = row.Cells[0].Value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }
            foreach (var o in _originalAgents)
                if (!names.Contains(o.AgentName)) _projectDb.DeleteProjectAgent(pid, o.AgentName);
            foreach (DataGridViewRow row in agentsGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string n = row.Cells[0].Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(n)) continue;
                _projectDb.SaveProjectAgent(new ProjectAgent { ProjectId = pid, AgentName = n, Role = row.Cells[1].Value?.ToString()?.Trim(), PreferredModel = row.Cells[2].Value?.ToString()?.Trim() });
            }
        }

        private void SaveSpecialistsFromGrid(string pid)
        {
            var types = new HashSet<string>();
            foreach (DataGridViewRow row in specialistsGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string t = row.Cells[0].Value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(t)) types.Add(t);
            }
            foreach (var o in _originalSpecialists)
                if (!types.Contains(o.AgentType)) _projectDb.DeleteProjectSpecialistAgent(pid, o.AgentType);
            foreach (DataGridViewRow row in specialistsGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string t = row.Cells[0].Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                bool en = true;
                if (row.Cells[1].Value is bool b) en = b;
                _projectDb.SaveProjectSpecialistAgent(new ProjectSpecialistAgent { ProjectId = pid, AgentType = t, IsEnabled = en, CustomPrompt = row.Cells[2].Value?.ToString()?.Trim() });
            }
        }

        private void SaveMcpFromGrid(string pid)
        {
            var names = new HashSet<string>();
            foreach (DataGridViewRow row in mcpGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string n = row.Cells[0].Value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }
            foreach (var o in _originalMcpServers)
                if (!names.Contains(o.ServerName)) _projectDb.DeleteProjectMcpServer(pid, o.ServerName);
            foreach (DataGridViewRow row in mcpGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string n = row.Cells[0].Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(n)) continue;
                bool en = true;
                if (row.Cells[1].Value is bool b) en = b;
                _projectDb.SaveProjectMcpServer(new ProjectMcpServer { ProjectId = pid, ServerName = n, IsEnabled = en });
            }
        }

        private void SaveSkillsFromGrid(string pid)
        {
            var names = new HashSet<string>();
            foreach (DataGridViewRow row in skillsGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string n = row.Cells[0].Value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }
            foreach (var o in _originalSkills)
                if (!names.Contains(o.SkillName)) _projectDb.DeleteProjectSkill(pid, o.SkillName);
            foreach (DataGridViewRow row in skillsGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string n = row.Cells[0].Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(n)) continue;
                bool en = true;
                if (row.Cells[1].Value is bool b) en = b;
                _projectDb.SaveProjectSkill(new ProjectSkill { ProjectId = pid, SkillName = n, IsEnabled = en });
            }
        }

        private void SavePromptsFromGrid(string pid)
        {
            var ids = new HashSet<int>();
            foreach (DataGridViewRow row in promptsGridView.Rows)
            {
                if (row.IsNewRow) continue;
                if (row.Tag is int id && id > 0) ids.Add(id);
            }
            foreach (var o in _originalPrompts)
                if (!ids.Contains(o.Id)) _projectDb.DeleteProjectPrompt(o.Id);
            foreach (DataGridViewRow row in promptsGridView.Rows)
            {
                if (row.IsNewRow) continue;
                string pt = row.Cells[0].Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(pt)) continue;
                string txt = row.Cells[1].Value?.ToString() ?? string.Empty;
                int ord = 0;
                if (row.Cells[2].Value != null) int.TryParse(row.Cells[2].Value.ToString(), out ord);
                int eid = row.Tag is int rid ? rid : 0;
                _projectDb.SaveProjectPrompt(new ProjectPromptEntry { Id = eid, ProjectId = pid, PromptType = pt, PromptText = txt, DisplayOrder = ord });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Theme
        // ─────────────────────────────────────────────────────────────────────

        private void ApplyTheme()
        {
            BackColor = _theme.ToolbarBackground;
            ForeColor = _theme.ToolbarForeground;
            ApplyThemeToButton(saveButton);
            ApplyThemeToButton(cancelButton);
            foreach (TabPage page in tabControl.TabPages)
            {
                page.BackColor = _theme.ToolbarBackground;
                page.ForeColor = _theme.ToolbarForeground;
                ApplyThemeToControls(page.Controls);
            }
            tabControl.BackColor = _theme.ToolbarBackground;
        }

        private void ApplyThemeToControls(Control.ControlCollection controls)
        {
            foreach (Control ctrl in controls)
            {
                if (ctrl is Label lbl) { lbl.ForeColor = _theme.ToolbarForeground; lbl.BackColor = Color.Transparent; }
                else if (ctrl is TextBox tb) { tb.BackColor = _theme.Background; tb.ForeColor = _theme.Foreground; tb.BorderStyle = BorderStyle.FixedSingle; }
                else if (ctrl is ComboBox cb) { cb.BackColor = _theme.Background; cb.ForeColor = _theme.Foreground; cb.FlatStyle = FlatStyle.Flat; }
                else if (ctrl is CheckBox chk) { chk.ForeColor = _theme.ToolbarForeground; chk.BackColor = Color.Transparent; }
                else if (ctrl is Button btn) { ApplyThemeToButton(btn); }
                else if (ctrl is DataGridView dgv) { ApplyThemeToDataGridView(dgv); }
                else if (ctrl is Panel pnl) { pnl.BackColor = _theme.ToolbarBackground; ApplyThemeToControls(pnl.Controls); }
                else if (ctrl is GroupBox grp) { grp.ForeColor = _theme.ToolbarForeground; grp.BackColor = _theme.ToolbarBackground; ApplyThemeToControls(grp.Controls); }
            }
        }

        private void ApplyThemeToButton(Button button)
        {
            if (_theme.IsDark)
            {
                button.BackColor = Color.FromArgb(62, 62, 66);
                button.ForeColor = Color.White;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(86, 86, 86);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 80, 84);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 90, 94);
            }
            else
            {
                button.BackColor = Color.FromArgb(225, 225, 225);
                button.ForeColor = Color.FromArgb(30, 30, 30);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(173, 173, 173);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 220, 240);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 200, 220);
            }
        }

        private void ApplyThemeToDataGridView(DataGridView dgv)
        {
            dgv.BackgroundColor = _theme.Background;
            dgv.ForeColor = _theme.Foreground;
            dgv.GridColor = _theme.IsDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(200, 200, 200);
            dgv.BorderStyle = BorderStyle.FixedSingle;
            dgv.DefaultCellStyle.BackColor = _theme.Background;
            dgv.DefaultCellStyle.ForeColor = _theme.Foreground;
            dgv.DefaultCellStyle.SelectionBackColor = _theme.SelectionBackground;
            dgv.DefaultCellStyle.SelectionForeColor = _theme.SelectionForeground;
            dgv.ColumnHeadersDefaultCellStyle.BackColor = _theme.ToolbarBackground;
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = _theme.ToolbarForeground;
            dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = _theme.ToolbarBackground;
            dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = _theme.ToolbarForeground;
            dgv.EnableHeadersVisualStyles = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Designer / InitializeComponent
        // ─────────────────────────────────────────────────────────────────────

        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();

            this.tabControl = new TabControl
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(8, 8),
                Name = "tabControl",
                Size = new Size(756, 525),
                TabIndex = 0
            };
            this.tabControl.SelectedIndexChanged += (s, e) => _visitedTabs.Add(tabControl.SelectedIndex);

            this.saveButton = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(590, 545),
                Name = "saveButton",
                Size = new Size(80, 30),
                TabIndex = 1,
                Text = "Save"
            };
            this.saveButton.Click += new EventHandler(this.SaveButton_Click);

            this.cancelButton = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
                Location = new Point(680, 545),
                Name = "cancelButton",
                Size = new Size(80, 30),
                TabIndex = 2,
                Text = "Cancel"
            };
            this.cancelButton.Click += new EventHandler(this.CancelButton_Click);

            var tab1 = new TabPage("General");
            BuildGeneralTab(tab1);
            this.tabControl.TabPages.Add(tab1);

            var tab2 = new TabPage("Paths & Commands");
            BuildPathsTab(tab2);
            this.tabControl.TabPages.Add(tab2);

            var tab3 = new TabPage("Git");
            BuildGitTab(tab3);
            this.tabControl.TabPages.Add(tab3);

            var tab4 = new TabPage("Agents");
            BuildAgentsTab(tab4);
            this.tabControl.TabPages.Add(tab4);

            var tab5 = new TabPage("MCP & Skills");
            BuildMcpSkillsTab(tab5);
            this.tabControl.TabPages.Add(tab5);

            var tab6 = new TabPage("Prompts");
            BuildPromptsTab(tab6);
            this.tabControl.TabPages.Add(tab6);

            this.AcceptButton = this.saveButton;
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new Size(772, 587);
            this.MinimumSize = new Size(620, 520);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimizeBox = false;
            this.Name = "EditProjectDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Edit Project";
            this.Controls.Add(this.tabControl);
            this.Controls.Add(this.saveButton);
            this.Controls.Add(this.cancelButton);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Tab builder methods
        // ─────────────────────────────────────────────────────────────────────

        private void BuildGeneralTab(TabPage tab)
        {
            int lx = 10, fx = 130, y = 14, rh = 30;

            var l1 = MkLbl("Name:", lx, y);
            this.nameTextBox = MkTxt(fx, y, 580);
            this.nameTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += rh;

            var l2 = MkLbl("Description:", lx, y);
            this.descriptionTextBox = MkTxt(fx, y, 580, multiline: true, height: 60);
            this.descriptionTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += 68;

            var l3 = MkLbl("Type:", lx, y);
            this.projectTypeComboBox = new ComboBox
            {
                Location = new Point(fx, y),
                Size = new Size(200, 23),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Name = "projectTypeComboBox"
            };
            this.projectTypeComboBox.Items.AddRange(new object[] { "", "dotnet", "multiterminal", "clarion-com", "clarion-webview2", "clarion", "node", "python", "other" });
            this.projectTypeComboBox.SelectedIndex = 0;
            y += rh;

            var l4 = MkLbl("Version:", lx, y);
            this.currentVersionTextBox = MkTxt(fx, y, 120);
            y += rh;

            var l5 = MkLbl("Icon:", lx, y);
            this.iconTextBox = MkTxt(fx, y, 200);
            y += rh;

            var l6 = MkLbl("Icon Color:", lx, y);
            this.iconColorTextBox = MkTxt(fx, y, 120);
            y += rh;

            this.isPinnedCheckBox = new CheckBox
            {
                Location = new Point(fx, y),
                Size = new Size(120, 20),
                Text = "Pinned",
                Name = "isPinnedCheckBox"
            };
            y += rh + 4;

            var l7 = MkLbl("Change Log:", lx, y);
            this.changeLogTextBox = MkTxt(fx, y, 580, multiline: true, height: 80);
            this.changeLogTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            this.changeLogTextBox.ScrollBars = ScrollBars.Vertical;

            tab.Controls.AddRange(new Control[] {
                l1, this.nameTextBox,
                l2, this.descriptionTextBox,
                l3, this.projectTypeComboBox,
                l4, this.currentVersionTextBox,
                l5, this.iconTextBox,
                l6, this.iconColorTextBox,
                this.isPinnedCheckBox,
                l7, this.changeLogTextBox
            });
        }

        private void BuildPathsTab(TabPage tab)
        {
            int lx = 10, fx = 140, y = 14, rh = 32;

            var l1 = MkLbl("Project Path:", lx, y);
            this.pathTextBox = MkTxt(fx, y, 510);
            this.pathTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.pathBrowseButton = MkBrowse(658, y);
            this.pathBrowseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.pathBrowseButton.Click += PathBrowseButton_Click;
            y += rh;

            var l2 = MkLbl("Source Path:", lx, y);
            this.sourcePathTextBox = MkTxt(fx, y, 510);
            this.sourcePathTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.sourcePathBrowseButton = MkBrowse(658, y);
            this.sourcePathBrowseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.sourcePathBrowseButton.Click += SourcePathBrowseButton_Click;
            y += rh;

            var l3 = MkLbl("Deploy Path:", lx, y);
            this.deployPathTextBox = MkTxt(fx, y, 510);
            this.deployPathTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.deployPathBrowseButton = MkBrowse(658, y);
            this.deployPathBrowseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.deployPathBrowseButton.Click += DeployPathBrowseButton_Click;
            y += rh;

            var l4 = MkLbl("Build Output:", lx, y);
            this.buildOutputPathTextBox = MkTxt(fx, y, 510);
            this.buildOutputPathTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.buildOutputPathBrowseButton = MkBrowse(658, y);
            this.buildOutputPathBrowseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.buildOutputPathBrowseButton.Click += BuildOutputPathBrowseButton_Click;
            y += rh + 8;

            var lsec = MkLbl("Commands:", lx, y);
            lsec.Font = new Font(lsec.Font, FontStyle.Bold);
            y += 22;

            var l5 = MkLbl("Build:", lx, y);
            this.buildCommandTextBox = MkTxt(fx, y, 555);
            this.buildCommandTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += rh;

            var l6 = MkLbl("Deploy:", lx, y);
            this.deployCommandTextBox = MkTxt(fx, y, 555);
            this.deployCommandTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += rh;

            var l7 = MkLbl("Launch:", lx, y);
            this.launchCommandTextBox = MkTxt(fx, y, 555);
            this.launchCommandTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            tab.Controls.AddRange(new Control[] {
                l1, this.pathTextBox, this.pathBrowseButton,
                l2, this.sourcePathTextBox, this.sourcePathBrowseButton,
                l3, this.deployPathTextBox, this.deployPathBrowseButton,
                l4, this.buildOutputPathTextBox, this.buildOutputPathBrowseButton,
                lsec,
                l5, this.buildCommandTextBox,
                l6, this.deployCommandTextBox,
                l7, this.launchCommandTextBox
            });
        }

        private void BuildGitTab(TabPage tab)
        {
            int lx = 10, fx = 140, y = 14, rh = 32;

            var l1 = MkLbl("Repo URL:", lx, y);
            this.gitRepoUrlTextBox = MkTxt(fx, y, 555);
            this.gitRepoUrlTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += rh;

            var l2 = MkLbl("Default Branch:", lx, y);
            this.gitDefaultBranchTextBox = MkTxt(fx, y, 200);
            y += rh;

            this.gitAutoCommitCheckBox = new CheckBox
            {
                Location = new Point(fx, y),
                Size = new Size(250, 20),
                Text = "Auto-commit after milestones",
                Name = "gitAutoCommitCheckBox"
            };
            y += rh + 4;

            var l3 = MkLbl("Source Account:", lx, y);
            this.sourceControlAccountComboBox = new ComboBox
            {
                Location = new Point(fx, y),
                Size = new Size(300, 23),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Name = "sourceControlAccountComboBox"
            };
            PopulateSourceControlAccounts();

            tab.Controls.AddRange(new Control[] {
                l1, this.gitRepoUrlTextBox,
                l2, this.gitDefaultBranchTextBox,
                this.gitAutoCommitCheckBox,
                l3, this.sourceControlAccountComboBox
            });
        }

        /// <summary>
        /// Fills the Source Account combo with a leading "(None)" sentinel followed by all
        /// configured source control accounts. Reuses the ProjectDatabase connection (same
        /// multiterminal.db file) so no second connection is opened. In legacy mode
        /// (_projectDb == null) only the "(None)" option is shown.
        /// </summary>
        private void PopulateSourceControlAccounts()
        {
            this.sourceControlAccountComboBox.Items.Clear();
            this.sourceControlAccountComboBox.Items.Add(SourceAccountItem.None);

            if (_projectDb?.Connection == null) return;

            try
            {
                var service = new SourceControlAccountService(_projectDb.Connection);
                foreach (var account in service.GetAll())
                    this.sourceControlAccountComboBox.Items.Add(new SourceAccountItem(account.Id, account.DisplayName));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[EditProjectDialog] Failed to load source control accounts: {ex.Message}");
            }

            this.sourceControlAccountComboBox.SelectedIndex = 0;
        }

        /// <summary>
        /// Combo item wrapping a source control account id + label. ToString() drives the
        /// display text; the "(None)" sentinel carries a null id meaning "no account assigned".
        /// </summary>
        private sealed class SourceAccountItem
        {
            public static readonly SourceAccountItem None = new SourceAccountItem(null, "(None)");

            public string Id { get; }
            private readonly string _label;

            public SourceAccountItem(string id, string label)
            {
                Id = id;
                _label = string.IsNullOrWhiteSpace(label) ? id : label;
            }

            public override string ToString() => _label;
        }

        private void BuildAgentsTab(TabPage tab)
        {
            var la = MkLbl("Team Agents:", 10, 10);
            la.Font = new Font(la.Font, FontStyle.Bold);

            this.agentsGridView = MkDgv(10, 30, 720, 150, new[]
            {
                ("AgentName", "Agent Name", 200, false),
                ("Role", "Role", 200, false),
                ("Model", "Preferred Model", 200, false)
            });
            this.agentsGridView.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            this.addAgentButton = MkBtn("Add", 10, 186);
            this.addAgentButton.Click += AddAgentButton_Click;
            this.removeAgentButton = MkBtn("Remove", 94, 186);
            this.removeAgentButton.Click += RemoveAgentButton_Click;

            var ls = MkLbl("Specialist Agents:", 10, 220);
            ls.Font = new Font(ls.Font, FontStyle.Bold);

            this.specialistsGridView = MkDgv(10, 240, 720, 130, new[]
            {
                ("AgentType", "Agent Type", 200, false),
                ("IsEnabled", "Enabled", 60, true),
                ("CustomPrompt", "Custom Prompt", 400, false)
            });
            this.specialistsGridView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            this.addSpecialistButton = MkBtn("Add", 10, 376);
            this.addSpecialistButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.addSpecialistButton.Click += AddSpecialistButton_Click;
            this.removeSpecialistButton = MkBtn("Remove", 94, 376);
            this.removeSpecialistButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.removeSpecialistButton.Click += RemoveSpecialistButton_Click;

            tab.Controls.AddRange(new Control[] {
                la, this.agentsGridView,
                this.addAgentButton, this.removeAgentButton,
                ls, this.specialistsGridView,
                this.addSpecialistButton, this.removeSpecialistButton
            });
        }

        private void BuildMcpSkillsTab(TabPage tab)
        {
            var lm = MkLbl("MCP Servers:", 10, 10);
            lm.Font = new Font(lm.Font, FontStyle.Bold);

            this.mcpGridView = MkDgv(10, 30, 720, 150, new[]
            {
                ("ServerName", "Server Name", 500, false),
                ("IsEnabled", "Enabled", 60, true)
            });
            this.mcpGridView.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            this.addMcpButton = MkBtn("Add", 10, 186);
            this.addMcpButton.Click += AddMcpButton_Click;
            this.removeMcpButton = MkBtn("Remove", 94, 186);
            this.removeMcpButton.Click += RemoveMcpButton_Click;

            var lsk = MkLbl("Skills:", 10, 220);
            lsk.Font = new Font(lsk.Font, FontStyle.Bold);

            this.skillsGridView = MkDgv(10, 240, 720, 130, new[]
            {
                ("SkillName", "Skill Name", 500, false),
                ("IsEnabled", "Enabled", 60, true)
            });
            this.skillsGridView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            this.addSkillButton = MkBtn("Add", 10, 376);
            this.addSkillButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.addSkillButton.Click += AddSkillButton_Click;
            this.removeSkillButton = MkBtn("Remove", 94, 376);
            this.removeSkillButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.removeSkillButton.Click += RemoveSkillButton_Click;

            tab.Controls.AddRange(new Control[] {
                lm, this.mcpGridView,
                this.addMcpButton, this.removeMcpButton,
                lsk, this.skillsGridView,
                this.addSkillButton, this.removeSkillButton
            });
        }

        private void BuildPromptsTab(TabPage tab)
        {
            var lp = MkLbl("Project Prompts:", 10, 10);
            lp.Font = new Font(lp.Font, FontStyle.Bold);

            this.promptsGridView = MkDgv(10, 30, 720, 350, new[]
            {
                ("PromptType", "Type", 120, false),
                ("PromptText", "Prompt Text", 500, false),
                ("DisplayOrder", "Order", 60, false)
            });
            this.promptsGridView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            this.addPromptButton = MkBtn("Add", 10, 386);
            this.addPromptButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.addPromptButton.Click += AddPromptButton_Click;
            this.removePromptButton = MkBtn("Remove", 94, 386);
            this.removePromptButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.removePromptButton.Click += RemovePromptButton_Click;

            tab.Controls.AddRange(new Control[] {
                lp, this.promptsGridView,
                this.addPromptButton, this.removePromptButton
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Control factory helpers
        // ─────────────────────────────────────────────────────────────────────

        private static Label MkLbl(string text, int x, int y)
            => new Label { Text = text, AutoSize = true, Location = new Point(x, y + 3) };

        private static TextBox MkTxt(int x, int y, int w, bool multiline = false, int height = 23)
            => new TextBox { Location = new Point(x, y), Size = new Size(w, multiline ? height : 23), Multiline = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None };

        private static Button MkBrowse(int x, int y)
            => new Button { Location = new Point(x, y), Size = new Size(44, 23), Text = "..." };

        private static Button MkBtn(string text, int x, int y)
            => new Button { Location = new Point(x, y), Size = new Size(80, 25), Text = text };

        private static DataGridView MkDgv(int x, int y, int w, int h, (string name, string header, int colW, bool isChk)[] cols)
        {
            var dgv = new DataGridView
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            foreach (var (cn, ch, cw, ic) in cols)
            {
                DataGridViewColumn col = ic
                    ? (DataGridViewColumn)new DataGridViewCheckBoxColumn { Name = cn, HeaderText = ch, Width = cw, FillWeight = cw, MinimumWidth = cw }
                    : new DataGridViewTextBoxColumn { Name = cn, HeaderText = ch, Width = cw, FillWeight = cw };
                dgv.Columns.Add(col);
            }
            return dgv;
        }
    }
}

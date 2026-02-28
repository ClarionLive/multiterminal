using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog for full project management - view all projects, search, sort, pin/unpin, CRUD operations.
    /// </summary>
    public partial class ProjectManagerDialog : Form
    {
        private readonly ProjectService _projectService;
        private readonly ProjectDatabase _projectDatabase;
        private readonly TerminalTheme _theme;
        private List<MultiTerminal.Models.Project> _allProjects;
        private List<MultiTerminal.Models.Project> _filteredProjects;

        // Controls
        private TextBox searchTextBox;
        private Button newProjectButton;
        private DataGridView projectsGridView;
        private Button openButton;
        private Button editButton;
        private Button removeButton;
        private Button deleteConfigButton;
        private Button closeButton;

        /// <summary>
        /// Gets the currently selected rich project.
        /// </summary>
        public MultiTerminal.Models.Project SelectedProject
        {
            get
            {
                if (projectsGridView.SelectedRows.Count > 0)
                {
                    return projectsGridView.SelectedRows[0].Tag as MultiTerminal.Models.Project;
                }
                return null;
            }
        }

        /// <summary>
        /// Gets the currently selected project registry entry (legacy compatibility).
        /// Maps from the rich project record stored in the row tag.
        /// </summary>
        public ProjectRegistryEntry SelectedProjectEntry
        {
            get
            {
                var project = SelectedProject;
                if (project != null)
                {
                    return new ProjectRegistryEntry
                    {
                        Id = project.Id,
                        Name = project.Name,
                        Path = project.Path,
                        IsPinned = project.IsPinned,
                        LastOpenedAt = project.LastOpenedAt
                    };
                }
                return null;
            }
        }

        /// <summary>
        /// Fired when a project is opened (double-click or Open button).
        /// </summary>
        public event EventHandler<ProjectEventArgs> ProjectOpened;

        /// <summary>
        /// Creates a new ProjectManagerDialog instance.
        /// </summary>
        /// <param name="projectService">The project service for CRUD operations.</param>
        /// <param name="projectDatabase">The project database for rich project data.</param>
        /// <param name="theme">The terminal theme to apply.</param>
        public ProjectManagerDialog(ProjectService projectService, ProjectDatabase projectDatabase, TerminalTheme theme)
        {
            _projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
            _projectDatabase = projectDatabase ?? throw new ArgumentNullException(nameof(projectDatabase));
            _theme = theme ?? TerminalTheme.Dark;

            InitializeComponent();
            ApplyTheme();
            LoadProjects();
        }

        /// <summary>
        /// Loads all projects from the database and populates the grid.
        /// Falls back to JSON registry if database returns no results.
        /// </summary>
        private void LoadProjects()
        {
            _allProjects = _projectDatabase.GetAllRichProjects();

            // Fall back to JSON registry if SQLite has no records yet
            if (_allProjects == null || _allProjects.Count == 0)
            {
                var registryEntries = _projectService.GetAllRegisteredProjects();
                _allProjects = registryEntries.Select(e => new MultiTerminal.Models.Project
                {
                    Id = e.Id,
                    Name = e.Name,
                    Path = e.Path,
                    IsPinned = e.IsPinned,
                    LastOpenedAt = e.LastOpenedAt
                }).ToList();
            }

            ApplyFilter();
        }

        /// <summary>
        /// Applies the current search filter to the project list.
        /// </summary>
        private void ApplyFilter()
        {
            string searchText = searchTextBox?.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(searchText))
            {
                _filteredProjects = _allProjects.ToList();
            }
            else
            {
                _filteredProjects = _allProjects
                    .Where(p => p.Name != null && p.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            // Sort: pinned first, then by name
            _filteredProjects = _filteredProjects
                .OrderByDescending(p => p.IsPinned)
                .ThenBy(p => p.Name)
                .ToList();

            PopulateGrid();
        }

        /// <summary>
        /// Populates the DataGridView with the filtered projects.
        /// </summary>
        private void PopulateGrid()
        {
            projectsGridView.Rows.Clear();

            foreach (var project in _filteredProjects)
            {
                int rowIndex = projectsGridView.Rows.Add();
                var row = projectsGridView.Rows[rowIndex];
                row.Tag = project;

                // Pin column (checkbox-like indicator)
                row.Cells["PinColumn"].Value = project.IsPinned ? "*" : "";

                // Name column
                row.Cells["NameColumn"].Value = project.Name ?? "(Unnamed)";

                // Path column
                row.Cells["PathColumn"].Value = TruncatePath(project.Path, 35);
                row.Cells["PathColumn"].ToolTipText = project.Path;

                // Type column
                row.Cells["TypeColumn"].Value = project.ProjectType ?? "";

                // Version column
                row.Cells["VersionColumn"].Value = project.CurrentVersion ?? "";

                // Last Opened column
                row.Cells["LastOpenedColumn"].Value = FormatLastOpened(project.LastOpenedAt);
            }

            UpdateButtonStates();
        }

        /// <summary>
        /// Truncates a path to fit within the specified length.
        /// </summary>
        private string TruncatePath(string path, int maxLength)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
                return path ?? "";

            return "..." + path.Substring(path.Length - maxLength + 3);
        }

        /// <summary>
        /// Formats the last opened date for display.
        /// </summary>
        private string FormatLastOpened(DateTime lastOpened)
        {
            if (lastOpened == default)
                return "Never";

            var now = DateTime.Now;
            var diff = now - lastOpened;

            if (lastOpened.Date == now.Date)
                return "Today " + lastOpened.ToString("h:mm tt");

            if (lastOpened.Date == now.Date.AddDays(-1))
                return "Yesterday";

            if (diff.TotalDays < 7)
                return lastOpened.DayOfWeek.ToString();

            return lastOpened.ToString("MMM d, yyyy");
        }

        /// <summary>
        /// Updates button enabled states based on selection.
        /// </summary>
        private void UpdateButtonStates()
        {
            bool hasSelection = projectsGridView.SelectedRows.Count > 0;
            openButton.Enabled = hasSelection;
            editButton.Enabled = hasSelection;
            removeButton.Enabled = hasSelection;
            deleteConfigButton.Enabled = hasSelection;
        }

        #region Event Handlers

        private void SearchTextBox_TextChanged(object sender, EventArgs e)
        {
            ApplyFilter();
        }

        private void NewProjectButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new EditProjectDialog(_projectDatabase))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var resultProject = dialog.ResultProject;
                        if (resultProject != null)
                        {
                            // Also register with JSON service for backward compatibility
                            _projectService.RegisterProject(
                                resultProject.Path,
                                resultProject.Name,
                                resultProject.Description
                            );
                        }

                        LoadProjects();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Failed to create project: {ex.Message}",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }
                }
            }
        }

        private void OpenButton_Click(object sender, EventArgs e)
        {
            OpenSelectedProject();
        }

        private void EditButton_Click(object sender, EventArgs e)
        {
            var project = SelectedProject;
            if (project == null) return;

            using (var dialog = new EditProjectDialog(project.Id, _projectDatabase))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var resultProject = dialog.ResultProject;
                        if (resultProject != null)
                        {
                            // Sync name/description back to JSON registry for backward compatibility
                            var entry = _projectService.GetAllRegisteredProjects()
                                .FirstOrDefault(e2 => e2.Id == resultProject.Id);
                            if (entry != null)
                            {
                                var legacyProject = _projectService.LoadProject(entry);
                                if (legacyProject != null)
                                {
                                    legacyProject.Name = resultProject.Name;
                                    legacyProject.Description = resultProject.Description;
                                    legacyProject.ChangeLog = resultProject.ChangeLog;
                                    _projectService.SaveProject(legacyProject);
                                }
                            }
                        }

                        LoadProjects();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Failed to save project: {ex.Message}",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }
                }
            }
        }

        private void RemoveButton_Click(object sender, EventArgs e)
        {
            var project = SelectedProject;
            if (project == null) return;

            var result = MessageBox.Show(
                $"Remove '{project.Name}' from the project list?\n\nThis will unregister the project but keep the .claude/project.json file intact.",
                "Remove Project",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                _projectService.UnregisterProject(project.Id, deleteLocalConfig: false);
                LoadProjects();
            }
        }

        private void DeleteConfigButton_Click(object sender, EventArgs e)
        {
            var project = SelectedProject;
            if (project == null) return;

            var result = MessageBox.Show(
                $"Delete project '{project.Name}' completely?\n\nThis will remove the project from the list AND delete the .claude/project.json configuration file.",
                "Delete Project Config",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (result == DialogResult.Yes)
            {
                _projectService.UnregisterProject(project.Id, deleteLocalConfig: true);
                LoadProjects();
            }
        }

        private void CloseButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void ProjectsGridView_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0)
            {
                OpenSelectedProject();
            }
        }

        private void ProjectsGridView_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == projectsGridView.Columns["PinColumn"].Index)
            {
                // Toggle pin state
                var project = projectsGridView.Rows[e.RowIndex].Tag as MultiTerminal.Models.Project;
                if (project != null)
                {
                    _projectService.ToggleProjectPinned(project.Id);
                    LoadProjects();
                }
            }
        }

        private void ProjectsGridView_SelectionChanged(object sender, EventArgs e)
        {
            UpdateButtonStates();
        }

        private void ProjectsGridView_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            // Sort by clicked column
            string columnName = projectsGridView.Columns[e.ColumnIndex].Name;

            switch (columnName)
            {
                case "NameColumn":
                    _filteredProjects = _filteredProjects
                        .OrderByDescending(p => p.IsPinned)
                        .ThenBy(p => p.Name)
                        .ToList();
                    break;
                case "PathColumn":
                    _filteredProjects = _filteredProjects
                        .OrderByDescending(p => p.IsPinned)
                        .ThenBy(p => p.Path)
                        .ToList();
                    break;
                case "TypeColumn":
                    _filteredProjects = _filteredProjects
                        .OrderByDescending(p => p.IsPinned)
                        .ThenBy(p => p.ProjectType)
                        .ToList();
                    break;
                case "VersionColumn":
                    _filteredProjects = _filteredProjects
                        .OrderByDescending(p => p.IsPinned)
                        .ThenBy(p => p.CurrentVersion)
                        .ToList();
                    break;
                case "LastOpenedColumn":
                    _filteredProjects = _filteredProjects
                        .OrderByDescending(p => p.IsPinned)
                        .ThenByDescending(p => p.LastOpenedAt)
                        .ToList();
                    break;
                case "PinColumn":
                    _filteredProjects = _filteredProjects
                        .OrderByDescending(p => p.IsPinned)
                        .ThenBy(p => p.Name)
                        .ToList();
                    break;
            }

            PopulateGrid();
        }

        private void OpenSelectedProject()
        {
            var project = SelectedProject;
            if (project == null) return;

            // Load full project from service (for prompts / legacy data)
            var entry = _projectService.GetAllRegisteredProjects()
                .FirstOrDefault(e => e.Id == project.Id);

            MultiTerminal.Models.Project fullProject;
            if (entry != null)
            {
                fullProject = _projectService.LoadProject(entry);
            }
            else
            {
                fullProject = project;
            }

            if (fullProject == null)
            {
                MessageBox.Show(
                    "Failed to load project. The project configuration may be missing or corrupted.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return;
            }

            _projectService.MarkProjectOpened(project.Id);
            ProjectOpened?.Invoke(this, new ProjectEventArgs(fullProject));
            DialogResult = DialogResult.OK;
            Close();
        }

        #endregion

        #region Theme Application

        private void ApplyTheme()
        {
            // Form colors
            BackColor = _theme.ToolbarBackground;
            ForeColor = _theme.ToolbarForeground;

            // Search TextBox
            ApplyThemeToTextBox(searchTextBox);

            // Buttons
            ApplyThemeToButton(newProjectButton);
            ApplyThemeToButton(openButton);
            ApplyThemeToButton(editButton);
            ApplyThemeToButton(removeButton);
            ApplyThemeToButton(deleteConfigButton);
            ApplyThemeToButton(closeButton);

            // DataGridView
            ApplyThemeToDataGridView(projectsGridView);
        }

        private void ApplyThemeToTextBox(TextBox textBox)
        {
            textBox.BackColor = _theme.Background;
            textBox.ForeColor = _theme.Foreground;
            textBox.BorderStyle = BorderStyle.FixedSingle;
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

            // Default cell style
            dgv.DefaultCellStyle.BackColor = _theme.Background;
            dgv.DefaultCellStyle.ForeColor = _theme.Foreground;
            dgv.DefaultCellStyle.SelectionBackColor = _theme.SelectionBackground;
            dgv.DefaultCellStyle.SelectionForeColor = _theme.SelectionForeground;

            // Header style
            dgv.ColumnHeadersDefaultCellStyle.BackColor = _theme.ToolbarBackground;
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = _theme.ToolbarForeground;
            dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = _theme.ToolbarBackground;
            dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = _theme.ToolbarForeground;
            dgv.EnableHeadersVisualStyles = false;

            // Row headers
            dgv.RowHeadersDefaultCellStyle.BackColor = _theme.ToolbarBackground;
            dgv.RowHeadersDefaultCellStyle.ForeColor = _theme.ToolbarForeground;
            dgv.RowHeadersDefaultCellStyle.SelectionBackColor = _theme.SelectionBackground;
            dgv.RowHeadersDefaultCellStyle.SelectionForeColor = _theme.SelectionForeground;

            // Alternating row style
            dgv.AlternatingRowsDefaultCellStyle.BackColor = _theme.IsDark
                ? Color.FromArgb(20, 20, 20)
                : Color.FromArgb(245, 245, 245);
            dgv.AlternatingRowsDefaultCellStyle.ForeColor = _theme.Foreground;
        }

        #endregion

        #region Designer Code

        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.searchTextBox = new TextBox();
            this.newProjectButton = new Button();
            this.projectsGridView = new DataGridView();
            this.openButton = new Button();
            this.editButton = new Button();
            this.removeButton = new Button();
            this.deleteConfigButton = new Button();
            this.closeButton = new Button();

            ((System.ComponentModel.ISupportInitialize)(this.projectsGridView)).BeginInit();
            this.SuspendLayout();

            // Label for search
            var searchLabel = new Label();
            searchLabel.AutoSize = true;
            searchLabel.Location = new Point(12, 18);
            searchLabel.Text = "Search:";
            searchLabel.ForeColor = _theme.ToolbarForeground;

            //
            // searchTextBox
            //
            this.searchTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.searchTextBox.Location = new Point(70, 15);
            this.searchTextBox.Name = "searchTextBox";
            this.searchTextBox.Size = new Size(450, 23);
            this.searchTextBox.TabIndex = 0;
            this.searchTextBox.TextChanged += new EventHandler(this.SearchTextBox_TextChanged);

            //
            // newProjectButton
            //
            this.newProjectButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.newProjectButton.Location = new Point(530, 13);
            this.newProjectButton.Name = "newProjectButton";
            this.newProjectButton.Size = new Size(140, 27);
            this.newProjectButton.TabIndex = 1;
            this.newProjectButton.Text = "+ New Project";
            this.newProjectButton.Click += new EventHandler(this.NewProjectButton_Click);

            //
            // projectsGridView
            //
            this.projectsGridView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            this.projectsGridView.AllowUserToAddRows = false;
            this.projectsGridView.AllowUserToDeleteRows = false;
            this.projectsGridView.AllowUserToResizeRows = false;
            this.projectsGridView.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            this.projectsGridView.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.projectsGridView.Location = new Point(12, 50);
            this.projectsGridView.MultiSelect = false;
            this.projectsGridView.Name = "projectsGridView";
            this.projectsGridView.ReadOnly = true;
            this.projectsGridView.RowHeadersVisible = false;
            this.projectsGridView.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            this.projectsGridView.Size = new Size(658, 350);
            this.projectsGridView.TabIndex = 2;
            this.projectsGridView.CellDoubleClick += new DataGridViewCellEventHandler(this.ProjectsGridView_CellDoubleClick);
            this.projectsGridView.CellClick += new DataGridViewCellEventHandler(this.ProjectsGridView_CellClick);
            this.projectsGridView.SelectionChanged += new EventHandler(this.ProjectsGridView_SelectionChanged);
            this.projectsGridView.ColumnHeaderMouseClick += new DataGridViewCellMouseEventHandler(this.ProjectsGridView_ColumnHeaderMouseClick);

            // Add columns to DataGridView
            var pinColumn = new DataGridViewTextBoxColumn();
            pinColumn.Name = "PinColumn";
            pinColumn.HeaderText = "Pin";
            pinColumn.Width = 35;
            pinColumn.FillWeight = 5;
            pinColumn.MinimumWidth = 35;
            this.projectsGridView.Columns.Add(pinColumn);

            var nameColumn = new DataGridViewTextBoxColumn();
            nameColumn.Name = "NameColumn";
            nameColumn.HeaderText = "Name";
            nameColumn.FillWeight = 30;
            this.projectsGridView.Columns.Add(nameColumn);

            var pathColumn = new DataGridViewTextBoxColumn();
            pathColumn.Name = "PathColumn";
            pathColumn.HeaderText = "Path";
            pathColumn.FillWeight = 30;
            this.projectsGridView.Columns.Add(pathColumn);

            var typeColumn = new DataGridViewTextBoxColumn();
            typeColumn.Name = "TypeColumn";
            typeColumn.HeaderText = "Type";
            typeColumn.FillWeight = 12;
            this.projectsGridView.Columns.Add(typeColumn);

            var versionColumn = new DataGridViewTextBoxColumn();
            versionColumn.Name = "VersionColumn";
            versionColumn.HeaderText = "Version";
            versionColumn.FillWeight = 10;
            this.projectsGridView.Columns.Add(versionColumn);

            var lastOpenedColumn = new DataGridViewTextBoxColumn();
            lastOpenedColumn.Name = "LastOpenedColumn";
            lastOpenedColumn.HeaderText = "Last Opened";
            lastOpenedColumn.FillWeight = 13;
            this.projectsGridView.Columns.Add(lastOpenedColumn);

            //
            // openButton
            //
            this.openButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.openButton.Location = new Point(12, 410);
            this.openButton.Name = "openButton";
            this.openButton.Size = new Size(80, 30);
            this.openButton.TabIndex = 3;
            this.openButton.Text = "Open";
            this.openButton.Click += new EventHandler(this.OpenButton_Click);

            //
            // editButton
            //
            this.editButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.editButton.Location = new Point(100, 410);
            this.editButton.Name = "editButton";
            this.editButton.Size = new Size(80, 30);
            this.editButton.TabIndex = 4;
            this.editButton.Text = "Edit...";
            this.editButton.Click += new EventHandler(this.EditButton_Click);

            //
            // removeButton
            //
            this.removeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.removeButton.Location = new Point(188, 410);
            this.removeButton.Name = "removeButton";
            this.removeButton.Size = new Size(80, 30);
            this.removeButton.TabIndex = 5;
            this.removeButton.Text = "Remove";
            this.removeButton.Click += new EventHandler(this.RemoveButton_Click);

            //
            // deleteConfigButton
            //
            this.deleteConfigButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            this.deleteConfigButton.Location = new Point(276, 410);
            this.deleteConfigButton.Name = "deleteConfigButton";
            this.deleteConfigButton.Size = new Size(100, 30);
            this.deleteConfigButton.TabIndex = 6;
            this.deleteConfigButton.Text = "Delete Config";
            this.deleteConfigButton.Click += new EventHandler(this.DeleteConfigButton_Click);

            //
            // closeButton
            //
            this.closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.closeButton.DialogResult = DialogResult.Cancel;
            this.closeButton.Location = new Point(590, 410);
            this.closeButton.Name = "closeButton";
            this.closeButton.Size = new Size(80, 30);
            this.closeButton.TabIndex = 7;
            this.closeButton.Text = "Close";
            this.closeButton.Click += new EventHandler(this.CloseButton_Click);

            //
            // ProjectManagerDialog
            //
            this.AcceptButton = this.openButton;
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this.closeButton;
            this.ClientSize = new Size(682, 453);
            this.Controls.Add(searchLabel);
            this.Controls.Add(this.searchTextBox);
            this.Controls.Add(this.newProjectButton);
            this.Controls.Add(this.projectsGridView);
            this.Controls.Add(this.openButton);
            this.Controls.Add(this.editButton);
            this.Controls.Add(this.removeButton);
            this.Controls.Add(this.deleteConfigButton);
            this.Controls.Add(this.closeButton);
            this.MinimizeBox = false;
            this.MinimumSize = new Size(700, 500);
            this.Name = "ProjectManagerDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Project Manager";

            ((System.ComponentModel.ISupportInitialize)(this.projectsGridView)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion
    }
}

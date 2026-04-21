using System;
using System.IO;
using System.Windows.Forms;
using MultiTerminal.Services;

namespace MultiTerminal
{
    /// <summary>
    /// Test form for Phase 2: Grid Cell Spawning.
    /// Tests spawning Claude Code in grid cells with MCP server access.
    /// </summary>
    public class TestSpawnForm : Form
    {
        private readonly MainForm _mainForm;

        // Controls — added to Controls collection and auto-disposed by base Form.Dispose().
#pragma warning disable CA2213
        private TextBox _nameTextBox;
        private TextBox _typeTextBox;
        private TextBox _dirTextBox;
        private Button _spawnButton;
        private TextBox _outputTextBox;
#pragma warning restore CA2213

        public TestSpawnForm(MainForm mainForm)
        {
            _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.Text = "Native Teams Spawn Test - Phase 2";
            this.Size = new System.Drawing.Size(600, 400);
            this.StartPosition = FormStartPosition.CenterScreen;

            int y = 20;

            // Name input
            var nameLabel = new Label { Text = "Agent Name:", Left = 20, Top = y, Width = 100 };
            _nameTextBox = new TextBox { Left = 130, Top = y, Width = 200, Text = "Alice" };
            this.Controls.Add(nameLabel);
            this.Controls.Add(_nameTextBox);
            y += 35;

            // Type input
            var typeLabel = new Label { Text = "Agent Type:", Left = 20, Top = y, Width = 100 };
            _typeTextBox = new TextBox { Left = 130, Top = y, Width = 200, Text = "researcher" };
            this.Controls.Add(typeLabel);
            this.Controls.Add(_typeTextBox);
            y += 35;

            // Directory input
            var dirLabel = new Label { Text = "Working Dir:", Left = 20, Top = y, Width = 100 };
            _dirTextBox = new TextBox
            {
                Left = 130,
                Top = y,
                Width = 400,
                Text = Directory.GetCurrentDirectory() // Deploy folder (where hooks are)
            };
            this.Controls.Add(dirLabel);
            this.Controls.Add(_dirTextBox);
            y += 35;

            // Spawn button
            _spawnButton = new Button
            {
                Text = "🚀 Spawn Terminal",
                Left = 20,
                Top = y,
                Width = 150,
                Height = 40
            };
            _spawnButton.Click += SpawnButton_Click;
            this.Controls.Add(_spawnButton);

            y += 50;

            // Output textbox
            var outputLabel = new Label { Text = "Output:", Left = 20, Top = y, Width = 100 };
            this.Controls.Add(outputLabel);
            y += 25;

            _outputTextBox = new TextBox
            {
                Left = 20,
                Top = y,
                Width = 540,
                Height = 180,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new System.Drawing.Font("Consolas", 9),
                ReadOnly = true
            };
            this.Controls.Add(_outputTextBox);
        }

        private void SpawnButton_Click(object sender, EventArgs e)
        {
            try
            {
                var name = _nameTextBox.Text.Trim();
                var type = _typeTextBox.Text.Trim();
                var dir = _dirTextBox.Text.Trim();

                System.Diagnostics.Trace.WriteLine("[TestSpawnForm] ===== SPAWN BUTTON CLICKED =====");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm] Name: '{name}'");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm] Type: '{type}'");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm] Dir: '{dir}'");

                if (string.IsNullOrEmpty(name))
                {
                    AppendOutput("❌ Name is required!");
                    System.Diagnostics.Trace.WriteLine("[TestSpawnForm] Validation failed: Name is empty");
                    return;
                }

                if (string.IsNullOrEmpty(dir))
                {
                    dir = Directory.GetCurrentDirectory();
                    System.Diagnostics.Trace.WriteLine($"[TestSpawnForm] Dir was empty, using current: '{dir}'");
                }

                AppendOutput($"🚀 Spawning grid cell: {name} ({type})...");
                AppendOutput($"   Working dir: {dir}");
                AppendOutput("");

                System.Diagnostics.Trace.WriteLine("[TestSpawnForm] Calling AddNewTerminal...");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm] Parameters:");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm]   workingDirectory: '{dir}'");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm]   identityName: '{name}'");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm]   autoRunCommand: 'claude'");

                // Spawn as a grid cell with auto-run "claude" command
                // Use --dangerously-skip-permissions to match "Launch as..." behavior
                _mainForm.AddNewTerminal(
                    workingDirectory: dir,
                    fontSize: null,
                    forceTabMode: false,
                    identityName: name,
                    autoRunCommand: "claude --dangerously-skip-permissions"
                );

                System.Diagnostics.Trace.WriteLine("[TestSpawnForm] AddNewTerminal returned");

                AppendOutput($"✅ Grid cell spawned!");
                AppendOutput($"   Identity: {name}");
                AppendOutput("");
                AppendOutput("💡 Check the grid cell for startup hook output.");
                AppendOutput("💡 Claude should auto-register with the MCP server.");
                AppendOutput("💡 Check debug output for environment variable setup.");
                AppendOutput("");

                System.Diagnostics.Trace.WriteLine("[TestSpawnForm] ===== SPAWN COMPLETE =====");
            }
            catch (Exception ex)
            {
                AppendOutput($"❌ Error: {ex.Message}");
                AppendOutput($"   {ex.StackTrace}");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm] ERROR: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[TestSpawnForm] Stack: {ex.StackTrace}");
            }
        }

        private void AppendOutput(string text)
        {
            _outputTextBox.AppendText(text + Environment.NewLine);
        }
    }
}

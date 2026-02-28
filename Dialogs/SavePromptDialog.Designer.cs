namespace MultiTerminal.Dialogs
{
    partial class SavePromptDialog
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.Label categoryLabel;
        private System.Windows.Forms.ComboBox categoryComboBox;
        private System.Windows.Forms.Label descriptionLabel;
        private System.Windows.Forms.TextBox descriptionTextBox;
        private System.Windows.Forms.Label promptLabel;
        private System.Windows.Forms.TextBox promptTextBox;
        private System.Windows.Forms.Label scopeLabel;
        private System.Windows.Forms.GroupBox scopeGroupBox;
        private System.Windows.Forms.RadioButton globalRadioButton;
        private System.Windows.Forms.RadioButton localRadioButton;
        private System.Windows.Forms.Button saveButton;
        private System.Windows.Forms.Button cancelButton;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.categoryLabel = new System.Windows.Forms.Label();
            this.categoryComboBox = new System.Windows.Forms.ComboBox();
            this.descriptionLabel = new System.Windows.Forms.Label();
            this.descriptionTextBox = new System.Windows.Forms.TextBox();
            this.promptLabel = new System.Windows.Forms.Label();
            this.promptTextBox = new System.Windows.Forms.TextBox();
            this.scopeLabel = new System.Windows.Forms.Label();
            this.scopeGroupBox = new System.Windows.Forms.GroupBox();
            this.globalRadioButton = new System.Windows.Forms.RadioButton();
            this.localRadioButton = new System.Windows.Forms.RadioButton();
            this.saveButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            this.scopeGroupBox.SuspendLayout();
            this.SuspendLayout();
            //
            // categoryLabel
            //
            this.categoryLabel.AutoSize = true;
            this.categoryLabel.Location = new System.Drawing.Point(12, 15);
            this.categoryLabel.Name = "categoryLabel";
            this.categoryLabel.Size = new System.Drawing.Size(52, 13);
            this.categoryLabel.TabIndex = 0;
            this.categoryLabel.Text = "Category:";
            //
            // categoryComboBox
            //
            this.categoryComboBox.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.categoryComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown;
            this.categoryComboBox.FormattingEnabled = true;
            this.categoryComboBox.Location = new System.Drawing.Point(100, 12);
            this.categoryComboBox.Name = "categoryComboBox";
            this.categoryComboBox.Size = new System.Drawing.Size(272, 21);
            this.categoryComboBox.TabIndex = 1;
            //
            // descriptionLabel
            //
            this.descriptionLabel.AutoSize = true;
            this.descriptionLabel.Location = new System.Drawing.Point(12, 45);
            this.descriptionLabel.Name = "descriptionLabel";
            this.descriptionLabel.Size = new System.Drawing.Size(63, 13);
            this.descriptionLabel.TabIndex = 2;
            this.descriptionLabel.Text = "Description:";
            //
            // descriptionTextBox
            //
            this.descriptionTextBox.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.descriptionTextBox.Location = new System.Drawing.Point(100, 42);
            this.descriptionTextBox.MaxLength = 100;
            this.descriptionTextBox.Name = "descriptionTextBox";
            this.descriptionTextBox.Size = new System.Drawing.Size(272, 20);
            this.descriptionTextBox.TabIndex = 3;
            //
            // promptLabel
            //
            this.promptLabel.AutoSize = true;
            this.promptLabel.Location = new System.Drawing.Point(12, 75);
            this.promptLabel.Name = "promptLabel";
            this.promptLabel.Size = new System.Drawing.Size(67, 13);
            this.promptLabel.TabIndex = 4;
            this.promptLabel.Text = "Prompt Text:";
            //
            // promptTextBox
            //
            this.promptTextBox.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.promptTextBox.Location = new System.Drawing.Point(100, 72);
            this.promptTextBox.Multiline = true;
            this.promptTextBox.Name = "promptTextBox";
            this.promptTextBox.ReadOnly = true;
            this.promptTextBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.promptTextBox.Size = new System.Drawing.Size(272, 100);
            this.promptTextBox.TabIndex = 5;
            this.promptTextBox.TabStop = false;
            //
            // scopeLabel
            //
            this.scopeLabel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.scopeLabel.AutoSize = true;
            this.scopeLabel.Location = new System.Drawing.Point(12, 185);
            this.scopeLabel.Name = "scopeLabel";
            this.scopeLabel.Size = new System.Drawing.Size(41, 13);
            this.scopeLabel.TabIndex = 6;
            this.scopeLabel.Text = "Scope:";
            //
            // scopeGroupBox
            //
            this.scopeGroupBox.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.scopeGroupBox.Controls.Add(this.localRadioButton);
            this.scopeGroupBox.Controls.Add(this.globalRadioButton);
            this.scopeGroupBox.Location = new System.Drawing.Point(100, 178);
            this.scopeGroupBox.Name = "scopeGroupBox";
            this.scopeGroupBox.Size = new System.Drawing.Size(272, 50);
            this.scopeGroupBox.TabIndex = 7;
            this.scopeGroupBox.TabStop = false;
            //
            // globalRadioButton
            //
            this.globalRadioButton.AutoSize = true;
            this.globalRadioButton.Checked = true;
            this.globalRadioButton.Location = new System.Drawing.Point(10, 19);
            this.globalRadioButton.Name = "globalRadioButton";
            this.globalRadioButton.Size = new System.Drawing.Size(55, 17);
            this.globalRadioButton.TabIndex = 0;
            this.globalRadioButton.TabStop = true;
            this.globalRadioButton.Text = "Global";
            this.globalRadioButton.UseVisualStyleBackColor = true;
            //
            // localRadioButton
            //
            this.localRadioButton.AutoSize = true;
            this.localRadioButton.Location = new System.Drawing.Point(100, 19);
            this.localRadioButton.Name = "localRadioButton";
            this.localRadioButton.Size = new System.Drawing.Size(118, 17);
            this.localRadioButton.TabIndex = 1;
            this.localRadioButton.Text = "Local to {projectName}";
            this.localRadioButton.UseVisualStyleBackColor = true;
            //
            // saveButton
            //
            this.saveButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.saveButton.Location = new System.Drawing.Point(216, 240);
            this.saveButton.Name = "saveButton";
            this.saveButton.Size = new System.Drawing.Size(75, 25);
            this.saveButton.TabIndex = 8;
            this.saveButton.Text = "Save";
            this.saveButton.UseVisualStyleBackColor = true;
            this.saveButton.Click += new System.EventHandler(this.SaveButton_Click);
            //
            // cancelButton
            //
            this.cancelButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Location = new System.Drawing.Point(297, 240);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(75, 25);
            this.cancelButton.TabIndex = 9;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            //
            // SavePromptDialog
            //
            this.AcceptButton = this.saveButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new System.Drawing.Size(384, 277);
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.saveButton);
            this.Controls.Add(this.scopeGroupBox);
            this.Controls.Add(this.scopeLabel);
            this.Controls.Add(this.promptTextBox);
            this.Controls.Add(this.promptLabel);
            this.Controls.Add(this.descriptionTextBox);
            this.Controls.Add(this.descriptionLabel);
            this.Controls.Add(this.categoryComboBox);
            this.Controls.Add(this.categoryLabel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "SavePromptDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Save Prompt";
            this.scopeGroupBox.ResumeLayout(false);
            this.scopeGroupBox.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// Manages grid-style arrangements of terminal documents in a DockPanel.
    /// </summary>
    public class GridLayoutManager
    {
        private readonly DockPanel _dockPanel;

        /// <summary>
        /// Predefined grid layout presets.
        /// </summary>
        public enum GridPreset
        {
            /// <summary>2x2 grid (4 terminals)</summary>
            Grid2x2,
            /// <summary>2 columns x 3 rows (6 terminals)</summary>
            Grid2x3,
            /// <summary>3 columns x 2 rows (6 terminals)</summary>
            Grid3x2,
            /// <summary>3 terminals in a horizontal row</summary>
            Horizontal3,
            /// <summary>3 terminals in a vertical column</summary>
            Vertical3,
            /// <summary>2 terminals side by side</summary>
            Horizontal2,
            /// <summary>2 terminals stacked vertically</summary>
            Vertical2
        }

        public GridLayoutManager(DockPanel dockPanel)
        {
            _dockPanel = dockPanel ?? throw new ArgumentNullException(nameof(dockPanel));
        }

        /// <summary>
        /// Gets all terminal documents currently in the dock panel.
        /// </summary>
        public List<TerminalDocument> GetTerminalDocuments()
        {
            return _dockPanel.Documents.OfType<TerminalDocument>().ToList();
        }

        /// <summary>
        /// Applies a grid preset to the current terminal documents.
        /// </summary>
        public void ApplyPreset(GridPreset preset)
        {
            switch (preset)
            {
                case GridPreset.Grid2x2:
                    ArrangeAsGrid(2, 2);
                    break;
                case GridPreset.Grid2x3:
                    ArrangeAsGrid(2, 3);
                    break;
                case GridPreset.Grid3x2:
                    ArrangeAsGrid(3, 2);
                    break;
                case GridPreset.Horizontal3:
                    ArrangeAsGrid(1, 3);
                    break;
                case GridPreset.Vertical3:
                    ArrangeAsGrid(3, 1);
                    break;
                case GridPreset.Horizontal2:
                    ArrangeAsGrid(1, 2);
                    break;
                case GridPreset.Vertical2:
                    ArrangeAsGrid(2, 1);
                    break;
            }
        }

        /// <summary>
        /// Arranges terminal documents in a grid pattern.
        /// </summary>
        /// <param name="rows">Number of rows</param>
        /// <param name="cols">Number of columns</param>
        public void ArrangeAsGrid(int rows, int cols)
        {
            var documents = GetTerminalDocuments();
            if (documents.Count == 0) return;

            int totalCells = rows * cols;
            int docCount = Math.Min(documents.Count, totalCells);

            // First, make sure all documents are in document state (not floating)
            foreach (var doc in documents)
            {
                if (doc.DockState == DockState.Float)
                {
                    doc.DockState = DockState.Document;
                }
            }

            if (docCount == 1)
            {
                // Single document - just show it
                documents[0].Show(_dockPanel, DockState.Document);
                return;
            }

            // For grid layout, we need to build up the structure
            // Start with the first document as the base
            documents[0].Show(_dockPanel, DockState.Document);

            if (rows == 1)
            {
                // Horizontal layout - split right (equal widths)
                for (int i = 1; i < docCount; i++)
                {
                    double proportion = (double)(cols - i) / (cols - i + 1);
                    documents[i].Show(documents[i - 1].Pane, DockAlignment.Right, proportion);
                }
            }
            else if (cols == 1)
            {
                // Vertical layout - split down (equal heights)
                for (int i = 1; i < docCount; i++)
                {
                    double proportion = (double)(rows - i) / (rows - i + 1);
                    documents[i].Show(documents[i - 1].Pane, DockAlignment.Bottom, proportion);
                }
            }
            else
            {
                // Grid layout - first create rows, then fill columns
                // Create the row structure first
                var rowLeaders = new List<TerminalDocument> { documents[0] };

                // Create additional rows by splitting down from first document (equal heights)
                for (int r = 1; r < rows && r < docCount; r++)
                {
                    int docIndex = r * cols;
                    if (docIndex < docCount)
                    {
                        double proportion = (double)(rows - r) / (rows - r + 1);
                        documents[docIndex].Show(rowLeaders[r - 1].Pane, DockAlignment.Bottom, proportion);
                        rowLeaders.Add(documents[docIndex]);
                    }
                }

                // Now fill in columns for each row (equal widths)
                for (int r = 0; r < rows; r++)
                {
                    int rowStart = r * cols;
                    if (rowStart >= docCount) break;

                    for (int c = 1; c < cols; c++)
                    {
                        int docIndex = rowStart + c;
                        if (docIndex >= docCount) break;

                        int prevIndex = rowStart + c - 1;
                        double proportion = (double)(cols - c) / (cols - c + 1);
                        documents[docIndex].Show(documents[prevIndex].Pane, DockAlignment.Right, proportion);
                    }
                }
            }

            // Focus the first terminal
            documents[0].Activate();
            documents[0].FocusTerminal();
        }

        /// <summary>
        /// Resets all terminals to tabbed document state.
        /// </summary>
        public void ResetToTabs()
        {
            var documents = GetTerminalDocuments();
            if (documents.Count == 0) return;

            // Show first document
            documents[0].Show(_dockPanel, DockState.Document);

            // Add rest as tabs in same pane
            for (int i = 1; i < documents.Count; i++)
            {
                documents[i].Show(documents[0].Pane, null);
            }

            documents[0].Activate();
        }

        /// <summary>
        /// Gets the recommended grid preset based on terminal count.
        /// </summary>
        public static GridPreset GetRecommendedPreset(int terminalCount)
        {
            switch (terminalCount)
            {
                case 1:
                    return GridPreset.Horizontal2; // Will just show one
                case 2:
                    return GridPreset.Horizontal2;
                case 3:
                    return GridPreset.Horizontal3;
                case 4:
                    return GridPreset.Grid2x2;
                case 5:
                case 6:
                    return GridPreset.Grid2x3;
                default:
                    return GridPreset.Grid3x2;
            }
        }
    }
}

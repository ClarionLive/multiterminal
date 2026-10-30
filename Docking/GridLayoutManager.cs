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
        /// The terminals DOCKED in the main window's document area. Not floated ones:
        /// <c>DockPanel.Documents</c> yields only <see cref="DockState.Document"/> contents. Use this
        /// for grid arrangement and pane placement, which are about the docked area; anything about
        /// "every terminal" wants <see cref="GetAllTerminalDocuments"/>.
        /// </summary>
        public List<TerminalDocument> GetDockedTerminalDocuments()
        {
            return _dockPanel.Documents.OfType<TerminalDocument>().ToList();
        }

        /// <summary>
        /// Every terminal shown in the dock panel, docked or in a floating window (GH #74), in the
        /// panel's content order, which is also the order layout.xml lists them in.
        /// </summary>
        public List<TerminalDocument> GetAllTerminalDocuments()
        {
            return _dockPanel.Contents.OfType<TerminalDocument>()
                .Where(d => IsListedState(d.DockState))
                .ToList();
        }

        /// <summary>
        /// Whether a terminal in <paramref name="state"/> is one of <see cref="GetAllTerminalDocuments"/>:
        /// docked or floated. Hidden and not-yet-shown contents are not, the same as for the docked list.
        /// </summary>
        public static bool IsListedState(DockState state)
            => state == DockState.Document || state == DockState.Float;

        /// <summary>
        /// The pane the Owner is in (GH #74): the panel's active content when that is a terminal,
        /// else its active document. <c>ActiveDocument</c> alone is never a floated pane, so anything
        /// asking "which terminal is active" from it would answer with a docked one while the Owner
        /// works in a floating window. The one rule for MainForm and for a terminal deciding whether
        /// to take focus when it starts.
        /// </summary>
        public static IDockContent ActiveTerminalOrDocument(DockPanel dockPanel)
            => dockPanel?.ActiveContent as TerminalDocument ?? dockPanel?.ActiveDocument;

        /// <summary>
        /// The active content of type <typeparamref name="T"/> that lives in <paramref name="window"/>
        /// (GH #74), for a key pressed there. In a floating window: the panel's active content only when
        /// it is in that window, else null, so a key never acts on a pane in another window. In the main
        /// window: the active content when it is in the document area, else the active document (a
        /// floated active content belongs to another window, however stale the library's tracking).
        /// </summary>
        public static T ActiveContentIn<T>(DockPanel dockPanel, System.Windows.Forms.Form window)
            where T : class
        {
            if (dockPanel == null) return null;
            var active = dockPanel.ActiveContent;

            if (window is FloatWindow floatWindow)
                return active is T inWindow && active.DockHandler.Pane?.FloatWindow == floatWindow ? inWindow : null;

            if (active is T docked && active.DockHandler.DockState == DockState.Document)
                return docked;
            return dockPanel.ActiveDocument as T;
        }

        /// <summary>
        /// The pane a key was pressed in (GH #74): the content of type <typeparamref name="T"/> that holds
        /// <paramref name="origin"/>, the control the key message was addressed to, when that content
        /// lives in <paramref name="window"/>. Falls back to <see cref="ActiveContentIn{T}"/> when the
        /// origin is unknown or not inside such a pane.
        ///
        /// <para>This covers a key that reaches a window's <c>ProcessCmdKey</c>, i.e. one pressed in a
        /// WinForms control. A key pressed INSIDE a terminal never gets here: the WinForms WebView2 turns
        /// its accelerator keys into <c>OnKeyDown</c> only, so Ctrl+Shift+H in a terminal is caught by
        /// <c>WebViewTerminalRenderer.IsReturnToStartScreenKey</c> in that pane instead (live check 3,
        /// which failed docked and floating alike). When no terminal pane is found here, MainForm logs
        /// it.</para>
        /// </summary>
        public static T ContentForKey<T>(DockPanel dockPanel, System.Windows.Forms.Form window, System.Windows.Forms.Control origin)
            where T : class
        {
            if (dockPanel == null) return null;

            for (var control = origin; control != null; control = control.Parent)
            {
                if (control is T match && control is IDockContent content
                    && content.DockHandler.DockPanel == dockPanel && IsIn(content, window))
                    return match;
            }

            return ActiveContentIn<T>(dockPanel, window);
        }

        private static bool IsIn(IDockContent content, System.Windows.Forms.Form window)
            => window is FloatWindow floatWindow
                ? content.DockHandler.DockState == DockState.Float && content.DockHandler.Pane?.FloatWindow == floatWindow
                : content.DockHandler.DockState == DockState.Document;

        /// <summary>
        /// Brings <paramref name="content"/> in front of the Owner (GH #74): restores its floating window
        /// first when that is minimised (maximised again if it was maximised before), then activates it.
        /// <c>Activate</c> alone only moves focus, which on a minimised window leaves the Owner looking
        /// at nothing while keystrokes go to it. See <c>NativeMethods.RestoreIfMinimized</c>.
        /// </summary>
        public static void Surface(DockContent content)
        {
            if (content == null) return;
            var floatWindow = content.DockState == DockState.Float ? content.Pane?.FloatWindow : null;
            if (floatWindow != null && floatWindow.IsHandleCreated)
                MultiTerminal.Terminal.NativeMethods.RestoreIfMinimized(floatWindow.Handle);
            content.Activate();
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
            var documents = GetDockedTerminalDocuments();
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
            var documents = GetDockedTerminalDocuments();
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

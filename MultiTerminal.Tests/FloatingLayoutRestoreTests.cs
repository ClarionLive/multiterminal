#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MultiTerminal.Docking;
using MultiTerminal.Models;
using WeifenLuo.WinFormsUI.Docking;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// A mixed docked/floating layout through a REAL DockPanelSuite DockPanel (GH #74): saved with
    /// <c>SaveAsXml</c>, read back with <c>LoadFromXml</c>, paired with saved sessions by
    /// <see cref="TerminalRestorePairing"/>. The panes are small stand-ins that save
    /// <see cref="TerminalPersistString"/> the way <see cref="TerminalDocument"/> does, because a real
    /// TerminalDocument starts WebView2 and a shell.
    ///
    /// <para>WHAT IS NOT COVERED: MainForm's own wiring (that SaveSession lists floated panes, that
    /// GetContentFromPersistString asks the pairing). These facts prove the library behaves as the
    /// fix assumes and that the pairing survives the library's real save/load order.</para>
    /// </summary>
    public class FloatingLayoutRestoreTests
    {
        private sealed class KeyedPane : DockContent
        {
            public KeyedPane(string key, string title)
            {
                Key = key;
                Text = title;
                DockAreas = DockAreas.Document | DockAreas.Float;
            }

            public string Key { get; }

            protected override string GetPersistString() => TerminalPersistString.For(Key);
        }

        private static void RunOnSta(Action action)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The STA test thread did not finish.");
            failure?.Throw();
        }

        private static (Form Form, DockPanel Panel) NewHost()
        {
            var form = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000),
                Size = new Size(800, 600),
            };
            var panel = new DockPanel
            {
                Dock = DockStyle.Fill,
                Theme = new VS2015DarkTheme(),
                DocumentStyle = DocumentStyle.DockingWindow,
            };
            form.Controls.Add(panel);
            form.Show();
            return (form, panel);
        }

        private static readonly Rectangle FloatBounds = new Rectangle(-19000, -19000, 400, 300);

        /// <summary>
        /// The premise of the bug: <c>Documents</c> leaves a floated pane out, while <c>Contents</c>,
        /// which layout.xml is written from, keeps it, in add order.
        /// </summary>
        [Fact]
        public void Documents_leaves_out_a_floated_pane_that_Contents_and_the_listed_filter_keep()
        {
            RunOnSta(() =>
            {
                var (form, panel) = NewHost();
                try
                {
                    var a = new KeyedPane(Guid.NewGuid().ToString("N"), "Alice");
                    var b = new KeyedPane(Guid.NewGuid().ToString("N"), "Bob");
                    var c = new KeyedPane(Guid.NewGuid().ToString("N"), "Carol");
                    a.Show(panel, DockState.Document);
                    b.Show(panel, FloatBounds);
                    c.Show(panel, DockState.Document);

                    Assert.Equal(DockState.Float, b.DockState);
                    Assert.Equal(new[] { "Alice", "Carol" }, panel.Documents.Cast<DockContent>().Select(d => d.Text).ToArray());
                    Assert.Equal(
                        new[] { "Alice", "Bob", "Carol" },
                        panel.Contents.Cast<DockContent>().Where(d => GridLayoutManager.IsListedState(d.DockState)).Select(d => d.Text).ToArray());
                }
                finally
                {
                    form.Close();
                    form.Dispose();
                }
            });
        }

        /// <summary>
        /// Save a layout of docked Alice, floated Bob, docked Carol, then restore it into a new panel.
        /// The sessions are saved in an order different from the layout's, so only keys can put each
        /// on its own pane, and Bob must come back floated with Bob's session.
        /// </summary>
        [Fact]
        public void A_mixed_docked_and_floating_layout_restores_each_pane_with_its_own_session()
        {
            RunOnSta(() =>
            {
                string keyA = Guid.NewGuid().ToString("N"), keyB = Guid.NewGuid().ToString("N"), keyC = Guid.NewGuid().ToString("N");
                byte[] layout;

                var (form1, panel1) = NewHost();
                try
                {
                    new KeyedPane(keyA, "Alice").Show(panel1, DockState.Document);
                    new KeyedPane(keyB, "Bob").Show(panel1, FloatBounds);
                    new KeyedPane(keyC, "Carol").Show(panel1, DockState.Document);

                    using var ms = new MemoryStream();
                    panel1.SaveAsXml(ms, Encoding.UTF8, true);
                    layout = ms.ToArray();
                }
                finally
                {
                    form1.Close();
                    form1.Dispose();
                }

                var sessions = new List<TerminalSessionInfo>
                {
                    new TerminalSessionInfo { CustomTitle = "Carol", PaneKey = keyC },
                    new TerminalSessionInfo { CustomTitle = "Bob", PaneKey = keyB },
                    new TerminalSessionInfo { CustomTitle = "Alice", PaneKey = keyA },
                };

                List<string> layoutKeys;
                using (var scan = new MemoryStream(layout)) layoutKeys = TerminalPersistString.ReadLayoutPaneKeys(scan);
                Assert.Equal(new[] { keyA, keyB, keyC }, layoutKeys);

                var pairing = new TerminalRestorePairing(sessions, layoutKeys);
                var restored = new List<KeyedPane>();

                var (form2, panel2) = NewHost();
                try
                {
                    using (var load = new MemoryStream(layout))
                    {
                        panel2.LoadFromXml(load, persistString =>
                        {
                            if (!TerminalPersistString.TryParse(persistString, out string? key)) return null;
                            var session = pairing.Take(key);
                            if (session == null) return null;
                            var pane = new KeyedPane(session.PaneKey!, session.CustomTitle);
                            restored.Add(pane);
                            return pane;
                        });
                    }

                    // The loader is called once per layout entry, in layout order (Alice, Bob, Carol).
                    // The sessions were saved Carol, Bob, Alice: paired by position this would read
                    // Carol, Bob, Alice.
                    Assert.Equal(new[] { "Alice", "Bob", "Carol" }, restored.Select(p => p.Text).ToArray());

                    Assert.Equal(DockState.Float, restored.Single(p => p.Text == "Bob").DockState);
                    Assert.Equal(DockState.Document, restored.Single(p => p.Text == "Alice").DockState);
                    Assert.Equal(DockState.Document, restored.Single(p => p.Text == "Carol").DockState);
                }
                finally
                {
                    form2.Close();
                    form2.Dispose();
                }
            });
        }
    }
}

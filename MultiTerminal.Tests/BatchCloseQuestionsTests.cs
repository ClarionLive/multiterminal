#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Docking;
using MultiTerminal.Services;
using Xunit;
using Answer = MultiTerminal.Services.PaneBatchCloseQuestions.Answer;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GH #74 phase 3: closing several panes at once (a floating window's close button, Close All Tabs,
    /// Close all but this) asks every "also close helpers?" question BEFORE closing anything
    /// (<see cref="PaneBatchCloseQuestions"/>), and dialogs go to a window the Owner can see
    /// (<see cref="DialogOwnerChoice"/>).
    ///
    /// <para>The first fact is the requirement itself: a Cancel on the second PM's question must leave
    /// the first PM open. Asked one pane at a time, as before, the first PM was already closed by then.
    /// MainForm's use of the answers (OnPaneFormClosing reusing them) is wiring no test executes.</para>
    ///
    /// <para>Falsified when written: carrying on past a Cancel turned the first fact red; ignoring
    /// whether a window is usable turned the minimised-main-window fact red. After pipeline run 1: a
    /// single pass turned the spawned-while-asking fact red; asking panes an earlier answer settled
    /// turned both cases of the settled-helper theory red.</para>
    /// </summary>
    public class BatchCloseQuestionsTests
    {
        private static Func<string, IReadOnlyCollection<string>> Helpers(Dictionary<string, List<string>> map)
            => id => map.TryGetValue(id, out var h) ? h : null;

        [Fact]
        public void A_cancel_on_a_later_question_comes_back_before_anything_is_closed()
        {
            var asked = new List<string>();
            var answers = PaneBatchCloseQuestions.AskAll(
                new[] { "pmA", "pmB", "pmC" },
                id => new[] { id + "-helper" },
                (id, _) =>
                {
                    asked.Add(id);
                    return id == "pmB" ? Answer.Cancel : Answer.CloseHelpersToo;
                });

            Assert.Null(answers);
            Assert.Equal(new[] { "pmA", "pmB" }, asked);
        }

        [Fact]
        public void Only_panes_with_open_helpers_are_asked_in_close_order_and_each_answer_keeps_what_it_named()
        {
            var asked = new List<string>();
            var answers = PaneBatchCloseQuestions.AskAll(
                new[] { "pmA", "plain", "pmB" },
                id => id.StartsWith("pm", StringComparison.Ordinal) ? new[] { id + "-h1", id + "-h2" } : Array.Empty<string>(),
                (id, _) =>
                {
                    asked.Add(id);
                    return id == "pmA" ? Answer.CloseHelpersToo : Answer.KeepHelpers;
                });

            Assert.Equal(new[] { "pmA", "pmB" }, asked);
            Assert.NotNull(answers);
            Assert.Equal(Answer.CloseHelpersToo, answers!["pmA"].Answer);
            Assert.Equal(new[] { "pmA-h1", "pmA-h2" }, answers["pmA"].Helpers);
            Assert.Equal(Answer.KeepHelpers, answers["pmB"].Answer);
            Assert.False(answers.ContainsKey("plain"));
        }

        [Fact]
        public void A_batch_with_no_helpers_asks_nothing_and_goes_ahead()
        {
            var answers = PaneBatchCloseQuestions.AskAll(new[] { "a", "b" }, _ => null, (_, _) => throw new InvalidOperationException("asked"));

            Assert.NotNull(answers);
            Assert.Empty(answers!);
        }

        [Fact]
        public void A_pane_listed_twice_is_asked_once()
        {
            int count = 0;
            PaneBatchCloseQuestions.AskAll(new[] { "pm", "pm" }, _ => new[] { "h" }, (_, _) =>
            {
                count++;
                return Answer.KeepHelpers;
            });

            Assert.Equal(1, count);
        }

        /// <summary>
        /// The adversary's case. pmB has no helpers when the questions start and is passed over; while
        /// pmC's question is on screen an agent spawns a helper for pmB. pmB must be asked before anything
        /// closes, so that its Cancel still closes nothing.
        /// </summary>
        [Fact]
        public void A_helper_spawned_while_a_question_is_up_is_asked_about_before_anything_closes()
        {
            var helpers = new Dictionary<string, List<string>> { ["pmC"] = new List<string> { "c1" } };
            var asked = new List<string>();

            var answers = PaneBatchCloseQuestions.AskAll(
                new[] { "plain", "pmB", "pmC" },
                Helpers(helpers),
                (id, _) =>
                {
                    asked.Add(id);
                    if (id == "pmC") helpers["pmB"] = new List<string> { "b1" };
                    return id == "pmB" ? Answer.Cancel : Answer.KeepHelpers;
                });

            Assert.Equal(new[] { "pmC", "pmB" }, asked);
            Assert.Null(answers);
        }

        /// <summary>
        /// A helper with a helper of its own is settled by its PM's answer, Yes or No; it is not asked,
        /// so its Cancel cannot cancel the batch and no answer is recorded that nothing reads.
        /// </summary>
        [Theory]
        [InlineData(Answer.CloseHelpersToo)]
        [InlineData(Answer.KeepHelpers)]
        public void A_helper_settled_by_its_pms_answer_is_not_asked_about_its_own_helpers(Answer pmAnswer)
        {
            var helpers = new Dictionary<string, List<string>>
            {
                ["pm"] = new List<string> { "helper" },
                ["helper"] = new List<string> { "grandchild" },
            };
            var asked = new List<string>();

            var answers = PaneBatchCloseQuestions.AskAll(
                new[] { "pm", "helper" },
                Helpers(helpers),
                (id, _) =>
                {
                    asked.Add(id);
                    return pmAnswer;
                });

            Assert.Equal(new[] { "pm" }, asked);
            Assert.False(answers!.ContainsKey("helper"));
        }

        /// <summary>A helper spawned after a PM's question is not part of that PM's answer.</summary>
        [Fact]
        public void An_answer_covers_only_the_helpers_its_question_named()
        {
            var helpers = new Dictionary<string, List<string>> { ["pm"] = new List<string> { "h1" } };

            var answers = PaneBatchCloseQuestions.AskAll(
                new[] { "pm" },
                Helpers(helpers),
                (id, named) =>
                {
                    Assert.Equal(new[] { "h1" }, named);
                    helpers["pm"].Add("late");
                    return Answer.CloseHelpersToo;
                });

            Assert.Equal(new[] { "h1" }, answers!["pm"].Helpers);
        }

        private sealed class Window
        {
            public Window(string name, bool usable)
            {
                Name = name;
                Usable = usable;
            }

            public string Name { get; }

            public bool Usable { get; }
        }

        /// <summary>
        /// The main window is minimised while the Owner works in a floating window: the dialog goes to the
        /// first usable window in preference order, not to the minimised main one.
        /// </summary>
        [Fact]
        public void A_dialog_skips_a_minimised_main_window_for_a_window_the_owner_can_see()
        {
            var main = new Window("main", usable: false);
            var floating = new Window("floating", usable: true);

            var owner = DialogOwnerChoice.Pick(new Window?[] { null, null, main, floating }, w => w.Usable);

            Assert.Same(floating, owner);
        }

        [Fact]
        public void The_window_holding_the_pane_comes_first_when_it_is_usable()
        {
            var paneWindow = new Window("pane", usable: true);
            var active = new Window("active", usable: true);

            Assert.Same(paneWindow, DialogOwnerChoice.Pick(new Window?[] { paneWindow, active }, w => w.Usable));
        }

        /// <summary>With nothing usable, the first window wins: an owner the Owner can restore beats none.</summary>
        [Fact]
        public void With_nothing_usable_the_first_window_is_still_the_owner()
        {
            var main = new Window("main", usable: false);
            var floating = new Window("floating", usable: false);

            Assert.Same(main, DialogOwnerChoice.Pick(new Window?[] { null, main, floating }, w => w.Usable));
            Assert.Null(DialogOwnerChoice.Pick(Array.Empty<Window?>(), w => w.Usable));
        }
    }
}

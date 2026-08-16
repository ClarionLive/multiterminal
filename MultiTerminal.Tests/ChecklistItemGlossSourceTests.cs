using System.Text.Json;
using MultiTerminal.MCPServer.Models;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Coverage for <see cref="ChecklistItemGloss.Source"/> — the authored-vs-generated marker
    /// added by task a455e295 so a reader can tell whether a step's explanation came from the
    /// planner or from a backfill agent.
    /// <para>Two of these tests guard failure modes that would be silent rather than loud, and
    /// they are the reason this file exists:</para>
    /// <list type="bullet">
    /// <item><description><see cref="Absent_source_normalizes_to_authored"/> — defaulting the
    /// other way would retroactively brand every hand-written gloss in the database as machine
    /// output. Nothing would throw; the UI would simply start lying about provenance.</description></item>
    /// <item><description><see cref="Null_gloss_stays_null_through_normalization"/> — null is
    /// the backfill's detection signal. If normalization ever conjures an empty gloss, every
    /// un-glossed item looks already-handled and the backfill silently never runs again.</description></item>
    /// </list>
    /// </summary>
    public class ChecklistItemGlossSourceTests
    {
        // ---- The legacy-misattribution guarantee ----

        [Fact]
        public void Absent_source_normalizes_to_authored()
        {
            // Every gloss written before this field existed was hand-written by a planner at
            // plan time. An absent Source therefore means "a person wrote this", not "we don't
            // know" — and certainly not "a machine wrote it".
            var gloss = new ChecklistItemGloss { What = "Move the task code into its own file." };

            gloss.NormalizeSource();

            Assert.Equal(ChecklistItemGloss.SourceAuthored, gloss.Source);
            Assert.False(gloss.IsGenerated);
        }

        [Fact]
        public void Unrecognized_source_normalizes_to_authored()
        {
            // The checklist is agent-authored JSON, so a garbage value is reachable. It must
            // degrade to the safe answer rather than throw — a malformed gloss can never make a
            // checklist unsaveable, same posture as DependsOn.
            var gloss = new ChecklistItemGloss { What = "x", Source = "hand-wavy" };

            gloss.NormalizeSource();

            Assert.Equal(ChecklistItemGloss.SourceAuthored, gloss.Source);
            Assert.False(gloss.IsGenerated);
        }

        [Fact]
        public void Blank_source_normalizes_to_authored()
        {
            var gloss = new ChecklistItemGloss { What = "x", Source = "   " };

            gloss.NormalizeSource();

            Assert.Equal(ChecklistItemGloss.SourceAuthored, gloss.Source);
        }

        // ---- Generated is preserved, and only when actually declared ----

        [Fact]
        public void Generated_source_survives_normalization()
        {
            // The whole point: a machine-written gloss must still say so after a round of
            // normalization, or the marker silently degrades to "authored" and the UI stops
            // warning the reader.
            var gloss = new ChecklistItemGloss { What = "x", Source = ChecklistItemGloss.SourceGenerated };

            gloss.NormalizeSource();

            Assert.Equal(ChecklistItemGloss.SourceGenerated, gloss.Source);
            Assert.True(gloss.IsGenerated);
        }

        [Theory]
        [InlineData("GENERATED")]
        [InlineData("Generated")]
        [InlineData("gEnErAtEd")]
        public void Source_matching_is_case_insensitive_and_canonicalizes(string wire)
        {
            // Accept what an agent plausibly writes, then store one spelling so downstream
            // string comparisons and the rendered UI never have to care about casing.
            var gloss = new ChecklistItemGloss { What = "x", Source = wire };

            gloss.NormalizeSource();

            Assert.Equal(ChecklistItemGloss.SourceGenerated, gloss.Source);
        }

        [Fact]
        public void IsGenerated_is_false_on_an_unnormalized_instance_off_the_wire()
        {
            // IsGenerated reads the normalized MEANING, so a caller that forgot to normalize
            // still gets the safe answer rather than a false "machine wrote this".
            var gloss = new ChecklistItemGloss { What = "x", Source = null };

            Assert.False(gloss.IsGenerated);
        }

        // ---- The detection signal the backfill depends on ----

        [Fact]
        public void Null_gloss_stays_null_through_normalization()
        {
            // If this goes red, the gloss backfill is dead: NormalizeFromLegacy would be
            // manufacturing an empty gloss for every legacy item, so "Gloss == null" would
            // never again be true and nothing would ever be detected as needing an explanation.
            var item = new ChecklistItem { Item = "Extract TaskService" };

            item.NormalizeFromLegacy();

            Assert.Null(item.Gloss);
        }

        [Fact]
        public void NormalizeFromLegacy_canonicalizes_an_existing_gloss_source()
        {
            // Normalizing a field on an object that already exists is fine; conjuring the
            // object is not. This test pins the first half, the one above pins the second.
            var item = new ChecklistItem
            {
                Item = "Extract TaskService",
                Gloss = new ChecklistItemGloss { What = "x", Source = "GENERATED" },
            };

            item.NormalizeFromLegacy();

            Assert.NotNull(item.Gloss);
            Assert.Equal(ChecklistItemGloss.SourceGenerated, item.Gloss.Source);
        }

        [Fact]
        public void NormalizeFromLegacy_defaults_an_existing_gloss_with_no_source_to_authored()
        {
            var item = new ChecklistItem
            {
                Item = "Extract TaskService",
                Gloss = new ChecklistItemGloss { What = "Move the code into its own file." },
            };

            item.NormalizeFromLegacy();

            Assert.Equal(ChecklistItemGloss.SourceAuthored, item.Gloss.Source);
        }

        // ---- Wire format ----

        [Fact]
        public void Source_round_trips_through_json()
        {
            // The checklist lives as JSON in tasks.checklist_json, so provenance has to survive
            // serialization to mean anything at all.
            var item = new ChecklistItem
            {
                Item = "Extract TaskService",
                Gloss = new ChecklistItemGloss
                {
                    What = "Move the task code into its own file.",
                    Why = "Gated on the inventory.",
                    Without = "You find the coupling from compiler errors mid-move.",
                    Source = ChecklistItemGloss.SourceGenerated,
                },
            };

            var json = JsonSerializer.Serialize(item);
            var restored = JsonSerializer.Deserialize<ChecklistItem>(json);
            restored.NormalizeFromLegacy();

            Assert.NotNull(restored.Gloss);
            Assert.True(restored.Gloss.IsGenerated);
            Assert.Equal(ChecklistItemGloss.SourceGenerated, restored.Gloss.Source);
        }

        [Fact]
        public void A_legacy_checklist_json_with_no_source_deserializes_as_authored()
        {
            // The literal shape sitting in the database today: a gloss written by task 60665c6c
            // before this field existed. It must read back as human-written.
            const string legacy = """
                {
                  "item": "Extract TaskService",
                  "status": "done",
                  "gloss": {
                    "what": "Move the task code into its own file.",
                    "why": "Gated on the inventory.",
                    "without": "You find the coupling from compiler errors mid-move."
                  }
                }
                """;

            var item = JsonSerializer.Deserialize<ChecklistItem>(
                legacy,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            item.NormalizeFromLegacy();

            Assert.NotNull(item.Gloss);
            Assert.Equal(ChecklistItemGloss.SourceAuthored, item.Gloss.Source);
            Assert.False(item.Gloss.IsGenerated);
        }
    }
}

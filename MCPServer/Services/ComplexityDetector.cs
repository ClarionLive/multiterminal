using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Detects task complexity to suggest when a plan should be created.
    /// Analyzes task title and description for signals indicating complex work.
    /// </summary>
    public class ComplexityDetector
    {
        /// <summary>
        /// Score threshold above which a plan is suggested. Default: 40
        /// </summary>
        public int PlanSuggestionThreshold { get; set; } = 40;

        // Scoring weights for each signal type
        private const int MultiFileScopeScore = 20;
        private const int ComplexKeywordsScore = 15;
        private const int UnknownsScore = 25;
        private const int DependenciesScore = 20;
        private const int SubtasksScore = 20;

        // Regex patterns for signal detection
        private static readonly Regex MultiFilePattern = new Regex(
            @"(?:files?|directories|folders?|modules?|components?|services?|controllers?|models?|views?)" +
            @"(?:\s+(?:and|,)\s+(?:files?|directories|folders?|modules?|components?|services?|controllers?|models?|views?))|" +
            @"(?:multiple|several|many|various|all|every)\s+(?:files?|directories|folders?|modules?|components?|services?|controllers?|models?|views?)|" +
            @"(?:across|throughout)\s+(?:the\s+)?(?:codebase|project|repository|system)|" +
            @"(?:\w+\.(?:cs|js|ts|py|java|cpp|h|json|xml|config|yaml|yml)(?:\s*,?\s*(?:and\s+)?\w+\.(?:cs|js|ts|py|java|cpp|h|json|xml|config|yaml|yml))+)|" +
            @"(?:src|lib|app|components|services|models|controllers|views|tests?)[/\\]\w+.*(?:and|,).*[/\\]\w+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ComplexKeywordsPattern = new Regex(
            @"\b(?:implement|implementing|implementation|" +
            @"integrate|integrating|integration|" +
            @"migrate|migrating|migration|" +
            @"refactor|refactoring|" +
            @"redesign|redesigning|" +
            @"architect|architecting|architecture|" +
            @"overhaul|overhauling|" +
            @"rewrite|rewriting|" +
            @"restructure|restructuring|" +
            @"modernize|modernizing|modernization|" +
            @"upgrade|upgrading|" +
            @"transform|transforming|transformation)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex UnknownsPattern = new Regex(
            @"\b(?:figure\s+out|figuring\s+out|" +
            @"investigate|investigating|investigation|" +
            @"design|designing|" +
            @"explore|exploring|exploration|" +
            @"research|researching|" +
            @"analyze|analyzing|analysis|" +
            @"evaluate|evaluating|evaluation|" +
            @"determine|determining|" +
            @"discover|discovering|" +
            @"understand|understanding|" +
            @"assess|assessing|assessment|" +
            @"prototype|prototyping|" +
            @"proof\s+of\s+concept|poc|" +
            @"spike|" +
            @"not\s+sure|unsure|unclear|unknown|" +
            @"need\s+to\s+(?:find|learn|understand|figure)|" +
            @"how\s+(?:to|do|should|can|will))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DependenciesPattern = new Regex(
            @"\b(?:after\s+(?:\w+\s+)?(?:is\s+)?(?:done|completed?|finished|ready)|" +
            @"depends?\s+on|depending\s+on|dependency|dependencies|" +
            @"requires?|requiring|requirement|" +
            @"blocked\s+by|blocking|blocker|" +
            @"waiting\s+(?:for|on)|" +
            @"prerequisite|" +
            @"(?:before|until)\s+(?:\w+\s+)?(?:can|is)|" +
            @"API|APIs|endpoint|endpoints|" +
            @"service|services|" +
            @"database|DB|" +
            @"external\s+(?:system|service|API)|" +
            @"third[- ]party|" +
            @"upstream|downstream|" +
            @"integration\s+(?:with|point))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SubtasksPattern = new Regex(
            @"(?:^|\n)\s*[-*]\s+\w|" +                    // Bullet points
            @"(?:^|\n)\s*\d+[.)]\s+\w|" +                 // Numbered lists
            @"\band\s+(?:also\s+)?(?:then\s+)?(?:\w+\s+){0,3}(?:create|add|update|delete|modify|change|implement|fix|test|check|verify|ensure|make)\b|" +  // "and" with action verbs
            @"(?:first|then|next|finally|lastly|also|additionally)\s*[,:]?\s*(?:\w+\s+){0,3}(?:create|add|update|delete|modify|change|implement|fix|test|check|verify|ensure|make)|" +  // Sequential markers
            @"(?:[.!?]\s+[A-Z].*?){2,}",                  // Multiple sentences (at least 3)
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Analyzes task title and description for complexity signals.
        /// Returns a ComplexityResult with score, signals, and recommendation.
        /// </summary>
        /// <param name="title">Task title</param>
        /// <param name="description">Task description (can be null or empty)</param>
        /// <returns>ComplexityResult with analysis results</returns>
        public ComplexityResult Analyze(string title, string description)
        {
            var result = new ComplexityResult
            {
                Score = 0,
                SuggestPlan = false,
                Signals = new List<string>(),
                Recommendation = null
            };

            // Combine title and description for analysis
            var text = CombineText(title, description);
            if (string.IsNullOrWhiteSpace(text))
            {
                result.Recommendation = "Task has no content to analyze.";
                return result;
            }

            // Check each signal type
            if (HasMultiFileScope(text))
            {
                result.Score += MultiFileScopeScore;
                result.Signals.Add($"Multi-file scope detected (+{MultiFileScopeScore})");
            }

            if (HasComplexKeywords(text))
            {
                result.Score += ComplexKeywordsScore;
                result.Signals.Add($"Complex keywords found (+{ComplexKeywordsScore})");
            }

            if (HasUnknowns(text))
            {
                result.Score += UnknownsScore;
                result.Signals.Add($"Unknowns/exploration needed (+{UnknownsScore})");
            }

            if (HasDependencies(text))
            {
                result.Score += DependenciesScore;
                result.Signals.Add($"Dependencies detected (+{DependenciesScore})");
            }

            if (HasSubtasks(text))
            {
                result.Score += SubtasksScore;
                result.Signals.Add($"Subtasks identified (+{SubtasksScore})");
            }

            // Determine if plan should be suggested
            result.SuggestPlan = result.Score >= PlanSuggestionThreshold;

            // Generate recommendation
            result.Recommendation = GenerateRecommendation(result);

            return result;
        }

        /// <summary>
        /// Detects if the task involves multiple files or directories.
        /// </summary>
        private bool HasMultiFileScope(string text)
        {
            return MultiFilePattern.IsMatch(text);
        }

        /// <summary>
        /// Detects keywords indicating complex work like implementation or refactoring.
        /// </summary>
        private bool HasComplexKeywords(string text)
        {
            return ComplexKeywordsPattern.IsMatch(text);
        }

        /// <summary>
        /// Detects language indicating unknowns or exploration work.
        /// </summary>
        private bool HasUnknowns(string text)
        {
            return UnknownsPattern.IsMatch(text);
        }

        /// <summary>
        /// Detects dependencies on other tasks, systems, or APIs.
        /// </summary>
        private bool HasDependencies(string text)
        {
            return DependenciesPattern.IsMatch(text);
        }

        /// <summary>
        /// Detects if the task contains subtasks (lists, bullets, multiple sentences).
        /// </summary>
        private bool HasSubtasks(string text)
        {
            return SubtasksPattern.IsMatch(text);
        }

        /// <summary>
        /// Combines title and description for analysis.
        /// </summary>
        private string CombineText(string title, string description)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(title))
                parts.Add(title.Trim());
            if (!string.IsNullOrWhiteSpace(description))
                parts.Add(description.Trim());
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Generates a human-readable recommendation based on analysis.
        /// </summary>
        private string GenerateRecommendation(ComplexityResult result)
        {
            if (result.Score == 0)
            {
                return "Task appears straightforward. No plan needed.";
            }

            if (result.Score < PlanSuggestionThreshold)
            {
                return $"Task has some complexity (score: {result.Score}/{PlanSuggestionThreshold}). " +
                       "A plan is optional but may help organize work.";
            }

            var signalSummary = string.Join(", ", result.Signals.Select(s =>
                s.Substring(0, s.IndexOf(" (", StringComparison.Ordinal))));

            return $"Complex task detected (score: {result.Score}). " +
                   $"Consider creating a plan. Signals: {signalSummary}.";
        }
    }

    /// <summary>
    /// Result of complexity analysis for a task.
    /// </summary>
    public class ComplexityResult
    {
        /// <summary>
        /// Complexity score from 0-100.
        /// Higher scores indicate more complex tasks.
        /// </summary>
        public int Score { get; set; }

        /// <summary>
        /// True if the score exceeds the threshold and a plan is recommended.
        /// </summary>
        public bool SuggestPlan { get; set; }

        /// <summary>
        /// List of detected complexity signals with their scores.
        /// </summary>
        public List<string> Signals { get; set; }

        /// <summary>
        /// Human-readable recommendation for the user.
        /// </summary>
        public string Recommendation { get; set; }
    }
}

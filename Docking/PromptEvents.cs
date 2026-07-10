using System;
using MultiTerminal.Services;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// Event arguments for prompt operations.
    /// </summary>
    /// <remarks>
    /// Relocated here from the (removed) dead PromptTreeDocument panel — these classes are
    /// live: ProjectPanelDocument raises them and MainForm handles them. (task df1f521f)
    /// </remarks>
    public class PromptEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the prompt associated with the event.
        /// </summary>
        public Prompt Prompt { get; }

        public PromptEventArgs(Prompt prompt)
        {
            Prompt = prompt;
        }
    }

    /// <summary>
    /// Event arguments for creating a new prompt in a specific category.
    /// </summary>
    public class NewPromptInCategoryEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the category name for the new prompt.
        /// </summary>
        public string Category { get; }

        public NewPromptInCategoryEventArgs(string category)
        {
            Category = category;
        }
    }
}

using System;
using System.Collections.Generic;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents a team member's profile with personal info, skills, and interests.
    /// Used to provide rich identity information beyond basic terminal activity.
    /// </summary>
    public class TeamMemberProfile
    {
        /// <summary>
        /// Unique identifier - typically matches terminal name (e.g., "Alice", "Bob").
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Optional friendly display name (can differ from terminal name).
        /// If null, falls back to Id.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// URL to avatar image (can be local file:// or https://).
        /// If null, UI falls back to first-letter circle avatar.
        /// </summary>
        public string AvatarUrl { get; set; }

        /// <summary>
        /// Role or title (e.g., "Senior Backend Engineer", "UI Specialist").
        /// </summary>
        public string Role { get; set; }

        /// <summary>
        /// Free-text biography or description.
        /// </summary>
        public string Bio { get; set; }

        /// <summary>
        /// JSON array of skills/expertise (e.g., ["C#", ".NET", "SQL", "Architecture"]).
        /// Stored as JSON string, use GetSkills()/SetSkills() for convenient access.
        /// </summary>
        public string SkillsJson { get; set; } = "[]";

        /// <summary>
        /// JSON array of interests (e.g., ["Performance", "Testing", "Documentation"]).
        /// Stored as JSON string, use GetInterests()/SetInterests() for convenient access.
        /// </summary>
        public string InterestsJson { get; set; } = "[]";

        /// <summary>
        /// JSON array of project IDs this user is assigned to.
        /// Stored as JSON string, use GetProjectIds()/SetProjectIds() for convenient access.
        /// </summary>
        public string ProjectIdsJson { get; set; } = "[]";

        /// <summary>
        /// Custom system prompt/personality for when spawned as a Team Agent.
        /// Baked into the agent's spawn prompt to give it identity and expertise.
        /// </summary>
        public string AgentInstructions { get; set; }

        /// <summary>
        /// Preferred model when spawning as a Team Agent (sonnet/opus/haiku).
        /// </summary>
        public string PreferredModel { get; set; } = "sonnet";

        /// <summary>
        /// Whether this terminal is currently online (connected to MultiTerminal).
        /// Set to true when terminal registers, false on session end or window close.
        /// </summary>
        public bool IsOnline { get; set; } = false;

        /// <summary>
        /// Whether this agent is designated as a team lead.
        /// Team leads receive a "Team Lead {Name} - {suffix}" naming convention on spawn.
        /// </summary>
        public bool IsTeamLead { get; set; } = false;

        /// <summary>
        /// When the profile was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the profile was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Get skills as list of strings.
        /// </summary>
        public List<string> GetSkills()
        {
            if (string.IsNullOrEmpty(SkillsJson)) return new List<string>();
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(SkillsJson) ?? new List<string>();
        }

        /// <summary>
        /// Set skills from list of strings.
        /// </summary>
        public void SetSkills(List<string> skills)
        {
            SkillsJson = System.Text.Json.JsonSerializer.Serialize(skills ?? new List<string>());
        }

        /// <summary>
        /// Get interests as list of strings.
        /// </summary>
        public List<string> GetInterests()
        {
            if (string.IsNullOrEmpty(InterestsJson)) return new List<string>();
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(InterestsJson) ?? new List<string>();
        }

        /// <summary>
        /// Set interests from list of strings.
        /// </summary>
        public void SetInterests(List<string> interests)
        {
            InterestsJson = System.Text.Json.JsonSerializer.Serialize(interests ?? new List<string>());
        }

        /// <summary>
        /// Get project IDs as list of strings.
        /// </summary>
        public List<string> GetProjectIds()
        {
            if (string.IsNullOrEmpty(ProjectIdsJson)) return new List<string>();
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(ProjectIdsJson) ?? new List<string>();
        }

        /// <summary>
        /// Set project IDs from list of strings.
        /// </summary>
        public void SetProjectIds(List<string> projectIds)
        {
            ProjectIdsJson = System.Text.Json.JsonSerializer.Serialize(projectIds ?? new List<string>());
        }
    }

    /// <summary>
    /// Result of creating a profile.
    /// </summary>
    public class CreateProfileResult
    {
        public bool Success { get; set; }
        public string ProfileId { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of updating a profile.
    /// </summary>
    public class UpdateProfileResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of getting a profile.
    /// </summary>
    public class GetProfileResult
    {
        public bool Success { get; set; }
        public TeamMemberProfile Profile { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of listing profiles.
    /// </summary>
    public class ListProfilesResult
    {
        public bool Success { get; set; }
        public List<TeamMemberProfile> Profiles { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of deleting a profile.
    /// </summary>
    public class DeleteProfileResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of setting profile online/offline status.
    /// </summary>
    public class SetProfileStatusResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}

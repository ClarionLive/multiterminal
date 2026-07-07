using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Team-member profile cache + CRUD + write path, extracted from <see cref="MessageBroker"/> (ticket
    /// 86f3fd21 — the SECOND region peeled off the broker god-file, validating the extraction template at
    /// <c>.claude/rules/broker-extraction-pattern.md</c> that <see cref="TaskService"/> established).
    ///
    /// <para>Owns the <c>_profiles</c> cache (single-owner-per-cache, per bb2b0104) and the profile write path
    /// (clone→mutate→persist→swap, from 1df2a534). The broker keeps its full public profile surface as
    /// one-line delegations to this service, and reaches back in as the service's <see cref="IProfileServiceHost"/>
    /// (event raising + the IsTemporaryAgent naming utility). The terminal-registration region reaches the
    /// cache through the read accessors (<see cref="TryGetProfile"/>, <see cref="ContainsProfile"/>) plus the
    /// public write path (<see cref="InsertProfile"/> for auto-create, <see cref="MutateProfile"/> for
    /// online/offline/team-lead field writes) — since e1643ccc converted registration onto the write path,
    /// there is no cache-only add bypass, so a persist failure can never leave a profile in cache but not the DB.</para>
    /// </summary>
    internal sealed class ProfileService
    {
        private readonly TaskDatabase _taskDb;
        private readonly IProfileServiceHost _host;

        // Team member profile storage (relocated from MessageBroker — single owner per cache, bb2b0104).
        private readonly ConcurrentDictionary<string, TeamMemberProfile> _profiles = new ConcurrentDictionary<string, TeamMemberProfile>();

        public ProfileService(TaskDatabase taskDb, IProfileServiceHost host)
        {
            _taskDb = taskDb ?? throw new ArgumentNullException(nameof(taskDb));
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Load team member profiles from the database into memory. Startup bootstrap seed FROM the DB (not a
        /// mutation) — a write-path bypass, same as LoadPersistedTasks. Called by the broker ctor.
        /// </summary>
        public void LoadPersistedProfiles()
        {
            try
            {
                var profiles = _taskDb.LoadAllProfiles();
                // Write-path bypass (P5): startup bootstrap seed from the DB, not a mutation (see LoadPersistedTasks).
                foreach (var profile in profiles)
                {
                    _profiles.TryAdd(profile.Id, profile);
                }
                _host.LogInfo($"Loaded {profiles.Count} profiles from database");
            }
            catch (Exception ex)
            {
                _host.LogError($"Failed to load profiles: {ex.Message}");
            }
        }

        /// <summary>
        /// Create a new team member profile.
        /// </summary>
        public CreateProfileResult CreateProfile(string id, string displayName, string avatarUrl, string role, string bio, List<string> skills, List<string> interests, List<string> projectIds = null, string agentInstructions = null, string preferredModel = null, bool? isTeamLead = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return new CreateProfileResult { Success = false, Error = "Profile ID is required" };
            }

            if (_profiles.ContainsKey(id))
            {
                return new CreateProfileResult { Success = false, Error = $"Profile already exists: {id}" };
            }

            var profile = new TeamMemberProfile
            {
                Id = id,
                DisplayName = displayName,
                AvatarUrl = avatarUrl,
                Role = role,
                Bio = bio,
                AgentInstructions = agentInstructions,
                PreferredModel = preferredModel ?? "sonnet",
                IsTeamLead = isTeamLead ?? false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (skills != null) profile.SetSkills(skills);
            if (interests != null) profile.SetInterests(interests);
            if (projectIds != null) profile.SetProjectIds(projectIds);

            // Persist-before-cache (write path): on a durable-write failure the profile never enters the
            // cache, and we return Success=false instead of the pre-P5 swallow-and-succeed.
            try
            {
                InsertProfile(profile);
            }
            catch (Exception ex)
            {
                _host.LogError($"CreateProfile: persist failed for {id}: {ex.Message}");
                return new CreateProfileResult { Success = false, Error = $"Failed to persist profile: {ex.Message}" };
            }

            BroadcastProfileUpdate();

            return new CreateProfileResult { Success = true, ProfileId = id };
        }

        /// <summary>
        /// Update an existing team member profile. Only provided fields are updated.
        /// </summary>
        public UpdateProfileResult UpdateProfile(string id, string displayName, string avatarUrl, string role, string bio, List<string> skills, List<string> interests, List<string> projectIds = null, string agentInstructions = null, string preferredModel = null, bool? isTeamLead = null)
        {
            if (!_profiles.ContainsKey(id))
            {
                return new UpdateProfileResult { Success = false, Error = $"Profile not found: {id}" };
            }

            // Write path: clone → mutate → persist → swap. A persist failure keeps the cached profile
            // (coherent) and returns Success=false instead of the pre-P5 swallow-and-succeed.
            try
            {
                MutateProfile(id, p =>
                {
                    // Update only provided fields (null means don't change).
                    if (displayName != null) p.DisplayName = displayName;
                    if (avatarUrl != null) p.AvatarUrl = avatarUrl;
                    if (role != null) p.Role = role;
                    if (bio != null) p.Bio = bio;
                    if (skills != null) p.SetSkills(skills);
                    if (interests != null) p.SetInterests(interests);
                    if (projectIds != null) p.SetProjectIds(projectIds);
                    if (agentInstructions != null) p.AgentInstructions = agentInstructions;
                    if (preferredModel != null) p.PreferredModel = preferredModel;
                    if (isTeamLead.HasValue) p.IsTeamLead = isTeamLead.Value;
                    p.UpdatedAt = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _host.LogError($"UpdateProfile: persist failed for {id}: {ex.Message}");
                return new UpdateProfileResult { Success = false, Error = $"Failed to persist profile update: {ex.Message}" };
            }

            BroadcastProfileUpdate();

            return new UpdateProfileResult { Success = true };
        }

        /// <summary>
        /// Get a team member profile by ID.
        /// </summary>
        public GetProfileResult GetProfile(string id)
        {
            if (_profiles.TryGetValue(id, out var profile))
            {
                return new GetProfileResult { Success = true, Profile = profile };
            }

            return new GetProfileResult { Success = false, Error = $"Profile not found: {id}" };
        }

        /// <summary>
        /// List all team member profiles.
        /// </summary>
        public ListProfilesResult ListProfiles()
        {
            var profiles = _profiles.Values
                .Where(p => !_host.IsTemporaryAgent(p.Id))
                .OrderBy(p => p.DisplayName ?? p.Id)
                .ToList();
            return new ListProfilesResult { Success = true, Profiles = profiles };
        }

        /// <summary>
        /// Delete a team member profile.
        /// </summary>
        public DeleteProfileResult DeleteProfile(string id)
        {
            if (!_profiles.ContainsKey(id))
            {
                return new DeleteProfileResult { Success = false, Error = $"Profile not found: {id}" };
            }

            // Write path: DB-delete before cache-remove (via DeleteProfileInternal). A failed delete keeps
            // the profile in both stores rather than dropping it from the UI only.
            try
            {
                DeleteProfileInternal(id);
            }
            catch (Exception ex)
            {
                _host.LogError($"DeleteProfile: persist failed for {id}: {ex.Message}");
                return new DeleteProfileResult { Success = false, Error = $"Failed to delete profile: {ex.Message}" };
            }

            BroadcastProfileUpdate();

            return new DeleteProfileResult { Success = true };
        }

        /// <summary>
        /// Set a profile's online status to true.
        /// Called by SessionStart hook when Claude session starts.
        /// </summary>
        public SetProfileStatusResult SetProfileOnline(string id)
        {
            try
            {
                // Auto-create profile if it doesn't exist (persist-before-cache via the write path).
                if (!_profiles.ContainsKey(id))
                {
                    // InsertProfile persists the full row with IsOnline=true, so no separate SetProfileOnline
                    // column write is needed (folds in 86f3fd21's redundant-write NIT).
                    InsertProfile(new TeamMemberProfile
                    {
                        Id = id,
                        DisplayName = id,
                        IsOnline = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                    _host.LogInfo($"Auto-created profile: {id}");
                }
                else
                {
                    // Targeted online-flag write as the persist step + coherent cache swap.
                    MutateProfile(id, p =>
                    {
                        p.IsOnline = true;
                        p.UpdatedAt = DateTime.UtcNow;
                    }, _ => _taskDb.SetProfileOnline(id));
                }

                BroadcastProfileUpdate();
                _host.LogInfo($"Set profile online: {id}");

                return new SetProfileStatusResult { Success = true };
            }
            catch (Exception ex)
            {
                _host.LogError($"Failed to set profile online: {ex.Message}");
                return new SetProfileStatusResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Set a profile's online status to false.
        /// Called by SessionEnd hook when Claude session ends.
        /// </summary>
        public SetProfileStatusResult SetProfileOffline(string id)
        {
            try
            {
                // If cached, swap coherently with the offline-flag write as the persist step; if not
                // cached, still write the DB flag (matches pre-P5, which always wrote it).
                if (MutateProfile(id, p =>
                {
                    p.IsOnline = false;
                    p.UpdatedAt = DateTime.UtcNow;
                }, _ => _taskDb.SetProfileOffline(id)) == null)
                {
                    _taskDb.SetProfileOffline(id);
                }

                BroadcastProfileUpdate();
                _host.LogInfo($"Set profile offline: {id}");

                return new SetProfileStatusResult { Success = true };
            }
            catch (Exception ex)
            {
                _host.LogError($"Failed to set profile offline: {ex.Message}");
                return new SetProfileStatusResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Broadcast profile updates to all clients. Private since e1643ccc: the CRUD + online/offline
        /// write-path methods are its only callers now that registration reaches the cache through the
        /// persist-first write path (no broker-side broadcast call remains).
        /// </summary>
        private void BroadcastProfileUpdate()
        {
            var profiles = _profiles.Values.OrderBy(p => p.DisplayName ?? p.Id).ToList();
            _host.RaiseProfilesUpdated(profiles);
        }

        // ── Narrow cache READ accessors for the broker-side terminal-registration + terminal-listing
        //    regions. (The write primitives are the public MutateProfile / InsertProfile below — since
        //    e1643ccc, registration bootstraps profiles through the write path, so there is no longer a
        //    cache-only add primitive.) ─────────────────────────────────────────────────────────────────

        /// <summary>Read accessor: cache lookup for the registration/terminal-listing regions.</summary>
        public bool TryGetProfile(string id, out TeamMemberProfile profile) => _profiles.TryGetValue(id, out profile);

        /// <summary>Read accessor: cache existence check for the registration region.</summary>
        public bool ContainsProfile(string id) => _profiles.ContainsKey(id);

        // Profile write path (P5 / 1df2a534) — clone → mutate → persist → swap, the mirror of the task /
        // project paths. `persist` defaults to a full-row SaveProfile; pass a custom action for a targeted
        // column write (e.g. the online-flag writers). Neither helper broadcasts (the caller decides).
        // PUBLIC since e1643ccc: the terminal-registration region reaches the write path through these
        // (MutateProfile for online/offline/team-lead field writes, InsertProfile for auto-create) instead
        // of a cache-only bypass, so a persist failure can no longer leave a profile in cache but not the DB.
        public TeamMemberProfile MutateProfile(string id, Action<TeamMemberProfile> mutate, Action<TeamMemberProfile> persist = null)
        {
            if (!_profiles.TryGetValue(id, out var current))
            {
                return null;
            }

            var updated = current.Clone();
            mutate(updated);
            if (persist != null)
            {
                persist(updated);
            }
            else
            {
                _taskDb.SaveProfile(updated);   // persist FIRST
            }

            _profiles[id] = updated;   // swap into cache only after the DB write succeeded
            return updated;
        }

        public TeamMemberProfile InsertProfile(TeamMemberProfile profile)
        {
            _taskDb.SaveProfile(profile);   // persist FIRST
            _profiles[profile.Id] = profile;   // add to cache only after the DB write succeeded
            return profile;
        }

        // Stays private + Internal-suffixed (no cross-region caller — DeleteProfile is the only entry point).
        private bool DeleteProfileInternal(string id)
        {
            if (!_profiles.ContainsKey(id))
            {
                return false;
            }

            _taskDb.DeleteProfile(id);        // delete from DB FIRST
            _profiles.TryRemove(id, out _);   // remove from cache only after the DB delete succeeded
            return true;
        }
    }
}

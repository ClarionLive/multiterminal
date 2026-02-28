using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Writes inter-terminal messages to per-terminal inbox files.
    /// Hook scripts (inbox-check-hook.js) read and delete these files
    /// to inject messages into Claude's context via additionalContext.
    /// This replaces unreliable ConPTY terminal paste injection with
    /// 100% reliable file-based delivery.
    /// </summary>
    public static class InboxFileWriter
    {
        private static readonly string InboxDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "multiterminal", "inbox");

        private static readonly object _writeLock = new object();

        /// <summary>
        /// Write a message to a terminal's inbox file.
        /// Appends to existing messages if the file already has content.
        /// Uses atomic temp-file-rename to prevent partial reads by hooks.
        /// </summary>
        public static bool WriteMessage(string terminalName, string messageId, string sender, string content)
        {
            try
            {
                // Ensure inbox directory exists
                Directory.CreateDirectory(InboxDir);

                string inboxPath = Path.Combine(InboxDir, terminalName + ".json");
                string tempPath = inboxPath + ".tmp";

                lock (_writeLock)
                {
                    // Read existing messages if file exists
                    var messages = new List<InboxMessage>();
                    if (File.Exists(inboxPath))
                    {
                        try
                        {
                            string existing = File.ReadAllText(inboxPath);
                            var parsed = JsonSerializer.Deserialize<List<InboxMessage>>(existing);
                            if (parsed != null)
                                messages = parsed;
                        }
                        catch (JsonException)
                        {
                            // Corrupted file — start fresh
                        }
                    }

                    // Append new message
                    messages.Add(new InboxMessage
                    {
                        Id = messageId,
                        Sender = sender,
                        Content = content,
                        Timestamp = DateTime.UtcNow.ToString("o")
                    });

                    // Write to temp file, then atomic rename
                    string json = JsonSerializer.Serialize(messages, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    File.WriteAllText(tempPath, json);

                    // Atomic rename (overwrites existing inbox file)
                    File.Move(tempPath, inboxPath, overwrite: true);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[InboxFileWriter] Message {messageId} from {sender} written to {terminalName} inbox");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[InboxFileWriter] Failed to write message {messageId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the inbox directory path (for diagnostics/cleanup).
        /// </summary>
        public static string GetInboxDirectory() => InboxDir;

        private class InboxMessage
        {
            public string Id { get; set; }
            public string Sender { get; set; }
            public string Content { get; set; }
            public string Timestamp { get; set; }
        }
    }
}

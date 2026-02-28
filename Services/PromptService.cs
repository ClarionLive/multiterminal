using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Service for managing global prompts.
    /// Global prompts are stored in %APPDATA%\MultiTerminal\prompts.json
    /// Local prompts are stored in project.json via ProjectService.
    /// </summary>
    public class PromptService
    {
        private readonly string _globalPromptsPath;

        public PromptService()
        {
            string appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MultiTerminal");

            try
            {
                if (!Directory.Exists(appDataFolder))
                    Directory.CreateDirectory(appDataFolder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to create prompts folder: {ex.Message}");
            }

            _globalPromptsPath = Path.Combine(appDataFolder, "prompts.json");
        }

        /// <summary>
        /// Gets all global prompts. Local prompts are in project.json via ProjectService.
        /// </summary>
        public List<Prompt> GetAllPrompts(string workingDirectory)
        {
            // Only return global prompts - local prompts are in project.json
            return GetGlobalPrompts();
        }

        /// <summary>
        /// Gets all global prompts.
        /// </summary>
        public List<Prompt> GetGlobalPrompts()
        {
            return LoadPrompts(_globalPromptsPath);
        }

        /// <summary>
        /// Saves a global prompt. Local prompts should be saved via ProjectService.
        /// </summary>
        public void SavePrompt(Prompt prompt, string workingDirectory)
        {
            if (prompt == null)
                throw new ArgumentNullException(nameof(prompt));

            // Local prompts should be saved via ProjectService, not here
            if (!prompt.IsGlobal)
                throw new InvalidOperationException("Local prompts should be saved via ProjectService");

            if (string.IsNullOrEmpty(prompt.Id))
                prompt.Id = Guid.NewGuid().ToString();

            if (prompt.CreatedAt == default)
                prompt.CreatedAt = DateTime.Now;

            var prompts = LoadPrompts(_globalPromptsPath);
            prompts.RemoveAll(p => p.Id == prompt.Id);
            prompts.Add(prompt);
            SavePrompts(_globalPromptsPath, prompts);
        }

        /// <summary>
        /// Deletes a global prompt by ID. Local prompts should be deleted via ProjectService.
        /// </summary>
        public bool DeletePrompt(string id, string workingDirectory)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            var globalPrompts = LoadPrompts(_globalPromptsPath);
            int removed = globalPrompts.RemoveAll(p => p.Id == id);
            if (removed > 0)
            {
                SavePrompts(_globalPromptsPath, globalPrompts);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets all distinct categories from all prompts.
        /// </summary>
        public List<string> GetCategories(string workingDirectory)
        {
            return GetAllPrompts(workingDirectory)
                .Where(p => !string.IsNullOrEmpty(p.Category))
                .Select(p => p.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();
        }

        private List<Prompt> LoadPrompts(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return new List<Prompt>();

            try
            {
                string json = File.ReadAllText(filePath);
                return ParsePromptsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to load prompts from {filePath}: {ex.Message}");
                return new List<Prompt>();
            }
        }

        private void SavePrompts(string filePath, List<Prompt> prompts)
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            try
            {
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string json = SerializePromptsJson(prompts);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to save prompts to {filePath}: {ex.Message}");
            }
        }

        #region Simple JSON Serialization (no external dependencies)

        private string SerializePromptsJson(List<Prompt> prompts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");

            for (int i = 0; i < prompts.Count; i++)
            {
                var p = prompts[i];
                sb.AppendLine("  {");
                sb.AppendLine($"    \"id\": {JsonEscape(p.Id)},");
                sb.AppendLine($"    \"category\": {JsonEscape(p.Category)},");
                sb.AppendLine($"    \"description\": {JsonEscape(p.Description)},");
                sb.AppendLine($"    \"text\": {JsonEscape(p.Text)},");
                sb.AppendLine($"    \"isGlobal\": {(p.IsGlobal ? "true" : "false")},");
                sb.AppendLine($"    \"createdAt\": {JsonEscape(p.CreatedAt.ToString("o"))}");
                sb.Append("  }");
                if (i < prompts.Count - 1)
                    sb.AppendLine(",");
                else
                    sb.AppendLine();
            }

            sb.AppendLine("]");
            return sb.ToString();
        }

        private string JsonEscape(string value)
        {
            if (value == null)
                return "null";

            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private List<Prompt> ParsePromptsJson(string json)
        {
            var prompts = new List<Prompt>();
            if (string.IsNullOrWhiteSpace(json))
                return prompts;

            json = json.Trim();
            if (!json.StartsWith("["))
                return prompts;

            int pos = 1;
            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == ']')
                    break;

                if (json[pos] == '{')
                {
                    var prompt = ParsePromptObject(json, ref pos);
                    if (prompt != null)
                        prompts.Add(prompt);
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            return prompts;
        }

        private Prompt ParsePromptObject(string json, ref int pos)
        {
            if (json[pos] != '{')
                return null;

            pos++;
            var prompt = new Prompt();

            while (pos < json.Length && json[pos] != '}')
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}')
                    break;

                string key = ParseJsonString(json, ref pos);
                SkipWhitespace(json, ref pos);

                if (pos < json.Length && json[pos] == ':')
                    pos++;

                SkipWhitespace(json, ref pos);

                if (key != null)
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "id":
                            prompt.Id = ParseJsonString(json, ref pos);
                            break;
                        case "category":
                            prompt.Category = ParseJsonString(json, ref pos);
                            break;
                        case "description":
                            prompt.Description = ParseJsonString(json, ref pos);
                            break;
                        case "text":
                            prompt.Text = ParseJsonString(json, ref pos);
                            break;
                        case "isglobal":
                            prompt.IsGlobal = ParseJsonBool(json, ref pos);
                            break;
                        case "createdat":
                            string dateStr = ParseJsonString(json, ref pos);
                            if (DateTime.TryParse(dateStr, out var dt))
                                prompt.CreatedAt = dt;
                            break;
                        default:
                            SkipJsonValue(json, ref pos);
                            break;
                    }
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == '}')
                pos++;

            return prompt;
        }

        private void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos]))
                pos++;
        }

        private string ParseJsonString(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);

            if (pos >= json.Length)
                return null;

            if (pos + 3 < json.Length && json.Substring(pos, 4) == "null")
            {
                pos += 4;
                return null;
            }

            if (json[pos] != '"')
                return null;

            pos++;
            var sb = new StringBuilder();

            while (pos < json.Length && json[pos] != '"')
            {
                if (json[pos] == '\\' && pos + 1 < json.Length)
                {
                    pos++;
                    switch (json[pos])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'u':
                            if (pos + 4 < json.Length)
                            {
                                string hex = json.Substring(pos + 1, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                    sb.Append((char)code);
                                pos += 4;
                            }
                            break;
                        default:
                            sb.Append(json[pos]);
                            break;
                    }
                }
                else
                {
                    sb.Append(json[pos]);
                }
                pos++;
            }

            if (pos < json.Length && json[pos] == '"')
                pos++;

            return sb.ToString();
        }

        private bool ParseJsonBool(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);

            if (pos + 3 < json.Length && json.Substring(pos, 4) == "true")
            {
                pos += 4;
                return true;
            }

            if (pos + 4 < json.Length && json.Substring(pos, 5) == "false")
            {
                pos += 5;
                return false;
            }

            return false;
        }

        private void SkipJsonValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length)
                return;

            char c = json[pos];
            if (c == '"')
            {
                ParseJsonString(json, ref pos);
            }
            else if (c == '{')
            {
                int depth = 1;
                pos++;
                while (pos < json.Length && depth > 0)
                {
                    if (json[pos] == '{') depth++;
                    else if (json[pos] == '}') depth--;
                    else if (json[pos] == '"')
                        ParseJsonString(json, ref pos);
                    else
                        pos++;
                }
            }
            else if (c == '[')
            {
                int depth = 1;
                pos++;
                while (pos < json.Length && depth > 0)
                {
                    if (json[pos] == '[') depth++;
                    else if (json[pos] == ']') depth--;
                    else if (json[pos] == '"')
                        ParseJsonString(json, ref pos);
                    else
                        pos++;
                }
            }
            else
            {
                while (pos < json.Length && json[pos] != ',' && json[pos] != '}' && json[pos] != ']')
                    pos++;
            }
        }

        #endregion
    }
}

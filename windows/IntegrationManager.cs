using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexStatusLight
{
    internal sealed class IntegrationSettings
    {
        public string Platform { get; set; }
        public int Brightness { get; set; }
        public bool TaskCountBlinkEnabled { get; set; }
    }

    internal static class IntegrationManager
    {
        internal const string CodexPlatform = "Codex";
        internal const string CursorPlatform = "Cursor";
        internal const int DefaultBrightness = 100;

        private static readonly string[] CodexEvents =
        {
            "UserPromptSubmit",
            "PermissionRequest",
            "PostToolUse",
            "Stop"
        };

        private static readonly string[] CursorObserverEvents =
        {
            "sessionStart",
            "beforeSubmitPrompt",
            "preToolUse",
            "postToolUse",
            "postToolUseFailure",
            "afterShellExecution",
            "afterMCPExecution",
            "stop",
            "sessionEnd"
        };

        private static readonly string[] CursorPermissionEvents =
        {
            "beforeShellExecution",
            "beforeMCPExecution"
        };

        private const string CursorToolPermissionMatcher = "WebSearch|WebFetch";

        internal static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexStatusLight", "settings.json");
            }
        }

        internal static string CodexHooksPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex", "hooks.json");
            }
        }

        internal static string CursorHooksPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cursor", "hooks.json");
            }
        }

        internal static string NormalizePlatform(string platform)
        {
            return string.Equals(platform, CursorPlatform, StringComparison.OrdinalIgnoreCase)
                ? CursorPlatform : CodexPlatform;
        }

        internal static string GetSelectedPlatform()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return CodexPlatform;
                string text = File.ReadAllText(SettingsPath, Encoding.UTF8);
                IntegrationSettings settings =
                    NewSerializer().Deserialize<IntegrationSettings>(text);
                return NormalizePlatform(settings == null ? null : settings.Platform);
            }
            catch (Exception ex)
            {
                Log.Write("Settings read warning: " + ex.Message);
                return CodexPlatform;
            }
        }

        internal static int NormalizeBrightness(int brightness)
        {
            return brightness >= 5 && brightness <= 100
                ? brightness : DefaultBrightness;
        }

        internal static int GetBrightness()
        {
            return GetBrightnessFromFile(SettingsPath);
        }

        internal static void SetBrightness(int brightness)
        {
            SaveSettings(
                SettingsPath,
                GetSelectedPlatform(),
                NormalizeBrightness(brightness),
                GetTaskCountBlinkEnabled());
        }

        internal static bool GetTaskCountBlinkEnabled()
        {
            return GetTaskCountBlinkEnabledFromFile(SettingsPath);
        }

        internal static void SetTaskCountBlinkEnabled(bool enabled)
        {
            SaveSettings(
                SettingsPath,
                GetSelectedPlatform(),
                GetBrightness(),
                enabled);
        }

        internal static bool IsConfigured(string platform)
        {
            string normalized = NormalizePlatform(platform);
            string path = normalized == CursorPlatform ? CursorHooksPath : CodexHooksPath;
            if (!File.Exists(path)) return false;
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (text.IndexOf("CodexStatusBridge.exe", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
                return normalized == CursorPlatform
                    ? text.IndexOf("cursor-hook", StringComparison.OrdinalIgnoreCase) >= 0
                    : text.IndexOf(" hook ", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        internal static void Configure(string platform, string executable)
        {
            ConfigureFiles(
                NormalizePlatform(platform),
                executable,
                CodexHooksPath,
                CursorHooksPath,
                SettingsPath,
                true);
        }

        internal static void RemoveAll(bool deleteSettings)
        {
            RemoveAllFiles(CodexHooksPath, CursorHooksPath, SettingsPath, deleteSettings, true);
        }

        internal static void ConfigureFiles(
            string platform,
            string executable,
            string codexHooksPath,
            string cursorHooksPath,
            string settingsPath,
            bool createBackups)
        {
            string normalized = NormalizePlatform(platform);
            RemoveBridgeHooksFromFile(codexHooksPath, createBackups);
            RemoveBridgeHooksFromFile(cursorHooksPath, createBackups);

            if (normalized == CursorPlatform)
                AddCursorHooks(cursorHooksPath, executable, createBackups);
            else
                AddCodexHooks(codexHooksPath, executable, createBackups);

            SaveSettings(
                settingsPath,
                normalized,
                GetBrightnessFromFile(settingsPath),
                GetTaskCountBlinkEnabledFromFile(settingsPath));
        }

        internal static void RemoveAllFiles(
            string codexHooksPath,
            string cursorHooksPath,
            string settingsPath,
            bool deleteSettings,
            bool createBackups)
        {
            RemoveBridgeHooksFromFile(codexHooksPath, createBackups);
            RemoveBridgeHooksFromFile(cursorHooksPath, createBackups);
            if (deleteSettings && File.Exists(settingsPath))
                File.Delete(settingsPath);
        }

        private static void AddCodexHooks(string path, string executable, bool createBackup)
        {
            Dictionary<string, object> root = ReadRoot(path);
            Dictionary<string, object> hooks = EnsureDictionary(root, "hooks");
            string[] states = { "WORKING", "WAITING", "WORKING", "IDLE" };

            for (int i = 0; i < CodexEvents.Length; ++i)
            {
                string command = "\"" + executable + "\" hook " + states[i];
                var commandHook = new Dictionary<string, object>
                {
                    { "type", "command" },
                    { "command", command },
                    { "commandWindows", command },
                    { "timeout", 3 }
                };
                var handler = new Dictionary<string, object>
                {
                    { "hooks", new object[] { commandHook } }
                };
                hooks[CodexEvents[i]] = Append(hooks, CodexEvents[i], handler);
            }
            SaveRoot(path, root, createBackup);
        }

        private static void AddCursorHooks(string path, string executable, bool createBackup)
        {
            Dictionary<string, object> root = ReadRoot(path);
            root["version"] = 1;
            Dictionary<string, object> hooks = EnsureDictionary(root, "hooks");
            string observerCommand = "\"" + executable + "\" cursor-hook";
            string escapedExecutable = executable.Replace("'", "''");
            string permissionCommand =
                "& '" + escapedExecutable + "' cursor-permission-hook; " +
                "[Console]::Out.Write('{\"permission\":\"ask\"}')";

            foreach (string eventName in CursorObserverEvents)
            {
                var handler = new Dictionary<string, object>
                {
                    { "command", observerCommand },
                    { "timeout", 3 }
                };
                hooks[eventName] = Append(hooks, eventName, handler);
            }

            foreach (string eventName in CursorPermissionEvents)
            {
                var handler = new Dictionary<string, object>
                {
                    { "command", permissionCommand },
                    { "timeout", 3 }
                };
                hooks[eventName] = Append(hooks, eventName, handler);
            }

            var webPermissionHandler = new Dictionary<string, object>
            {
                { "command", permissionCommand },
                { "matcher", CursorToolPermissionMatcher },
                { "timeout", 3 }
            };
            hooks["preToolUse"] = Append(hooks, "preToolUse", webPermissionHandler);
            SaveRoot(path, root, createBackup);
        }

        private static void RemoveBridgeHooksFromFile(string path, bool createBackup)
        {
            if (!File.Exists(path)) return;
            Dictionary<string, object> root = ReadRoot(path);
            object hooksObject;
            Dictionary<string, object> hooks;
            if (!root.TryGetValue("hooks", out hooksObject) ||
                (hooks = hooksObject as Dictionary<string, object>) == null)
                return;

            bool changed = false;
            var eventNames = new List<string>(hooks.Keys);
            foreach (string eventName in eventNames)
            {
                var kept = new List<object>();
                foreach (object handler in Enumerate(hooks[eventName]))
                {
                    if (IsBridgeHook(handler))
                        changed = true;
                    else
                        kept.Add(handler);
                }
                if (kept.Count == 0)
                    hooks.Remove(eventName);
                else
                    hooks[eventName] = kept.ToArray();
            }
            if (changed)
                SaveRoot(path, root, createBackup);
        }

        private static bool IsBridgeHook(object handler)
        {
            string json = NewSerializer().Serialize(handler);
            return json.IndexOf("CodexStatusBridge.exe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   json.IndexOf("CodexStatusLight-OneClick.exe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object[] Append(
            Dictionary<string, object> hooks,
            string eventName,
            object handler)
        {
            var values = new List<object>();
            object existing;
            if (hooks.TryGetValue(eventName, out existing))
                values.AddRange(Enumerate(existing));
            values.Add(handler);
            return values.ToArray();
        }

        private static IEnumerable<object> Enumerate(object value)
        {
            if (value == null) yield break;
            IEnumerable sequence = value as IEnumerable;
            if (sequence == null || value is string)
            {
                yield return value;
                yield break;
            }
            foreach (object item in sequence)
                yield return item;
        }

        private static Dictionary<string, object> ReadRoot(string path)
        {
            if (!File.Exists(path))
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> root =
                NewSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (root == null)
                throw new InvalidOperationException("Hook 配置不是有效的 JSON 对象：" + path);
            return root;
        }

        private static Dictionary<string, object> EnsureDictionary(
            Dictionary<string, object> parent,
            string name)
        {
            object value;
            Dictionary<string, object> dictionary;
            if (parent.TryGetValue(name, out value) &&
                (dictionary = value as Dictionary<string, object>) != null)
                return dictionary;
            dictionary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            parent[name] = dictionary;
            return dictionary;
        }

        private static void SaveRoot(
            string path,
            Dictionary<string, object> root,
            bool createBackup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (createBackup && File.Exists(path))
            {
                string backup = path + ".backup-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                File.Copy(path, backup, false);
            }
            WriteAtomic(path, NewSerializer().Serialize(root));
        }

        private static int GetBrightnessFromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return DefaultBrightness;
                IntegrationSettings settings = NewSerializer().Deserialize<IntegrationSettings>(
                    File.ReadAllText(path, Encoding.UTF8));
                return NormalizeBrightness(settings == null
                    ? DefaultBrightness : settings.Brightness);
            }
            catch (Exception ex)
            {
                Log.Write("Brightness settings read warning: " + ex.Message);
                return DefaultBrightness;
            }
        }

        private static bool GetTaskCountBlinkEnabledFromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                IntegrationSettings settings = NewSerializer().Deserialize<IntegrationSettings>(
                    File.ReadAllText(path, Encoding.UTF8));
                return settings != null && settings.TaskCountBlinkEnabled;
            }
            catch (Exception ex)
            {
                Log.Write("Task count blink settings read warning: " + ex.Message);
                return false;
            }
        }

        private static void SaveSettings(
            string path,
            string platform,
            int brightness,
            bool taskCountBlinkEnabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteAtomic(path, NewSerializer().Serialize(
                new IntegrationSettings
                {
                    Platform = NormalizePlatform(platform),
                    Brightness = NormalizeBrightness(brightness),
                    TaskCountBlinkEnabled = taskCountBlinkEnabled
                }));
        }

        private static void WriteAtomic(string path, string content)
        {
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }

        private static JavaScriptSerializer NewSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 100
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexStatusLight
{
    internal enum LightState
    {
        Idle,
        Review,
        Working,
        Waiting,
        Error
    }

    internal sealed class HookPayload
    {
        public string session_id { get; set; }
        public string turn_id { get; set; }
        public string conversation_id { get; set; }
        public string generation_id { get; set; }
        public string hook_event_name { get; set; }
        public string tool_name { get; set; }
        public string status { get; set; }
        public string reason { get; set; }
        public string failure_type { get; set; }
        public bool is_interrupt { get; set; }
    }

    internal sealed class HookMessage
    {
        public string State { get; set; }
        public string Source { get; set; }
        public string SessionId { get; set; }
        public string TurnId { get; set; }
    }

    internal sealed class SessionRecord
    {
        public SessionPayload payload { get; set; }
    }

    internal sealed class SessionPayload
    {
        public string type { get; set; }
        public string input { get; set; }
    }

    internal sealed class TurnStatus
    {
        public LightState State;
        public DateTime LastUpdatedUtc;
    }

    internal sealed class EffectiveLightStatus
    {
        public LightState State;
        public int ActiveTaskCount;
        public int PendingReviewCount;
    }

    internal sealed class StatusSnapshot
    {
        public bool ConnectionEnabled;
        public bool Connected;
        public string ConnectedPort;
        public string PreferredPort;
        public string FirmwareVersion;
        public string CodexState;
        public int ActiveTaskCount;
        public int PendingReviewCount;
        public string Platform;
        public bool IntegrationConfigured;
        public bool DisplayEnabled;
        public int BrightnessPercent;
        public bool BrightnessSupported;
        public bool TaskCountBlinkEnabled;
        public string StatusText;
    }

    internal sealed class ReviewAcknowledgementData
    {
        public string[] Acknowledged { get; set; }
    }

    internal static class ReviewAcknowledgementStore
    {
        private static string StatePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexStatusLight", "review-state.json");
            }
        }

        internal static HashSet<string> Load()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(StatePath)) return result;
                ReviewAcknowledgementData data = new JavaScriptSerializer().Deserialize<ReviewAcknowledgementData>(
                    File.ReadAllText(StatePath, Encoding.UTF8));
                if (data == null || data.Acknowledged == null) return result;
                foreach (string id in data.Acknowledged)
                    if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            }
            catch (Exception ex)
            {
                Log.Write("Review acknowledgement load warning: " + ex.Message);
            }
            return result;
        }

        internal static void Save(IEnumerable<string> acknowledged)
        {
            try
            {
                string directory = Path.GetDirectoryName(StatePath);
                Directory.CreateDirectory(directory);
                var values = new List<string>();
                foreach (string id in acknowledged)
                    if (!string.IsNullOrWhiteSpace(id)) values.Add(id);
                values.Sort(StringComparer.OrdinalIgnoreCase);
                var data = new ReviewAcknowledgementData { Acknowledged = values.ToArray() };
                File.WriteAllText(StatePath,
                    new JavaScriptSerializer().Serialize(data), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Write("Review acknowledgement save warning: " + ex.Message);
            }
        }
    }

    internal static class ReviewStateReader
    {
        private const int SqliteOpenReadOnly = 0x00000001;
        private const int SqliteRow = 100;
        private const int SqliteDone = 101;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(
            byte[] filename,
            out IntPtr database,
            int flags,
            IntPtr vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(
            IntPtr database,
            byte[] sql,
            int byteCount,
            out IntPtr statement,
            IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);

        internal static bool TryRead(
            string platform,
            out HashSet<string> reviewIds,
            out string error)
        {
            if (string.Equals(platform, IntegrationManager.CursorPlatform,
                StringComparison.OrdinalIgnoreCase))
                return TryReadCursor(out reviewIds, out error);
            return TryReadCodex(out reviewIds, out error);
        }

        internal static HashSet<string> ParseCodexUnreadIds(string json)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return result;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            var container = root;
            object persistedObject;
            if (root != null && root.TryGetValue("electron-persisted-atom-state", out persistedObject))
            {
                var persisted = persistedObject as Dictionary<string, object>;
                if (persisted != null) container = persisted;
            }
            object hostsObject;
            if (container == null ||
                !container.TryGetValue("unread-thread-ids-by-host-v1", out hostsObject))
                return result;
            var hosts = hostsObject as Dictionary<string, object>;
            if (hosts == null) return result;
            foreach (object value in hosts.Values)
            {
                object[] ids = value as object[];
                if (ids == null) continue;
                foreach (object item in ids)
                {
                    string id = item as string;
                    if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
                }
            }
            return result;
        }

        internal static HashSet<string> ParseCursorHeadersJson(string json)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return result;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object headersObject;
            if (root == null || !root.TryGetValue("allComposers", out headersObject))
                return result;
            object[] headers = headersObject as object[];
            if (headers == null) return result;
            foreach (object item in headers)
            {
                var header = item as Dictionary<string, object>;
                if (header == null || !JsonBoolean(header, "hasUnreadMessages") ||
                    JsonBoolean(header, "isArchived") || JsonBoolean(header, "isDraft") ||
                    JsonBoolean(header, "isSubagent"))
                    continue;
                object idObject;
                string id = header.TryGetValue("composerId", out idObject) ? idObject as string : null;
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            }
            return result;
        }

        private static bool TryReadCodex(out HashSet<string> reviewIds, out string error)
        {
            reviewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", ".codex-global-state.json");
            if (!File.Exists(path)) return true;
            try
            {
                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    json = reader.ReadToEnd();
                foreach (string id in ParseCodexUnreadIds(json))
                    reviewIds.Add(IntegrationManager.CodexPlatform + ":" + id);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryReadCursor(out HashSet<string> reviewIds, out string error)
        {
            reviewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(path)) return true;

            var values = new List<string>();
            string query =
                "SELECT composerId FROM composerHeaders " +
                "WHERE ifnull(isArchived,0)=0 AND ifnull(isSubagent,0)=0 " +
                "AND instr(value, '\"hasUnreadMessages\":true')>0 " +
                "AND instr(value, '\"isDraft\":true')=0";
            if (TryQueryStrings(path, query, values, out error))
            {
                foreach (string id in values)
                    if (!string.IsNullOrWhiteSpace(id))
                        reviewIds.Add(IntegrationManager.CursorPlatform + ":" + id);
                return true;
            }

            // Older Cursor builds keep the same headers as one JSON value.
            values.Clear();
            string fallbackError;
            if (!TryQueryStrings(path,
                "SELECT CAST(value AS TEXT) FROM ItemTable WHERE key='composer.composerHeaders'",
                values, out fallbackError))
            {
                error = string.IsNullOrEmpty(error) ? fallbackError : error + "; " + fallbackError;
                return false;
            }
            if (values.Count > 0)
                foreach (string id in ParseCursorHeadersJson(values[0]))
                    reviewIds.Add(IntegrationManager.CursorPlatform + ":" + id);
            error = null;
            return true;
        }

        private static bool TryQueryStrings(
            string path,
            string query,
            List<string> values,
            out string error)
        {
            error = null;
            IntPtr database = IntPtr.Zero;
            IntPtr statement = IntPtr.Zero;
            try
            {
                int code = sqlite3_open_v2(Utf8Bytes(path), out database, SqliteOpenReadOnly, IntPtr.Zero);
                if (code != 0)
                {
                    error = "SQLite open failed: " + SqliteError(database, code);
                    return false;
                }
                code = sqlite3_prepare_v2(database, Utf8Bytes(query), -1, out statement, IntPtr.Zero);
                if (code != 0)
                {
                    error = "SQLite query failed: " + SqliteError(database, code);
                    return false;
                }
                while ((code = sqlite3_step(statement)) == SqliteRow)
                    values.Add(Utf8Column(statement, 0));
                if (code != SqliteDone)
                {
                    error = "SQLite read failed: " + SqliteError(database, code);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (statement != IntPtr.Zero) sqlite3_finalize(statement);
                if (database != IntPtr.Zero) sqlite3_close(database);
            }
        }

        private static byte[] Utf8Bytes(string value)
        {
            return Encoding.UTF8.GetBytes((value ?? "") + "\0");
        }

        private static string Utf8Column(IntPtr statement, int column)
        {
            IntPtr pointer = sqlite3_column_text(statement, column);
            int count = sqlite3_column_bytes(statement, column);
            if (pointer == IntPtr.Zero || count <= 0) return "";
            byte[] bytes = new byte[count];
            Marshal.Copy(pointer, bytes, 0, count);
            return Encoding.UTF8.GetString(bytes);
        }

        private static string SqliteError(IntPtr database, int code)
        {
            if (database == IntPtr.Zero) return "code " + code;
            IntPtr pointer = sqlite3_errmsg(database);
            if (pointer == IntPtr.Zero) return "code " + code;
            int count = 0;
            while (Marshal.ReadByte(pointer, count) != 0 && count < 4096) ++count;
            byte[] bytes = new byte[count];
            Marshal.Copy(pointer, bytes, 0, count);
            return Encoding.UTF8.GetString(bytes) + " (" + code + ")";
        }

        private static bool JsonBoolean(Dictionary<string, object> value, string key)
        {
            object raw;
            if (!value.TryGetValue(key, out raw) || raw == null) return false;
            if (raw is bool) return (bool)raw;
            return string.Equals(Convert.ToString(raw), "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Convert.ToString(raw), "1", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class AppIcon
    {
        private static Icon current;

        internal static Icon Current
        {
            get
            {
                if (current != null) return current;
                try
                {
                    current = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
                catch { }
                if (current == null) current = SystemIcons.Application;
                return current;
            }
        }
    }

    internal static class Program
    {
        internal const int IpcPort = 38451;
        internal const string MutexName = "Local\\CodexStatusLightBridge";
        internal const string StartupValueName = "CodexStatusLightBridge";
        private static readonly string[] StartupValueNames =
        {
            StartupValueName,
            "CodexStatusBridge",
            "CodexStatusLight",
            "CodexStatusLight-OneClick"
        };

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 2 && string.Equals(args[0], "hook", StringComparison.OrdinalIgnoreCase))
                    return SendHook(args[1]);

                if (args.Length >= 1 && string.Equals(args[0], "cursor-hook", StringComparison.OrdinalIgnoreCase))
                    return SendCursorHook(false);

                if (args.Length >= 1 && string.Equals(args[0], "cursor-permission-hook", StringComparison.OrdinalIgnoreCase))
                    return SendCursorHook(true);

                if (args.Length >= 2 && string.Equals(args[0], "--configure-platform", StringComparison.OrdinalIgnoreCase))
                    return ConfigurePlatform(args[1]);

                if (args.Length >= 1 && string.Equals(args[0], "--remove-integrations", StringComparison.OrdinalIgnoreCase))
                    return RemoveIntegrations();

                if (args.Length >= 1 && string.Equals(args[0], "--install-startup", StringComparison.OrdinalIgnoreCase))
                    return ConfigureStartup(true);

                if (args.Length >= 1 && string.Equals(args[0], "--uninstall-startup", StringComparison.OrdinalIgnoreCase))
                    return ConfigureStartup(false);

                if (args.Length >= 1 && string.Equals(args[0], "--display-off", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureBridgeRunning();
                    SendControl("DISPLAY_OFF");
                    return 0;
                }

                if (args.Length >= 2 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                    return RunSelfTest(args[1]);

                if (args.Length >= 2 && string.Equals(args[0], "--exit-self-test", StringComparison.OrdinalIgnoreCase))
                    return RunExitSelfTest(args[1]);

                bool createdNew;
                using (var mutex = new Mutex(true, MutexName, out createdNew))
                {
                    if (!createdNew)
                    {
                        if (args.Length == 0)
                            SendControl("SHOW");
                        return 0;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new BridgeApplicationContext(args.Length == 0));
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("Fatal: " + ex);
                return 1;
            }
        }

        private static int SendHook(string requestedState)
        {
            HookPayload payload = ReadHookPayload(IntegrationManager.CodexPlatform);

            EnsureBridgeRunning();
            var message = new HookMessage
            {
                State = requestedState.ToUpperInvariant(),
                Source = IntegrationManager.CodexPlatform,
                SessionId = payload.session_id ?? "unknown-session",
                TurnId = payload.turn_id ?? "unknown-turn"
            };
            SendMessage(message);
            return 0;
        }

        private static int SendCursorHook(bool requestPermission)
        {
            HookPayload payload = ReadHookPayload(IntegrationManager.CursorPlatform);
            if (!IsUsableCursorHookPayload(payload))
            {
                Log.Write("Cursor hook ignored because its event or session id is missing.");
                return 0;
            }
            HookMessage message = CreateCursorHookMessage(payload, requestPermission);
            EnsureBridgeRunning();
            SendMessage(message);

            return 0;
        }

        private static HookPayload ReadHookPayload(string source)
        {
            try
            {
                byte[] inputBytes;
                using (Stream input = Console.OpenStandardInput())
                using (var buffer = new MemoryStream())
                {
                    input.CopyTo(buffer);
                    inputBytes = buffer.ToArray();
                }
                return DeserializeCursorHookPayload(inputBytes);
            }
            catch (Exception ex)
            {
                Log.Write(source + " hook input parse warning: " + ex.Message);
            }
            return null;
        }

        internal static HookPayload DeserializeCursorHookPayload(byte[] inputBytes)
        {
            if (inputBytes == null || inputBytes.Length == 0)
                return new HookPayload();

            string input;
            if (inputBytes.Length >= 2 && inputBytes[0] == 0xFF && inputBytes[1] == 0xFE)
                input = Encoding.Unicode.GetString(inputBytes, 2, inputBytes.Length - 2);
            else if (inputBytes.Length >= 2 && inputBytes[0] == 0xFE && inputBytes[1] == 0xFF)
                input = Encoding.BigEndianUnicode.GetString(inputBytes, 2, inputBytes.Length - 2);
            else
            {
                int offset = inputBytes.Length >= 3 &&
                    inputBytes[0] == 0xEF && inputBytes[1] == 0xBB && inputBytes[2] == 0xBF
                    ? 3 : 0;
                input = new UTF8Encoding(false, true).GetString(
                    inputBytes, offset, inputBytes.Length - offset);
            }

            input = input.TrimStart('\uFEFF', '\u200B', '\0', ' ', '\t', '\r', '\n');
            if (string.IsNullOrWhiteSpace(input))
                return new HookPayload();
            try
            {
                return new JavaScriptSerializer().Deserialize<HookPayload>(input) ??
                    new HookPayload();
            }
            catch (Exception)
            {
                HookPayload recovered = RecoverCursorHookMetadata(input);
                if (IsUsableCursorHookPayload(recovered))
                {
                    Log.Write("Recovered Cursor hook metadata from malformed JSON input.");
                    return recovered;
                }
                throw;
            }
        }

        internal static bool IsUsableCursorHookPayload(HookPayload payload)
        {
            return payload != null &&
                !string.IsNullOrWhiteSpace(payload.hook_event_name) &&
                (!string.IsNullOrWhiteSpace(payload.conversation_id) ||
                    !string.IsNullOrWhiteSpace(payload.session_id));
        }

        private static HookPayload RecoverCursorHookMetadata(string input)
        {
            return new HookPayload
            {
                conversation_id = ExtractJsonString(input, "conversation_id"),
                session_id = ExtractJsonString(input, "session_id"),
                generation_id = ExtractJsonString(input, "generation_id"),
                turn_id = ExtractJsonString(input, "turn_id"),
                hook_event_name = ExtractJsonString(input, "hook_event_name"),
                status = ExtractJsonString(input, "status"),
                reason = ExtractJsonString(input, "reason")
            };
        }

        private static string ExtractJsonString(string input, string property)
        {
            if (string.IsNullOrEmpty(input)) return null;
            string marker = "\"" + property + "\"";
            int markerIndex = input.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) return null;
            int colonIndex = input.IndexOf(':', markerIndex + marker.Length);
            if (colonIndex < 0) return null;
            int valueStart = colonIndex + 1;
            while (valueStart < input.Length && char.IsWhiteSpace(input[valueStart])) ++valueStart;
            if (valueStart >= input.Length || input[valueStart] != '"') return null;

            var value = new StringBuilder();
            bool escaped = false;
            for (int i = valueStart + 1; i < input.Length; ++i)
            {
                char current = input[i];
                if (escaped)
                {
                    if (current == '"' || current == '\\' || current == '/')
                        value.Append(current);
                    else if (current == 'b') value.Append('\b');
                    else if (current == 'f') value.Append('\f');
                    else if (current == 'n') value.Append('\n');
                    else if (current == 'r') value.Append('\r');
                    else if (current == 't') value.Append('\t');
                    else return null;
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    return value.ToString();
                }
                else
                {
                    value.Append(current);
                }
            }
            return null;
        }

        internal static HookMessage CreateCursorHookMessage(
            HookPayload payload,
            bool requestPermission)
        {
            string eventName = payload.hook_event_name ?? "";
            string state = "WORKING";

            if (requestPermission)
                state = "WAITING";
            else if (string.Equals(eventName, "sessionStart", StringComparison.OrdinalIgnoreCase))
                state = "CLEAR_SESSION";
            else if (string.Equals(eventName, "beforeSubmitPrompt", StringComparison.OrdinalIgnoreCase))
                state = "RESET_WORKING";
            else if (string.Equals(eventName, "stop", StringComparison.OrdinalIgnoreCase))
                state = string.Equals(payload.status, "completed", StringComparison.OrdinalIgnoreCase)
                    ? "IDLE" : "ERROR";
            else if (string.Equals(eventName, "sessionEnd", StringComparison.OrdinalIgnoreCase))
                state = string.Equals(payload.reason, "error", StringComparison.OrdinalIgnoreCase)
                    ? "ERROR" : "CLEAR_SESSION";

            string sessionId = payload.conversation_id ?? payload.session_id ?? "unknown-cursor-session";
            string turnId = payload.generation_id ?? payload.turn_id ?? "unknown-cursor-turn";
            return new HookMessage
            {
                State = state,
                Source = IntegrationManager.CursorPlatform,
                SessionId = sessionId,
                TurnId = turnId
            };
        }

        private static void SendMessage(HookMessage message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(message));
            using (var client = new UdpClient())
            {
                client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, IpcPort));
            }
        }

        private static void EnsureBridgeRunning()
        {
            bool createdNew;
            using (var probe = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                    return;

                probe.ReleaseMutex();
            }

            var start = new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--background",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(start);
            Thread.Sleep(350);
        }

        private static void SendControl(string state)
        {
            var message = new HookMessage
            {
                State = state,
                Source = "Control",
                SessionId = "bridge-control",
                TurnId = "bridge-control"
            };
            SendMessage(message);
        }

        private static int ConfigurePlatform(string platform)
        {
            IntegrationManager.Configure(
                platform,
                Assembly.GetExecutingAssembly().Location);
            return 0;
        }

        private static int RemoveIntegrations()
        {
            IntegrationManager.RemoveAll(true);
            return 0;
        }

        private static int ConfigureStartup(bool install)
        {
            SetStartupEnabled(install, Assembly.GetExecutingAssembly().Location);
            return 0;
        }

        internal static string InstallApplication(bool enableStartup, string platform)
        {
            string installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexStatusLight");
            string target = Path.Combine(installDir, "CodexStatusBridge.exe");
            Directory.CreateDirectory(installDir);

            string current = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                File.Copy(current, target, true);

            SetStartupEnabled(enableStartup, target);
            IntegrationManager.Configure(platform, target);
            return target;
        }

        internal static bool IsInstalled()
        {
            string target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexStatusLight", "CodexStatusBridge.exe");
            return string.Equals(Assembly.GetExecutingAssembly().Location, target, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsStartupEnabled()
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            foreach (string name in StartupValueNames)
            {
                if (File.Exists(Path.Combine(startupFolder, name + ".lnk")) ||
                    File.Exists(Path.Combine(startupFolder, name + ".cmd")) ||
                    File.Exists(Path.Combine(startupFolder, name + ".bat")))
                    return true;
            }

            foreach (RegistryView view in StartupRegistryViews())
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                using (RegistryKey key = baseKey.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Run", false))
                {
                    if (key == null) continue;
                    foreach (string name in StartupValueNames)
                        if (key.GetValue(name) != null) return true;
                }
            }
            return false;
        }

        internal static void SetStartupEnabled(bool enabled)
        {
            string installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexStatusLight", "CodexStatusBridge.exe");
            string executable = File.Exists(installed) ? installed : Assembly.GetExecutingAssembly().Location;
            SetStartupEnabled(enabled, executable);
        }

        private static void SetStartupEnabled(bool enabled, string executable)
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            foreach (string name in StartupValueNames)
            {
                DeleteIfPresent(Path.Combine(startupFolder, name + ".lnk"));
                DeleteIfPresent(Path.Combine(startupFolder, name + ".cmd"));
                DeleteIfPresent(Path.Combine(startupFolder, name + ".bat"));
            }

            // Clean both registry views because older 32-bit builds may have written
            // a separate Run value on 64-bit Windows.
            foreach (RegistryView view in StartupRegistryViews())
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                {
                    bool preferredView = view == PreferredStartupRegistryView();
                    using (RegistryKey key = enabled && preferredView
                        ? baseKey.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")
                        : baseKey.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                    {
                        if (key == null)
                        {
                            if (enabled && preferredView)
                                throw new InvalidOperationException("无法访问当前用户的开机启动设置。");
                            continue;
                        }
                        foreach (string name in StartupValueNames)
                            key.DeleteValue(name, false);
                        if (enabled && preferredView)
                            key.SetValue(StartupValueName, "\"" + executable + "\" --background");
                    }
                }
            }

            if (IsStartupEnabled() != enabled)
                throw new InvalidOperationException(enabled
                    ? "开机自动启动未能启用，请检查启动项权限。"
                    : "仍检测到旧的开机启动项，请检查启动文件夹权限。");
        }

        private static RegistryView[] StartupRegistryViews()
        {
            return Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Registry32 };
        }

        private static RegistryView PreferredStartupRegistryView()
        {
            return Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static int RunSelfTest(string outputPath)
        {
            try
            {
                var json = new JavaScriptSerializer();
                HookPayload payload = json.Deserialize<HookPayload>(
                    "{\"session_id\":\"session-a\",\"turn_id\":\"turn-a\",\"hook_event_name\":\"Stop\"}");
                if (payload == null || payload.session_id != "session-a" || payload.turn_id != "turn-a")
                    throw new InvalidOperationException("Hook JSON parsing failed.");

                var turns = new Dictionary<string, TurnStatus>();
                turns["a"] = new TurnStatus { State = LightState.Working, LastUpdatedUtc = DateTime.UtcNow };
                turns["b"] = new TurnStatus { State = LightState.Waiting, LastUpdatedUtc = DateTime.UtcNow };
                if (BridgeApplicationContext.CountActiveTasks(turns) != 2)
                    throw new InvalidOperationException("Active task counting failed.");
                if (BridgeApplicationContext.CalculateOverallState(turns) != LightState.Waiting)
                    throw new InvalidOperationException("State priority failed.");
                turns.Remove("b");
                if (BridgeApplicationContext.CalculateOverallState(turns) != LightState.Working)
                    throw new InvalidOperationException("Working aggregation failed.");
                turns["error"] = new TurnStatus { State = LightState.Error, LastUpdatedUtc = DateTime.UtcNow };
                turns.Remove("a");
                if (BridgeApplicationContext.CalculateOverallState(turns) != LightState.Error)
                    throw new InvalidOperationException("Error aggregation failed.");
                turns.Clear();
                if (BridgeApplicationContext.CalculateOverallState(turns) != LightState.Idle)
                    throw new InvalidOperationException("Idle aggregation failed.");

                if (BridgeApplicationContext.DeviceCommandFor(LightState.Working, 1, false, true) != "THINKING" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Working, 2, false, true) != "THINKING" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Working, 2, true, true) != "WORKING2" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Working, 3, true, true) != "WORKING3" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Working, 8, true, true) != "WORKING3" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Waiting, 3, false, true) != "PERMISSION" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Review, 0, false, true) != "COMPLETE" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Idle, 0, false, true) != "OFF" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Error, 0, false, true) != "ERROR" ||
                    BridgeApplicationContext.DeviceCommandFor(LightState.Working, 2, true, false) != "OFF")
                    throw new InvalidOperationException("Device command mapping failed.");
                if (!BridgeApplicationContext.SupportsSuspend("5") ||
                    BridgeApplicationContext.SupportsSuspend("4") ||
                    BridgeApplicationContext.SupportsSuspend(null))
                    throw new InvalidOperationException("Firmware suspend capability detection failed.");

                string pendingSerialResponse = "";
                var deviceErrors = new List<string>();
                if (BridgeApplicationContext.ParseSerialResponses(
                        ref pendingSerialResponse, "OK PI", deviceErrors) ||
                    !BridgeApplicationContext.ParseSerialResponses(
                        ref pendingSerialResponse, "NG\r\nOK OFF\r\nERR TEST\r\n", deviceErrors) ||
                    pendingSerialResponse.Length != 0 ||
                    deviceErrors.Count != 1 || deviceErrors[0] != "ERR TEST")
                    throw new InvalidOperationException("Serial response parsing failed.");
                if (IntegrationManager.NormalizeBrightness(65) != 65 ||
                    IntegrationManager.NormalizeBrightness(4) != IntegrationManager.DefaultBrightness ||
                    IntegrationManager.NormalizeBrightness(101) != IntegrationManager.DefaultBrightness)
                    throw new InvalidOperationException("Brightness normalization failed.");

                HashSet<string> codexUnread = ReviewStateReader.ParseCodexUnreadIds(
                    "{\"unread-thread-ids-by-host-v1\":{\"local\":[\"thread-a\",\"thread-b\"]," +
                    "\"remote\":[\"thread-c\"]}}");
                if (codexUnread.Count != 3 || !codexUnread.Contains("thread-b"))
                    throw new InvalidOperationException("Codex unread project parsing failed.");
                codexUnread = ReviewStateReader.ParseCodexUnreadIds(
                    "{\"electron-persisted-atom-state\":{" +
                    "\"unread-thread-ids-by-host-v1\":{\"local\":[\"thread-new\"]}}}");
                if (codexUnread.Count != 1 || !codexUnread.Contains("thread-new"))
                    throw new InvalidOperationException("Codex nested unread project parsing failed.");
                HashSet<string> cursorUnread = ReviewStateReader.ParseCursorHeadersJson(
                    "{\"allComposers\":[" +
                    "{\"composerId\":\"cursor-a\",\"hasUnreadMessages\":true}," +
                    "{\"composerId\":\"cursor-b\",\"hasUnreadMessages\":false}," +
                    "{\"composerId\":\"cursor-draft\",\"hasUnreadMessages\":true,\"isDraft\":true}]}");
                if (cursorUnread.Count != 1 || !cursorUnread.Contains("cursor-a"))
                    throw new InvalidOperationException("Cursor unread project parsing failed.");
                var rawReviews = new HashSet<string>(new[] { "Codex:a", "Codex:b" },
                    StringComparer.OrdinalIgnoreCase);
                var acknowledgedReviews = new HashSet<string>(new[] { "Codex:b" },
                    StringComparer.OrdinalIgnoreCase);
                if (BridgeApplicationContext.CountPendingReviews(rawReviews, acknowledgedReviews) != 1)
                    throw new InvalidOperationException("Review acknowledgement filtering failed.");
                HashSet<string> actualReviewIds;
                string actualReviewError;
                if (!ReviewStateReader.TryRead(IntegrationManager.CodexPlatform,
                    out actualReviewIds, out actualReviewError))
                    throw new InvalidOperationException("Codex unread state read failed: " + actualReviewError);
                if (!ReviewStateReader.TryRead(IntegrationManager.CursorPlatform,
                    out actualReviewIds, out actualReviewError))
                    throw new InvalidOperationException("Cursor unread state read failed: " + actualReviewError);

                HookPayload cursorPayload = json.Deserialize<HookPayload>(
                    "{\"conversation_id\":\"cursor-session\",\"generation_id\":\"cursor-turn\"," +
                    "\"hook_event_name\":\"stop\",\"status\":\"error\"}");
                HookMessage cursorMessage = CreateCursorHookMessage(cursorPayload, false);
                if (cursorMessage.Source != IntegrationManager.CursorPlatform ||
                    cursorMessage.State != "ERROR" ||
                    cursorMessage.SessionId != "cursor-session")
                    throw new InvalidOperationException("Cursor hook mapping failed.");

                string cursorPipeJson =
                    "{\"conversation_id\":\"光标会话\",\"generation_id\":\"并行任务\"," +
                    "\"hook_event_name\":\"beforeSubmitPrompt\"}";
                byte[] cursorPipeBytes = new UTF8Encoding(true).GetBytes(cursorPipeJson);
                HookPayload cursorPipePayload = DeserializeCursorHookPayload(cursorPipeBytes);
                if (cursorPipePayload.conversation_id != "光标会话" ||
                    cursorPipePayload.generation_id != "并行任务" ||
                    cursorPipePayload.hook_event_name != "beforeSubmitPrompt" ||
                    !IsUsableCursorHookPayload(cursorPipePayload))
                    throw new InvalidOperationException("Cursor Windows UTF-8 pipe parsing failed.");

                string malformedCursorJson =
                    "{\"conversation_id\":\"recovered-session\"," +
                    "\"generation_id\":\"recovered-turn\",\"prompt\":\"损坏," +
                    "\"hook_event_name\":\"beforeSubmitPrompt\"}";
                HookPayload recoveredCursorPayload = DeserializeCursorHookPayload(
                    Encoding.UTF8.GetBytes(malformedCursorJson));
                if (recoveredCursorPayload.conversation_id != "recovered-session" ||
                    recoveredCursorPayload.generation_id != "recovered-turn" ||
                    recoveredCursorPayload.hook_event_name != "beforeSubmitPrompt" ||
                    !IsUsableCursorHookPayload(recoveredCursorPayload) ||
                    IsUsableCursorHookPayload(new HookPayload()))
                    throw new InvalidOperationException("Malformed Cursor hook recovery failed.");

                string integrationTestDir = Path.Combine(
                    Path.GetDirectoryName(outputPath),
                    "integration-self-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(integrationTestDir);
                    string codexHooks = Path.Combine(integrationTestDir, "codex-hooks.json");
                    string cursorHooks = Path.Combine(integrationTestDir, "cursor-hooks.json");
                    string settings = Path.Combine(integrationTestDir, "settings.json");
                    File.WriteAllText(codexHooks,
                        "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\"," +
                        "\"command\":\"other-tool.exe\"}]}]}}", Encoding.UTF8);
                    File.WriteAllText(cursorHooks,
                        "{\"version\":1,\"hooks\":{\"stop\":[{\"command\":\"other-cursor-tool.exe\"}]}}",
                        Encoding.UTF8);
                    File.WriteAllText(settings,
                        "{\"Platform\":\"Codex\",\"Brightness\":65," +
                        "\"TaskCountBlinkEnabled\":true}", Encoding.UTF8);

                    IntegrationManager.ConfigureFiles(
                        IntegrationManager.CursorPlatform,
                        @"C:\Test\CodexStatusBridge.exe",
                        codexHooks,
                        cursorHooks,
                        settings,
                        false);
                    string codexText = File.ReadAllText(codexHooks, Encoding.UTF8);
                    string cursorText = File.ReadAllText(cursorHooks, Encoding.UTF8);
                    IntegrationSettings configuredSettings =
                        json.Deserialize<IntegrationSettings>(File.ReadAllText(settings, Encoding.UTF8));
                    if (codexText.IndexOf("other-tool.exe", StringComparison.Ordinal) < 0 ||
                        codexText.IndexOf("CodexStatusBridge.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("Codex hook preservation failed.");
                    if (cursorText.IndexOf("other-cursor-tool.exe", StringComparison.Ordinal) < 0 ||
                        cursorText.IndexOf("cursor-hook", StringComparison.OrdinalIgnoreCase) < 0 ||
                        cursorText.IndexOf("cursor-permission-hook", StringComparison.OrdinalIgnoreCase) < 0 ||
                        cursorText.IndexOf("[Console]::Out.Write", StringComparison.Ordinal) < 0 ||
                        cursorText.IndexOf("WebSearch|WebFetch", StringComparison.Ordinal) < 0 ||
                        configuredSettings == null || configuredSettings.Brightness != 65 ||
                        !configuredSettings.TaskCountBlinkEnabled)
                        throw new InvalidOperationException("Cursor hook configuration failed.");

                    IntegrationManager.ConfigureFiles(
                        IntegrationManager.CodexPlatform,
                        @"C:\Test\CodexStatusBridge.exe",
                        codexHooks,
                        cursorHooks,
                        settings,
                        false);
                    codexText = File.ReadAllText(codexHooks, Encoding.UTF8);
                    cursorText = File.ReadAllText(cursorHooks, Encoding.UTF8);
                    if (codexText.IndexOf(" hook WORKING", StringComparison.OrdinalIgnoreCase) < 0 ||
                        cursorText.IndexOf("CodexStatusBridge.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("Platform switching failed.");
                }
                finally
                {
                    if (Directory.Exists(integrationTestDir))
                        Directory.Delete(integrationTestDir, true);
                }

                Application.EnableVisualStyles();
                var previewContext = (BridgeApplicationContext)FormatterServices.GetUninitializedObject(
                    typeof(BridgeApplicationContext));
                using (var form = new StatusForm(previewContext))
                {
                    form.Size = new Size(620, 520);
                    form.PerformLayout();
                    if (form.StatusColumnCountForTest != 2 ||
                        form.TestColumnCountForTest != 2 ||
                        form.TestRowCountForTest != 4 ||
                        form.UiScaleForTest >= 1F)
                        throw new InvalidOperationException("Compact responsive layout failed.");

                    form.Size = new Size(1100, 780);
                    form.PerformLayout();
                    if (form.StatusColumnCountForTest != 4 ||
                        form.TestColumnCountForTest != 4 ||
                        form.TestRowCountForTest != 2 ||
                        form.UiScaleForTest <= 1F)
                        throw new InvalidOperationException("Expanded responsive layout failed.");
                    form.PrepareForExit();
                }

                File.WriteAllText(outputPath, "PASS\r\n", Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, "FAIL\r\n" + ex, Encoding.UTF8);
                return 1;
            }
        }

        private static int RunExitSelfTest(string outputPath)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var context = new BridgeApplicationContext(false, false);
                ManualResetEvent lockHeld = context.HoldSyncForSelfTest(3000);
                if (!lockHeld.WaitOne(1000))
                    throw new InvalidOperationException("Could not establish the simulated serial lock.");

                long exitMilliseconds = -1;
                var exitTimer = new System.Windows.Forms.Timer { Interval = 150 };
                exitTimer.Tick += delegate
                {
                    exitTimer.Stop();
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    context.ExitFromUi();
                    stopwatch.Stop();
                    exitMilliseconds = stopwatch.ElapsedMilliseconds;
                };
                exitTimer.Start();
                Application.Run(context);
                exitTimer.Dispose();

                if (exitMilliseconds < 0 || exitMilliseconds >= 1000)
                    throw new InvalidOperationException(
                        "UI exit was blocked for " + exitMilliseconds + " ms.");
                File.WriteAllText(outputPath,
                    "PASS exit_ms=" + exitMilliseconds + "\r\n", Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, "FAIL\r\n" + ex, Encoding.UTF8);
                return 1;
            }
        }
    }

    internal sealed class BridgeApplicationContext : ApplicationContext
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, TurnStatus> turns = new Dictionary<string, TurnStatus>();
        private readonly Dictionary<string, long> sessionOffsets = new Dictionary<string, long>();
        private readonly Dictionary<string, LightState> sessionStates = new Dictionary<string, LightState>();
        private readonly Dictionary<string, DateTime> sessionUpdatedUtc = new Dictionary<string, DateTime>();
        private readonly HashSet<string> reviewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> acknowledgedReviewIds = ReviewAcknowledgementStore.Load();
        private readonly NotifyIcon notifyIcon;
        private readonly System.Threading.Timer timer;
        private readonly System.Windows.Forms.Timer startupTimer;
        private readonly Thread listenerThread;
        private UdpClient listener;
        private SerialPort serial;
        private DateTime nextScanUtc = DateTime.MinValue;
        private DateTime nextPingUtc = DateTime.MinValue;
        private DateTime heartbeatDeadlineUtc = DateTime.MinValue;
        private DateTime nextSessionPollUtc = DateTime.MinValue;
        private DateTime nextReviewPollUtc = DateTime.MinValue;
        private DateTime nextReviewErrorLogUtc = DateTime.MinValue;
        private DateTime recentCursorCompletionUntilUtc = DateTime.MinValue;
        private string connectedPort;
        private string serialResponseBuffer = "";
        private string preferredPort = "AUTO";
        private string firmwareVersion = "-";
        private LightState lastLogicalState = LightState.Idle;
        private int lastActiveTaskCount;
        private int lastPendingReviewCount;
        private bool connectionEnabled = true;
        private bool displayEnabled = true;
        private int brightnessPercent = IntegrationManager.DefaultBrightness;
        private bool taskCountBlinkEnabled;
        private bool systemSuspended;
        private bool powerEventsSubscribed;
        private ToolStripMenuItem displayMenuItem;
        private readonly StatusForm statusForm;
        private string statusText = "正在启动并扫描串口";
        private string selectedPlatform;
        private bool integrationConfigured;
        private volatile bool stopping;
        private int tickRunning;
        private int exitStarted;

        internal BridgeApplicationContext(bool showOnStart)
            : this(showOnStart, true)
        {
        }

        internal BridgeApplicationContext(bool showOnStart, bool startWorkers)
        {
            selectedPlatform = IntegrationManager.GetSelectedPlatform();
            brightnessPercent = IntegrationManager.GetBrightness();
            taskCountBlinkEnabled = IntegrationManager.GetTaskCountBlinkEnabled();
            integrationConfigured = IntegrationManager.IsConfigured(selectedPlatform);
            if (string.Equals(selectedPlatform, IntegrationManager.CodexPlatform,
                StringComparison.OrdinalIgnoreCase))
                InitializeSessionOffsets();
            PollPendingReviews();

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示当前状态", null, delegate { ShowStatusWindow(); });
            displayMenuItem = new ToolStripMenuItem("关闭显示");
            displayMenuItem.Click += delegate { ToggleDisplay(); };
            menu.Items.Add(displayMenuItem);
            menu.Items.Add("立即重新扫描串口", null, delegate { ForceRescan(); });
            menu.Items.Add("打开日志", null, delegate { OpenLog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitBridge(); });

            notifyIcon = new NotifyIcon
            {
                Icon = AppIcon.Current,
                Text = "AI 指示灯：正在启动",
                ContextMenuStrip = menu,
                Visible = true
            };
            notifyIcon.DoubleClick += delegate { ShowStatusWindow(); };

            statusForm = new StatusForm(this);

            if (startWorkers)
            {
                listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "CodexStatusLight IPC" };
                listenerThread.Start();
                timer = new System.Threading.Timer(Tick, null, 0, 1000);
                try
                {
                    SystemEvents.PowerModeChanged += OnPowerModeChanged;
                    powerEventsSubscribed = true;
                }
                catch (Exception ex)
                {
                    Log.Write("Power event subscription failed: " + ex.Message);
                }
                Log.Write("Bridge started.");
            }
            else
            {
                listenerThread = null;
                timer = null;
                connectionEnabled = false;
            }

            if (showOnStart)
            {
                startupTimer = new System.Windows.Forms.Timer { Interval = 250 };
                startupTimer.Tick += delegate
                {
                    startupTimer.Stop();
                    ShowStatusWindow();
                };
                startupTimer.Start();
            }
        }

        private void ListenLoop()
        {
            try
            {
                listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, Program.IpcPort));
                while (!stopping)
                {
                    IPEndPoint sender = null;
                    byte[] bytes = listener.Receive(ref sender);
                    string json = Encoding.UTF8.GetString(bytes);
                    HookMessage message = new JavaScriptSerializer().Deserialize<HookMessage>(json);
                    if (message != null)
                        ApplyHookMessage(message);
                }
            }
            catch (SocketException ex)
            {
                if (!stopping) Log.Write("IPC socket error: " + ex.Message);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log.Write("IPC listener error: " + ex);
            }
        }

        private void ApplyHookMessage(HookMessage message)
        {
            if (string.Equals(message.State, "SHOW", StringComparison.OrdinalIgnoreCase))
            {
                ShowStatusWindow();
                return;
            }

            if (string.Equals(message.State, "DISPLAY_OFF", StringComparison.OrdinalIgnoreCase))
            {
                lock (sync)
                {
                    displayEnabled = false;
                    displayMenuItem.Text = "恢复显示";
                    SendStateNoThrow(CalculateEffectiveStatus());
                }
                Log.Write("Display disabled by control command.");
                return;
            }

            string source = string.IsNullOrEmpty(message.Source)
                ? IntegrationManager.CodexPlatform
                : IntegrationManager.NormalizePlatform(message.Source);
            if (!string.Equals(source, selectedPlatform, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write("Ignored " + source + " hook while " + selectedPlatform + " is selected.");
                return;
            }

            string sessionPrefix = source + ":" +
                (message.SessionId ?? "unknown-session") + ":";
            string key = sessionPrefix + (message.TurnId ?? "unknown-turn");
            lock (sync)
            {
                if (string.Equals(message.State, "CLEAR_SESSION", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(message.State, "RESET_WORKING", StringComparison.OrdinalIgnoreCase))
                    RemoveTurnKeys(sessionPrefix);

                if (string.Equals(message.State, "CLEAR_SESSION", StringComparison.OrdinalIgnoreCase))
                {
                    SendStateNoThrow(CalculateEffectiveStatus());
                }
                else if (string.Equals(message.State, "IDLE", StringComparison.OrdinalIgnoreCase))
                {
                    turns.Remove(key);
                    if (string.Equals(source, IntegrationManager.CursorPlatform,
                        StringComparison.OrdinalIgnoreCase))
                        recentCursorCompletionUntilUtc = DateTime.UtcNow.AddSeconds(3);
                }
                else
                {
                    LightState state;
                    if (string.Equals(message.State, "WAITING", StringComparison.OrdinalIgnoreCase))
                        state = LightState.Waiting;
                    else if (string.Equals(message.State, "ERROR", StringComparison.OrdinalIgnoreCase))
                        state = LightState.Error;
                    else
                        state = LightState.Working;
                    turns[key] = new TurnStatus { State = state, LastUpdatedUtc = DateTime.UtcNow };
                }

                SendStateNoThrow(CalculateEffectiveStatus());
            }
            Log.Write(source + " hook " + message.State + " " + key);
        }

        private void RemoveTurnKeys(string prefix)
        {
            var remove = new List<string>();
            foreach (string key in turns.Keys)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    remove.Add(key);
            foreach (string key in remove)
                turns.Remove(key);
        }

        internal static LightState CalculateOverallState(IDictionary<string, TurnStatus> activeTurns)
        {
            bool working = false;
            bool error = false;
            foreach (TurnStatus status in activeTurns.Values)
            {
                if (status.State == LightState.Waiting)
                    return LightState.Waiting;
                if (status.State == LightState.Working)
                    working = true;
                if (status.State == LightState.Error)
                    error = true;
            }
            if (working) return LightState.Working;
            return error ? LightState.Error : LightState.Idle;
        }

        internal static int CountActiveTasks(IDictionary<string, TurnStatus> activeTurns)
        {
            int count = 0;
            foreach (TurnStatus status in activeTurns.Values)
                if (status.State == LightState.Working || status.State == LightState.Waiting)
                    ++count;
            return count;
        }

        internal static int CountPendingReviews(
            IEnumerable<string> unreadIds,
            ISet<string> acknowledgedIds)
        {
            int count = 0;
            foreach (string id in unreadIds)
                if (!acknowledgedIds.Contains(id)) ++count;
            return count;
        }

        private int CurrentPendingReviewCount()
        {
            return CountPendingReviews(reviewIds, acknowledgedReviewIds);
        }

        private EffectiveLightStatus CalculateEffectiveStatus()
        {
            int hookWorking = 0;
            int hookWaiting = 0;
            int hookErrors = 0;
            foreach (TurnStatus turn in turns.Values)
            {
                if (turn.State == LightState.Waiting) ++hookWaiting;
                else if (turn.State == LightState.Working) ++hookWorking;
                else if (turn.State == LightState.Error) ++hookErrors;
            }

            int sessionWorking = 0;
            int sessionWaiting = 0;
            int sessionErrors = 0;
            bool monitorCodex = string.Equals(
                selectedPlatform,
                IntegrationManager.CodexPlatform,
                StringComparison.OrdinalIgnoreCase);
            if (monitorCodex)
            {
                foreach (LightState state in sessionStates.Values)
                {
                    if (state == LightState.Waiting) ++sessionWaiting;
                    else if (state == LightState.Working) ++sessionWorking;
                    else if (state == LightState.Error) ++sessionErrors;
                }
            }

            var result = new EffectiveLightStatus();
            if (hookWaiting + sessionWaiting > 0)
                result.State = LightState.Waiting;
            else if (hookWorking + sessionWorking > 0)
                result.State = LightState.Working;
            else if (hookErrors + sessionErrors > 0)
                result.State = LightState.Error;
            else if (CurrentPendingReviewCount() > 0 ||
                recentCursorCompletionUntilUtc > DateTime.UtcNow)
                result.State = LightState.Review;
            else
                result.State = LightState.Idle;

            result.PendingReviewCount = CurrentPendingReviewCount();

            int hookActive = hookWorking + hookWaiting;
            int sessionActive = sessionWorking + sessionWaiting;
            result.ActiveTaskCount = monitorCodex
                ? Math.Max(hookActive, sessionActive)
                : hookActive;
            if ((result.State == LightState.Working || result.State == LightState.Waiting) &&
                result.ActiveTaskCount == 0)
                result.ActiveTaskCount = 1;
            return result;
        }

        private void Tick(object ignored)
        {
            if (stopping || Interlocked.Exchange(ref tickRunning, 1) != 0)
                return;

            try
            {
                lock (sync)
                {
                    if (systemSuspended)
                        return;
                    RemoveStaleTurns();
                    bool monitorCodex = string.Equals(
                        selectedPlatform,
                        IntegrationManager.CodexPlatform,
                        StringComparison.OrdinalIgnoreCase);
                    bool sessionStateExpired = monitorCodex && RemoveStaleSessionStates();
                    bool cursorCompletionExpired =
                        recentCursorCompletionUntilUtc != DateTime.MinValue &&
                        DateTime.UtcNow >= recentCursorCompletionUntilUtc;
                    if (cursorCompletionExpired)
                        recentCursorCompletionUntilUtc = DateTime.MinValue;
                    bool logicalInputChanged = sessionStateExpired || cursorCompletionExpired;
                    if (monitorCodex && DateTime.UtcNow >= nextSessionPollUtc)
                    {
                        nextSessionPollUtc = DateTime.UtcNow.AddSeconds(1);
                        logicalInputChanged = PollCodexSessions() || logicalInputChanged;
                    }
                    if (DateTime.UtcNow >= nextReviewPollUtc)
                    {
                        nextReviewPollUtc = DateTime.UtcNow.AddSeconds(1);
                        logicalInputChanged = PollPendingReviews() || logicalInputChanged;
                    }
                    if (logicalInputChanged)
                        SendStateNoThrow(CalculateEffectiveStatus());
                    if (!connectionEnabled)
                        return;
                    if (serial == null || !serial.IsOpen)
                    {
                        if (DateTime.UtcNow >= nextScanUtc)
                        {
                            // A failed driver close can take several seconds. Schedule
                            // the next scan after this attempt finishes so timer callbacks
                            // cannot pile up and starve the UI thread.
                            nextScanUtc = DateTime.MaxValue;
                            try { ConnectToDevice(); }
                            finally { nextScanUtc = DateTime.UtcNow.AddSeconds(4); }
                        }
                        return;
                    }

                    try
                    {
                        DrainSerialResponses();
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Serial response read failed: " + ex.Message);
                        DisconnectSerial();
                        return;
                    }

                    if (heartbeatDeadlineUtc != DateTime.MinValue &&
                        DateTime.UtcNow >= heartbeatDeadlineUtc)
                    {
                        Log.Write("Serial heartbeat acknowledgement timed out.");
                        DisconnectSerial();
                        return;
                    }

                    if (DateTime.UtcNow >= nextPingUtc)
                    {
                        try
                        {
                            serial.WriteLine("PING");
                            if (heartbeatDeadlineUtc == DateTime.MinValue)
                                heartbeatDeadlineUtc = DateTime.UtcNow.AddSeconds(8);
                            nextPingUtc = DateTime.UtcNow.AddSeconds(2);
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Serial heartbeat failed: " + ex.Message);
                            DisconnectSerial();
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref tickRunning, 0);
            }
        }

        private void InitializeSessionOffsets()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "sessions");
            if (!Directory.Exists(root)) return;

            try
            {
                DateTime replayCutoff = DateTime.UtcNow.AddMinutes(-30);
                int replayed = 0;
                foreach (string file in Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    FileInfo info = new FileInfo(file);
                    if (info.LastWriteTimeUtc >= replayCutoff)
                    {
                        // Reconstruct currently running tasks after a bridge restart.
                        // The bounded tail avoids loading an unusually large session file.
                        sessionOffsets[file] = Math.Max(0, info.Length - (2 * 1024 * 1024));
                        ++replayed;
                    }
                    else
                    {
                        sessionOffsets[file] = info.Length;
                    }
                }
                Log.Write("Session monitor initialized for " + sessionOffsets.Count +
                    " existing files; replaying " + replayed + " recent files.");
            }
            catch (Exception ex)
            {
                Log.Write("Session monitor initialization warning: " + ex.Message);
            }
        }

        private bool PollCodexSessions()
        {
            bool changed = false;
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "sessions");
            if (!Directory.Exists(root)) return false;

            try
            {
                string[] files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (string file in files)
                {
                    FileInfo info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff) continue;

                    long offset;
                    if (!sessionOffsets.TryGetValue(file, out offset) || offset > info.Length)
                        offset = 0;
                    if (offset == info.Length) continue;

                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Position = offset;
                        int count = checked((int)(stream.Length - offset));
                        byte[] bytes = new byte[count];
                        int total = 0;
                        while (total < count)
                        {
                            int read = stream.Read(bytes, total, count - total);
                            if (read == 0) break;
                            total += read;
                        }

                        string text = Encoding.UTF8.GetString(bytes, 0, total);
                        int lastNewline = text.LastIndexOf('\n');
                        if (lastNewline < 0) continue;

                        string complete = text.Substring(0, lastNewline + 1);
                        sessionOffsets[file] = offset + Encoding.UTF8.GetByteCount(complete);
                        string[] lines = complete.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        LightState currentState;
                        if (lines.Length > 0 &&
                            sessionStates.TryGetValue(file, out currentState) &&
                            currentState != LightState.Idle)
                            sessionUpdatedUtc[file] = DateTime.UtcNow;
                        foreach (string line in lines)
                        {
                            LightState? detectedState = null;
                            SessionRecord record;
                            try
                            {
                                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
                                record = serializer.Deserialize<SessionRecord>(line);
                            }
                            catch { continue; }
                            if (record == null || record.payload == null) continue;

                            if (record.payload.type == "task_started")
                                detectedState = LightState.Working;
                            else if (record.payload.type == "task_complete")
                                detectedState = LightState.Idle;
                            else if (record.payload.type == "custom_tool_call" &&
                                     !string.IsNullOrEmpty(record.payload.input) &&
                                     record.payload.input.IndexOf("\"sandbox_permissions\":\"require_escalated\"", StringComparison.Ordinal) >= 0)
                                detectedState = LightState.Waiting;
                            else if (record.payload.type == "custom_tool_call_output")
                            {
                                LightState previousState;
                                if (sessionStates.TryGetValue(file, out previousState) && previousState == LightState.Waiting)
                                    detectedState = LightState.Working;
                            }

                            if (detectedState.HasValue)
                            {
                                sessionUpdatedUtc[file] = DateTime.UtcNow;
                                LightState previous;
                                if (!sessionStates.TryGetValue(file, out previous) || previous != detectedState.Value)
                                {
                                    sessionStates[file] = detectedState.Value;
                                    changed = true;
                                    Log.Write("Session " + detectedState.Value.ToString().ToUpperInvariant() + " " + Path.GetFileName(file));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Session monitor warning: " + ex.Message);
            }
            return changed;
        }

        private void RemoveStaleTurns()
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-30);
            var stale = new List<string>();
            foreach (KeyValuePair<string, TurnStatus> pair in turns)
                if (pair.Value.LastUpdatedUtc < cutoff) stale.Add(pair.Key);
            foreach (string key in stale) turns.Remove(key);
        }

        private bool RemoveStaleSessionStates()
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-30);
            var stale = new List<string>();
            foreach (KeyValuePair<string, LightState> pair in sessionStates)
            {
                DateTime updated;
                if (pair.Value != LightState.Idle &&
                    (!sessionUpdatedUtc.TryGetValue(pair.Key, out updated) || updated < cutoff))
                    stale.Add(pair.Key);
            }

            foreach (string key in stale)
            {
                sessionStates.Remove(key);
                sessionUpdatedUtc.Remove(key);
                Log.Write("Expired stale session state " + Path.GetFileName(key));
            }
            return stale.Count > 0;
        }

        private bool PollPendingReviews()
        {
            HashSet<string> discovered;
            string error;
            if (!ReviewStateReader.TryRead(selectedPlatform, out discovered, out error))
            {
                if (DateTime.UtcNow >= nextReviewErrorLogUtc)
                {
                    nextReviewErrorLogUtc = DateTime.UtcNow.AddSeconds(30);
                    Log.Write("Unread state monitor warning: " + error);
                }
                return false;
            }

            bool changed = !reviewIds.SetEquals(discovered);
            if (changed)
            {
                reviewIds.Clear();
                foreach (string id in discovered) reviewIds.Add(id);
            }

            string prefix = selectedPlatform + ":";
            var staleAcknowledgements = new List<string>();
            foreach (string id in acknowledgedReviewIds)
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !reviewIds.Contains(id))
                    staleAcknowledgements.Add(id);
            foreach (string id in staleAcknowledgements)
                acknowledgedReviewIds.Remove(id);
            if (staleAcknowledgements.Count > 0)
            {
                ReviewAcknowledgementStore.Save(acknowledgedReviewIds);
                changed = true;
            }

            if (changed)
                Log.Write(selectedPlatform + " unread projects=" + reviewIds.Count +
                    " pending=" + CurrentPendingReviewCount());
            return changed;
        }

        private void ConnectToDevice()
        {
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports, delegate(string left, string right)
            {
                if (string.Equals(left, "COM15", StringComparison.OrdinalIgnoreCase)) return -1;
                if (string.Equals(right, "COM15", StringComparison.OrdinalIgnoreCase)) return 1;
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });

            if (!string.Equals(preferredPort, "AUTO", StringComparison.OrdinalIgnoreCase))
            {
                var selected = new List<string>();
                foreach (string port in ports)
                    if (string.Equals(port, preferredPort, StringComparison.OrdinalIgnoreCase)) selected.Add(port);
                ports = selected.ToArray();
            }

            foreach (string portName in ports)
            {
                SerialPort candidate = null;
                try
                {
                    candidate = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                    {
                        NewLine = "\n",
                        ReadTimeout = 220,
                        WriteTimeout = 500,
                        DtrEnable = false,
                        RtsEnable = false
                    };
                    candidate.Open();
                    candidate.DiscardInBuffer();

                    // Send one probe only. Repeated writes can fill the small
                    // ESP32-C3 USB Serial/JTAG FIFO when USB CDC is disabled.
                    candidate.WriteLine("IDENTIFY");
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(1800);
                    bool identified = false;
                    while (DateTime.UtcNow < deadline && !identified)
                    {
                        try
                        {
                            string line = candidate.ReadLine().Trim();
                            if (line == "CODEX_STATUS_LIGHT:1" ||
                                line == "CODEX_STATUS_LIGHT:2" ||
                                line == "CODEX_STATUS_LIGHT:3" ||
                                line == "CODEX_STATUS_LIGHT:4" ||
                                line == "CODEX_STATUS_LIGHT:5")
                            {
                                identified = true;
                                firmwareVersion = line.Substring(line.LastIndexOf(':') + 1);
                            }
                        }
                        catch (TimeoutException) { }
                    }

                    if (!identified)
                    {
                        candidate.Close();
                        candidate.Dispose();
                        continue;
                    }

                    serial = candidate;
                    connectedPort = portName;
                    nextPingUtc = DateTime.MinValue;
                    heartbeatDeadlineUtc = DateTime.MinValue;
                    serialResponseBuffer = "";
                    SendBrightnessNoThrow();
                    SendStateNoThrow(CalculateEffectiveStatus());
                    UpdateTray("AI 指示灯：已连接 " + portName, AppIcon.Current);
                    Log.Write("Connected to " + portName);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Write("Probe " + portName + " failed: " + ex.Message);
                    if (candidate != null)
                    {
                        try { candidate.Close(); } catch { }
                        candidate.Dispose();
                    }
                }
            }

            UpdateTray("AI 指示灯：未找到设备", SystemIcons.Warning);
        }

        internal static string DeviceCommandFor(
            LightState state,
            int activeTaskCount,
            bool taskCountBlinkEnabled,
            bool displayEnabled)
        {
            if (!displayEnabled) return "OFF";
            if (state == LightState.Working)
            {
                if (!taskCountBlinkEnabled) return "THINKING";
                if (activeTaskCount >= 3) return "WORKING3";
                if (activeTaskCount == 2) return "WORKING2";
                return "THINKING";
            }
            if (state == LightState.Review) return "COMPLETE";
            if (state == LightState.Idle) return "OFF";
            if (state == LightState.Error) return "ERROR";
            return "PERMISSION";
        }

        internal static bool SupportsSuspend(string version)
        {
            return string.Equals(version, "5", StringComparison.Ordinal);
        }

        private void SendStateNoThrow(EffectiveLightStatus status)
        {
            LightState state = status == null ? LightState.Idle : status.State;
            int activeTaskCount = status == null ? 0 : Math.Max(0, status.ActiveTaskCount);
            int pendingReviewCount = status == null ? 0 : Math.Max(0, status.PendingReviewCount);
            if ((state == LightState.Working || state == LightState.Waiting) &&
                activeTaskCount == 0)
                activeTaskCount = 1;
            bool changed = lastLogicalState != state ||
                lastActiveTaskCount != activeTaskCount ||
                lastPendingReviewCount != pendingReviewCount;
            lastLogicalState = state;
            lastActiveTaskCount = activeTaskCount;
            lastPendingReviewCount = pendingReviewCount;
            string deviceCommand = DeviceCommandFor(
                state,
                activeTaskCount,
                taskCountBlinkEnabled,
                displayEnabled);
            if (firmwareVersion != "3" && firmwareVersion != "4" &&
                firmwareVersion != "5" &&
                (deviceCommand == "WORKING2" || deviceCommand == "WORKING3"))
                deviceCommand = "THINKING";
            if (changed)
                Log.Write("Logical state " + state.ToString().ToUpperInvariant() +
                    " tasks=" + activeTaskCount + " reviews=" + pendingReviewCount +
                    " command=" + deviceCommand);
            if (serial == null || !serial.IsOpen) return;
            try
            {
                serial.WriteLine(deviceCommand);
                string taskText = (state == LightState.Working || state == LightState.Waiting)
                    ? " / " + activeTaskCount + " 个任务"
                    : (state == LightState.Review ? " / " + pendingReviewCount + " 个待检查" : "");
                UpdateTray("AI 指示灯：" + state + taskText + " / " + connectedPort,
                    AppIcon.Current);
            }
            catch (Exception ex)
            {
                Log.Write("Serial state write failed: " + ex.Message);
                DisconnectSerial();
            }
        }

        private void DisconnectSerial()
        {
            if (serial != null)
            {
                try { serial.Close(); } catch { }
                serial.Dispose();
                serial = null;
            }
            connectedPort = null;
            firmwareVersion = "-";
            heartbeatDeadlineUtc = DateTime.MinValue;
            serialResponseBuffer = "";
            nextScanUtc = DateTime.UtcNow.AddSeconds(2);
            UpdateTray("AI 指示灯：连接已断开", SystemIcons.Warning);
        }

        private void ToggleDisplay()
        {
            lock (sync)
            {
                displayEnabled = !displayEnabled;
                displayMenuItem.Text = displayEnabled ? "关闭显示" : "恢复显示";
                SendStateNoThrow(CalculateEffectiveStatus());
            }
        }

        internal string[] AvailablePorts()
        {
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports, StringComparer.OrdinalIgnoreCase);
            return ports;
        }

        internal StatusSnapshot Snapshot()
        {
            // The UI refresh timer must never wait behind a slow or broken serial
            // driver. A slightly stale snapshot is preferable to a frozen window.
            SerialPort currentSerial = serial;
            bool connected = false;
            try
            {
                connected = currentSerial != null && currentSerial.IsOpen;
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }

            return new StatusSnapshot
            {
                ConnectionEnabled = connectionEnabled,
                Connected = connected,
                ConnectedPort = connectedPort ?? "-",
                PreferredPort = preferredPort,
                FirmwareVersion = firmwareVersion,
                CodexState = lastLogicalState.ToString(),
                ActiveTaskCount = lastActiveTaskCount,
                PendingReviewCount = lastPendingReviewCount,
                Platform = selectedPlatform ?? IntegrationManager.CodexPlatform,
                IntegrationConfigured = integrationConfigured,
                DisplayEnabled = displayEnabled,
                BrightnessPercent = IntegrationManager.NormalizeBrightness(brightnessPercent),
                BrightnessSupported = firmwareVersion == "4" || firmwareVersion == "5",
                TaskCountBlinkEnabled = taskCountBlinkEnabled,
                StatusText = statusText
            };
        }

        internal void ConnectFromUi(string port)
        {
            lock (sync)
            {
                preferredPort = string.IsNullOrEmpty(port) ? "AUTO" : port;
                connectionEnabled = true;
                DisconnectSerial();
                nextScanUtc = DateTime.MinValue;
            }
        }

        internal void DisconnectFromUi()
        {
            lock (sync)
            {
                connectionEnabled = false;
                SendSuspendNoThrow("manual disconnect");
                DisconnectSerial();
                statusText = "已手动断开";
            }
        }

        internal void SetDisplayFromUi(bool enabled)
        {
            lock (sync)
            {
                displayEnabled = enabled;
                displayMenuItem.Text = displayEnabled ? "关闭显示" : "恢复显示";
                SendStateNoThrow(CalculateEffectiveStatus());
            }
        }

        internal void SetBrightnessFromUi(int brightness)
        {
            lock (sync)
            {
                brightnessPercent = IntegrationManager.NormalizeBrightness(brightness);
                IntegrationManager.SetBrightness(brightnessPercent);
                SendBrightnessNoThrow();
            }
        }

        internal void SetTaskCountBlinkFromUi(bool enabled)
        {
            lock (sync)
            {
                taskCountBlinkEnabled = enabled;
                IntegrationManager.SetTaskCountBlinkEnabled(enabled);
                SendStateNoThrow(CalculateEffectiveStatus());
                Log.Write("Task count blink " + (enabled ? "enabled" : "disabled") + ".");
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (stopping)
                return;

            if (e.Mode == PowerModes.Suspend)
            {
                lock (sync)
                {
                    if (stopping)
                        return;
                    systemSuspended = true;
                    Log.Write("System suspend detected.");
                    SendSuspendNoThrow("system suspend");
                }
                return;
            }

            if (e.Mode == PowerModes.Resume)
            {
                lock (sync)
                {
                    if (stopping)
                        return;
                    systemSuspended = false;
                    Log.Write("System resume detected.");
                    nextPingUtc = DateTime.MinValue;
                    heartbeatDeadlineUtc = DateTime.MinValue;
                    if (serial != null && serial.IsOpen)
                        SendStateNoThrow(CalculateEffectiveStatus());
                    else
                        nextScanUtc = DateTime.MinValue;
                }
            }
        }

        internal void MarkAllReviewsCheckedFromUi()
        {
            lock (sync)
            {
                foreach (string id in reviewIds)
                    acknowledgedReviewIds.Add(id);
                ReviewAcknowledgementStore.Save(acknowledgedReviewIds);
                SendStateNoThrow(CalculateEffectiveStatus());
                Log.Write("All current unread projects marked checked in the bridge.");
            }
        }

        private void SendBrightnessNoThrow()
        {
            if (serial == null || !serial.IsOpen ||
                (firmwareVersion != "4" && firmwareVersion != "5")) return;
            try
            {
                serial.WriteLine("BRIGHTNESS " + brightnessPercent);
                Log.Write("Brightness set to " + brightnessPercent + "%");
            }
            catch (Exception ex)
            {
                Log.Write("Serial brightness write failed: " + ex.Message);
                DisconnectSerial();
            }
        }

        private bool SendSuspendNoThrow(string reason)
        {
            if (serial == null || !serial.IsOpen || !SupportsSuspend(firmwareVersion))
                return false;
            try
            {
                serial.WriteLine("SUSPEND");
                Log.Write("Device suspended for " + reason + ".");
                return true;
            }
            catch (Exception ex)
            {
                // Intentional shutdown is best effort. Unexpected connection loss
                // must still leave the firmware heartbeat error behavior intact.
                Log.Write("Serial suspend command failed during " + reason + ": " + ex.Message);
                return false;
            }
        }

        private void DrainSerialResponses()
        {
            if (serial == null || !serial.IsOpen || serial.BytesToRead <= 0)
                return;

            var deviceErrors = new List<string>();
            bool heartbeatAcknowledged = ParseSerialResponses(
                ref serialResponseBuffer,
                serial.ReadExisting(),
                deviceErrors);
            if (heartbeatAcknowledged)
                heartbeatDeadlineUtc = DateTime.MinValue;
            foreach (string error in deviceErrors)
                Log.Write("Device response: " + error);
        }

        internal static bool ParseSerialResponses(
            ref string pending,
            string incoming,
            IList<string> deviceErrors)
        {
            pending = (pending ?? "") + (incoming ?? "");
            bool heartbeatAcknowledged = false;
            int newlineIndex;
            while ((newlineIndex = pending.IndexOf('\n')) >= 0)
            {
                string line = pending.Substring(0, newlineIndex).Trim();
                pending = pending.Substring(newlineIndex + 1);
                if (string.Equals(line, "OK PING", StringComparison.OrdinalIgnoreCase))
                    heartbeatAcknowledged = true;
                else if (line.StartsWith("ERR ", StringComparison.OrdinalIgnoreCase) &&
                    deviceErrors != null)
                    deviceErrors.Add(line);
            }

            // A valid device response is only a few dozen characters. Bound an
            // unterminated response so a faulty device cannot grow memory forever.
            const int MaxPendingResponseLength = 4096;
            if (pending.Length > MaxPendingResponseLength)
                pending = pending.Substring(pending.Length - MaxPendingResponseLength);
            return heartbeatAcknowledged;
        }

        internal void SendTestCommand(string command)
        {
            lock (sync)
            {
                if (serial == null || !serial.IsOpen)
                    throw new InvalidOperationException("设备尚未连接。");
                serial.WriteLine(command);
            }
        }

        internal bool InstallFromUi(bool enableStartup, string platform)
        {
            string normalized = IntegrationManager.NormalizePlatform(platform);
            string target = Program.InstallApplication(enableStartup, normalized);
            lock (sync)
            {
                selectedPlatform = normalized;
                integrationConfigured = true;
                turns.Clear();
                sessionStates.Clear();
                sessionUpdatedUtc.Clear();
                sessionOffsets.Clear();
                reviewIds.Clear();
                recentCursorCompletionUntilUtc = DateTime.MinValue;
                if (string.Equals(selectedPlatform, IntegrationManager.CodexPlatform,
                    StringComparison.OrdinalIgnoreCase))
                    InitializeSessionOffsets();
                PollPendingReviews();
                SendStateNoThrow(CalculateEffectiveStatus());
            }
            if (!string.Equals(Assembly.GetExecutingAssembly().Location, target, StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(target);
                ExitBridge();
                return true;
            }
            return false;
        }

        internal void ExitFromUi()
        {
            ExitBridge();
        }

        internal ManualResetEvent HoldSyncForSelfTest(int milliseconds)
        {
            var ready = new ManualResetEvent(false);
            var thread = new Thread(new ThreadStart(delegate
            {
                lock (sync)
                {
                    ready.Set();
                    Thread.Sleep(milliseconds);
                }
            }))
            {
                IsBackground = true,
                Name = "CodexStatusLight exit self-test"
            };
            thread.Start();
            return ready;
        }

        private void ForceRescan()
        {
            lock (sync)
            {
                DisconnectSerial();
                nextScanUtc = DateTime.MinValue;
            }
        }

        private void OpenLog()
        {
            try { Process.Start("notepad.exe", Log.Path); }
            catch (Exception ex) { Log.Write("Open log failed: " + ex.Message); }
        }

        private void ShowStatusWindow()
        {
            if (statusForm.InvokeRequired)
            {
                statusForm.BeginInvoke(new Action(ShowStatusWindow));
                return;
            }
            statusForm.Show();
            statusForm.WindowState = FormWindowState.Normal;
            statusForm.BringToFront();
            statusForm.Activate();
        }

        private void UpdateTray(string text, Icon icon)
        {
            try
            {
                statusText = text.Replace("AI 指示灯：", "");
                notifyIcon.Text = text.Length <= 63 ? text : text.Substring(0, 63);
                notifyIcon.Icon = icon;
            }
            catch { }
        }

        private void ExitBridge()
        {
            if (Interlocked.Exchange(ref exitStarted, 1) != 0)
                return;

            stopping = true;
            Log.Write("Bridge exit requested.");
            try { timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }

            bool lockTaken = false;
            try
            {
                // Keep exit responsive if a USB driver is stuck during a scan.
                lockTaken = Monitor.TryEnter(sync, 250);
                if (lockTaken)
                    SendSuspendNoThrow("application exit");
                else
                    Log.Write("Device suspend skipped because the serial worker was busy during exit.");
            }
            finally
            {
                if (lockTaken) Monitor.Exit(sync);
            }

            if (powerEventsSubscribed)
            {
                try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
                powerEventsSubscribed = false;
            }

            // Never wait for the serial scan lock on the UI thread. Some USB serial
            // drivers can block Close/Dispose for several seconds; the operating
            // system will release the handle when this background process exits.
            try { statusForm.PrepareForExit(); } catch { }
            try
            {
                if (startupTimer != null)
                {
                    startupTimer.Stop();
                    startupTimer.Dispose();
                }
            }
            catch { }
            try { timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            try { timer.Dispose(); } catch { }
            try { if (listener != null) listener.Close(); } catch { }
            try { notifyIcon.Visible = false; } catch { }
            try { notifyIcon.Dispose(); } catch { }
            ExitThread();
        }
    }

    internal static class Log
    {
        private static readonly object Sync = new object();
        internal static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexStatusLight", "bridge.log");

        internal static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}

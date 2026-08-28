using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace IZCode.Mod.Diagnostics
{
    public enum IZLogLevel
    {
        Off = 0,
        Error = 1,
        Warn = 2,
        Info = 3,
        Debug = 4,
        Trace = 5,
    }

    /// <summary>
    /// The subject of each message. It exists so only the part being investigated can
    /// be switched on: <c>Highlight</c> and <c>Hover</c> talk on every frame and every
    /// keystroke, and would drown out the rest if left on.
    /// </summary>
    [Flags]
    public enum IZLogArea
    {
        None = 0,
        Load = 1 << 0,        // mod loading, Harmony patches, console commands
        Chip = 1 << 1,        // chip compilation and lifecycle
        Vm = 1 << 2,          // per-tick execution
        Editor = 1 << 3,      // overlay, editor context, keys
        Completion = 1 << 4,  // code completion
        Hover = 1 << 5,       // tooltip
        Highlight = 1 << 6,   // syntax highlighting
        Catalog = 1 << 7,     // prefab scanning
        All = Load | Chip | Vm | Editor | Completion | Hover | Highlight | Catalog,
    }

    /// <summary>
    /// The mod's log - and the only place where it is switched on and off.
    ///
    /// Three ways to change it, from the most permanent to the most immediate:
    ///
    ///   1. the <c>Default*</c> constants just below, for whoever compiles it;
    ///   2. the <c>Documents\My Games\Stationeers\izcode\log.cfg</c> file, read at load
    ///      time and rewritten on every change;
    ///   3. the <c>izcode_log</c> console command, which changes it live and persists it.
    ///
    /// The channel is Unity's <c>Debug.Log</c> (which lands in Player.log) and,
    /// optionally, a file of its own - useful because Player.log is rewritten every
    /// session and lives somewhere nobody finds.
    /// </summary>
    public static class IZLog
    {
        // ==================================================================
        //  THE SWITCH
        // ==================================================================

        /// <summary>Master switch. When false no message comes out, whatever happens.</summary>
        public const bool DefaultEnabled = true;

        /// <summary>How far to talk. <c>Info</c> tells what happened without narrating frames.</summary>
        public const IZLogLevel DefaultLevel = IZLogLevel.Info;

        /// <summary>
        /// Subjects on by default. Hover and Highlight are left out because they talk
        /// on every frame and every keystroke.
        /// </summary>
        public const IZLogArea DefaultAreas = IZLogArea.All & ~(IZLogArea.Hover | IZLogArea.Highlight);

        /// <summary>Besides Player.log, write to <c>izcode\izcode.log</c>.</summary>
        public const bool DefaultWriteFile = true;

        // ==================================================================

        private const string ConfigFileName = "log.cfg";
        private const string LogFileName = "izcode.log";
        private const string Prefix = "[IZCode]";

        /// <summary>A log file that grows too fat is worse than none.</summary>
        private const long MaxLogFileBytes = 2 * 1024 * 1024;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, float> Throttles = new Dictionary<string, float>();
        private static readonly HashSet<string> Seen = new HashSet<string>();

        private static StreamWriter? _file;
        private static bool _fileFailed;

        public static bool Enabled { get; private set; } = DefaultEnabled;
        public static IZLogLevel Level { get; private set; } = DefaultLevel;
        public static IZLogArea Areas { get; private set; } = DefaultAreas;
        public static bool WriteFile { get; private set; } = DefaultWriteFile;

        public static string ConfigPath => IZPaths.Combine(ConfigFileName);
        public static string LogPath => IZPaths.Combine(LogFileName);

        // ------------------------------------------------------------------
        //  Queries
        // ------------------------------------------------------------------

        /// <summary>
        /// Is it worth building the message? Use it before any expensive concatenation -
        /// the point of switching the log off is not paying for it.
        /// </summary>
        public static bool IsOn(IZLogArea area, IZLogLevel level) =>
            Enabled && level != IZLogLevel.Off && level <= Level && (Areas & area) != 0;

        // ------------------------------------------------------------------
        //  Writing
        // ------------------------------------------------------------------

        public static void Error(IZLogArea area, string message) => Write(area, IZLogLevel.Error, message);
        public static void Warn(IZLogArea area, string message) => Write(area, IZLogLevel.Warn, message);
        public static void Info(IZLogArea area, string message) => Write(area, IZLogLevel.Info, message);
        public static void Debug(IZLogArea area, string message) => Write(area, IZLogLevel.Debug, message);
        public static void Trace(IZLogArea area, string message) => Write(area, IZLogLevel.Trace, message);

        /// <summary>
        /// Always comes out, even with the log off.
        ///
        /// Reserved for mod loading. "Did the mod load?" is the first question of every
        /// diagnosis, and if the answer depended on the log configuration a mod with
        /// logging off would be indistinguishable from one that was never called.
        /// </summary>
        public static void Banner(string message)
        {
            try { UnityEngine.Debug.Log(Prefix + " " + message); }
            catch { }

            AppendToFile(IZLogLevel.Info, IZLogArea.Load, message);
        }

        /// <summary>An exception with context. Always Error level, always with the stack.</summary>
        public static void Exception(IZLogArea area, string context, Exception ex) =>
            Write(area, IZLogLevel.Error, context + ": " + ex);

        /// <summary>
        /// Writes at most once every <paramref name="seconds"/> for the same key. For
        /// whatever runs in <c>Update</c>: without it a single problem in the overlay
        /// fills Player.log within seconds.
        /// </summary>
        public static void Throttled(IZLogArea area, IZLogLevel level, string key,
                                     float seconds, Func<string> message)
        {
            if (!IsOn(area, level)) return;

            float now;
            try { now = Time.realtimeSinceStartup; }
            catch { now = 0f; }                       // outside the Unity thread

            lock (Gate)
            {
                if (Throttles.TryGetValue(key, out float last) && now - last < seconds) return;
                Throttles[key] = now;
            }

            Write(area, level, message());
        }

        /// <summary>Writes only the first time the key shows up in this session.</summary>
        public static void Once(IZLogArea area, IZLogLevel level, string key, string message)
        {
            if (!IsOn(area, level)) return;

            lock (Gate)
            {
                if (!Seen.Add(key)) return;
            }

            Write(area, level, message);
        }

        private static void Write(IZLogArea area, IZLogLevel level, string message)
        {
            if (!IsOn(area, level)) return;

            string line = Prefix + " " + Tag(level) + " " + area + ": " + message;

            try
            {
                switch (level)
                {
                    case IZLogLevel.Error: UnityEngine.Debug.LogError(line); break;
                    case IZLogLevel.Warn: UnityEngine.Debug.LogWarning(line); break;
                    default: UnityEngine.Debug.Log(line); break;
                }
            }
            catch
            {
                // No Unity (background thread, tests): the file still works.
            }

            AppendToFile(level, area, message);
        }

        private static string Tag(IZLogLevel level)
        {
            switch (level)
            {
                case IZLogLevel.Error: return "ERROR";
                case IZLogLevel.Warn: return "WARN ";
                case IZLogLevel.Info: return "INFO ";
                case IZLogLevel.Debug: return "DEBUG";
                default: return "TRACE";
            }
        }

        // ------------------------------------------------------------------
        //  File
        // ------------------------------------------------------------------

        private static void AppendToFile(IZLogLevel level, IZLogArea area, string message)
        {
            if (!WriteFile || _fileFailed) return;

            lock (Gate)
            {
                try
                {
                    if (_file == null && !OpenFileNoLock()) return;

                    _file!.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    _file.Write(' ');
                    _file.Write(Tag(level));
                    _file.Write(' ');
                    _file.Write(area.ToString());
                    _file.Write(": ");
                    _file.WriteLine(message);
                    _file.Flush();          // the game can close without warning
                }
                catch
                {
                    // Disk full, file locked: give up on the file, keep Unity.
                    _fileFailed = true;
                    CloseFileNoLock();
                }
            }
        }

        private static bool OpenFileNoLock()
        {
            if (!IZPaths.EnsureFolder()) { _fileFailed = true; return false; }

            try
            {
                // Truncate what grew too fat instead of growing without bound.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogFileBytes)
                    File.Delete(LogPath);

                _file = new StreamWriter(LogPath, true, new UTF8Encoding(false));
                _file.WriteLine();
                _file.WriteLine("=== session started at " +
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                " ===");
                return true;
            }
            catch
            {
                _fileFailed = true;
                return false;
            }
        }

        private static void CloseFileNoLock()
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
        }

        public static void CloseFile()
        {
            lock (Gate) CloseFileNoLock();
        }

        // ------------------------------------------------------------------
        //  Configuration
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads <c>log.cfg</c>. Called once at load time; when the file does not exist
        /// it writes one with the defaults and the comments explaining each key - that
        /// way what to change can be found without opening the documentation.
        /// </summary>
        public static void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    SaveConfig();
                    return;
                }

                foreach (string raw in File.ReadAllLines(ConfigPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "enabled": Enabled = ParseBool(value, DefaultEnabled); break;
                        case "level": Level = ParseLevel(value, DefaultLevel); break;
                        case "areas": Areas = ParseAreas(value, DefaultAreas); break;
                        case "file": WriteFile = ParseBool(value, DefaultWriteFile); break;
                    }
                }
            }
            catch (Exception ex)
            {
                // A broken config must not stop the mod from loading.
                try { UnityEngine.Debug.LogWarning(Prefix + " could not read " + ConfigPath + ": " + ex.Message); }
                catch { }
            }
        }

        public static bool SaveConfig()
        {
            try
            {
                if (!IZPaths.EnsureFolder()) return false;

                var sb = new StringBuilder();
                sb.AppendLine("# IZCode - log. Editable with the game closed, or through the");
                sb.AppendLine("# 'izcode_log' console command, which rewrites this file.");
                sb.AppendLine("#");
                sb.AppendLine("# enabled  true|false   master switch");
                sb.AppendLine("# level    off|error|warn|info|debug|trace");
                sb.AppendLine("# areas    all|none|comma separated list:");
                sb.AppendLine("#          load,chip,vm,editor,completion,hover,highlight,catalog");
                sb.AppendLine("# file     true|false   also write to izcode.log");
                sb.AppendLine();
                sb.Append("enabled=").AppendLine(Enabled ? "true" : "false");
                sb.Append("level=").AppendLine(Level.ToString().ToLowerInvariant());
                sb.Append("areas=").AppendLine(FormatAreas(Areas));
                sb.Append("file=").AppendLine(WriteFile ? "true" : "false");

                File.WriteAllText(ConfigPath, sb.ToString());
                return true;
            }
            catch (Exception ex)
            {
                try { UnityEngine.Debug.LogWarning(Prefix + " could not write " + ConfigPath + ": " + ex.Message); }
                catch { }
                return false;
            }
        }

        public static void SetEnabled(bool value) { Enabled = value; SaveConfig(); }
        public static void SetLevel(IZLogLevel value) { Level = value; SaveConfig(); }
        public static void SetAreas(IZLogArea value) { Areas = value; SaveConfig(); }

        public static void SetWriteFile(bool value)
        {
            WriteFile = value;
            if (!value) CloseFile();
            else lock (Gate) { _fileFailed = false; }
            SaveConfig();
        }

        /// <summary>Current state on one line, for the console command.</summary>
        public static string Describe() =>
            "enabled=" + (Enabled ? "true" : "false") +
            "  level=" + Level.ToString().ToLowerInvariant() +
            "  areas=" + FormatAreas(Areas) +
            "  file=" + (WriteFile ? "true" : "false");

        // ------------------------------------------------------------------
        //  Parsing
        // ------------------------------------------------------------------

        private static bool ParseBool(string value, bool fallback)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "on": case "yes": return true;
                case "false": case "0": case "off": case "no": return false;
                default: return fallback;
            }
        }

        public static bool TryParseLevel(string value, out IZLogLevel level)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "off": case "none": level = IZLogLevel.Off; return true;
                case "error": level = IZLogLevel.Error; return true;
                case "warn": case "warning": level = IZLogLevel.Warn; return true;
                case "info": level = IZLogLevel.Info; return true;
                case "debug": level = IZLogLevel.Debug; return true;
                case "trace": case "all": level = IZLogLevel.Trace; return true;
                default: level = IZLogLevel.Info; return false;
            }
        }

        private static IZLogLevel ParseLevel(string value, IZLogLevel fallback) =>
            TryParseLevel(value, out var level) ? level : fallback;

        public static bool TryParseAreas(string value, out IZLogArea areas)
        {
            areas = IZLogArea.None;
            string trimmed = value.Trim().ToLowerInvariant();

            if (trimmed == "all") { areas = IZLogArea.All; return true; }
            if (trimmed == "none") { areas = IZLogArea.None; return true; }

            bool any = false;
            foreach (string part in trimmed.Split(',', ' ', ';'))
            {
                string name = part.Trim();
                if (name.Length == 0) continue;

                switch (name)
                {
                    case "load": areas |= IZLogArea.Load; any = true; break;
                    case "chip": areas |= IZLogArea.Chip; any = true; break;
                    case "vm": areas |= IZLogArea.Vm; any = true; break;
                    case "editor": areas |= IZLogArea.Editor; any = true; break;
                    case "completion": areas |= IZLogArea.Completion; any = true; break;
                    case "hover": areas |= IZLogArea.Hover; any = true; break;
                    case "highlight": areas |= IZLogArea.Highlight; any = true; break;
                    case "catalog": areas |= IZLogArea.Catalog; any = true; break;
                    default: return false;
                }
            }
            return any;
        }

        private static IZLogArea ParseAreas(string value, IZLogArea fallback) =>
            TryParseAreas(value, out var areas) ? areas : fallback;

        private static string FormatAreas(IZLogArea areas)
        {
            if (areas == IZLogArea.All) return "all";
            if (areas == IZLogArea.None) return "none";

            var sb = new StringBuilder();
            Append(sb, areas, IZLogArea.Load, "load");
            Append(sb, areas, IZLogArea.Chip, "chip");
            Append(sb, areas, IZLogArea.Vm, "vm");
            Append(sb, areas, IZLogArea.Editor, "editor");
            Append(sb, areas, IZLogArea.Completion, "completion");
            Append(sb, areas, IZLogArea.Hover, "hover");
            Append(sb, areas, IZLogArea.Highlight, "highlight");
            Append(sb, areas, IZLogArea.Catalog, "catalog");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, IZLogArea areas, IZLogArea flag, string name)
        {
            if ((areas & flag) == 0) return;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name);
        }
    }
}

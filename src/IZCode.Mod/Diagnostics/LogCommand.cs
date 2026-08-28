using System;
using System.Text;
using Util.Commands;

namespace IZCode.Mod.Diagnostics
{
    /// <summary>
    /// The <c>izcode_log</c> console command: switches the log on, off and tunes it
    /// without leaving the game.
    ///
    /// Every change is written to <c>log.cfg</c> right away, so what is chosen here
    /// also applies to the next session.
    /// </summary>
    public sealed class LogCommand : CommandBase
    {
        public const string Key = "izcode_log";

        public override string HelpText =>
            "Switches the IZCode log on and off. 'izcode_log' shows the state; " +
            "'on'/'off' the master switch; 'level <off|error|warn|info|debug|trace>'; " +
            "'areas <all|none|load,chip,vm,editor,completion,hover,highlight,catalog>'; " +
            "'file on|off'; 'path' shows where the file is.";

        public override string[] Arguments =>
            new[] { "[on|off|level|areas|file|path]", "[value]" };

        public override bool IsLaunchCmd => false;

        public override string Execute(string[] args)
        {
            try
            {
                // The game hands the arguments over WITHOUT the command name.
                if (args == null || args.Length == 0) return Status();

                string verb = args[0].Trim().ToLowerInvariant();
                string value = args.Length > 1 ? args[1] : string.Empty;

                switch (verb)
                {
                    case "on":
                    case "true":
                        IZLog.SetEnabled(true);
                        IZLog.Info(IZLogArea.Load, "log switched on from the console");
                        return Status();

                    case "off":
                    case "false":
                        // The last message goes out before switching off, so the file
                        // records why the log stopped.
                        IZLog.Info(IZLogArea.Load, "log switched off from the console");
                        IZLog.SetEnabled(false);
                        return Status();

                    case "level":
                        if (!IZLog.TryParseLevel(value, out var level))
                            return "invalid level: '" + value + "'. Use off, error, warn, info, debug or trace.";
                        IZLog.SetLevel(level);
                        return Status();

                    case "areas":
                    case "area":
                        if (!IZLog.TryParseAreas(JoinRest(args), out var areas))
                            return "invalid area: '" + value + "'. Use all, none, or a list of " +
                                   "load,chip,vm,editor,completion,hover,highlight,catalog.";
                        IZLog.SetAreas(areas);
                        return Status();

                    case "file":
                        if (value.Length == 0) return "use 'izcode_log file on' or 'izcode_log file off'.";
                        IZLog.SetWriteFile(value.Trim().ToLowerInvariant() != "off" &&
                                           value.Trim().ToLowerInvariant() != "false");
                        return Status();

                    case "path":
                        return IZLog.LogPath + "\n" + IZLog.ConfigPath;

                    default:
                        return "did not understand '" + verb + "'.\n" + HelpText;
                }
            }
            catch (Exception ex)
            {
                return "failed: " + ex.Message;
            }
        }

        /// <summary>'areas load, chip' arrives split across several arguments.</summary>
        private static string JoinRest(string[] args)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(args[i]);
            }
            return sb.ToString();
        }

        private static string Status() =>
            IZLog.Describe() + "\n" + IZLog.LogPath;
    }
}

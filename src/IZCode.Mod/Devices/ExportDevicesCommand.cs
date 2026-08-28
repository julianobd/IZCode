using System;
using System.Text;
using IZCode.Mod.Diagnostics;
using Util.Commands;

namespace IZCode.Mod.Devices
{
    /// <summary>
    /// The <c>izcode_devices</c> console command: rescans and writes the catalog.
    ///
    /// The automatic scan already happens when the game version changes, but
    /// installing another mod that adds devices does not change the version - which is
    /// why a way to force it is worth having.
    /// </summary>
    public sealed class ExportDevicesCommand : CommandBase
    {
        public const string Key = "izcode_devices";

        public override string HelpText =>
            "Scans the prefabs and writes the IZCode device catalog (devices.txt and devices.json).";

        public override string[] Arguments => new[] { "[rescan]" };

        public override bool IsLaunchCmd => false;

        public override string Execute(string[] args)
        {
            try
            {
                // The game hands the arguments over WITHOUT the command name:
                // 'izcode_devices rescan' arrives as args[0] == "rescan".
                bool force = args != null && args.Length > 0 &&
                             string.Equals(args[0], "rescan", StringComparison.OrdinalIgnoreCase);

                var catalog = force
                    ? CatalogStore.Rescan(Log)
                    : CatalogStore.LoadOrScan(Log);

                var sb = new StringBuilder();
                sb.Append(catalog.Devices.Count).Append(" devices");

                int properties = 0;
                foreach (var device in catalog.Devices) properties += device.Properties.Count;
                sb.Append(", ").Append(properties).Append(" properties\n");

                sb.Append(CatalogStore.CatalogPath).Append('\n');
                sb.Append(CatalogStore.JsonPath);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                IZLog.Exception(IZLogArea.Catalog, "izcode_devices failed", ex);
                return "failed: " + ex.Message;
            }
        }

        private static void Log(string message) => IZLog.Info(IZLogArea.Catalog, message);
    }
}

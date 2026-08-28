using System;
using System.IO;

namespace IZCode.Mod.Diagnostics
{
    /// <summary>
    /// Where the mod writes what has to outlive the game: device catalog, log
    /// configuration and the log itself.
    ///
    /// Always in the user's folder, never in the mod's: a mod coming from the Workshop
    /// may sit in a read-only directory.
    /// </summary>
    public static class IZPaths
    {
        private const string FolderName = "izcode";

        private static string? _folder;

        /// <summary><c>Documents\My Games\Stationeers\izcode</c>.</summary>
        public static string Folder
        {
            get
            {
                if (_folder != null) return _folder;

                try
                {
                    string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    _folder = Path.Combine(Path.Combine(documents, "My Games", "Stationeers"), FolderName);
                }
                catch
                {
                    // No documents folder (dedicated server, sandbox): fall back to the
                    // working directory, which always exists.
                    _folder = Path.Combine(Directory.GetCurrentDirectory(), FolderName);
                }
                return _folder;
            }
        }

        public static string Combine(string fileName) => Path.Combine(Folder, fileName);

        /// <summary>Creates the folder if it can; returns false instead of throwing.</summary>
        public static bool EnsureFolder()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

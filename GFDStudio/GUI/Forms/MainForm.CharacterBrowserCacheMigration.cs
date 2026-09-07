using System;
using System.IO;

namespace GFDStudio.GUI.Forms
{
    public partial class MainForm
    {
        // BodyTargetNames in the Character Browser animation scan cache used to contain
        // only literal controller names. Cross-game compatibility now expands those
        // names with semantic P5/R <-> Dancing aliases, so invalidate the old cache once.
        private readonly bool mCharacterBrowserCrossGameCacheMigration =
            InvalidateLegacyCharacterBrowserAnimationCache();

        private static bool InvalidateLegacyCharacterBrowserAnimationCache()
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GFDStudio");
                var markerPath = Path.Combine(
                    directory,
                    "character_browser_animation_scan.cross_game_aliases_v1");
                if (File.Exists(markerPath))
                    return true;

                Directory.CreateDirectory(directory);

                var cachePath = Path.Combine(directory, "character_browser_animation_scan.bin");
                if (File.Exists(cachePath))
                    File.Delete(cachePath);

                File.WriteAllText(markerPath, "1");
            }
            catch
            {
                // Cache migration is best-effort. A failed delete only means the user
                // may need to rescan/delete the cache manually once.
            }

            return true;
        }
    }
}

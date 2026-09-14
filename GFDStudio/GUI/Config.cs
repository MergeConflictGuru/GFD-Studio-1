using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GFDStudio
{
    public class Config
    {
        private static string SettingsPath =>
            Path.Combine( AppContext.BaseDirectory, "Config.json" );

        public bool DarkMode { get; set; } = true;
        public bool RetainTextureNames { get; set; } = true;
        public bool RetainMaterialColors { get; set; } = false;
        public bool SaveReplacedTexturesExternally { get; set; } = true;
        // Keep the legacy result as the safe default. Local bind-space mode is
        // an opt-in compatibility switch for rigs that need it.
        public bool UseLocalBindSpaceRetargeting { get; set; } = false;

        public void SaveJson( Config settings )
        {
            File.WriteAllText( SettingsPath, JsonConvert.SerializeObject( settings, Newtonsoft.Json.Formatting.Indented ) );
        }

        public Config LoadJson()
        {
            if ( !File.Exists( SettingsPath ) )
                return new Config();

            return JsonConvert.DeserializeObject<Config>( File.ReadAllText( SettingsPath ) );
        }
    }
    
}

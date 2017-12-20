using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    class Constants
    {
        // This is needed for version, since torch *also* loads the version from the plugin attribute. :-(
        public const string Version = "1.1.3";
        public static readonly string ERROR_Reflection = "WARNING: Could not reflect '{0}', some functions may not work";
        public static readonly string SettingsFilename = "PrivateMods.cfg";
        public static readonly string ScriptFilename = "privatemods_script.txt";

        public const bool IsDebug =
#if DEBUG
            true
#else
            false
#endif
            ;
    }
}

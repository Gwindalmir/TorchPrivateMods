using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    public class Settings
    {
        public bool UseKeenWorkshopCode = true;
        public bool ContinueOnError = true;
        public bool AlwaysUseSteamCMD = false;
        public string SteamCMDPath;
        public string SteamUsername;
        public string EncryptedSteamPassword;
    }
}

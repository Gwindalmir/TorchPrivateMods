using NLog;
using Sandbox;
using Sandbox.Engine.Networking;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using VRage.Game;
using VRage.Utils;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    [Plugin("Private Mods", Constants.Version, "AA4988CB-7693-46AA-B3FA-219A28653256")]
    public class PrivateModsPlugin : TorchPluginBase, INotifyPropertyChanged, IWpfPlugin
    {
        public static PrivateModsPlugin Instance { get; private set; }
        public bool AllowLocalMods { get; private set; }        // This doesn't work, due to longs in mod list
        public Persistent<Settings> Settings { get; private set; }

        bool m_continueOnDownloadError = true;
        bool m_alwaysUseSteamCMD = true;
        string m_pathToSteamCMD;
        string m_steamUsername;
        SecureString m_steamPassword;
        private UserControl m_control;
        public static readonly Logger Log = LogManager.GetLogger("PrivateMods");

        #region WPF Properties
        public bool ContinueOnDownloadError
        {
            get { return m_continueOnDownloadError; }
            set
            {
                if (value != m_continueOnDownloadError)
                {
                    m_continueOnDownloadError = value;
                    RaisePropertyChanged();
                }
            }
        }

        public bool AlwaysUseSteamCMD
        {
            get { return m_alwaysUseSteamCMD; }
            set
            {
                if (value != m_alwaysUseSteamCMD)
                {
                    m_alwaysUseSteamCMD = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string PathToSteamCMD
        {
            get { return m_pathToSteamCMD; }
            set
            {
                if (value != m_pathToSteamCMD)
                {
                    m_pathToSteamCMD = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string SteamUsername
        {
            get { return string.IsNullOrEmpty(m_steamUsername) ? "anonymous" : m_steamUsername; }
            set
            {
                if (value != m_steamUsername)
                {
                    m_steamUsername = value;
                    RaisePropertyChanged();
                }
            }
        }

        public SecureString SteamPassword
        {
            get { return m_steamPassword; }
            set
            {
                m_steamPassword = value;
                RaisePropertyChanged("HasPassword");
            }
        }

        public bool HasPassword
        {
            get { return SteamPassword?.Length > 0; }
        }
        #endregion WPF Properties

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;

        internal void RaisePropertyChanged([CallerMemberName]string prop = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }
        #endregion INotifyPropertyChanged

        public PrivateModsPlugin()
        {
            Instance = this;
        }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            InjectMethod();

            // Load existing settings
            Settings = Persistent<Settings>.Load(Path.Combine(StoragePath, Constants.SettingsFilename));
            ContinueOnDownloadError = Settings.Data.ContinueOnError;
            PathToSteamCMD = Settings.Data.SteamCMDPath;

            if (string.IsNullOrEmpty(PathToSteamCMD))
                PathToSteamCMD = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location) + Path.DirectorySeparatorChar + "steamcmd" + Path.DirectorySeparatorChar + "steamcmd.exe";

            SteamUsername = Settings.Data.SteamUsername;
            AlwaysUseSteamCMD = Settings.Data.AlwaysUseSteamCMD;

            if ( !string.IsNullOrEmpty(Settings.Data.EncryptedSteamPassword))
                SteamPassword = new SecureString().SetString(Encryption.AESThenHMAC.SimpleDecryptWithPassword(Settings.Data.EncryptedSteamPassword, GetEncryptionKey()));

            torch.SessionUnloaded += () => SaveSettings();
        }

        private string GetEncryptionKey()
        {
            // TODO: Use something else
            var password = $"{Environment.MachineName}{Environment.UserDomainName}{Environment.UserName}";

            if (password.Length < 12)
                password += "XXXXXXXXXXXX";

            return password;
        }

        public void SaveSettings()
        {
            Settings.Data.AlwaysUseSteamCMD = AlwaysUseSteamCMD;
            Settings.Data.ContinueOnError = ContinueOnDownloadError;
            Settings.Data.SteamCMDPath = PathToSteamCMD;
            Settings.Data.SteamUsername = SteamUsername;

            if (SteamPassword?.Length > 0)
                Settings.Data.EncryptedSteamPassword = Encryption.AESThenHMAC.SimpleEncryptWithPassword(SteamPassword.GetString(), GetEncryptionKey());

            Settings.Save();
        }

        public override void Dispose()
        {
            try
            {
                SaveSettings();
            }
            catch
            {
                // TODO: do something
                // do nothing
            }
            GC.SuppressFinalize(this);
        }

        public InputBox InputBox { get; private set; }

        public UserControl GetControl()
        {
            InputBox = new InputBox("Enter Steam Authenticator Code." + Environment.NewLine +
                                    $"If you do not wish to enter your code here, you can cancel this and manually run {Environment.NewLine}`{PathToSteamCMD} +login {SteamUsername}` to cache the credentials.", "Steam Authenticator Code");

            return m_control ?? (m_control = new PrivateModsPage { DataContext = this });
        }

        private void InjectMethod()
        {
            var methodtoreplace = typeof(MySteamWorkshop).GetMethod("DownloadWorldModsBlocking", BindingFlags.Static | BindingFlags.Public);
            var methodtoinject = typeof(MySteamWorkshopReplacement).GetMethod("DownloadWorldModsBlocking", BindingFlags.Static | BindingFlags.Public);

            MyDebug.AssertDebug(methodtoreplace != null);
            if (methodtoreplace != null)
            {
                var parameters = methodtoreplace.GetParameters();
                MyDebug.AssertDebug(parameters.Count() == 1);
                MyDebug.AssertDebug(parameters[0].ParameterType == typeof(List<MyObjectBuilder_Checkpoint.ModItem>));

                if (!(parameters.Count() == 1 && parameters[0].ParameterType == typeof(List<MyObjectBuilder_Checkpoint.ModItem>)))
                    methodtoreplace = null;
            }

            var sourceIsDebug = typeof(MySteamWorkshop).Assembly.GetCustomAttributes(false).OfType<System.Diagnostics.DebuggableAttribute>().Select(da => da.IsJITTrackingEnabled).FirstOrDefault();

            if (methodtoreplace != null && methodtoinject != null)
                MethodUtil.ReplaceMethod(methodtoreplace, methodtoinject, sourceIsDebug && Constants.IsDebug);
            else
                MySandboxGame.Log.WriteLineAndConsole(string.Format(Constants.ERROR_Reflection, "DownloadWorldModsBlocking"));
        }
    }
}

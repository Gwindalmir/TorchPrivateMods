using Sandbox;
using Sandbox.Engine.Networking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using VRage.Game;
using VRage.Utils;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    [Plugin("Private Mods", "1.0", "AA4988CB-7693-46AA-B3FA-219A28653256")]
    public class PrivateModsPlugin : TorchPluginBase
    {
        public static PrivateModsPlugin Instance { get; private set; }
        public bool AllowLocalMods { get; private set; }        // This doesn't work, due to longs in mod list
        public bool ContinueOnDownloadError { get; private set; }

        public PrivateModsPlugin()
        {
            Instance = this;
            AllowLocalMods = true;
            ContinueOnDownloadError = true;
        }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            InjectMethod();
        }

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
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

            if (methodtoreplace != null && methodtoinject != null)
                MethodUtil.ReplaceMethod(methodtoreplace, methodtoinject);
            else
                MySandboxGame.Log.WriteLineAndConsole(string.Format(Constants.ERROR_Reflection, "DownloadWorldModsBlocking"));
        }
    }
}

﻿using Sandbox;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Utils;
using Sandbox.Game.Gui;
using SteamSDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using static Sandbox.Engine.Networking.MySteamWorkshop;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    class MySteamWorkshopReplacement
    {
        static MethodInfo isModUpToDateBlockingMethod;
        static FieldInfo asyncDownloadScreenField;
        static FieldInfo stopField;                 // This is volatile, not sure if this works with reflection properly

        static MySteamWorkshopReplacement()
        {
            asyncDownloadScreenField = typeof(MySteamWorkshop).GetField("m_asyncDownloadScreen", BindingFlags.Static | BindingFlags.NonPublic);
            if( asyncDownloadScreenField == null )
                MySandboxGame.Log.WriteLineAndConsole(string.Format(Constants.ERROR_Reflection, "m_asyncDownloadScreen"));

            stopField = typeof(MySteamWorkshop).GetField("m_stop", BindingFlags.Static | BindingFlags.NonPublic);
            if (stopField == null)
                MySandboxGame.Log.WriteLineAndConsole(string.Format(Constants.ERROR_Reflection, "m_stop"));

            isModUpToDateBlockingMethod = typeof(MySteamWorkshop).GetMethod("IsModUpToDateBlocking", BindingFlags.Static | BindingFlags.NonPublic);
            if (isModUpToDateBlockingMethod == null)
                MySandboxGame.Log.WriteLineAndConsole(string.Format(Constants.ERROR_Reflection, "IsModUpToDateBlocking"));
        }

        /// <summary>
        /// Do NOT call this method from update thread.
        /// </summary>
        public static ResultData DownloadWorldModsBlocking(List<MyObjectBuilder_Checkpoint.ModItem> mods)
        {
            ResultData ret = new ResultData();
            ret.Success = true;
            if (!MyFakes.ENABLE_WORKSHOP_MODS)
            {
                return ret;
            }

            MySandboxGame.Log.WriteLine("Downloading world mods - START");
            MySandboxGame.Log.IncreaseIndent();

            stopField?.SetValue(null, false);

            if (mods != null && mods.Count > 0)
            {
                var publishedFileIds = new List<ulong>();
                foreach (var mod in mods)
                {
                    if (mod.PublishedFileId != 0)
                    {
                        publishedFileIds.Add(mod.PublishedFileId);
                    }
                    else if (MySandboxGame.IsDedicated)
                    {
                        if (PrivateModsPlugin.Instance.AllowLocalMods)
                        {
                            MySandboxGame.Log.WriteLineAndConsole("Local mods being allowed by OfflineMod plugin.");
                        }
                        else
                        {
                            MySandboxGame.Log.WriteLineAndConsole("Local mods are not allowed in multiplayer.");
                            MySandboxGame.Log.DecreaseIndent();
                            return new ResultData();
                        }
                    }
                }

                // Check if the world doesn't contain duplicate mods, if it does, log it and remove the duplicate entry
                publishedFileIds.Sort();
                for (int i = 0; i < publishedFileIds.Count - 1;)
                {
                    ulong id1 = publishedFileIds[i];
                    ulong id2 = publishedFileIds[i + 1];
                    if (id1 == id2)
                    {
                        MySandboxGame.Log.WriteLine(string.Format("Duplicate mod entry for id: {0}", id1));
                        publishedFileIds.RemoveAt(i + 1);
                    }
                    else
                    {
                        i++;
                    }
                }

                if (MySandboxGame.IsDedicated)
                {
                    using (ManualResetEvent mrEvent = new ManualResetEvent(false))
                    {
                        string xml = "";

                        MySteamWebAPI.GetPublishedFileDetails(publishedFileIds, delegate (bool success, string data)
                        {
                            if (!success)
                            {
                                MySandboxGame.Log.WriteLine("Could not retrieve mods details.");
                            }
                            else
                            {
                                xml = data;
                            }
                            ret.Success = success;
                            mrEvent.Set();
                        });

                        while (!mrEvent.WaitOne(17))
                        {
                            mrEvent.Reset();
                            if (MySteam.Server != null)
                                MySteam.Server.RunCallbacks();
                            else
                            {
                                MySandboxGame.Log.WriteLine("Steam server API unavailable");
                                ret.Success = false;
                                break;
                            }
                        }

                        if (ret.Success)
                        {
                            try
                            {
                                System.Xml.XmlReaderSettings settings = new System.Xml.XmlReaderSettings()
                                {
                                    DtdProcessing = System.Xml.DtdProcessing.Ignore,
                                };
                                using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new StringReader(xml), settings))
                                {
                                    reader.ReadToFollowing("result");

                                    Result xmlResult = (Result)reader.ReadElementContentAsInt();

                                    if (xmlResult != Result.OK)
                                    {
                                        MySandboxGame.Log.WriteLine(string.Format("Failed to download mods: result = {0}", xmlResult));
                                        ret.Success = false;
                                    }

                                    reader.ReadToFollowing("resultcount");
                                    int count = reader.ReadElementContentAsInt();

                                    if (count != publishedFileIds.Count)
                                    {
                                        MySandboxGame.Log.WriteLine(string.Format("Failed to download mods details: Expected {0} results, got {1}", publishedFileIds.Count, count));
                                    }

                                    var array = mods.ToArray();

                                    for (int i = 0; i < array.Length; ++i)
                                    {
                                        array[i].FriendlyName = array[i].Name;
                                    }

                                    var processed = new List<ulong>(publishedFileIds.Count);

                                    for (int i = 0; i < publishedFileIds.Count; ++i)
                                    {
                                        mrEvent.Reset();

                                        reader.ReadToFollowing("publishedfileid");
                                        ulong publishedFileId = Convert.ToUInt64(reader.ReadElementContentAsString());

                                        if (processed.Contains(publishedFileId))
                                        {
                                            MySandboxGame.Log.WriteLineAndConsole(string.Format("Duplicate mod: id = {0}", publishedFileId));
                                            continue;
                                        }
                                        processed.Add(publishedFileId);

                                        reader.ReadToFollowing("result");
                                        Result itemResult = (Result)reader.ReadElementContentAsInt();

                                        if (itemResult != Result.OK)
                                        {
                                            var fullPath = Path.Combine(MyFileSystem.ModsPath, publishedFileId.ToString() + ".sbm");
                                            MySandboxGame.Log.WriteLineAndConsole(string.Format("Failed to download mod: id = {0}, result = {1}", publishedFileId, itemResult));
                                            if (PrivateModsPlugin.Instance.ContinueOnDownloadError && File.Exists(fullPath))
                                            {
                                                using (var file = File.OpenRead(fullPath))
                                                {
                                                    var localTimeUpdated = File.GetLastWriteTimeUtc(fullPath);
                                                    MySandboxGame.Log.IncreaseIndent();
                                                    MySandboxGame.Log.WriteLineAndConsole(string.Format("Found existing download: size = {0,8:0.000} MiB, last modified = {1}", file.Length / 1024f / 1024f, localTimeUpdated));
                                                    MySandboxGame.Log.WriteLineAndConsole("Continuing due to OfflineMod plugin.");
                                                    MySandboxGame.Log.DecreaseIndent();
                                                }
                                            }
                                            else
                                            {
                                                MySandboxGame.Log.WriteLineAndConsole("Existing download not found, cannot load mod.");
                                                ret.Success = false;
                                            }
                                            continue;
                                        }

                                        reader.ReadToFollowing("consumer_app_id");
                                        int appid = reader.ReadElementContentAsInt();
                                        if (appid != MySteam.AppId)
                                        {
                                            MySandboxGame.Log.WriteLineAndConsole(string.Format("Failed to download mod: id = {0}, wrong appid, got {1}, expected {2}", publishedFileId, appid, MySteam.AppId));
                                            ret.Success = false;
                                            continue;
                                        }

                                        reader.ReadToFollowing("file_size");
                                        long fileSize = reader.ReadElementContentAsLong();

                                        reader.ReadToFollowing("file_url");
                                        string url = reader.ReadElementContentAsString();

                                        reader.ReadToFollowing("title");
                                        string title = reader.ReadElementContentAsString();

                                        for (int j = 0; j < array.Length; ++j)
                                        {
                                            if (array[j].PublishedFileId == publishedFileId)
                                            {
                                                array[j].FriendlyName = title;
                                                break;
                                            }
                                        }

                                        reader.ReadToFollowing("time_updated");
                                        uint timeUpdated = (uint)reader.ReadElementContentAsLong();

                                        var mod = new SubscribedItem() { Title = title, PublishedFileId = publishedFileId, TimeUpdated = timeUpdated };

                                        if ((bool)isModUpToDateBlockingMethod?.Invoke(null, new object[] { Path.Combine(MyFileSystem.ModsPath, publishedFileId.ToString() + ".sbm"), mod, false, fileSize }))
                                        {
                                            MySandboxGame.Log.WriteLineAndConsole(string.Format("Up to date mod:  id = {0}", publishedFileId));
                                            continue;
                                        }

                                        MySandboxGame.Log.WriteLineAndConsole(string.Format("Downloading mod: id = {0}, size = {1,8:0.000} MiB", publishedFileId, (double)fileSize / 1024f / 1024f));

                                        if (fileSize > 10 * 1024 * 1024) // WTF Steam
                                        {
                                            if (!DownloadModFromURLStream(url, publishedFileId, delegate (bool success)
                                            {
                                                if (!success)
                                                {
                                                    MySandboxGame.Log.WriteLineAndConsole(string.Format("Could not download mod: id = {0}, url = {1}", publishedFileId, url));
                                                }
                                                mrEvent.Set();
                                            }))
                                            {
                                                ret.Success = false;
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            if (!DownloadModFromURL(url, publishedFileId, delegate (bool success)
                                            {
                                                if (!success)
                                                {
                                                    MySandboxGame.Log.WriteLineAndConsole(string.Format("Could not download mod: id = {0}, url = {1}", publishedFileId, url));
                                                }
                                                mrEvent.Set();
                                            }))
                                            {
                                                ret.Success = false;
                                                break;
                                            }
                                        }

                                        while (!mrEvent.WaitOne(17))
                                        {
                                            mrEvent.Reset();
                                            if (MySteam.Server != null)
                                                MySteam.Server.RunCallbacks();
                                            else
                                            {
                                                MySandboxGame.Log.WriteLine("Steam server API unavailable");
                                                ret.Success = false;
                                                break;
                                            }
                                        }
                                    }
                                    mods.Clear();
                                    mods.AddArray(array);
                                }
                            }
                            catch (Exception e)
                            {
                                MySandboxGame.Log.WriteLine(string.Format("Failed to download mods: {0}", e));
                                ret.Success = false;
                            }
                        }
                    }
                }
                else // client
                {
                    var toGet = new List<SubscribedItem>(publishedFileIds.Count);

                    if (!GetItemsBlocking(toGet, publishedFileIds))
                    {
                        MySandboxGame.Log.WriteLine("Could not obtain workshop item details");
                        ret.Success = false;
                    }
                    else if (publishedFileIds.Count != toGet.Count)
                    {
                        MySandboxGame.Log.WriteLine(string.Format("Could not obtain all workshop item details, expected {0}, got {1}", publishedFileIds.Count, toGet.Count));
                        ret.Success = false;
                    }
                    else
                    {
                        var m_asyncDownloadScreen = asyncDownloadScreenField?.GetValue(null) as MyGuiScreenProgressAsync;
                        if (m_asyncDownloadScreen != null )
                            m_asyncDownloadScreen.ProgressTextString = MyTexts.GetString(MyCommonTexts.ProgressTextDownloadingMods) + " 0 of " + toGet.Count.ToString();

                        ret = DownloadModsBlocking(toGet);
                        if (ret.Success == false)
                        {
                            MySandboxGame.Log.WriteLine("Downloading mods failed");
                        }
                        else
                        {
                            var array = mods.ToArray();

                            for (int i = 0; i < array.Length; ++i)
                            {
                                var mod = toGet.Find(x => x.PublishedFileId == array[i].PublishedFileId);
                                if (mod != null)
                                {
                                    array[i].FriendlyName = mod.Title;
                                }
                                else
                                {
                                    array[i].FriendlyName = array[i].Name;
                                }
                            }
                            mods.Clear();
                            mods.AddArray(array);
                        }
                    }
                }
            }
            MySandboxGame.Log.DecreaseIndent();
            MySandboxGame.Log.WriteLine("Downloading world mods - END");
            return ret;
        }
    }
}
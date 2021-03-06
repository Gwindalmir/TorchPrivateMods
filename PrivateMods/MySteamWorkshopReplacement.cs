﻿using Sandbox;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Utils;
using Sandbox.Game.Gui;
using SteamSDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.Steam;
using static Sandbox.Engine.Networking.MySteamWorkshop;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    class MySteamWorkshopReplacement
    {
        static MySteamService MySteam { get => (MySteamService)MyServiceManager.Instance.GetService<VRage.GameServices.IMyGameService>(); }
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

        // NOTE: This method (DownloadWorldModsBlocking) is from Space Engineers, and is copyrighted by Keen.
        // Therefore, it is *NOT* subject to the repository license.
        // I have only made some changes.
        // The original is located here: https://github.com/KeenSoftwareHouse/SpaceEngineers/blob/master/Sources/Sandbox.Game/Engine/Networking/MySteamWorkshop.cs#L1326
        public static ResultData DownloadWorldModsBlocking(List<MyObjectBuilder_Checkpoint.ModItem> mods)
        {
            var collectionMods = new List<MyObjectBuilder_Checkpoint.ModItem>();
            ResultData ret = new ResultData();
            ret.Success = true;
            if (!MyFakes.ENABLE_WORKSHOP_MODS && !PrivateModsPlugin.Instance.AlwaysUseSteamCMD)
            {
                return ret;
            }
            bool sewtDownloaded = false;

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
                        // Check for, and get information on, collections
                        var list = WorkshopTool.WorkshopHelper.GetCollectionDetails(mod.PublishedFileId);

                        if (list?.Count() > 0)
                            list?.ForEach(i =>
                                    {
                                        publishedFileIds.Add(i.PublishedFileId);
                                        collectionMods.Add(new MyObjectBuilder_Checkpoint.ModItem(i.PublishedFileId));
                                    });
                        else
                            publishedFileIds.Add(mod.PublishedFileId);
                    }
                    else if (MySandboxGame.IsDedicated)
                    {
                        if (PrivateModsPlugin.Instance.AllowLocalMods)
                        {
                            MySandboxGame.Log.WriteLineAndConsole("Local mods being allowed by PrivateMod plugin.");
                        }
                        else
                        {
                            MySandboxGame.Log.WriteLineAndConsole("Local mods are not allowed in multiplayer.");
                            MySandboxGame.Log.DecreaseIndent();
                            return new ResultData();
                        }
                    }
                }

                // Download mods with steamcmd
                if( PrivateModsPlugin.Instance.AlwaysUseSteamCMD)
                {
                    sewtDownloaded = DownloadModsExternally(publishedFileIds);
                }

                collectionMods.ForEach(m =>
                {
                    // This is probably slow, but we need to check if the mod we're processing wasn't already added previously.
                    // This is to prevent duplicates from piling on when processing collections.
                    if (mods.Where(i => i.PublishedFileId == m.PublishedFileId).Count() == 0)
                        mods.Add(m);
                });


                // Check if the world doesn't contain duplicate mods, if it does, log it and remove the duplicate entry
                publishedFileIds.Sort();
                for (int i = 0; i < publishedFileIds.Count - 1;)
                {
                    ulong id1 = publishedFileIds[i];
                    ulong id2 = publishedFileIds[i + 1];
                    if (id1 == id2)
                    {
                        if(collectionMods.Where((m) => m.PublishedFileId == id1).Count() == 0)
                            MySandboxGame.Log.WriteLine(string.Format("Duplicate mod entry for id: {0}", id1));
                        publishedFileIds.RemoveAt(i + 1);
                    }
                    else
                    {
                        i++;
                    }
                }

                collectionMods.Clear();

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
                            if (MySteam.SteamServerAPI?.GameServer != null)
                                MySteam.SteamServerAPI.GameServer.RunCallbacks();
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
                                            // Try to download with SEWT
                                            if (!sewtDownloaded)
                                                DownloadModsExternally(new List<ulong>() { publishedFileId });

                                            var fullPath = Path.Combine(MyFileSystem.ModsPath, publishedFileId.ToString() + ".sbm");
                                            MySandboxGame.Log.WriteLineAndConsole(string.Format("Failed to download mod: id = {0}, result = {1}", publishedFileId, itemResult));
                                            if (PrivateModsPlugin.Instance.ContinueOnDownloadError && File.Exists(fullPath))
                                            {
                                                using (var file = File.OpenRead(fullPath))
                                                {
                                                    var localTimeUpdated = File.GetLastWriteTimeUtc(fullPath);
                                                    MySandboxGame.Log.IncreaseIndent();
                                                    MySandboxGame.Log.WriteLineAndConsole(string.Format("Found existing download: size = {0,8:0.000} MiB, last modified = {1}", file.Length / 1024f / 1024f, localTimeUpdated));
                                                    MySandboxGame.Log.WriteLineAndConsole("Continuing due to PrivateMod plugin.");
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

                                        string url = null;
                                        if (MyFakes.ENABLE_WORKSHOP_MODS)
                                        {
                                            reader.ReadToFollowing("file_url");
                                            url = reader.ReadElementContentAsString();
                                        }

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

                                        if (MyFakes.ENABLE_WORKSHOP_MODS)
                                        {
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
                                                if (MySteam.SteamServerAPI?.GameServer != null)
                                                    MySteam.SteamServerAPI?.GameServer.RunCallbacks();
                                                else
                                                {
                                                    MySandboxGame.Log.WriteLine("Steam server API unavailable");
                                                    ret.Success = false;
                                                    break;
                                                }
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

        /// <summary>
        /// Downloads mods externally, with SEWT
        /// </summary>
        /// <param name="mods"></param>
        public static bool DownloadModsExternally(List<ulong> mods)
        {
            if (string.IsNullOrEmpty(PrivateModsPlugin.Instance.PathToSteamCMD) || !File.Exists(PrivateModsPlugin.Instance.PathToSteamCMD))
                return false;
            var dir = Path.GetDirectoryName(PrivateModsPlugin.Instance.PathToSteamCMD);
            var script = Path.Combine(dir, "privatemods_script.txt");
            File.WriteAllLines(script,
                                new string[] {
                                    $"login {PrivateModsPlugin.Instance.SteamUsername}",
                                    "force_install_dir ..\\ ",
                                    "workshop_download_item 244850 " + string.Join(Environment.NewLine + "workshop_download_item 244850 ", mods),
                                    "quit"
                                }
                                );

            var steamcmd = new ProcessStartInfo(PrivateModsPlugin.Instance.PathToSteamCMD, $"+runscript \"{script}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.ASCII,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(PrivateModsPlugin.Instance.PathToSteamCMD)
            };

            var regex = new Regex("^Success\\. Downloaded item ([0-9]+) to \"(.+)\"");

            Debug.WriteLine(steamcmd);
            var cmd = Process.Start(steamcmd);
            cmd.StandardInput.AutoFlush = true;
            for (;;)
            {
                if (!cmd.StandardOutput.EndOfStream)
                {
                    var line = cmd.StandardOutput.ReadLine();

                    // Steam *may* prompt for a password, if this is the first login, or cached credentials are lost
                    // The actual prompt is 'password: ', but since that line doens't have a CRLF (due to prompt), it's not available yet.
                    if (line.StartsWith("Logging in user"))
                    {
                        // TODO: Maybe make this more secure, by reading the contents manually and passing directly to Stdin.
                        cmd.StandardInput.WriteLine(PrivateModsPlugin.Instance.SteamPassword.GetString());
                        MySandboxGame.Log.WriteLineAndConsole(line);
                    }
                    else if (line == "Enter the current code from your Steam Guard Mobile Authenticator app")
                    {
                        PrivateModsPlugin.Log.Log(NLog.LogLevel.Info, line);
                        string code = string.Empty;

                        PrivateModsPlugin.Instance.InputBox.Dispatcher.Invoke(() =>
                        {
                            if (PrivateModsPlugin.Instance.InputBox.ShowDialog() == true && !string.IsNullOrEmpty(PrivateModsPlugin.Instance.InputBox.InputText))
                            {
                                code = PrivateModsPlugin.Instance.InputBox.InputText;
                            }
                        });
                        cmd.StandardInput.WriteLine(code);
                    }
                    else if(line.StartsWith("Success. Downloaded item "))
                    {
                        var matches = regex.Matches(line);
                        if( matches.Count == 1 && matches[0].Groups.Count == 3)
                        {
                            var id = matches[0].Groups[1].Value;
                            var path = matches[0].Groups[2].Value;

                            // File was downloaded, copy it to the session mod location
                            File.Copy(path, Path.Combine(PrivateModsPlugin.Instance.Torch.Config.InstancePath, "Mods", $"{id}.sbm"), true);
                        }
                        MySandboxGame.Log.WriteLineAndConsole(line);
                    }
                    else
                    {
                        MySandboxGame.Log.WriteLineAndConsole(line);
                    }
                }
                if( !cmd.StandardError.EndOfStream )
                    MySandboxGame.Log.WriteLineAndConsole(cmd.StandardError.ReadLine());

                if (cmd.HasExited && cmd.StandardOutput.EndOfStream && cmd.StandardOutput.EndOfStream)
                    break;

                Thread.Sleep(100);
            }

            File.Delete(script);

            if (cmd.ExitCode == 0)
                return true;

            return false;
        }

        #region From SE
        private static readonly string m_workshopModsPath = MyFileSystem.ModsPath;
        private static readonly string m_workshopModSuffix = ".sbm";

        public static bool DownloadModFromURLStream(string url, ulong publishedFileId, Action<bool> callback)
        {
            uint handle = HTTP.CreateHTTPRequest(HTTPMethod.GET, url);
            if (handle == 0)
            {
                callback(false);
                return false;
            }

            if (!HTTP.SetHTTPRequestContextValue(handle, publishedFileId))
            {
                MySandboxGame.Log.WriteLine(string.Format("HTTP: could not set context value = {0}", publishedFileId));
                callback(false);
                return false;
            }

            var localPackedModFullPath = Path.Combine(MyFileSystem.ModsPath, publishedFileId.ToString() + m_workshopModSuffix);

            var fs = File.OpenWrite(localPackedModFullPath);

            DataReceived onDataRecieved = delegate (HTTPRequestDataReceived data)
            {
                if (data.ContextValue != publishedFileId)
                    return;

                var buffer = new byte[data.BytesReceived];
                HTTP.GetHTTPStreamingResponseBodyData(data.Request, data.Offset, buffer, data.BytesReceived);
                fs.Write(buffer, 0, (int)data.BytesReceived);
            };

            HTTP.DataReceived += onDataRecieved;

            if (!HTTP.SendHTTPRequestAndStreamResponse(handle, delegate (bool ioFailure, HTTPRequestCompleted data)
            {
                HTTP.DataReceived -= onDataRecieved;
                var success = !ioFailure && data.RequestSuccessful && data.StatusCode == HTTPStatusCode.OK;
                if (success)
                {
                    MySandboxGame.Log.WriteLine(string.Format("HTTP: Downloaded mod publishedFileId = {0}, size = {1}B to: '{2}' url: '{3}'", publishedFileId, fs.Length, localPackedModFullPath, url));
                }
                else
                {
                    MySandboxGame.Log.WriteLine(string.Format("HTTP: Error downloading file: Id = {0}, status = {1}, url = '{2}'", publishedFileId, data.StatusCode, url));
                }
                fs.Dispose();
                callback(success);
            }))
            {
                MySandboxGame.Log.WriteLine(string.Format("HTTP: could not send HTTP request url = '{0}'", url));
                callback(false);
                return false;
            }
            return true;
        }

        public static bool DownloadModFromURL(string url, ulong publishedFileId, Action<bool> callback)
        {
            uint handle = HTTP.CreateHTTPRequest(HTTPMethod.GET, url);
            if (handle == 0)
                return false;
            bool success = false;
            uint dataSize = 0;

            if (!HTTP.SendHTTPRequest(handle, delegate (bool ioFailure, HTTPRequestCompleted data)
            {
                if (!ioFailure && data.RequestSuccessful && data.StatusCode == HTTPStatusCode.OK)
                {
                    var localPackedModFullPath = Path.Combine(m_workshopModsPath, publishedFileId + m_workshopModSuffix);
                    if (HTTP.GetHTTPResponseBodySize(data.Request, out dataSize))
                    {
                        var bodyData = new byte[dataSize];
                        if (HTTP.GetHTTPResponseBodyData(data.Request, bodyData, dataSize))
                        {
                            try
                            {
                                File.WriteAllBytes(localPackedModFullPath, bodyData);
                                success = true;
                                MySandboxGame.Log.WriteLine(string.Format("HTTP: Downloaded mod publishedFileId = {0}, size = {1} bytes to: '{2}' from: '{3}'", publishedFileId, dataSize, localPackedModFullPath, url));
                            }
                            catch (Exception e)
                            {
                                MySandboxGame.Log.WriteLine(string.Format("HTTP: failed to write data {0} bytes to file = '{1}', error: {2}", dataSize, localPackedModFullPath, e));
                            }
                        }
                        else
                        {
                            MySandboxGame.Log.WriteLine(string.Format("HTTP: failed to read response body data, size = {0}", dataSize));
                        }
                    }
                    else
                    {
                        MySandboxGame.Log.WriteLine("HTTP: failed to read response body size");
                    }
                }
                else
                {
                    MySandboxGame.Log.WriteLine(string.Format("HTTP: error {0}", data.StatusCode));
                }
                callback(success);
            }))
                return false;
            return true;
        }
        #endregion

    }
}

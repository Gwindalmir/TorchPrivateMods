﻿using Sandbox.Engine.Networking;
using SteamSDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VRage;
using VRage.FileSystem;
using VRage.Steam;
using VRage.Utils;
using MySubscribedItem = Sandbox.Engine.Networking.MySteamWorkshop.SubscribedItem;

namespace Phoenix.WorkshopTool
{
    class WorkshopHelper
    {
        static MySteamService MySteam { get => (MySteamService)MyServiceManager.Instance.GetService<VRage.GameServices.IMyGameService>(); }

        static private Dictionary<uint, Action<bool, string>> m_callbacks = new Dictionary<uint, Action<bool, string>>();

        public static MySubscribedItem GetSubscribedItem(ulong modid)
        {
            MySubscribedItem item = new MySubscribedItem();

            if (MySteam.API == null)
                return item;

            using (var mrEvent = new ManualResetEvent(false))
            {
                MySteam.API.RemoteStorage.GetPublishedFileDetails(modid, 0, (ioFailure, result) =>
                {
                    if (!ioFailure && result.Result == SteamSDK.Result.OK)
                    {
                        item.Description = result.Description;
                        item.Title = result.Title;
                        item.UGCHandle = result.FileHandle;
                        item.Tags = result.Tags.Split(',');
                        item.SteamIDOwner = result.SteamIDOwner;
                        item.TimeUpdated = result.TimeUpdated;
                        item.PublishedFileId = result.PublishedFileId;
                    }
                    mrEvent.Set();
                });

                mrEvent.WaitOne();
                mrEvent.Reset();
            }
            return item;
        }

        #region Collections
        public static IEnumerable<MySubscribedItem> GetCollectionDetails(ulong modid)
        {
            IEnumerable<MySubscribedItem> details = new List<MySubscribedItem>();

            using (var mrEvent = new ManualResetEvent(false))
            {
                GetCollectionDetails(new List<ulong>() { modid }, (IOFailure, result) =>
                {
                    if (!IOFailure)
                    {
                        details = result;
                    }
                    mrEvent.Set();
                });

                mrEvent.WaitOne();
                mrEvent.Reset();
            }

            return details;
        }

        // code from Rexxar, modified to use XML
        public static bool GetCollectionDetails(IEnumerable<ulong> publishedFileIds, Action<bool, IEnumerable<MySubscribedItem>> callback)
        {
            string xml = "";
            var modsInCollection = new List<MySubscribedItem>();
            bool failure = false;
            MyLog.Default.IncreaseIndent();
            try
            {
                var request = WebRequest.Create(string.Format("https://api.steampowered.com/{0}/{1}/v{2:0000}/?format=xml", "ISteamRemoteStorage", "GetCollectionDetails", 1));
                request.Method = "POST";
                request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";

                StringBuilder sb = new StringBuilder();
                sb.Append("?&collectioncount=").Append(publishedFileIds.Count());
                int i = 0;

                foreach (var id in publishedFileIds)
                    sb.AppendFormat("&publishedfileids[{0}]={1}", i++, id);

                var d = Encoding.UTF8.GetBytes(sb.ToString());
                request.ContentLength = d.Length;
                using (var rs = request.GetRequestStream())
                    rs.Write(d, 0, d.Length);

                var response = request.GetResponse();

                var sbr = new StringBuilder(100);
                var buffer = new byte[1024];
                int count;

                while ((count = response.GetResponseStream().Read(buffer, 0, 1024)) > 0)
                {
                    sbr.Append(Encoding.UTF8.GetString(buffer, 0, count));
                }
                xml = sbr.ToString();

                System.Xml.XmlReaderSettings settings = new System.Xml.XmlReaderSettings()
                {
                    DtdProcessing = System.Xml.DtdProcessing.Ignore,
                };

                using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new StringReader(xml), settings))
                {
                    reader.ReadToFollowing("result");

                    var xmlResult = reader.ReadElementContentAsInt();
                    if (xmlResult != 1 /* OK */)
                    {
                        MyLog.Default.WriteLine(string.Format("Failed to download collections: result = {0}", xmlResult));
                        failure = true;
                    }

                    reader.ReadToFollowing("resultcount");
                    count = reader.ReadElementContentAsInt();

                    if (count != publishedFileIds.Count())
                    {
                        MyLog.Default.WriteLine(string.Format("Failed to download collection details: Expected {0} results, got {1}", publishedFileIds.Count(), count));
                    }

                    var processed = new List<ulong>(publishedFileIds.Count());

                    for (i = 0; i < publishedFileIds.Count(); ++i)
                    {
                        reader.ReadToFollowing("publishedfileid");
                        ulong publishedFileId = Convert.ToUInt64(reader.ReadElementContentAsString());

                        reader.ReadToFollowing("result");
                        xmlResult = reader.ReadElementContentAsInt();

                        if (xmlResult == 1 /* OK */)
                        {
                            MyLog.Default.WriteLineAndConsole(string.Format("Collection {0} contains the following items:", publishedFileId.ToString()));

                            reader.ReadToFollowing("children");
                            using (var sub = reader.ReadSubtree())
                            {
                                while (sub.ReadToFollowing("publishedfileid"))
                                {
                                    var item = new MySubscribedItem() { PublishedFileId = Convert.ToUInt64(sub.ReadElementContentAsString()) };
                                    MyLog.Default.WriteLineAndConsole(string.Format("Id - {0}", item.PublishedFileId));
                                    modsInCollection.Add(item);
                                }
                            }

                            failure = false;
                        }
                        else
                        {
                            // don't do anything, this could be a regular mod item
                            // TODO: Log if it is not a mod, and we got an error
                            //MyLog.Default.WriteLineAndConsole(string.Format("Item {0} returned the following error: {1}", publishedFileId.ToString(), (Result)xmlResult));
                            failure = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine(ex);
                return false;
            }
            finally
            {
                MyLog.Default.DecreaseIndent();
                callback(failure, modsInCollection);
            }
            return failure;
        }

        #endregion Collections
    }
}

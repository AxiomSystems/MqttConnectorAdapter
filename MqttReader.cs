using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using ThinkIQ.DataManagement;
using Newtonsoft.Json.Linq;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace SmipMqttConnector
{
    public class MqttReader : IHistoryReader
    {
        /// <summary>
        /// Stores the list of tags this Reader will service
        /// </summary>
        internal IDictionary<string, ITag> _tagDict { get; set; }
        internal int SouthBridgeReads = 0;

        internal static Dictionary<string, int> _dictFilesToDelete = new Dictionary<string, int>();

        /// <summary>
        /// Constructor, creates a new Reader to service a specific list of tags
        /// </summary>
        /// <param name="tagDict">The list of tags this Reader instance will service</param>
        /// <param name="acceptStartBoundValue"></param>
        public MqttReader(IDictionary<string, ITag> tagDict, bool acceptStartBoundValue)
        {
            //remember the tagDict requested in the constructor
            _tagDict = tagDict;
            Log.Information("MQTT Adapter: Reader created for: " + Newtonsoft.Json.JsonConvert.SerializeObject(tagDict));
        }

        /// <summary>
        /// Called by the South Bridge service when it needs this reader to service its tag list
        /// when sample values.
        /// </summary>
        /// <param name="startTime">The start of the time range for which to send sample values</param>
        /// <param name="endTime">The end of the time range for which to send sample values</param>
        /// <returns>A list of ItemData values to be historized by the platform</returns>
        public IList<ItemData> ReadRaw(DateTime startTime, DateTime endTime)
        {
            Log.Debug("MQTT Adapter: Servicing read requested for " + startTime.ToShortTimeString() + " through " + endTime.ToShortTimeString());
            var newData = new List<ItemData>();
            if (MqttConnector.ReadCount < 2)
            {
                Log.Information("MQTT Adapter: IList reading raw for: " + Newtonsoft.Json.JsonConvert.SerializeObject(_tagDict));
            }
            else
            {
                Log.Information("MQTT Adapter: IList reading raw.");
            }
            
            foreach (var tag in _tagDict.Keys)  //for each of the tags this Reader was created to service
            {
                ItemData myItemData = new ItemData { VSTs = new List<VST>(), Item = tag };  //Create a new ItemData

                Log.Debug("MQTT Adapter: Loading cached payload for tag: " + tag);
                if (tag.Contains("/:/"))
                {
                    Log.Debug("MQTT Adapter: Requested topic contains parseable data points in its payload, which will be treated as tags.");
                    var tagParts = tag.Split(new[] { "/:/" }, StringSplitOptions.None);
                    if (tagParts.Length > 1)
                    {
                        var useTag = tagParts[0];
                        var usePayload = tagParts[1];
                        var usePath = Path.Combine(MqttConnector.FindDataRoot(), MqttConnector.HistRoot, (MqttConnector.Base64Encode(useTag) + ".txt"));

                        Log.Debug("MQTT Adapter: Loading cached payload from: " + usePath);
                        Log.Debug("MQTT Adapter: Payload data member: " + usePayload);
                        var useValue = parseJsonPayloadForKey(usePayload, usePath, true);
                        if (useValue != null)
                        {
                            Log.Debug("MQTT Adapter: Parsed data member value: " + useValue);
                            //Prep data for SMIP
                            myItemData.VSTs.Add(new VST(useValue, 192, endTime));
                            newData.Add(myItemData);
                        }
                    } else
                    {
                        Log.Warning("MQTT Adapter: The topic structure was corrupted, the data will be skipped, but processing should be able to continue.");
                    }
                } else
                {
                    Log.Debug("MQTT Adapter: Requested topic contains a single datapoint");
                    var usePath = Path.Combine(MqttConnector.FindDataRoot(), MqttConnector.HistRoot, (MqttConnector.Base64Encode(tag) + ".txt"));
                    Log.Debug("MQTT Adapter: Loading cached payload from: " + usePath);
                    try {
                        //TODO: Probably should use a StreamReader here for safety
                        // string useValue = File.ReadAllText(usePath);
                        string useValue = File_ReadAllText(usePath, true);
                        Log.Debug("MQTT Adapter: Single datapoint value: " + useValue);

                        //Prep data for SMIP
                        myItemData.VSTs.Add(new VST(useValue, 192, startTime));
                        newData.Add(myItemData);
                    }
                    catch (Exception ex)
                    {
                        Log.Information("MQTT Adapter: An error occurred reading the topic payload history file " + usePath + ". It may not be cached yet.");
                        Log.Debug("MQTT Adapter: " + ex.Message);
                    }                   
                }
            }
            if (MqttConnector.SouthBridgeReaper && MqttConnector.SouthBridgeMaxLife > 0)
            {
                if (SouthBridgeReads >= MqttConnector.SouthBridgeMaxLife)
                {
                    Log.Information("South Bridge Reaper firing at MaxLife of " + MqttConnector.SouthBridgeMaxLife);
                    // MqttConnector.CycleSouthBridgeService();
                    SouthBridgeReads = 0;
                }
                SouthBridgeReads++;
            }

            // Cleanup Files
            DeleteAllFiles();

            //return the list of new ItemData points
            return newData;
        }

        /// <summary>
        /// File_ReadAllText - Replaces File.ReadAllText, which cannot open files that other processes are using. 
        /// </summary>
        /// <param name="strPath"></param>
        /// <returns></returns>
        public static string File_ReadAllText(string strPath, bool bDeleteAfterRead)
        {
            string strOutput;
            var fs = new FileStream(strPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var sr = new StreamReader(fs, Encoding.Default);
            { 
                strOutput = sr.ReadToEnd();
            }
            sr.Close();
            sr.Dispose();
            fs.Close();
            fs.Dispose();

            if (bDeleteAfterRead)
            {
                File_AddToDeleteFilesList(strPath);
            }


            return strOutput;
        }

        private static bool File_AddToDeleteFilesList (string strPath)
        {
            if (_dictFilesToDelete.ContainsKey(strPath))
            {
                int count = _dictFilesToDelete[strPath];
                _dictFilesToDelete[strPath] = count + 1;
            }
            else
            {
                _dictFilesToDelete.Add(strPath, 1);
            }
            return true;
        }

        private static void DeleteAllFiles()
        { 
            int cRetry=3;
            foreach (KeyValuePair<string,int> kvp in _dictFilesToDelete)
            {
                string strPath = kvp.Key;
                int cTagsInFile = kvp.Value;
                bool bTryAgain = true;
                string strFileName = Path.GetFileName(strPath);
                string strOriginalTag = strFileName.Replace(".txt", "");
                try
                {
                    strOriginalTag = MqttConnector.Base64Decode(strOriginalTag);
                }
                catch { }
                for (int iRetry = cRetry; iRetry > 0 && bTryAgain; iRetry--)
                {
                    try
                    {
                        File.Delete(strPath);
                        // Log.Information($"MQTT Adapter: Deleted file {strPath} -- return = {iRetry}. Used in {cTagsInFile} tags.");
                    }
                    catch
                    {
                        Log.Information($"MQTT Adapter: *** Error *** trying to delete file {strPath} -- return = {iRetry}. Used in {cTagsInFile} tags. Original Tag = {strOriginalTag}");
                        System.Threading.Thread.Sleep(25);
                    }

                    bTryAgain = File.Exists(strPath);
                    if (bTryAgain)
                    {
                        System.Threading.Thread.Sleep(25);
                    }
                    else
                    {
                        Log.Information($"MQTT Adapter: Deleted file {strPath} -- return = {iRetry}. Used in {cTagsInFile} tags. Original Tag = {strOriginalTag}");
                    }
                }
            }


            return;
        }

        //TODO: The Mqtt Service only preserves the last payload right now, so historical reads and live data reads are the same
        IList<ItemData> IHistoryReader.ReadRaw(DateTime startTime, DateTime endTime)
        {
            if (MqttConnector.ReadCount < 1)
            {
                Log.Information("MQTT Adapter: Historical read requested for " + startTime.ToShortTimeString() + " through " + endTime.ToShortTimeString() + " but historical read is not implemented, returning live data instead.");
                Log.Information("MQTT Adapter: IHistoryReader reading raw for: " + Newtonsoft.Json.JsonConvert.SerializeObject(_tagDict));
            } else
            {
                Log.Information("MQTT Adapter: IHistoryReader reading raw.");
            }
            MqttConnector.ReadCount = MqttConnector.ReadCount + 1;
            return ReadRaw(startTime, endTime);
        }

        private string parseJsonPayloadForKey(string compoundKey, string payloadPath, bool bDeleteAfterRead)
        {
            String strReturnValue = String.Empty;
            try {
                compoundKey = compoundKey.Replace("/", ".");
                StreamReader file = File.OpenText(payloadPath);
                JsonTextReader reader = new JsonTextReader(file);
                JObject payloadObj = (JObject)JToken.ReadFrom(reader);
                Log.Debug("MQTT Adapter: Parsed stored payload: " + Newtonsoft.Json.JsonConvert.SerializeObject(payloadObj));
                strReturnValue = (string)payloadObj.SelectToken(compoundKey);

                reader.Close();
                file.Close();
                file.Dispose();

                if (bDeleteAfterRead)
                {
                    File_AddToDeleteFilesList(payloadPath);
                }
                return strReturnValue;

            }
            catch (Exception ex) {
                Log.Warning("MQTT Adapter: A MQTT payload could not be loaded or parsed, data will be skipped, but processing should be able to continue.");
                Log.Warning(ex.Message);
            }
            return null;
        }

        bool IHistoryReader.ContainsTag(string tagName)
        {
            Log.Information("MQTT Adapter: Incoming ContainsTag query " + tagName);
            try {
                //var topics = File.ReadAllLines(Path.Combine(MqttConnector.FindDataRoot(), MqttConnector.TopicListFile));
                List<string> topics = new List<string>();
                using (var fs = new FileStream(Path.Combine(MqttConnector.FindDataRoot(), MqttConnector.TopicListFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.Default))
                {
                    while (!sr.EndOfStream)
                    {
                        topics.Add(sr.ReadLine());
                    }
                }
                return topics.Contains(tagName);
                //return Array.IndexOf(topics, tagName) != -1;
            }
            catch (Exception ex)
            {
                Log.Error("MQTT Adapter: An error occurred reading the topic list file: " + Path.Combine(MqttConnector.FindDataRoot(), MqttConnector.TopicListFile));
                Log.Error("MQTT Adapter: " + ex.Message);
                return false;
            }
        }

        IDictionary<string, ITag> IHistoryReader.GetCurrentTags()
        {
            Log.Information("MQTT Adapter: Current tags requested, returning: " + Newtonsoft.Json.JsonConvert.SerializeObject(_tagDict));
            return MqttConnector.Browse();
        }

        void IDisposable.Dispose()
        {
            Log.Information("MQTT Adapter: IDisposable Dispose called");
            MqttConnector.ReadCount = 0;
        }
    }
}

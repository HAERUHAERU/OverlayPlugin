﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Advanced_Combat_Tracker;
using System.Diagnostics;
using System.Windows.Forms;
using FFXIV_ACT_Plugin.Common.Models;

namespace RainbowMage.OverlayPlugin.EventSources
{
    partial class MiniParseEventSource : EventSourceBase
    {
        private string prevEncounterId { get; set; }
        private DateTime prevEndDateTime { get; set; }
        private bool prevEncounterActive { get; set; }

        private List<string> importedLogs = new List<string>();
        private static Dictionary<uint, string> StatusMap = new Dictionary<uint, string>
        {
            { 0, "Online" },
            { 12, "Busy" },
            { 15, "InCutscene" },
            { 17, "AFK" },
            { 21, "LookingToMeld" },
            { 22, "RP" },
            { 23, "LookingForParty" },
        };

        private const string CombatDataEvent = "CombatData";
        private const string LogLineEvent = "LogLine";
        private const string ImportedLogLinesEvent = "ImportedLogLines";
        private const string ChangeZoneEvent = "ChangeZone";
        private const string ChangePrimaryPlayerEvent = "ChangePrimaryPlayer";
        private const string FileChangedEvent = "FileChanged";
        private const string OnlineStatusChangedEvent = "OnlineStatusChanged";
        private const string PartyChangedEvent = "PartyChanged";

        // Event Source

        public MiniParseEventSourceConfig Config { get; set; }

        public MiniParseEventSource(ILogger logger) : base(logger)
        {
            this.Name = "MiniParse";

            // FileChanged isn't actually raised by this event source. That event is generated in MiniParseOverlay directly.
            RegisterEventTypes(new List<string> {
                CombatDataEvent,
                FileChangedEvent,
                LogLineEvent,
                ImportedLogLinesEvent,
            });

            // These events need to deliver cached values to new subscribers.
            RegisterCachedEventTypes(new List<string> {
                ChangePrimaryPlayerEvent,
                ChangeZoneEvent,
                OnlineStatusChangedEvent,
                PartyChangedEvent,
            });

            RegisterEventHandler("getLanguage", (msg) => {
                var lang = FFXIVRepository.GetLanguage();
                return JObject.FromObject(new
                {
                    language = lang.ToString("g"),
                    languageId = lang.ToString("d"),
                });
            });

            ActGlobals.oFormActMain.BeforeLogLineRead += LogLineHandler;
            NetworkParser.OnOnlineStatusChanged += (o, e) =>
            {
                var obj = new JObject();
                obj["type"] = OnlineStatusChangedEvent;
                obj["target"] = e.Target;
                obj["rawStatus"] = e.Status;
                obj["status"] = StatusMap.ContainsKey(e.Status) ? StatusMap[e.Status] : "Unknown";

                DispatchAndCacheEvent(obj);
            };

            FFXIVRepository.RegisterPartyChangeDelegate((partyList, partySize) => DispatchPartyChangeEvent());

            InitializeEnmityEventSource();
        }

        private void LogLineHandler(bool isImport, LogLineEventArgs args)
        {
            if (isImport)
            {
                lock (importedLogs)
                {
                    importedLogs.Add(args.originalLogLine);
                }
                return;
            }

            LogMessageType lineType;
            var line = args.originalLogLine.Split('|');

            if (!int.TryParse(line[0], out int lineTypeInt))
            {
                return;
            }

            try
            {
                lineType = (LogMessageType)lineTypeInt;
            } catch
            {
                return;
            }

            switch (lineType)
            {
                case LogMessageType.ChangeZone:
                    if (line.Length < 3) return;

                    var zoneID = Convert.ToUInt32(line[2], 16);

                    DispatchAndCacheEvent(JObject.FromObject(new
                    {
                        type = ChangeZoneEvent,
                        zoneID,
                    }));
                    break;

                case LogMessageType.ChangePrimaryPlayer:
                    if (line.Length < 4) return;

                    var charID = Convert.ToUInt32(line[2], 16);
                    var charName = line[3];

                    DispatchAndCacheEvent(JObject.FromObject(new
                    {
                        type = ChangePrimaryPlayerEvent,
                        charID,
                        charName,
                    }));
                    break;
            }

            DispatchEvent(JObject.FromObject(new
            {
                type = LogLineEvent,
                line,
                rawLine = args.originalLogLine,
            }));
        }

        struct PartyMember
        {
            // Player id in hex (for ease in matching logs).
            public string id;
            public string name;
            public uint worldId;
            // Raw job id.
            public int job;
            // In immediate party (true), vs in alliance (false).
            public bool inParty;
        }

        private void DispatchPartyChangeEvent()
        {
            var combatants = FFXIVRepository.GetCombatants();
            if (combatants == null)
                return;

            List<PartyMember> result = new List<PartyMember>(24);

            // The partyList contents from the PartyListChangedDelegate
            // are equivalent to the set of ids enumerated by |query|
            var query = combatants.Where(c => c.PartyType != PartyTypeEnum.None);
            foreach (var c in query)
            {
                result.Add(new PartyMember()
                {
                    id = $"{c.ID:X}",
                    name = c.Name,
                    worldId = c.WorldID,
                    job = c.Job,
                    inParty = c.PartyType == PartyTypeEnum.Party,
                });
            }

            DispatchAndCacheEvent(JObject.FromObject(new
            {
                type = PartyChangedEvent,
                party = result,
            }));
        }

        public override Control CreateConfigControl()
        {
            return new MiniParseEventSourceConfigPanel(this);
        }

        public override void LoadConfig(IPluginConfig config)
        {
            this.Config = MiniParseEventSourceConfig.LoadConfig(config);

            this.Config.UpdateIntervalChanged += (o, e) =>
            {
                this.Start();
            };

            LoadEnmityConfig();
        }

        public override void SaveConfig(IPluginConfig config)
        {
            this.Config.SaveConfig(config);
        }

        public override void Start()
        {
            this.timer.Change(0, this.Config.UpdateInterval * 1000);
        }

        protected override void Update()
        {
            var importing = ActGlobals.oFormImportProgress?.Visible == true;

            if (CheckIsActReady() && (!importing || this.Config.UpdateDpsDuringImport))
            {
                if (!HasSubscriber(CombatDataEvent))
                {
                    return;
                }

                // 最終更新時刻に変化がないなら更新を行わない
                if (this.prevEncounterId == ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.EncId &&
                    this.prevEndDateTime == ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.EndTime &&
                    this.prevEncounterActive == ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.Active)
                {
                    return;
                }

                this.prevEncounterId = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.EncId;
                this.prevEndDateTime = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.EndTime;
                this.prevEncounterActive = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.Active;

                DispatchEvent(this.CreateCombatData());
            }
            
            if (importing && HasSubscriber(ImportedLogLinesEvent))
            {
                List<string> logs = null;

                lock (importedLogs)
                {
                    if (importedLogs.Count > 0)
                    {
                        logs = importedLogs;
                        importedLogs = new List<string>();
                    }
                }

                if (logs != null)
                {
                    DispatchEvent(JObject.FromObject(new
                    {
                        type = ImportedLogLinesEvent,
                        logLines = logs
                    }));
                }
            }
        }

        internal JObject CreateCombatData()
        {
            if (!CheckIsActReady())
            {
                return new JObject();
            }

#if DEBUG
            //var stopwatch = new Stopwatch();
            //stopwatch.Start();
#endif

            var allies = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.GetAllies();
            Dictionary<string, string> encounter = null;
            List<KeyValuePair<CombatantData, Dictionary<string, string>>> combatant = null;

            var encounterTask = Task.Run(() =>
            {
                encounter = GetEncounterDictionary(allies);
            });
            var combatantTask = Task.Run(() =>
            {
                combatant = GetCombatantList(allies);
            });
            Task.WaitAll(encounterTask, combatantTask);

            if (encounter == null || combatant == null) return new JObject();

            JObject obj = new JObject();

            obj["type"] = "CombatData";
            obj["Encounter"] = JObject.FromObject(encounter);
            obj["Combatant"] = new JObject();

            if (this.Config.SortKey != null && this.Config.SortKey != "")
            {
                int factor = this.Config.SortDesc ? -1 : 1;
                var key = this.Config.SortKey;

                try
                {
                    combatant.Sort((a, b) =>
                    {
                        try
                        {
                            var aValue = float.Parse(a.Value[key]);
                            var bValue = float.Parse(b.Value[key]);

                            return factor * aValue.CompareTo(bValue);
                        } catch(FormatException)
                        {
                            return 0;
                        } catch(KeyNotFoundException)
                        {
                            return 0;
                        }
                    });
                }
                catch(Exception e)
                {
                    Log(LogLevel.Error, Resources.ListSortFailed, key, e);
                }
            }

            foreach (var pair in combatant)
            {
                JObject value = new JObject();
                foreach (var pair2 in pair.Value)
                {
                    value.Add(pair2.Key, Util.ReplaceNaNString(pair2.Value, "---"));
                }

                obj["Combatant"][pair.Key.Name] = value;
            }

            obj["isActive"] = ActGlobals.oFormActMain.ActiveZone.ActiveEncounter.Active ? "true" : "false";

#if DEBUG
            //stopwatch.Stop();
            //Log(LogLevel.Trace, "CreateUpdateScript: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif
            return obj;
        }

        private List<KeyValuePair<CombatantData, Dictionary<string, string>>> GetCombatantList(List<CombatantData> allies)
        {
#if DEBUG
            //var stopwatch = new Stopwatch();
            //stopwatch.Start();
#endif

            var combatantList = new List<KeyValuePair<CombatantData, Dictionary<string, string>>>();
            Parallel.ForEach(allies, (ally) =>
            //foreach (var ally in allies)
            {
                var valueDict = new Dictionary<string, string>();
                foreach (var exportValuePair in CombatantData.ExportVariables)
                {
                    try
                    {
                        // NAME タグには {NAME:8} のようにコロンで区切られたエクストラ情報が必要で、
                        // プラグインの仕組み的に対応することができないので除外する
                        if (exportValuePair.Key.StartsWith("NAME"))
                        {
                            continue;
                        }

                        // ACT_FFXIV_Plugin が提供する LastXXDPS は、
                        // ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items に All キーが存在しない場合に、
                        // プラグイン内で例外が発生してしまい、パフォーマンスが悪化するので代わりに空の文字列を挿入する
                        if (exportValuePair.Key == "Last10DPS" ||
                            exportValuePair.Key == "Last30DPS" ||
                            exportValuePair.Key == "Last60DPS" ||
                            exportValuePair.Key == "Last180DPS")
                        {
                            if (!ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items.ContainsKey("All"))
                            {
                                valueDict.Add(exportValuePair.Key, "");
                                continue;
                            }
                        }

                        var value = exportValuePair.Value.GetExportString(ally, "");
                        valueDict.Add(exportValuePair.Key, value);
                    }
                    catch (Exception e)
                    {
                        Log(LogLevel.Debug, "GetCombatantList: {0}: {1}: {2}", ally.Name, exportValuePair.Key, e);
                        continue;
                    }
                }

                lock (combatantList)
                {
                    combatantList.Add(new KeyValuePair<CombatantData, Dictionary<string, string>>(ally, valueDict));
                }
            }
            );

#if DEBUG
            //stopwatch.Stop();
            //Log(LogLevel.Trace, "GetCombatantList: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif

            return combatantList;
        }

        private Dictionary<string, string> GetEncounterDictionary(List<CombatantData> allies)
        {
#if DEBUG
            //var stopwatch = new Stopwatch();
            //stopwatch.Start();
#endif

            var encounterDict = new Dictionary<string, string>();
            foreach (var exportValuePair in EncounterData.ExportVariables)
            {
                try
                {
                    // ACT_FFXIV_Plugin が提供する LastXXDPS は、
                    // ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items に All キーが存在しない場合に、
                    // プラグイン内で例外が発生してしまい、パフォーマンスが悪化するので代わりに空の文字列を挿入する
                    if (exportValuePair.Key == "Last10DPS" ||
                        exportValuePair.Key == "Last30DPS" ||
                        exportValuePair.Key == "Last60DPS" ||
                        exportValuePair.Key == "Last180DPS")
                    {
                        if (!allies.All((ally) => ally.Items[CombatantData.DamageTypeDataOutgoingDamage].Items.ContainsKey("All")))
                        {
                            encounterDict.Add(exportValuePair.Key, "");
                            continue;
                        }
                    }

                    var value = exportValuePair.Value.GetExportString(
                        ActGlobals.oFormActMain.ActiveZone.ActiveEncounter,
                        allies,
                        "");
                    //lock (encounterDict)
                    //{
                    encounterDict.Add(exportValuePair.Key, value);
                    //}
                }
                catch (Exception e)
                {
                    Log(LogLevel.Debug, "GetEncounterDictionary: {0}: {1}", exportValuePair.Key, e);
                }
            }

#if DEBUG
            //stopwatch.Stop();
            //Log(LogLevel.Trace, "GetEncounterDictionary: {0} msec", stopwatch.Elapsed.TotalMilliseconds);
#endif

            return encounterDict;
        }

        private static bool CheckIsActReady()
        {
            if (ActGlobals.oFormActMain?.ActiveZone?.ActiveEncounter != null &&
                EncounterData.ExportVariables != null &&
                CombatantData.ExportVariables != null)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}

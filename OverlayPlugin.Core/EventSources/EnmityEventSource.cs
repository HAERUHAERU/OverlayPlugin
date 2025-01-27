using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace RainbowMage.OverlayPlugin.EventSources
{
    partial class MiniParseEventSource : EventSourceBase
    {
        private EnmityMemory memory;
        private Timer enmityTimer;
        private const int defaultEnmityInterval = 100;

        // General information about the target, focus target, hover target.  Also, enmity entries for main target.
        private const string EnmityTargetDataEvent = "EnmityTargetData";
        // All of the mobs with aggro on the player.  Equivalent of the sidebar aggro list in game.
        private const string EnmityAggroListEvent = "EnmityAggroList";

        public void InitializeEnmityEventSource()
        {
            this.memory = new EnmityMemory(logger);

            RegisterEventTypes(new List<string> {
                EnmityTargetDataEvent, EnmityAggroListEvent,
            });
        }

        public void LoadEnmityConfig()
        {
            enmityTimer = new Timer(UpdateEnmity, null, Timeout.Infinite, this.Config.EnmityIntervalMs);
            enmityTimer.Change(0, this.Config.EnmityIntervalMs);
            this.Config.EnmityIntervalChanged += (o, e) =>
            {
                enmityTimer.Change(0, this.Config.EnmityIntervalMs);
            };
        }

        protected void UpdateEnmity(object state)
        {
            try
            {
                if (memory == null || !memory.IsValid())
                    return;

                bool targetData = HasSubscriber(EnmityTargetDataEvent);
                bool aggroList = HasSubscriber(EnmityAggroListEvent);
                if (!targetData && !aggroList)
                    return;

                var combatants = memory.GetCombatantList();

                if (targetData)
                {
                    this.DispatchEvent(CreateTargetData(combatants));
                }
                if (aggroList)
                {
                    this.DispatchEvent(CreateAggroList(combatants));
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "UpdateEnmity: {0}", ex.ToString());
            }
        }

        [Serializable]
        internal class EnmityTargetDataObject
        {
            public string type = EnmityTargetDataEvent;
            public Combatant Target;
            public Combatant Focus;
            public Combatant Hover;
            public Combatant TargetOfTarget;
            public List<EnmityEntry> Entries;
        }

        [Serializable]
        internal class EnmityAggroListObject
        {
            public string type = EnmityAggroListEvent;
            public List<AggroEntry> AggroList;
        }

        internal JObject CreateTargetData(List<Combatant> combatants)
        {
            EnmityTargetDataObject enmity = new EnmityTargetDataObject();
            try
            {
                var mychar = memory.GetSelfCombatant();
                enmity.Target = memory.GetTargetCombatant();
                if (enmity.Target != null)
                {
                    if (enmity.Target.TargetID > 0)
                    {
                        enmity.TargetOfTarget = combatants.FirstOrDefault((Combatant x) => x.ID == (enmity.Target.TargetID));
                    }
                    enmity.Target.Distance = mychar.DistanceString(enmity.Target);

                    if (enmity.Target.Type == ObjectType.Monster)
                    {
                        enmity.Entries = memory.GetEnmityEntryList(combatants);
                    }
                }

                enmity.Focus = memory.GetFocusCombatant();
                enmity.Hover = memory.GetHoverCombatant();
                if (enmity.Focus != null)
                {
                    enmity.Focus.Distance = mychar.DistanceString(enmity.Focus);
                }
                if (enmity.Hover != null)
                {
                    enmity.Hover.Distance = mychar.DistanceString(enmity.Hover);
                }
                if (enmity.TargetOfTarget != null)
                {
                    enmity.TargetOfTarget.Distance = mychar.DistanceString(enmity.TargetOfTarget);
                }
            }
            catch (Exception ex)
            {
                this.logger.Log(LogLevel.Error, "CreateTargetData: {0}", ex);
            }
            return JObject.FromObject(enmity);
        }

        internal JObject CreateAggroList(List<Combatant> combatants)
        {
            EnmityAggroListObject enmity = new EnmityAggroListObject();
            try
            {
                enmity.AggroList = memory.GetAggroList(combatants);
            }
            catch (Exception ex)
            {
                this.logger.Log(LogLevel.Error, "CreateAggroList: {0}", ex);
            }
            return JObject.FromObject(enmity);
        }
    }
}

using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Thin wrapper around AllaganTools' raw Dalamud IPC channels.
    /// AllaganTools does not ship a convenience API class like AutoRetainer does,
    /// so we call its IPC subscribers directly.
    /// </summary>
    public class AllaganToolsConnector
    {
        private readonly ICallGateSubscriber<bool> isInitialized;

        public AllaganToolsConnector(IDalamudPluginInterface pluginInterface)
        {
            isInitialized = pluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
            getCharacterItems = pluginInterface.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");
        }

        /// <summary>
        /// Returns true if AllaganTools is installed, running, and ready to respond to IPC calls.
        /// Returns false (never throws) if AllaganTools is not installed or not responding.
        /// </summary>
        public bool IsReady()
        {
            try
            {
                return isInitialized.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
        private readonly ICallGateSubscriber<ulong, HashSet<ulong[]>> getCharacterItems;

        public class ParsedItem
        {
            public uint Container;
            public uint Slot;
            public uint ItemId;
            public uint Quantity;
            public ulong RetainerId;
            public uint SortedContainer;
            public int SortedSlotIndex;
            public uint[] GearSetIds = Array.Empty<uint>();
        }

        public List<ParsedItem> GetCharacterItems(ulong characterId)
        {
            var result = new List<ParsedItem>();

            try
            {
                var raw = getCharacterItems.InvokeFunc(characterId);
                foreach (var item in raw)
                {
                    if (item.Length < 24) continue;

                    // GearSets occupy every index from 25 onward — a variable-length
                    // list of gear set IDs this item currently belongs to (can be
                    // empty for items not saved into any gear set).
                    var gearSetCount = item.Length > 25 ? item.Length - 25 : 0;
                    var gearSetIds = gearSetCount > 0
                        ? new uint[gearSetCount]
                        : Array.Empty<uint>();
                    for (var i = 0; i < gearSetCount; i++)
                    {
                        gearSetIds[i] = (uint)item[25 + i];
                    }

                    result.Add(new ParsedItem
                    {
                        Container = (uint)item[0],
                        Slot = (uint)item[1],
                        ItemId = (uint)item[2],
                        Quantity = (uint)item[3],
                        RetainerId = item[23],
                        SortedContainer = (uint)item[20],
                        SortedSlotIndex = (int)item[22],
                        GearSetIds = gearSetIds
                    });
                }
            }
            catch
            {
                // AllaganTools not available or call failed — return empty list.
            }

            return result;
        }
    }
}

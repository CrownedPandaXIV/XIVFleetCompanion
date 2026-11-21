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
    public partial class AllaganToolsConnector
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


        /// <summary>Reads one source. Call on the game's thread: Allagan Tools changes its inventories there.</summary>
        public Answer Read(ulong characterId)
        {
            HashSet<ulong[]>? raw;
            try
            {
                raw = getCharacterItems.InvokeFunc(characterId);
            }
            catch (Exception ex)
            {
                return new(new(), $"{ex.GetType().Name}: {ex.Message}");
            }
            return Parse(raw);
        }

        /// <summary>
        /// Every inventory slot AllaganTools has for a character, retainer or Free Company id, or null when it could not
        /// be asked (for the Check page, which only shows them).
        /// </summary>
        public List<ParsedItem>? GetCharacterItems(ulong characterId)
        {
            var answer = Read(characterId);
            return answer.Problem == null ? answer.Items : null;
        }
    }
}

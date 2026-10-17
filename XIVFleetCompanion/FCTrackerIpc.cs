using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Asks a running FCTracker for Free Company and house details through its IPC (FCTracker's
    /// IPC/IPCProvider.cs), instead of reading its config file. FCTracker versions without these calls, or an
    /// FCTracker that is not running, answer null, and the file is read as before.
    /// Call on the game's thread: FCTracker's data is only changed there.
    /// </summary>
    public class FCTrackerIpc
    {
        private readonly ICallGateSubscriber<ulong, FCTrackerConnector.CharEntry?> getCharData;
        private readonly ICallGateSubscriber<ulong, FCTrackerConnector.FcEntry?> getFcData;

        public FCTrackerIpc(IDalamudPluginInterface pluginInterface)
        {
            getCharData = pluginInterface.GetIpcSubscriber<ulong, FCTrackerConnector.CharEntry?>("FCTracker.GetCharData");
            getFcData = pluginInterface.GetIpcSubscriber<ulong, FCTrackerConnector.FcEntry?>("FCTracker.GetFCData");
        }

        /// <summary>
        /// The same as <see cref="FCTrackerConnector.ReadHousingData"/> for these characters, from FCTracker
        /// itself; null (never throws) when FCTracker does not offer these calls or one fails.
        /// </summary>
        public Dictionary<ulong, FCTrackerConnector.HousingInfo>? ReadHousing(IEnumerable<ulong> cids, HashSet<ulong>? withoutFc = null)
        {
            try
            {
                return FCTrackerConnector.ReadHousingLive(cids, cid => getCharData.InvokeFunc(cid), id => getFcData.InvokeFunc(id), withoutFc);
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"Fleet Companion: FCTracker's IPC is not available ({ex.GetType().Name}); its file is read instead.");
                return null;
            }
        }
    }
}

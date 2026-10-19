using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Asks a running FCTracker for Free Company and house details through its IPC (FCTracker's
    /// IPC/IPCProvider.cs), instead of reading its config file. Newer FCTrackers hand over every character and Free
    /// Company in one call each; slightly older ones are asked one at a time. FCTracker versions without these
    /// calls, or an FCTracker that is not running, answer null, and the file is read as before.
    /// Call on the game's thread: FCTracker's data is only changed there.
    /// </summary>
    public class FCTrackerIpc
    {
        private readonly ICallGateSubscriber<ulong, FCTrackerConnector.CharEntry?> getCharData;
        private readonly ICallGateSubscriber<ulong, FCTrackerConnector.FcEntry?> getFcData;
        // false: every one FCTracker knows, from all its accounts.
        private readonly ICallGateSubscriber<bool, Dictionary<ulong, FCTrackerConnector.CharEntry>?> getAllCharData;
        private readonly ICallGateSubscriber<bool, Dictionary<ulong, FCTrackerConnector.FcEntry>?> getAllFcData;
        private readonly ICallGateSubscriber<int> getIpcVersion;

        public FCTrackerIpc(IDalamudPluginInterface pluginInterface)
        {
            getCharData = pluginInterface.GetIpcSubscriber<ulong, FCTrackerConnector.CharEntry?>("FCTracker.GetCharData");
            getFcData = pluginInterface.GetIpcSubscriber<ulong, FCTrackerConnector.FcEntry?>("FCTracker.GetFCData");
            getAllCharData = pluginInterface.GetIpcSubscriber<bool, Dictionary<ulong, FCTrackerConnector.CharEntry>?>("FCTracker.GetAllCharData");
            getAllFcData = pluginInterface.GetIpcSubscriber<bool, Dictionary<ulong, FCTrackerConnector.FcEntry>?>("FCTracker.GetAllFCData");
            getIpcVersion = pluginInterface.GetIpcSubscriber<int>("FCTracker.GetIPCVersion");
        }

        /// <summary>
        /// The same as <see cref="FCTrackerConnector.ReadHousingData"/> for these characters, from FCTracker
        /// itself; null (never throws) when FCTracker does not offer these calls or one fails.
        /// </summary>
        public Dictionary<ulong, FCTrackerConnector.HousingInfo>? ReadHousing(IEnumerable<ulong> cids, HashSet<ulong>? withoutFc = null)
        {
            try
            {
                return FCTrackerConnector.ReadHousingLiveAll(cids, () => getAllCharData.InvokeFunc(false), () => getAllFcData.InvokeFunc(false),
                    cid => getCharData.InvokeFunc(cid), id => getFcData.InvokeFunc(id), withoutFc);
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"Fleet Companion: FC Tracker's IPC is not available ({ex.GetType().Name}); its file is read instead.");
                return null;
            }
        }

        /// <summary>The version of FCTracker's IPC (1 and up), or null when it does not say (older, or not running).</summary>
        public int? IpcVersion()
        {
            try
            {
                return getIpcVersion.InvokeFunc();
            }
            catch
            {
                return null;
            }
        }
    }
}

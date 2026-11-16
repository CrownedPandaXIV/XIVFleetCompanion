namespace XIVFleetCompanion;

/// <summary>
/// One database connection on screen (2.4.3). There used to be a Local / Remote switch between two saved connections;
/// both are still kept (PostgresCredentialStore), and Configuration.UseRemoteConnection says which is in use. No Dalamud
/// here, so it is tested on its own (tests/FleetWriterTests).
/// </summary>
public static class ConnectionSlot
{
    /// <summary>Which saved connection to use (true: the one once called Remote): the one in use, unless only the other
    /// is saved, so nobody is left without their connection.</summary>
    public static bool ToUse(bool usingRemote, bool localSaved, bool remoteSaved) =>
        usingRemote ? remoteSaved || !localSaved : remoteSaved && !localSaved;
}

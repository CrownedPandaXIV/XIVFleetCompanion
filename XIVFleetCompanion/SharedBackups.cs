using Npgsql;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Settings → Backups for a shared database, as the app's Settings → Backups: Back up now, the daily backup, the
    /// list with Test and Restore, Restore from a file, and Get the backup tools. The settings and the tools are the
    /// app's own (in %APPDATA%\XIV Fleet Companion), so a PC with both shares them. The app makes its daily backup with
    /// a Windows scheduled task; where that isn't set up (no app on this PC), a running game makes it instead, once it's
    /// due (and only one game, if several are running).
    /// </summary>
    public sealed class SharedBackups
    {
        public static string Folder => Path.GetDirectoryName(FileDatabase.DefaultPath)!;
        public static string SettingsFile => Path.Combine(Folder, "backups.json");
        public static string ToolsDir => Path.Combine(Folder, "postgres-tools");
        public const string AppTaskName = "XIV Fleet Companion backup";

        private readonly Func<PgLogin?> login;
        private readonly Func<bool> isShared;
        private readonly Action<string> log;
        private DateTime nextDailyLook;
        private bool? appTask;
        private DateTime appTaskLookedAt;

        public SharedBackups(Func<PgLogin?> login, Func<bool> isShared, Action<string> log)
        {
            this.login = login;
            this.isShared = isShared;
            this.log = log;
        }

        public bool Busy { get; private set; }

        /// <summary>What the last thing done said (or what's happening now).</summary>
        public string? Status { get; private set; }

        public bool StatusOk { get; private set; } = true;

        /// <summary>The last test's table counts (null before one).</summary>
        public List<PgBackups.TableCount>? TestRows { get; private set; }

        public PgBackups.Tools? Tools { get; private set; }
        public string? ToolsError { get; private set; }
        public bool ToolsLooked { get; private set; }

        /// <summary>Whether the app's scheduled task makes the daily backup on this PC (null: not known yet).</summary>
        public bool? AppTask => appTask;

        public (PgBackups.Settings Settings, PgBackups.LastResult? Last) Load() => PgBackups.Load(SettingsFile);

        /// <summary>Saves settings from the window: null when saved, else why not.</summary>
        public string? Save(PgBackups.Settings input)
        {
            var (s, error) = PgBackups.Validate(input);
            if (s == null) return error;
            PgBackups.Save(SettingsFile, s);
            ToolsLooked = false;
            return null;
        }

        /// <summary>Looks for PostgreSQL's tools again (after the bin folder changes, or the tools are fetched).</summary>
        public void FindTools()
        {
            ToolsLooked = true;
            var chosen = Load().Settings.PgBin;
            Task.Run(async () =>
            {
                var (tools, error) = await PgBackups.FindTools(chosen, ToolsDir);
                Tools = tools;
                ToolsError = error;
            });
        }

        private void Start(string doing, Func<PgBackups.Tools, PgLogin, Task<(bool Ok, string Message)>> work)
        {
            if (Busy) return;
            Busy = true;
            Status = doing;
            StatusOk = true;
            Task.Run(async () =>
            {
                try
                {
                    var l = login();
                    if (l == null) throw new InvalidOperationException("The database connection isn't set up yet (Settings, Database).");
                    var (tools, error) = await PgBackups.FindTools(Load().Settings.PgBin, ToolsDir);
                    Tools = tools;
                    ToolsError = error;
                    ToolsLooked = true;
                    if (tools == null) throw new InvalidOperationException(error);
                    var (ok, message) = await work(tools, l);
                    Status = message;
                    StatusOk = ok;
                }
                catch (Exception e)
                {
                    Status = e.Message;
                    StatusOk = false;
                }
                finally
                {
                    Busy = false;
                }
            });
        }

        private async Task<(bool, string)> MakeBackup(PgBackups.Tools tools, PgLogin l)
        {
            var settings = Load().Settings;
            var r = await PgBackups.BackupNow(tools, l, settings, DateTime.Now);
            PgBackups.SaveLast(SettingsFile, new PgBackups.LastResult(r.Ok, new DateTimeOffset(r.At).ToUnixTimeMilliseconds(), r.Message, r.File));
            return (r.Ok && r.CopyError == null, r.Message);
        }

        public void BackUpNow() => Start("Backing up...", MakeBackup);

        public void Test(string file)
        {
            TestRows = null;
            Start("Testing the backup (loading it into a scratch database)...", async (tools, l) =>
            {
                var r = await PgBackups.Test(tools, l, file);
                TestRows = r.Rows;
                return (r.Ok, r.Message);
            });
        }

        public void Restore(string file) => Start("Restoring (backing up the database as it is first)...", async (tools, l) =>
        {
            var check = await PgBackups.CheckFile(tools, file);
            if (check != null) return (false, check);
            var r = await PgBackups.Restore(tools, l, Load().Settings, file, DateTime.Now);
            return (r.Ok, r.Message);
        });

        /// <summary>Get the backup tools: PostgreSQL's zip for the database's version, only the tools kept.</summary>
        public void GetTools(string connectionString)
        {
            if (Busy) return;
            Busy = true;
            Status = "Asking the database which PostgreSQL it runs...";
            StatusOk = true;
            Task.Run(async () =>
            {
                try
                {
                    int version;
                    await using (var c = new NpgsqlConnection(connectionString))
                    {
                        await c.OpenAsync();
                        await using var cmd = new NpgsqlCommand("SHOW server_version_num", c);
                        version = int.Parse((string)(await cmd.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    var (file, message) = PgBackups.ChooseDownload(version);
                    if (file == null) throw new InvalidOperationException(message);
                    var (bin, v, error) = await PgBackups.GetTools(file, ToolsDir, Path.Combine(Path.GetTempPath(), "xiv-fleet-downloads"), text => Status = $"PostgreSQL {file.Version}'s backup tools (about {file.SizeMb} MB): {text}");
                    if (bin == null) throw new InvalidOperationException(error);
                    Status = $"Got the backup tools ({v}).";
                    StatusOk = true;
                    var (tools, toolsError) = await PgBackups.FindTools(Load().Settings.PgBin, ToolsDir);
                    Tools = tools;
                    ToolsError = toolsError;
                    ToolsLooked = true;
                }
                catch (Exception e)
                {
                    Status = e.Message;
                    StatusOk = false;
                }
                finally
                {
                    Busy = false;
                }
            });
        }

        /// <summary>Called every frame: once a minute, makes the daily backup if it's due, the database is shared, and the
        /// app's scheduled task doesn't make it on this PC.</summary>
        public void Tick(DateTime now)
        {
            if (now < nextDailyLook || Busy) return;
            nextDailyLook = now.AddMinutes(1);
            if (!isShared()) return;
            var settings = Load().Settings;
            if (!settings.Enabled || !PgBackups.Due(settings, PgBackups.List(settings.Folder), now)) return;
            Busy = true;
            Task.Run(async () =>
            {
                try
                {
                    if (await AppTaskInstalled()) return;
                    // Only one game at a time: the others find it's done once they get the folder.
                    Directory.CreateDirectory(settings.Folder);
                    FileStream gate;
                    try { gate = new FileStream(Path.Combine(settings.Folder, "backup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { return; }
                    using (gate)
                    {
                        if (!PgBackups.Due(settings, PgBackups.List(settings.Folder), DateTime.Now)) return;
                        var l = login();
                        if (l == null) return;
                        var (tools, error) = await PgBackups.FindTools(settings.PgBin, ToolsDir);
                        if (tools == null)
                        {
                            PgBackups.SaveLast(SettingsFile, new PgBackups.LastResult(false, DateTimeOffset.Now.ToUnixTimeMilliseconds(), error ?? "", null));
                            return;
                        }
                        var (ok, message) = await MakeBackup(tools, l);
                        log($"Daily backup: {message}");
                        Status = $"Daily backup: {message}";
                        StatusOk = ok;
                    }
                }
                catch (Exception e)
                {
                    log($"Daily backup failed: {e.Message}");
                }
                finally
                {
                    Busy = false;
                }
            });
        }

        // Whether the app's daily scheduled task is set up on this PC (looked at once an hour).
        private async Task<bool> AppTaskInstalled()
        {
            if (!OperatingSystem.IsWindows()) return false;
            if (appTask is bool known && DateTime.Now - appTaskLookedAt < TimeSpan.FromHours(1)) return known;
            var r = await PgBackups.Run("schtasks.exe", new[] { "/Query", "/TN", AppTaskName });
            appTask = r.Ok;
            appTaskLookedAt = DateTime.Now;
            return r.Ok;
        }

        /// <summary>Looks whether the app's task is set up, for the window to say which makes the daily backup.</summary>
        public void LookForAppTask()
        {
            if (appTask != null && DateTime.Now - appTaskLookedAt < TimeSpan.FromHours(1)) return;
            appTaskLookedAt = DateTime.Now;
            Task.Run(async () =>
            {
                appTask = null;
                await AppTaskInstalled();
            });
        }
    }
}

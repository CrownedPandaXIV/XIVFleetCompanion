using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The Sub Calculator's newer data, as the app's sub-data-update.js: once a day the plugin asks the Releases repository
    /// for the copy the app's sub-data.yml last published, and keeps it when it is fine and newer than the one in use
    /// (SubCalculatorData.Adopt). A day missed while the game was closed is made up for when it next opens. Nothing changes
    /// when it can't be reached. No Dalamud here, so it is tested on its own.
    /// </summary>
    public sealed class SubDataUpdate
    {
        private readonly string stateFile;
        private readonly Func<Task<string>> get;
        private readonly Action<string> log;
        private DateTime? lastCheck;
        private DateTime nextLook = DateTime.MinValue;
        private volatile bool running;

        /// <summary>folder: the plugin's config folder; get: the published file's text (downloaded; made up in the tests).</summary>
        public SubDataUpdate(string folder, Action<string> log, Func<Task<string>>? get = null)
        {
            Directory.CreateDirectory(folder);
            SubCalculatorData.UseFolder(folder);
            stateFile = Path.Combine(folder, "sub-data-check.txt");
            this.log = log;
            this.get = get ?? Download;
            lastCheck = ReadState(stateFile);
        }

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

        private static async Task<string> Download()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SubCalculatorData.Url);
            request.Headers.UserAgent.ParseAdd("xiv-fleet-companion");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            return await response.Content.ReadAsStringAsync();
        }

        private static DateTime? ReadState(string file)
        {
            try
            {
                return File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Called every frame: at most once a minute it looks whether a check is due, and starts one in the background.</summary>
        public void Tick(DateTime nowUtc)
        {
            if (running || nowUtc < nextLook) return;
            nextLook = nowUtc.AddMinutes(1);
            if (!SubCalculatorData.Due(lastCheck, nowUtc)) return;
            running = true;
            Task.Run(async () =>
            {
                try { await CheckNow(nowUtc); }
                finally { running = false; }
            });
        }

        /// <summary>One check: whether a newer copy was kept, and what went wrong if anything did.</summary>
        public async Task<(bool Adopted, string? Error)> CheckNow(DateTime nowUtc)
        {
            (bool, string?) done;
            try
            {
                var adopted = SubCalculatorData.Adopt(await get());
                done = (adopted, null);
                if (adopted) log($"Sub Calculator data updated (made {SubCalculatorData.Current().About.Made:u}).");
            }
            catch (Exception ex)
            {
                done = (false, ex.Message);
                log($"Sub Calculator data: no newer copy fetched ({ex.Message}).");
            }
            lastCheck = nowUtc;
            try { File.WriteAllText(stateFile, new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)); }
            catch (Exception) { /* checked again on the next start */ }
            return done;
        }
    }
}

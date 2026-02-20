using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using JacRed.Engine.CORE;
using JacRed.Models.tParse;
using IO = System.IO;
using JacRed.Engine;
using JacRed.Models.Details;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http;
using System.Net;
using CoreHttp = JacRed.Engine.CORE.HttpClient;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/torrentby/[action]")]
    public class TorrentByController : Controller
    {
        static Dictionary<string, List<TaskParse>> taskParse = new Dictionary<string, List<TaskParse>>();
        static readonly object taskLock = new object();

        static TorrentByController()
        {
            try
            {
                if (IO.File.Exists("Data/temp/torrentby_taskParse.json"))
                    taskParse = JsonConvert.DeserializeObject<Dictionary<string, List<TaskParse>>>(IO.File.ReadAllText("Data/temp/torrentby_taskParse.json"));
            }
            catch { }
        }

        readonly IMemoryCache memoryCache;
        static readonly object rndLock = new object();
        static readonly Random rnd = new Random();

        public TorrentByController(IMemoryCache memoryCache)
        {
            this.memoryCache = memoryCache;
        }

        #region Cookie / Login / Persist
        static readonly string CookiePath = "Data/temp/torrentby.cookie";

        static string NormalizeCookie(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var piece in raw.Split(';'))
            {
                var p = piece?.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                var kv = p.Split(new[] { '=' }, 2);
                if (kv.Length < 2) continue;

                var name = kv[0].Trim();
                var val = kv[1].Trim().Trim('\"');

                // Skip Set-Cookie attributes
                if (name.Equals("path", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("domain", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("expires", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("max-age", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("secure", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("httponly", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("samesite", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (name.Length > 0)
                    dict[name] = val;
            }

            return string.Join("; ", dict.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        static void SaveCookie(IMemoryCache memoryCache, string raw)
        {
            var norm = NormalizeCookie(raw);
            if (string.IsNullOrWhiteSpace(norm)) return;

            memoryCache.Set("torrentby:cookie", norm, DateTime.Now.AddDays(1));
            AppInit.conf.TorrentBy.cookie = norm;

            try
            {
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(CookiePath));
                IO.File.WriteAllText(CookiePath, norm);
            }
            catch { }
        }

        static string LoadCookie(IMemoryCache memoryCache)
        {
            if (memoryCache.TryGetValue("torrentby:cookie", out string c) && !string.IsNullOrWhiteSpace(c))
                return c;

            if (!string.IsNullOrWhiteSpace(AppInit.conf.TorrentBy.cookie))
            {
                var n = NormalizeCookie(AppInit.conf.TorrentBy.cookie);
                if (!string.IsNullOrWhiteSpace(n))
                {
                    SaveCookie(memoryCache, n);
                    return n;
                }
            }

            try
            {
                if (IO.File.Exists(CookiePath))
                {
                    var fromFile = NormalizeCookie(IO.File.ReadAllText(CookiePath));
                    if (!string.IsNullOrWhiteSpace(fromFile))
                    {
                        SaveCookie(memoryCache, fromFile);
                        return fromFile;
                    }
                }
            }
            catch { }

            return null;
        }

        async Task<bool> EnsureLogin()
        {
            var cookie = LoadCookie(memoryCache);
            if (!string.IsNullOrEmpty(cookie))
                return true;

            return await TakeLogin();
        }

        async Task<bool> TakeLogin()
        {
            string authKey = "torrentby:TakeLogin()";
            if (memoryCache.TryGetValue(authKey, out _))
                return false;

            memoryCache.Set(authKey, 0, TimeSpan.FromMinutes(2));

            try
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false };
                handler.ServerCertificateCustomValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

                using (var client = new System.Net.Http.HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
                    client.DefaultRequestHeaders.Add("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                    var post = new Dictionary<string, string>
                    {
                        { "username", AppInit.conf.TorrentBy.login.u },
                        { "password", AppInit.conf.TorrentBy.login.p }
                    };

                    using (var content = new FormUrlEncodedContent(post))
                    {
                        using (var resp = await client.PostAsync($"{AppInit.conf.TorrentBy.host}/login/", content))
                        {
                            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                            {
                                var pairs = setCookies
                                    .Select(sc => sc?.Split(';')?.FirstOrDefault()?.Trim())
                                    .Where(s => !string.IsNullOrWhiteSpace(s));
                                var combined = string.Join("; ", pairs);

                                SaveCookie(memoryCache, combined);
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
        }
        #endregion

        #region Helpers
        // с категорией "series"
        static readonly string[] categories = new[] { "films", "movies", "serials", "series", "tv", "humor", "cartoons", "anime", "sport" };

        static string[] MapTypes(string cat)
        {
            switch (cat)
            {
                case "films":
                case "movies":
                    return new[] { "movie" };
                case "series":
                case "serials":
                    return new[] { "serial" };
                case "tv":
                case "humor":
                    return new[] { "tvshow" };
                case "cartoons":
                    return new[] { "multfilm", "multserial" };
                case "anime":
                    return new[] { "anime" };
                case "sport":
                    return new[] { "sport" };
                default:
                    return Array.Empty<string>();
            }
        }

        async Task DelayWithJitter()
        {
            int baseMs = AppInit.conf.TorrentBy.parseDelay;
            if (baseMs <= 0) baseMs = 1000;
            int jitter;
            lock (rndLock) jitter = rnd.Next(250, 1250);
            await Task.Delay(baseMs + jitter);
        }

        static string HtmlDecode(string s) => string.IsNullOrEmpty(s) ? s : HttpUtility.HtmlDecode(s);
        static string StripTags(string s) => string.IsNullOrEmpty(s) ? s : Regex.Replace(s, "<.*?>", string.Empty);

        static string NormalizeUrlToHost(string anyHref)
        {
            if (string.IsNullOrWhiteSpace(anyHref)) return null;

            try
            {
                if (anyHref.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var abs = new Uri(anyHref, UriKind.Absolute);
                    var baseU = new Uri(AppInit.conf.TorrentBy.host, UriKind.Absolute);
                    return $"{baseU.Scheme}://{baseU.Host}{abs.AbsolutePath}";
                }

                if (anyHref.StartsWith("/"))
                    return $"{AppInit.conf.TorrentBy.host}{anyHref}";
            }
            catch { }

            return null;
        }
        #endregion

        #region Parse (manual)
        static bool workParse = false;

        // /cron/torrentby/parse?page=0
        [HttpGet]
        public async Task<string> Parse(int page = 0)  // по умолчанию 0
        {
            if (workParse) return "work";
            workParse = true;
            var sb = new StringBuilder();

            try
            {
                await EnsureLogin();

                foreach (var cat in categories)
                {
                    var (ok, ins, upd) = await parsePage(cat, page);
                    sb.AppendLine($"{cat} - {(ok ? "ok" : "empty")} (ins: {ins}, upd: {upd})");
                    await DelayWithJitter();
                }
            }
            catch { }
            finally
            {
                workParse = false;
            }

            return sb.Length == 0 ? "ok" : sb.ToString();
        }
        #endregion

        #region UpdateTasksParse (init + daily)
        static bool _taskWork = false;

        // /cron/torrentby/UpdateTasksParse
        [HttpGet]
        public async Task<string> UpdateTasksParse()
        {
            if (_taskWork) return "work";
            _taskWork = true;

            try
            {
                await EnsureLogin();

                // init pages plan if empty -> 0..9
                lock (taskLock)
                {
                    foreach (var cat in categories)
                    {
                        if (!taskParse.ContainsKey(cat))
                            taskParse[cat] = Enumerable.Range(0, 10).Select(p => new TaskParse(p)).ToList();
                    }
                }

                foreach (var kv in taskParse.ToArray())
                {
                    foreach (var tp in kv.Value.OrderBy(a => a.updateTime))
                    {
                        if (tp.updateTime.Date == DateTime.Today)
                            continue;

                        var (ok, _, __) = await parsePage(kv.Key, tp.page);
                        if (ok)
                            tp.updateTime = DateTime.Today;

                        await DelayWithJitter();
                    }
                }
            }
            catch { }
            finally
            {
                _taskWork = false;
                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    IO.File.WriteAllText("Data/temp/torrentby_taskParse.json", JsonConvert.SerializeObject(taskParse));
                }
                catch { }
            }

            return "ok";
        }
        #endregion

        #region ParseAllTask
        static bool _parseAllTaskWork = false;

        // /cron/torrentby/ParseAllTask
        [HttpGet]
        public async Task<string> ParseAllTask()
        {
            if (_parseAllTaskWork)
                return "work";

            _parseAllTaskWork = true;

            try
            {
                await EnsureLogin();

                foreach (var task in taskParse.ToArray())
                {
                    foreach (var val in task.Value.ToArray())
                    {
                        try
                        {
                            if (DateTime.Today == val.updateTime)
                                continue;

                            var (ok, _, __) = await parsePage(task.Key, val.page);
                            if (ok)
                                val.updateTime = DateTime.Today;

                            await DelayWithJitter();
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                _parseAllTaskWork = false;
                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    IO.File.WriteAllText("Data/temp/torrentby_taskParse.json", JsonConvert.SerializeObject(taskParse));
                }
                catch { }
            }

            return "ok";
        }
        #endregion

        #region ResetTasksPlan
        // /cron/torrentby/ResetTasksPlan
        [HttpGet]
        public string ResetTasksPlan()
        {
            try
            {
                lock (taskLock)
                {
                    // пересобираем план 0..9
                    var fresh = new Dictionary<string, List<TaskParse>>();
                    foreach (var cat in categories)
                        fresh[cat] = Enumerable.Range(0, 10).Select(p => new TaskParse(p)).ToList();

                    taskParse = fresh;
                }

                // удаляем старый файл, затем сохраняем новый
                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    if (IO.File.Exists("Data/temp/torrentby_taskParse.json"))
                        IO.File.Delete("Data/temp/torrentby_taskParse.json");

                    IO.File.WriteAllText("Data/temp/torrentby_taskParse.json", JsonConvert.SerializeObject(taskParse));
                }
                catch { }

                return "ok";
            }
            catch
            {
                return "error";
            }
        }
        #endregion

        #region parsePage
        // Возвращает: ok, inserted, updated
        async Task<(bool ok, int inserted, int updated)> parsePage(string cat, int page)
        {
            var cookie = LoadCookie(memoryCache);

            string url = $"{AppInit.conf.TorrentBy.rqHost()}/{cat}/?page={page}";
            string html = await CoreHttp.Get(url,
                useproxy: AppInit.conf.TorrentBy.useproxy,
                cookie: cookie,
                addHeaders: new List<(string name, string val)>
                {
                    ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
                    ("Accept-Language", "ru,en-US;q=0.9,en;q=0.8"),
                    ("Cache-Control", "no-cache"),
                    ("Pragma", "no-cache"),
                    ("Connection", "keep-alive"),
                    ("Upgrade-Insecure-Requests", "1"),
                    ("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36")
                });

            if (html == null)
                return (false, 0, 0);

            // Если пришла страница логина — релогин и ещё раз
            if (html.Contains("name=\"username\"") && html.Contains("name=\"password\""))
            {
                await TakeLogin();
                await Task.Delay(500);

                html = await CoreHttp.Get(url,
                    useproxy: AppInit.conf.TorrentBy.useproxy,
                    cookie: LoadCookie(memoryCache));
                if (html == null)
                    return (false, 0, 0);
            }

            var torrents = new List<TorrentBaseDetails>();

            // Устойчиво собираем строки таблицы
            var rowMatches = Regex.Matches(html, @"<tr[^>]*class\s*=\s*""ttable_col[^""]*""[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match rm in rowMatches)
            {
                string row = tParse.ReplaceBadNames(rm.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(row))
                    continue;

                // Магнит должен быть в списке (по условию)
                var mMag = Regex.Match(row, @"href\s*=\s*['""]\s*(magnet:[^'""]+)['""]", RegexOptions.IgnoreCase);
                if (!mMag.Success)
                    continue;

                string magnet = WebUtility.UrlDecode(HtmlDecode(mMag.Groups[1].Value));
                if (string.IsNullOrWhiteSpace(magnet))
                    continue;

                // Ссылка и заголовок (не magnet)
                string hrefAny = null, title = null;

                var mHrefTitle = Regex.Match(row,
                    @"<a[^>]+href\s*=\s*['""](?!magnet:)(https?:\/\/[^'""]+|\/[^'""]+)['""][^>]*>([^<]+)</a>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (mHrefTitle.Success)
                {
                    hrefAny = HtmlDecode(mHrefTitle.Groups[1].Value);
                    title = StripTags(HtmlDecode(mHrefTitle.Groups[2].Value));
                }
                else
                {
                    // fallback: сначала href, потом любой текстовый <a>
                    var mHref = Regex.Match(row, @"<a[^>]+href\s*=\s*['""](https?:\/\/[^'""]+|\/[^'""]+)['""][^>]*>", RegexOptions.IgnoreCase);
                    if (mHref.Success)
                        hrefAny = HtmlDecode(mHref.Groups[1].Value);

                    var mTitle = Regex.Match(row, @"<a[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
                    if (mTitle.Success)
                        title = StripTags(HtmlDecode(mTitle.Groups[1].Value));
                }

                string fullUrl = NormalizeUrlToHost(hrefAny);
                if (string.IsNullOrEmpty(fullUrl) || string.IsNullOrEmpty(title))
                    continue;

                // Размер
                string sizeName = Regex.Match(row, @"</td>\s*<td[^>]*>\s*([^<]+)\s*</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(sizeName))
                {
                    var mSize2 = Regex.Match(row, @">\s*([0-9\.,]+\s*(?:TB|GB|MB|KiB|MiB|GiB))\s*<", RegexOptions.IgnoreCase);
                    if (mSize2.Success) sizeName = mSize2.Groups[1].Value.Trim();
                }

                // Сиды/пиры
                int sid = 0, pir = 0;
                int.TryParse(Regex.Match(row, @"<font[^>]*color\s*=\s*""green""[^>]*>[^0-9]*([0-9]+)</font>", RegexOptions.IgnoreCase).Groups[1].Value, out sid);
                int.TryParse(Regex.Match(row, @"<font[^>]*color\s*=\s*""red""[^>]*>[^0-9]*([0-9]+)</font>", RegexOptions.IgnoreCase).Groups[1].Value, out pir);

                // Дата: Сегодня/Вчера [+ время], или  YYYY-MM-DD[ HH:MM] / YYYY.MM.DD[ HH:MM]
                DateTime createTime = default;

                var mToday = Regex.Match(row, @"сегодня[^0-9]*([0-9]{2}):([0-9]{2})", RegexOptions.IgnoreCase);
                var mYesterday = Regex.Match(row, @"вчера[^0-9]*([0-9]{2}):([0-9]{2})", RegexOptions.IgnoreCase);
                if (mToday.Success && int.TryParse(mToday.Groups[1].Value, out int th) && int.TryParse(mToday.Groups[2].Value, out int tm))
                    createTime = DateTime.Today.AddHours(th).AddMinutes(tm);
                else if (mYesterday.Success && int.TryParse(mYesterday.Groups[1].Value, out int yh) && int.TryParse(mYesterday.Groups[2].Value, out int ym))
                    createTime = DateTime.Today.AddDays(-1).AddHours(yh).AddMinutes(ym);
                else
                {
                    if (Regex.IsMatch(row, @"\bсегодня\b", RegexOptions.IgnoreCase)) createTime = DateTime.Today;
                    else if (Regex.IsMatch(row, @"\bвчера\b", RegexOptions.IgnoreCase)) createTime = DateTime.Today.AddDays(-1);
                    else
                    {
                        // YYYY-MM-DD [HH:MM]  или  YYYY.MM.DD [HH:MM]
                        var mDate = Regex.Match(row,
                            @"([0-9]{4})[.\-]([0-9]{2})[.\-]([0-9]{2})(?:\s*&nbsp;\s*|\s+)?(?:(\d{2}):(\d{2}))?",
                            RegexOptions.IgnoreCase);
                        if (mDate.Success)
                        {
                            int y = int.Parse(mDate.Groups[1].Value);
                            int mo = int.Parse(mDate.Groups[2].Value);
                            int d = int.Parse(mDate.Groups[3].Value);
                            int hh = 0, mm = 0;
                            if (mDate.Groups[4].Success) int.TryParse(mDate.Groups[4].Value, out hh);
                            if (mDate.Groups[5].Success) int.TryParse(mDate.Groups[5].Value, out mm);
                            try { createTime = new DateTime(y, mo, d, hh, mm, 0); } catch { }
                        }
                    }
                }
                if (createTime == default) continue;

                // Названия/год
                string name = null, originalname = null;
                int relased = 0;
                var g = Regex.Match(title, @"^\s*(?<ru>[^/]+?)(?:\s*/\s*(?<en>[^/]+?))?\s*\((?<y>\d{4})", RegexOptions.Singleline);
                if (g.Success)
                {
                    name = tParse.ReplaceBadNames(g.Groups["ru"].Value).Trim();
                    originalname = tParse.ReplaceBadNames(g.Groups["en"].Value).Trim();
                    int.TryParse(g.Groups["y"].Value, out relased);
                }

                var types = MapTypes(cat);

                torrents.Add(new TorrentBaseDetails()
                {
                    trackerName = "torrentby",
                    types = types,
                    url = fullUrl,
                    title = title,
                    sid = sid,
                    pir = pir,
                    sizeName = sizeName,
                    magnet = magnet,
                    createTime = createTime,
                    name = name,
                    originalname = originalname,
                    relased = relased
                });
            }

            int inserted = 0, updated = 0;
            if (torrents.Count > 0)
            {
                await FileDB.AddOrUpdate<TorrentBaseDetails>(
                    torrents,
                    async (tor, db) =>
                    {
                        if (db != null && db.ContainsKey(tor.url))
                            updated++;
                        else
                            inserted++;
                        return true;
                    }
                );
            }

            return (torrents.Count > 0, inserted, updated);
        }
        #endregion
    }
}

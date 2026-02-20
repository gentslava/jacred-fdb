using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using JacRed.Engine.CORE;
using JacRed.Engine;
using JacRed.Models.Details;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
// Алиас на ваш JacRed.Engine.CORE.HttpClient, чтобы избежать конфликта с System.Net.Http.HttpClient
using CoreHttp = JacRed.Engine.CORE.HttpClient;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/lostfilm/[action]")]
    public class LostfilmController : BaseController
    {
        // === Управление логированием ===
        // По умолчанию — тихий режим (только финальный summary).
        static bool _verbose = false;
        static void VLog(string msg) { if (_verbose) Console.WriteLine(msg); }

        #region Hosts & Aggregators
        static string lfHost => AppInit.conf.Lostfilm.rqHost();

        // Бэкапные (если не удалось получить подписанный URL через v_search.php)
        static readonly string[] PlainAggTemplates = new[]
        {
            "https://insearch.site/v3/index.php?c={c}&s={s}&e={e}&n=1",
            "https://insearch.ws/v3/index.php?c={c}&s={s}&e={e}&n=1",
            "https://n.tracktor.site/index.php?c={c}&s={s}&e={e}&n=1",
            "https://n.tracktor.site/s/lostfilm?c={c}&s={s}&e={e}"
        };
        #endregion

        #region Helpers: torrent parse / scrape
        static (string[] announces, string primary) TryExtractAnnounces(byte[] torrent)
        {
            string text = null;
            try { text = Encoding.UTF8.GetString(torrent); } catch { }
            if (string.IsNullOrEmpty(text))
                try { text = Encoding.GetEncoding(1251).GetString(torrent); } catch { }

            var list = new List<string>();
            if (!string.IsNullOrEmpty(text))
            {
                // primary announce
                var m = Regex.Match(text, @"8:announce(\d+):", RegexOptions.Singleline);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int len))
                {
                    int start = m.Index + m.Length;
                    if (start + len <= text.Length)
                    {
                        string url = text.Substring(start, len);
                        if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                            list.Add(url);
                    }
                }

                // announce-list
                foreach (Match ml in Regex.Matches(text, @"13:announce-listl(.*?)e", RegexOptions.Singleline))
                {
                    foreach (Match mu in Regex.Matches(ml.Groups[1].Value, @"\d+:https?://[^\s\e]+"))
                    {
                        var parts = mu.Value.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            string url = parts[1];
                            int sp = url.IndexOf('e');
                            if (sp > 0) url = url.Substring(0, sp);
                            if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                                list.Add(url);
                        }
                    }
                }
            }

            var distinct = list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
            return (distinct, distinct.FirstOrDefault());
        }

        static string TryBuildScrapeUrl(string announce)
        {
            if (string.IsNullOrEmpty(announce))
                return null;

            if (!announce.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !announce.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return null;

            if (announce.Contains("/announce"))
                return announce.Replace("/announce", "/scrape");

            return announce;
        }

        static string UrlEncodeBytes(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (byte b in bytes)
                sb.Append('%').Append(b.ToString("X2"));
            return sb.ToString();
        }

        static (int seeds, int peers) TryParseScrapeResponse(string bencoded)
        {
            if (string.IsNullOrEmpty(bencoded))
                return (0, 0);

            var mc = Regex.Match(bencoded, @"8:completei(\d+)e", RegexOptions.Singleline);
            var mi = Regex.Match(bencoded, @"9:incompletei(\d+)e", RegexOptions.Singleline);

            int seeds = mc.Success && int.TryParse(mc.Groups[1].Value, out var s1) ? s1 : 0;
            int peers = mi.Success && int.TryParse(mi.Groups[1].Value, out var p1) ? p1 : 0;
            return (seeds, peers);
        }

        static byte[] FromBase32(string s)
        {
            const string ALPH = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            int bits = 0, value = 0;
            var bytes = new List<byte>();
            foreach (char c in s.TrimEnd('='))
            {
                int idx = ALPH.IndexOf(char.ToUpperInvariant(c));
                if (idx < 0) continue;
                value = (value << 5) | idx;
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }
            return bytes.ToArray();
        }
        #endregion

        #region EpisodeId extraction + v_search redirect
        static string ExtractEpisodeId(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            string id;

            // /v_search.php?a=123456
            id = Regex.Match(html, @"v_search\.php\?a=(\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
            if (!string.IsNullOrEmpty(id)) return id;

            // v_search.php', { a: '123456' }
            id = Regex.Match(html, @"v_search\.php['""]\s*,\s*\{\s*a\s*:\s*'(\d+)'", RegexOptions.IgnoreCase).Groups[1].Value;
            if (!string.IsNullOrEmpty(id)) return id;

            // data-episode / data-episode-id
            id = Regex.Match(html, @"data-episode(?:-id)?\s*=\s*['""](\d+)['""]", RegexOptions.IgnoreCase).Groups[1].Value;
            if (!string.IsNullOrEmpty(id)) return id;

            // episodeId = '123456';
            id = Regex.Match(html, @"episodeId\s*=\s*['""](\d+)['""]", RegexOptions.IgnoreCase).Groups[1].Value;
            if (!string.IsNullOrEmpty(id)) return id;

            return null;
        }

        // Получаем подписанный URL агрегатора, повторяем до 5 «переходов»
        static async Task<string> GetRedirectFinalUrl(string vsearchUrl, string referer, string cookie)
        {
            string current = vsearchUrl;
            for (int i = 0; i < 5; i++)
            {
                using (var handler = new System.Net.Http.HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                    UseCookies = false
                })
                using (var client = new System.Net.Http.HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(20);
                    var req = new HttpRequestMessage(HttpMethod.Get, current);
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
                    req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    req.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
                    if (!string.IsNullOrEmpty(referer))
                        req.Headers.Referrer = new Uri(referer);
                    if (!string.IsNullOrEmpty(cookie))
                        req.Headers.TryAddWithoutValidation("Cookie", cookie);

                    using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        // 3xx Location
                        if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400 && resp.Headers.Location != null)
                        {
                            string next = resp.Headers.Location.IsAbsoluteUri
                                ? resp.Headers.Location.AbsoluteUri
                                : new Uri(new Uri(current), resp.Headers.Location).AbsoluteUri;

                            VLog($"[lostfilm] v_search redirect {i + 1}: {next}");
                            if (next.Contains("insearch", StringComparison.OrdinalIgnoreCase) ||
                                next.Contains("tracktor", StringComparison.OrdinalIgnoreCase))
                                return next;

                            referer = current;
                            current = next;
                            continue;
                        }

                        // Не редирект — читаем body
                        string body = await resp.Content.ReadAsStringAsync();

                        // meta refresh
                        var m1 = Regex.Match(body, @"http-equiv\s*=\s*[""']refresh[""']\s*content\s*=\s*[""'][^;]*;\s*url=([^""']+)", RegexOptions.IgnoreCase);
                        if (m1.Success)
                        {
                            string next = m1.Groups[1].Value.Trim();
                            if (!next.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                next = new Uri(new Uri(current), next).AbsoluteUri;

                            VLog($"[lostfilm] v_search meta-refresh: {next}");
                            if (next.Contains("insearch", StringComparison.OrdinalIgnoreCase) ||
                                next.Contains("tracktor", StringComparison.OrdinalIgnoreCase))
                                return next;

                            referer = current;
                            current = next;
                            continue;
                        }

                        // window.location / location.href
                        var m2 = Regex.Match(body, @"location(?:\.href)?\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
                        if (m2.Success)
                        {
                            string next = m2.Groups[1].Value.Trim();
                            if (!next.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                next = new Uri(new Uri(current), next).AbsoluteUri;

                            VLog($"[lostfilm] v_search window.location: {next}");
                            if (next.Contains("insearch", StringComparison.OrdinalIgnoreCase) ||
                                next.Contains("tracktor", StringComparison.OrdinalIgnoreCase))
                                return next;

                            referer = current;
                            current = next;
                            continue;
                        }

                        // url="https://...."
                        var m3 = Regex.Match(body, @"url=(""?)(https?://[^""'>\s]+)", RegexOptions.IgnoreCase);
                        if (m3.Success)
                        {
                            string next = m3.Groups[2].Value.Trim();
                            VLog($"[lostfilm] v_search url=: {next}");
                            if (next.Contains("insearch", StringComparison.OrdinalIgnoreCase) ||
                                next.Contains("tracktor", StringComparison.OrdinalIgnoreCase))
                                return next;

                            referer = current;
                            current = next;
                            continue;
                        }

                        VLog($"[lostfilm] v_search no final URL on step {i + 1}");
                        return null;
                    }
                }
            }

            return null;
        }
        #endregion

        #region Parse aggregator page → ВСЕ варианты
        class MagnetInfo
        {
            public string magnet;
            public string quality;   // 2160p/1080p/720p/SD/MP4
            public string rip;       // WEBRip/WEB-DL/HDTV/BluRay/...
            public string sizeName;
            public int seeds;
            public int peers;
        }

        static string ParseQuality(string s)
        {
            var m = Regex.Match(s ?? "", "(2160p|1440p|1080p|720p|SD|MP4)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }
        static string ParseRip(string s)
        {
            var m = Regex.Match(s ?? "", @"\b(WEB[-\s]?DL|WEBRIP|HDTV|BLURAY|BDRIP|HDRIP)\b", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var v = m.Groups[1].Value.ToUpperInvariant();
            v = v.Replace("WEBDL", "WEB-DL").Replace("WEB DL","WEB-DL").Replace("  ", " ").Trim();
            return v;
        }

        async Task<List<MagnetInfo>> TryExtractAllFromAggHtml(string shtml, string referer)
        {
            var result = new List<MagnetInfo>();
            if (string.IsNullOrWhiteSpace(shtml))
                return result;

            string flat = Regex.Replace(shtml, "[\n\r\t]+", " ");

            // кандидаты (html + js)
            var re1 = new Regex(@"<div\s+class=""inner-box--link\s+main""\s*>\s*<a[^>]*href=""([^""]+)""[^>]*>\s*([^<]+)\s*</a>", RegexOptions.IgnoreCase);
            var re2 = new Regex(@"href=""(https?://[^""]*(?:/td\.php[^""]*|\.torrent))""[^>]*>([^<]*)<", RegexOptions.IgnoreCase);
            var re3 = new Regex(@"location(?:\.href)?\s*=\s*['""](https?://[^'""]*td\.php[^'""]*)['""]", RegexOptions.IgnoreCase);

            var candidates = new List<(string link, string label)>();
            foreach (Match m in re1.Matches(flat)) candidates.Add((m.Groups[1].Value, m.Groups[2].Value));
            foreach (Match m in re2.Matches(flat)) candidates.Add((m.Groups[1].Value, m.Groups[2].Value));
            foreach (Match m in re3.Matches(flat)) candidates.Add((m.Groups[1].Value, ""));

            VLog($"[lostfilm] agg links: {candidates.Count}");

            var seenMagnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (link, label) in candidates)
            {
                if (string.IsNullOrWhiteSpace(link))
                    continue;

                byte[] torrent = await CoreHttp.Download(
                    link,
                    referer: referer,
                    timeoutSeconds: 30,
                    useproxy: AppInit.conf.Lostfilm.useproxy
                );

                VLog($"[lostfilm] torrent GET {(torrent?.Length ?? 0)} from {link}");

                if (torrent == null || torrent.Length == 0)
                    continue;

                string magnet = BencodeTo.Magnet(torrent);
                if (string.IsNullOrWhiteSpace(magnet) || !seenMagnets.Add(magnet))
                    continue;

                string sizeName = BencodeTo.SizeName(torrent);

                int seeds = 0, peers = 0;
                try
                {
                    var (announces, primary) = TryExtractAnnounces(torrent);
                    string scrape = TryBuildScrapeUrl(primary);

                    string ih = Regex.Match(magnet, @"xt=urn:btih:([A-Za-z0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                    byte[] ihBytes = null;
                    if (!string.IsNullOrEmpty(ih))
                    {
                        try
                        {
                            if (ih.Length == 40)
                                ihBytes = Enumerable.Range(0, ih.Length / 2).Select(i => Convert.ToByte(ih.Substring(i * 2, 2), 16)).ToArray();
                            else
                                ihBytes = FromBase32(ih);
                        }
                        catch { ihBytes = null; }
                    }

                    if (!string.IsNullOrEmpty(scrape) && ihBytes != null)
                    {
                        string qsep = scrape.Contains("?") ? "&" : "?";
                        string resp = await CoreHttp.Get(
                            scrape + qsep + "info_hash=" + UrlEncodeBytes(ihBytes),
                            timeoutSeconds: 10,
                            useproxy: AppInit.conf.Lostfilm.useproxy
                        );
                        var sp = TryParseScrapeResponse(resp);
                        seeds = sp.seeds;
                        peers = sp.peers;
                    }
                }
                catch { }

                result.Add(new MagnetInfo
                {
                    magnet = magnet,
                    quality = ParseQuality(label),
                    rip = ParseRip(label),
                    sizeName = sizeName,
                    seeds = seeds,
                    peers = peers
                });
            }

            return result;
        }
        #endregion

        #region Основной сбор: ВСЕ варианты по эпизоду
        async Task<List<MagnetInfo>> GetAllMagnetsForEpisode(string url)
        {
            var all = new List<MagnetInfo>();

            // страница эпизода
            string fullNews = await CoreHttp.Get(
                url,
                cookie: AppInit.conf.Lostfilm.cookie,
                useproxy: AppInit.conf.Lostfilm.useproxy,
                httpversion: 2,
                addHeaders: new List<(string, string)> { ("accept-language", "ru-RU,ru;q=0.9") }
            );

            if (string.IsNullOrWhiteSpace(fullNews))
            {
                VLog($"[lostfilm] EMPTY episode page: {url}");
                return all;
            }

            // c/s/e
            string seriesId = null, season = null, episode = null;
            var pe3 = Regex.Match(
                fullNews,
                @"PlayEpisode\(\s*'(?<c>\d+)'\s*,\s*'(?<s>\d+)'\s*,\s*'(?<e>\d+)'\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline
            );
            if (pe3.Success)
            {
                seriesId = pe3.Groups["c"].Value;
                season   = pe3.Groups["s"].Value;
                episode  = pe3.Groups["e"].Value;
                VLog($"[lostfilm] PlayEpisode c/s/e: {seriesId}/{season}/{episode}");
            }
            else
            {
                seriesId = Regex.Match(fullNews, @"/Images/(\d+)/Posters/", RegexOptions.IgnoreCase).Groups[1].Value;
                var se = Regex.Match(url, @"season_(\d+)/episode_(\d+)/", RegexOptions.IgnoreCase);
                season  = se.Groups[1].Value;
                episode = se.Groups[2].Value;
                VLog($"[lostfilm] Fallback c/s/e via og+url: {seriesId}/{season}/{episode}");
            }

            // v_search → подписанный агрегатор
            string episodeId = ExtractEpisodeId(fullNews);
            string signedAggUrl = null;

            if (!string.IsNullOrEmpty(episodeId))
            {
                string vsearch = $"{lfHost}/v_search.php?a={episodeId}";
                VLog($"[lostfilm] v_search: {vsearch}");
                signedAggUrl = await GetRedirectFinalUrl(vsearch, referer: url, cookie: AppInit.conf.Lostfilm.cookie);
                VLog($"[lostfilm] v_search final: {signedAggUrl}");
            }

            // Источники:
            // 1) если есть подписанный URL — используем ТОЛЬКО его;
            // 2) иначе — plain-шаблоны.
            var aggUrls = new List<string>();
            bool haveSigned = !string.IsNullOrWhiteSpace(signedAggUrl);
            if (haveSigned)
            {
                VLog("[lostfilm] using signed aggregator; skip plain fallbacks");
                aggUrls.Add(signedAggUrl);
            }
            else if (!string.IsNullOrWhiteSpace(seriesId) && !string.IsNullOrWhiteSpace(season) && !string.IsNullOrWhiteSpace(episode))
            {
                foreach (var tpl in PlainAggTemplates)
                    aggUrls.Add(tpl.Replace("{c}", seriesId).Replace("{s}", season).Replace("{e}", episode));
            }

            var seenMagnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var aggUrl in aggUrls)
            {
                string shtml = await CoreHttp.Get(
                    aggUrl,
                    timeoutSeconds: 25,
                    useproxy: AppInit.conf.Lostfilm.useproxy,
                    httpversion: 2,
                    addHeaders: new List<(string, string)>
                    {
                        ("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36"),
                        ("accept-language","ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7"),
                        ("referer", url)
                    }
                );

                VLog($"[lostfilm] agg GET: {aggUrl} len={(shtml?.Length ?? 0)}");

                // отсечём "тонкие" ответы
                if (string.IsNullOrEmpty(shtml) || shtml.Length < 600)
                    continue;

                var items = await TryExtractAllFromAggHtml(shtml, referer: aggUrl);

                int before = all.Count;
                foreach (var it in items)
                {
                    if (seenMagnets.Add(it.magnet))
                        all.Add(it);
                }

                // как только получили что-то — дальше не трогаем другие источники
                if (all.Count > before)
                    break;
            }

            return all;
        }
        #endregion

        #region Debug endpoint
        [HttpGet]
        public async Task<string> debug(string url, int verbose = 0)
        {
            bool old = _verbose; _verbose = verbose != 0;
            try
            {
                var all = await GetAllMagnetsForEpisode(url);
                var sb = new StringBuilder();
                sb.AppendLine($"url={url}");
                sb.AppendLine($"variants={all.Count}");
                int i = 0;
                foreach (var v in all)
                {
                    string q = string.Join(" ", new[] { v.quality, v.rip }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    if (string.IsNullOrWhiteSpace(q)) q = "WEB-DLRip";
                    sb.AppendLine($"[{++i}] {q} | size={v.sizeName} | seeds={v.seeds} peers={v.peers} | magnet={(v.magnet?.Substring(0, Math.Min(60, v.magnet.Length)) ?? "")}...");
                }
                return sb.ToString();
            }
            finally
            {
                _verbose = old;
            }
        }
        #endregion

        #region Parse (мульти-добавление всех вариантов) — только финальный лог по умолчанию
        static bool _workParse = false;

        [HttpGet]
        async public Task<string> Parse(int maxpage = 1, int verbose = 0)
        {
            if (_workParse)
                return "work";

            bool old = _verbose; _verbose = verbose != 0;
            _workParse = true;

            int totalChecked = 0; // эпизодов просмотрено
            int totalFound = 0;   // вариантов найдено (с агрегатора)
            int totalAdded = 0;   // реально добавлено в БД

            try
            {
                for (int i = 1; i <= maxpage; i++)
                {
                    if (i > 1)
                        await Task.Delay(AppInit.conf.Lostfilm.parseDelay);

                    var (checkedEp, foundVar, added) = await parsePage(i);
                    totalChecked += checkedEp;
                    totalFound   += foundVar;
                    totalAdded   += added;

                    // Лог по страницам — только в verbose
                    VLog($"[lostfilm] page {i}: checked={checkedEp}, found={foundVar}, added={added}");
                }
            }
            catch (Exception ex)
            {
                VLog("[lostfilm] Parse error: " + ex.Message);
            }
            finally
            {
                _workParse = false;
                _verbose = old;
            }

            Console.WriteLine($"[lostfilm] summary: checked={totalChecked}, found={totalFound}, added={totalAdded}");
            return $"ok (checked={totalChecked}, found={totalFound}, added={totalAdded})";
        }
        #endregion

        #region parsePage (/new) → строим ВСЕ варианты и одним заходом пишем в БД
        async Task<(int checkedCount, int foundCount, int addedCount)> parsePage(int page)
        {
            string url = page > 1 ? $"{lfHost}/new/page_{page}" : $"{lfHost}/new/";
            string html = await CoreHttp.Get(url, useproxy: AppInit.conf.Lostfilm.useproxy, httpversion: 2);
            if (html == null || !html.Contains("LostFilm.TV</title>"))
                return (0, 0, 0);

            int checkedCount = 0; // эпизодов
            int foundCount = 0;   // суммарно вариантов
            int addedCount = 0;

            // Соберём список базовых карточек эпизодов
            var episodes = new List<(string baseUrl, string baseTitle, string name, string originalname, int relased, DateTime createTime)>();

            foreach (string row in tParse.ReplaceBadNames(html).Split("class=\"hor-breaker dashed\"").Skip(1))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(row))
                        continue;

                    string Match(string pattern, int index = 1)
                    {
                        string res = HttpUtility.HtmlDecode(new Regex(pattern, RegexOptions.IgnoreCase).Match(row).Groups[index].Value.Trim());
                        res = Regex.Replace(res, "[\n\r\t ]+", " ");
                        return res.Trim();
                    }

                    DateTime createTime = tParse.ParseCreateTime(Match("<div class=\"right-part\">([0-9]{2}\\.[0-9]{2}\\.[0-9]{4})</div>"), "dd.MM.yyyy");
                    if (createTime == default)
                    {
                        if (page != 1)
                            continue;
                        createTime = DateTime.UtcNow;
                    }

                    // предпочтительно — прямая ссылка на эпизод
                    string hrefEpisode = Regex.Match(row, "href=\"/(series/[^\"\\s]+/season_\\d+/episode_\\d+/)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                    string hrefSeries  = Regex.Match(row, "href=\"/(series/[^\"\\s]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;

                    string sinfo = Match("<div class=\"left-part\">([^<]+)</div>");
                    string name = Match("<div class=\"name-ru\">([^<]+)</div>");
                    string originalname = Match("<div class=\"name-en\">([^<]+)</div>");
                    if (string.IsNullOrWhiteSpace(hrefSeries) ||
                        string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(originalname) || string.IsNullOrWhiteSpace(sinfo))
                        continue;

                    // вытаскиваем сезон/эпизод из текста
                    int s = 0, e = 0;
                    int.TryParse(Regex.Match(sinfo, @"([0-9]+)\s*сез", RegexOptions.IgnoreCase).Groups[1].Value, out s);
                    int.TryParse(Regex.Match(sinfo, @"([0-9]+)\s*сер", RegexOptions.IgnoreCase).Groups[1].Value, out e);

                    string fullUrl;
                    if (!string.IsNullOrEmpty(hrefEpisode))
                        fullUrl = $"{lfHost}/{hrefEpisode}";
                    else if (s > 0 && e > 0)
                        fullUrl = $"{lfHost}/{hrefSeries.TrimEnd('/')}/season_{s}/episode_{e}/";
                    else
                        fullUrl = $"{lfHost}/{hrefSeries}";

                    // relased (год)
                    int relased = 0;
                    string serieName = Regex.Match(fullUrl, @"https?://[^/]+/series/([^/]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(serieName))
                        continue;

                    string relasedPath = $"Data/temp/lostfilm/{serieName}.relased";
                    if (System.IO.File.Exists(relasedPath))
                    {
                        relased = int.Parse(System.IO.File.ReadAllText(relasedPath));
                    }
                    else
                    {
                        string series = await CoreHttp.Get($"{lfHost}/series/{serieName}", timeoutSeconds: 20, httpversion: 2);
                        if (!string.IsNullOrWhiteSpace(series))
                        {
                            string dateCreated = Regex.Match(series, "itemprop=\"dateCreated\" content=\"([0-9]{4})-[0-9]{2}-[0-9]{2}\"").Groups[1].Value;
                            if (int.TryParse(dateCreated, out int _date) && _date > 0)
                                relased = _date;
                        }

                        if (relased > 0)
                            System.IO.File.WriteAllText(relasedPath, relased.ToString());
                        else
                            continue;
                    }

                    string baseTitle = $"{name} / {originalname} / {sinfo} [{relased}]";
                    episodes.Add((fullUrl, baseTitle, name, originalname, relased, createTime));
                }
                catch { }
            }

            // Для каждого эпизода берём ВСЕ варианты и формируем итоговый список для записи
            var toInsert = new List<TorrentDetails>();

            foreach (var ep in episodes)
            {
                checkedCount++;

                var all = await GetAllMagnetsForEpisode(ep.baseUrl);
                foundCount += all.Count;

                foreach (var v in all)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(v.quality)) parts.Add(v.quality);
                    if (!string.IsNullOrWhiteSpace(v.rip))     parts.Add(v.rip);

                    string qstr = string.Join(" ", parts);
                    // гарантируем непустой id/label
                    string idLabel = string.IsNullOrWhiteSpace(qstr) ? "WEB-DLRip" : qstr;

                    var det = new TorrentDetails()
                    {
                        trackerName = "lostfilm",
                        types = new[] { "serial" },
                        url = ep.baseUrl + (ep.baseUrl.Contains("?") ? "&" : "?") + "id=" + Uri.EscapeDataString(idLabel),
                        title = ep.baseTitle.Replace("]", $", {idLabel}]"),
                        sid = v.seeds,
                        pir = v.peers,
                        createTime = ep.createTime,
                        name = ep.name,
                        originalname = ep.originalname,
                        relased = ep.relased,
                        magnet = v.magnet,
                        sizeName = v.sizeName
                    };

                    toInsert.Add(det);
                }
            }

            // Одним заходом обновляем/добавляем — считаем именно добавленные
            await FileDB.AddOrUpdate(toInsert, async (t, db) =>
            {
                if (db.TryGetValue(t.url, out TorrentDetails existing) && !string.IsNullOrWhiteSpace(existing.magnet))
                {
                    // уже есть такой вариант
                    return false;
                }

                // иначе — добавляем / обновляем поля
                addedCount++;
                return true;
            });

            return (checkedCount, foundCount, addedCount);
        }
        #endregion
    }
}

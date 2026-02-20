using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using JacRed.Engine;
using JacRed.Engine.CORE;
using JacRed.Models.Details;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/leproduction/[action]")]
    public class LeProductionController : BaseController
    {
        static bool workParse = false;

        class CrawlStats
        {
            public int PagesRequested;
            public int PagesOk;
            public int PagesFail;

            public int PostsFound;
            public int TorrentsDiscovered;

            public int TorrentsSaved;           // удалось получить magnet
            public int TorrentsSkippedCache;    // запись актуальна и пропущена (совпали ключевые поля и есть magnet)
            public int TorrentsFailed;          // не нашли id/магнет/ошибка запроса

            public Stopwatch Watch = new Stopwatch();
        }

        /// <summary>
        /// GET /cron/leproduction/parse
        /// Парсит все страницы по каждому разделу; если limit_page == 0 — кол-во страниц берётся из пагинации.
        /// Пример: /cron/leproduction/parse?limit_page=3 — только первые 3 страницы каждого раздела.
        /// </summary>
        async public Task<string> Parse(int limit_page = 0)
        {
            if (workParse)
                return "work";

            workParse = true;

            // Разделы сайта
            var cats = new List<string> { "anime", "dorama", "film", "serial", "fulcartoon", "cartoon" };
            var total = new CrawlStats();
            var sb = new StringBuilder();
            var swAll = Stopwatch.StartNew();

            try
            {
                foreach (string cat in cats)
                {
                    var st = new CrawlStats();
                    st.Watch.Start();

                    int totalPages = await resolveTotalPages(cat, limit_page);
                    if (totalPages <= 0) totalPages = 1;

                    sb.AppendLine($"[{cat}] pages={totalPages} (limit={(limit_page > 0 ? limit_page.ToString() : "auto")})");

                    for (int page = 1; page <= totalPages; page++)
                    {
                        st.PagesRequested++;
                        var pageWatch = Stopwatch.StartNew();
                        var (ok, posts, torrents) = await parsePage(cat, page, st, sb);
                        pageWatch.Stop();

                        if (ok) st.PagesOk++; else st.PagesFail++;
                        sb.AppendLine($"  page={page}/{totalPages}: posts={posts}; torrents={torrents}; time={pageWatch.Elapsed.TotalSeconds:F1}s");
                    }

                    st.Watch.Stop();
                    total.PagesRequested += st.PagesRequested;
                    total.PagesOk += st.PagesOk;
                    total.PagesFail += st.PagesFail;
                    total.PostsFound += st.PostsFound;
                    total.TorrentsDiscovered += st.TorrentsDiscovered;
                    total.TorrentsSaved += st.TorrentsSaved;
                    total.TorrentsSkippedCache += st.TorrentsSkippedCache;
                    total.TorrentsFailed += st.TorrentsFailed;

                    sb.AppendLine($"[{cat}] summary posts={st.PostsFound}; torrents={st.TorrentsDiscovered}; saved={st.TorrentsSaved}; skipped={st.TorrentsSkippedCache}; failed={st.TorrentsFailed}; time={st.Watch.Elapsed.TotalSeconds:F1}s");
                }
            }
            finally
            {
                workParse = false;
            }

            swAll.Stop();
            sb.AppendLine($"TOTAL pages={total.PagesRequested} (ok={total.PagesOk}, fail={total.PagesFail}); posts={total.PostsFound}; torrents={total.TorrentsDiscovered}; saved={total.TorrentsSaved}, skipped={total.TorrentsSkippedCache}, failed={total.TorrentsFailed}; time={swAll.Elapsed.TotalSeconds:F1}s");
            return sb.ToString();
        }

        #region helpers

        async Task<int> resolveTotalPages(string cat, int limit_page)
        {
            if (limit_page > 0)
                return limit_page;

            string url = $"{AppInit.conf.Leproduction.rqHost()}/{cat}/";
            string html = await HttpClient.Get(url, useproxy: AppInit.conf.Leproduction.useproxy);
            if (html == null)
                return 1;

            // Пагинация вида .../{cat}/page/2/
            var nums = Regex.Matches(html, $@"href\s*=\s*""[^""]*/{cat}/page/([0-9]+)/""", RegexOptions.IgnoreCase)
                            .Cast<Match>()
                            .Select(m => m.Groups[1].Value)
                            .Where(v => int.TryParse(v, out _))
                            .Select(int.Parse)
                            .ToList();

            return nums.Count == 0 ? 1 : nums.Max();
        }

        async Task<(bool ok, int posts, int torrents)> parsePage(string cat, int page, CrawlStats st, StringBuilder sb)
        {
            string pageUrl = page == 1
                ? $"{AppInit.conf.Leproduction.rqHost()}/{cat}/"
                : $"{AppInit.conf.Leproduction.rqHost()}/{cat}/page/{page}/";

            string html = await HttpClient.Get(pageUrl, useproxy: AppInit.conf.Leproduction.useproxy);
            if (html == null)
                return (false, 0, 0);

            // карточки: <a class="short-img" href=".../{cat}/slug.html">
            var postUrls = Regex.Matches(html, $@"<a\s+class=""short-img""\s+href=""(?<url>(?:https?://[^""]+)?/{cat}/[^""]+?\.html)""", RegexOptions.IgnoreCase)
                                .Cast<Match>()
                                .Select(m => m.Groups["url"].Value)
                                .Distinct()
                                .ToList();

            if (postUrls.Count == 0)
            {
                // альтернативный вариант (по заголовку)
                postUrls = Regex.Matches(html, $@"<h3>\s*<a\s+href=""(?<url>(?:https?://[^""]+)?/{cat}/[^""]+?\.html)""", RegexOptions.IgnoreCase)
                                .Cast<Match>()
                                .Select(m => m.Groups["url"].Value)
                                .Distinct()
                                .ToList();
            }

            // нормализуем относительные ссылки в абсолютные
            for (int i = 0; i < postUrls.Count; i++)
            {
                if (postUrls[i].StartsWith("/"))
                    postUrls[i] = $"{AppInit.conf.Leproduction.rqHost()}{postUrls[i]}";
            }

            st.PostsFound += postUrls.Count;

            int torrentsOnPage = 0;
            var torrents = new List<TorrentDetails>();

            foreach (string postUrl in postUrls)
            {
                try
                {
                    string dhtml = await HttpClient.Get(postUrl, useproxy: AppInit.conf.Leproduction.useproxy, referer: pageUrl);
                    if (dhtml == null)
                        continue;

                    // Имена из инфобокса / заголовка
                    string nameRu = Regex.Match(dhtml, @"Русское\s+название:\s*</div>\s*<div[^>]*class=""info-desc""[^>]*>\s*([^<]+)\s*</div>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                    string nameEn = Regex.Match(dhtml, @"Оригинальное\s+название:\s*</div>\s*<div[^>]*class=""info-desc""[^>]*>\s*([^<]+)\s*</div>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                    if (string.IsNullOrWhiteSpace(nameRu))
                    {
                        string h1 = Regex.Match(dhtml, @"<h1>([^<]+)</h1>", RegexOptions.IgnoreCase).Groups[1].Value?.Trim();
                        if (!string.IsNullOrWhiteSpace(h1))
                            nameRu = Regex.Replace(h1, @"\s*/\s*.*$", "").Trim();
                    }

                    // Год выпуска — строго из инфоблока
                    int relased = 0;
                    var myear = Regex.Match(dhtml, @"info-label"">Год выпуска:</div>\s*<div[^>]*class=""info-desc""[^>]*>\s*<a[^>]*>(\d{4})</a>", RegexOptions.IgnoreCase);
                    if (myear.Success) int.TryParse(myear.Groups[1].Value, out relased);

                    // Все ID раздач на карточке
                    string dhtmlDecoded = HttpUtility.HtmlDecode(dhtml) ?? dhtml;
                    var ids = Regex.Matches(dhtmlDecoded, @"index\.php\?do=download&id=(\d+)", RegexOptions.IgnoreCase)
                                   .Cast<Match>()
                                   .Select(m => m.Groups[1].Value)
                                   .Distinct()
                                   .ToList();

                    if (ids.Count == 0)
                    {
                        ids = Regex.Matches(dhtml, @"index\.php\?do=download(?:&amp;|&|%26)id=(\d+)", RegexOptions.IgnoreCase)
                                   .Cast<Match>()
                                   .Select(m => m.Groups[1].Value)
                                   .Distinct()
                                   .ToList();
                    }

                    // последний шанс — по контейнерам id="torrent_{id}_info"
                    if (ids.Count == 0)
                    {
                        ids = Regex.Matches(dhtml, @"id\s*=\s*""torrent_(\d+)_info""", RegexOptions.IgnoreCase)
                                   .Cast<Match>()
                                   .Select(m => m.Groups[1].Value)
                                   .Distinct()
                                   .ToList();
                    }

                    if (ids.Count == 0)
                    {
                        sb.AppendLine($"    [warn] no torrents on post: {postUrl}");
                        continue;
                    }

                    // Соберём counters + имя файла + MAGNET (!)
                    
                    // Разбиваем страницу на блоки по маркерам id="torrent_{id}_info"
                    var idMatches = Regex.Matches(dhtml, @"id\s*=\s*""torrent_(\d+)_info""", RegexOptions.IgnoreCase).Cast<Match>().ToList();
                    var blocks = new Dictionary<string, string>();
                    if (idMatches.Count > 0)
                    {
                        for (int i = 0; i < idMatches.Count; i++)
                        {
                            string tidHere = idMatches[i].Groups[1].Value;
                            int start = idMatches[i].Index;
                            int end = (i + 1 < idMatches.Count) ? idMatches[i + 1].Index : dhtml.Length;
                            string slice = dhtml.Substring(start, Math.Max(0, end - start));

                            // на всякий случай декодируем
                            string sliceDecoded = HttpUtility.HtmlDecode(slice) ?? slice;
                            blocks[tidHere] = sliceDecoded;
                        }
                    }

                    // параллельно соберём все магнеты на странице (в порядке следования)
                    var pageMagnets = Regex.Matches(dhtml, @"href\s*=\s*""(magnet:[^""]+)""", RegexOptions.IgnoreCase)
                                           .Cast<Match>()
                                           .Select(m => HttpUtility.HtmlDecode(m.Groups[1].Value))
                                           .Where(s => !string.IsNullOrWhiteSpace(s))
                                           .ToList();

                    // Соберём counters + имя файла + MAGNET (!)
                    var counters = new Dictionary<string, (int sid, int pir, string sizeName, double size, DateTime createTime, string fileName, string magnet)>();

                    foreach (string tid in ids)
                    {
                        int sid = 0, pir = 0;
                        string sizeName = null;
                        double size = 0;
                        DateTime createTime = DateTime.MinValue;
                        string fileName = null;
                        string magnet = null;

                        string around;
                        if (blocks.TryGetValue(tid, out var block))
                        {
                            around = block;
                        }
                        else
                        {
                            // fallback: окрестность маркера (если нет явного блока у текущей разметки)
                            around = takeAround(dhtml, $"torrent_{tid}_info", 20000);
                            if (string.IsNullOrEmpty(around))
                            {
                                var d2 = HttpUtility.HtmlDecode(dhtml) ?? dhtml;
                                around = takeAround(d2, $"torrent_{tid}_info", 20000);
                            }
                        }

                        // Имя файла (для качества)
                        var mname = Regex.Match(around, @"class=""info_d1-le""[^>]*>\s*([^<]+)\s*</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (mname.Success) fileName = mname.Groups[1].Value.Trim();

                        // Сидеры/личеры
                        int.TryParse(Regex.Match(around, @"Раздают:\s*</b>\s*<span[^>]*class=""li_distribute_m-le""[^>]*>\s*([0-9]+)\s*</span>", RegexOptions.IgnoreCase).Groups[1].Value, out sid);
                        int.TryParse(Regex.Match(around, @"Качают:\s*</b>\s*<span[^>]*class=""li_swing_m-le""[^>]*>\s*([0-9]+)\s*</span>", RegexOptions.IgnoreCase).Groups[1].Value, out pir);

                        // Размер
                        var msize = Regex.Match(around, @"Размер:\s*<span[^>]*>\s*([0-9]+(?:[.,][0-9]+)?)\s*G[bB]\s*</span>", RegexOptions.IgnoreCase);
                        if (msize.Success)
                        {
                            sizeName = Regex.Replace(msize.Groups[0].Value, @"^\s*Размер:\s*", "", RegexOptions.IgnoreCase).Trim();
                            double.TryParse(msize.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out size);
                        }

                        // Дата
                        var mdate = Regex.Match(around, @"\(([0-9]{1,2}\s+[А-ЯЁа-яё]+?\s+[0-9]{4}\s+[0-9]{1,2}:[0-9]{2})\)", RegexOptions.IgnoreCase);
                        if (mdate.Success) createTime = tParse.ParseCreateTime(mdate.Groups[1].Value, "dd.MM.yyyy HH:mm");

                        // MAGNET из блока
                        var mm = Regex.Match(around, @"href\s*=\s*""(magnet:[^""]+)""", RegexOptions.IgnoreCase);
                        if (!mm.Success) mm = Regex.Match(around, @"(magnet:[^\s""'<]+)", RegexOptions.IgnoreCase);
                        if (mm.Success) magnet = HttpUtility.HtmlDecode(mm.Groups[1].Value);

                        counters[tid] = (sid, pir, sizeName, size, createTime, fileName, magnet);
                    }

                    // Пост-обработка: если у некоторых tid магнета нет, но количество pageMagnets == ids.Count, матчим по порядку
                    if (pageMagnets.Count == ids.Count)
                    {
                        for (int i = 0; i < ids.Count; i++)
                        {
                            var key = ids[i];
                            if (counters.TryGetValue(key, out var c) && string.IsNullOrWhiteSpace(c.magnet))
                            {
                                c.magnet = pageMagnets[i];
                                counters[key] = c;
                            }
                        }
                    }                    


                    foreach (string tid in ids)
                    {
                        counters.TryGetValue(tid, out var c);

                        // Качество из имени файла (1080p/720p)
                        string q = null;
                        if (!string.IsNullOrWhiteSpace(c.fileName))
                        {
                            var mq = Regex.Match(c.fileName, @"\b([0-9]{3,4}p)\b", RegexOptions.IgnoreCase);
                            if (mq.Success) q = mq.Groups[1].Value;
                        }
                        string qDigits = (q ?? "0").ToLower().Replace("p", "");

                        // Типы по разделу
                        string[] types = cat switch
                        {
                            "anime" => new[] { "anime" },
                            "dorama" => new[] { "serial" },
                            "film" => new[] { "movie" },
                            "serial" => new[] { "serial" },
                            "fulcartoon" => new[] { "multfilm" },
                            "cartoon" => new[] { "multserial" },
                            _ => new[] { "movie" }
                        };

                        // Заголовок: RU / EN + год + [качество]
                        string title = !string.IsNullOrWhiteSpace(nameEn) ? $"{nameRu} / {nameEn}" : nameRu;
                        if (relased > 0) title += $" {relased}";
                        if (!string.IsNullOrWhiteSpace(q)) title += $" [{q}]";
                        
                        string url = $"{postUrl}?q={qDigits}&id={tid}";

                        torrents.Add(new TorrentDetails
                        {
                            trackerName = "leproduction",
                            types = types,
                            url = url,
                            title = title,
                            sid = c.sid,
                            pir = c.pir,
                            createTime = c.createTime == DateTime.MinValue ? DateTime.Today : c.createTime,
                            name = nameRu,
                            originalname = nameEn,
                            relased = relased,
                            size = c.size,
                            sizeName = string.IsNullOrWhiteSpace(c.sizeName) && c.size > 0 ? $"{c.size:0.##} Gb" : c.sizeName,
                            magnet = c.magnet // <-- кладём magnet прямо при добавлении, если нашли
                        });

                        torrentsOnPage++;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"    [error] post: {postUrl} -> {ex.Message}");
                }
            }

            // Если magnet уже есть — сохраняем сразу; если нет — пробуем достать со страницы index.php?do=download&id=...
            await FileDB.AddOrUpdate(torrents, async (t, db) =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(t.magnet))
                    {
                        st.TorrentsSaved++;
                        return true;
                    }

                    if (db.TryGetValue(t.url, out TorrentDetails cached) && cached.title == t.title && !string.IsNullOrWhiteSpace(cached.magnet))
                    {
                        st.TorrentsSkippedCache++;
                        return true; // запись актуальна
                    }

                    string id = Regex.Match(t.url ?? "", @"[?&]id=([0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        st.TorrentsFailed++;
                        return false;
                    }

                    string magHtml = await HttpClient.Get($"{AppInit.conf.Leproduction.rqHost()}/index.php?do=download&id={id}", referer: AppInit.conf.Leproduction.rqHost(), useproxy: AppInit.conf.Leproduction.useproxy);
                    if (magHtml == null)
                    {
                        st.TorrentsFailed++;
                        return false;
                    }

                    var mm = Regex.Match(magHtml, @"href\s*=\s*""(magnet:[^""]+)""", RegexOptions.IgnoreCase);
                    if (!mm.Success)
                        mm = Regex.Match(magHtml, @"(magnet:[^\s""'<]+)", RegexOptions.IgnoreCase);

                    if (!mm.Success)
                    {
                        st.TorrentsFailed++;
                        return false;
                    }

                    string magnet = HttpUtility.HtmlDecode(mm.Groups[1].Value);
                    if (string.IsNullOrWhiteSpace(magnet))
                    {
                        st.TorrentsFailed++;
                        return false;
                    }

                    t.magnet = magnet;
                    st.TorrentsSaved++;
                    return true;
                }
                catch
                {
                    st.TorrentsFailed++;
                    return false;
                }
            });

            st.TorrentsDiscovered += torrentsOnPage;
            return (true, postUrls.Count, torrentsOnPage);
        }

        static string takeAround(string text, string needle, int radius)
        {
            int i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (i == -1) return "";
            int s = Math.Max(0, i - radius);
            int e = Math.Min(text.Length, i + needle.Length + radius);
            return text.Substring(s, e - s);
        }

        #endregion
    }
}

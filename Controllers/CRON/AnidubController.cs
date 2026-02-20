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
    [Route("/cron/anidub/[action]")]
    public class AnidubController : BaseController
    {
        static bool workParse = false;

        class CrawlStats
        {
            public int PagesRequested;
            public int PagesOk;
            public int PagesFail;

            public int PostsFound;
            public int TorrentsDiscovered;

            public int TorrentsSaved;           // удалось скачать .torrent и извлечь magnet/size
            public int TorrentsSkippedCache;    // запись актуальна и пропущена (совпали ключевые поля)
            public int TorrentsFailed;          // не смогли скачать/распарсить .torrent или нет id

            public Stopwatch Watch = new Stopwatch();
        }

        // /cron/anidub/parse?limit_page=N
        async public Task<string> Parse(int limit_page = 0)
        {
            if (workParse)
                return "work";

            workParse = true;

            var cats = new List<string> { "anime_tv", "anime_movie", "anime_ova", "dorama" };
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
                        var pageRes = await parsePage(cat, page, st, sb);
                        pageWatch.Stop();

                        if (pageRes.ok) st.PagesOk++; else st.PagesFail++;

                        sb.AppendLine(
                            $"  - page {page}/{totalPages}: ok={pageRes.ok} posts={pageRes.posts} torrents={pageRes.torrents} in {pageWatch.Elapsed.TotalSeconds:F1}s"
                        );

                        await Task.Delay(AppInit.conf.Anidub.parseDelay);
                    }

                    st.Watch.Stop();

                    // сводка по категории
                    sb.AppendLine(
                        $"[{cat}] summary: pages ok/fail={st.PagesOk}/{st.PagesFail}, posts={st.PostsFound}, torrents: discovered={st.TorrentsDiscovered}, saved={st.TorrentsSaved}, skipped={st.TorrentsSkippedCache}, failed={st.TorrentsFailed}, time={st.Watch.Elapsed.TotalSeconds:F1}s"
                    );
                    sb.AppendLine();

                    // аккумулируем в итог
                    total.PagesRequested += st.PagesRequested;
                    total.PagesOk += st.PagesOk;
                    total.PagesFail += st.PagesFail;
                    total.PostsFound += st.PostsFound;
                    total.TorrentsDiscovered += st.TorrentsDiscovered;
                    total.TorrentsSaved += st.TorrentsSaved;
                    total.TorrentsSkippedCache += st.TorrentsSkippedCache;
                    total.TorrentsFailed += st.TorrentsFailed;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[error] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                workParse = false;
            }

            swAll.Stop();
            sb.AppendLine($"[TOTAL] pages ok/fail={total.PagesOk}/{total.PagesFail} (req={total.PagesRequested}), posts={total.PostsFound}, torrents: discovered={total.TorrentsDiscovered}, saved={total.TorrentsSaved}, skipped={total.TorrentsSkippedCache}, failed={total.TorrentsFailed}, time={swAll.Elapsed.TotalSeconds:F1}s");

            return sb.ToString();
        }

        #region helpers

        async Task<int> resolveTotalPages(string cat, int limit_page)
        {
            if (limit_page > 0)
                return limit_page;

            string url = $"{AppInit.conf.Anidub.rqHost()}/{cat}/";
            string html = await HttpClient.Get(url, useproxy: AppInit.conf.Anidub.useproxy);
            if (html == null)
                return 1;

            var nums = Regex.Matches(html, $@"href=""[^""]*/{cat}/page/([0-9]+)/""", RegexOptions.IgnoreCase)
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
                ? $"{AppInit.conf.Anidub.rqHost()}/{cat}/"
                : $"{AppInit.conf.Anidub.rqHost()}/{cat}/page/{page}/";

            string html = await HttpClient.Get(pageUrl, useproxy: AppInit.conf.Anidub.useproxy);
            if (html == null)
                return (false, 0, 0);

            // ссылки на релизы
            var postUrls = Regex.Matches(html, @"href=""(https?://tr\.anidub\.(?:com|co)/[^""]+?\.html)""", RegexOptions.IgnoreCase)
                                .Cast<Match>()
                                .Select(m => m.Groups[1].Value)
                                .Where(u => !Regex.IsMatch(u, @"(/engine/|/page/)", RegexOptions.IgnoreCase))
                                .Distinct()
                                .ToList();

            int postsCount = postUrls.Count;
            st.PostsFound += postsCount;

            var torrents = new List<TorrentDetails>();
            int torrentsOnPage = 0;

            foreach (string postUrl in postUrls)
            {
                try
                {
                    string dhtml = await HttpClient.Get(postUrl, useproxy: AppInit.conf.Anidub.useproxy);
                    if (dhtml == null)
                        continue;

                    string rawTitle = HttpUtility.HtmlDecode(Regex.Match(dhtml, "<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value).Trim();
                    if (rawTitle.Contains("»"))
                        rawTitle = rawTitle.Split('»')[0].Trim();

                    string nameRu = rawTitle;
                    string nameEn = null;

                    var parts = rawTitle.Split(new[] { " / " }, StringSplitOptions.None);
                    if (parts.Length >= 2)
                    {
                        nameRu = parts[0].Trim();
                        nameEn = string.Join(" / ", parts.Skip(1)).Trim();
                    }

                    nameRu = tParse.ReplaceBadNames(nameRu);
                    if (!string.IsNullOrWhiteSpace(nameEn))
                        nameEn = tParse.ReplaceBadNames(nameEn);

                    int relased = 0;
                    var mYear = Regex.Match(dhtml, @"Год[^0-9]*([12][0-9]{3})", RegexOptions.IgnoreCase);
                    if (mYear.Success)
                        int.TryParse(mYear.Groups[1].Value, out relased);

                    DateTime createTime = DateTime.Today;
                    var mDMYdash = Regex.Match(dhtml, @"([0-9]{2}-[0-9]{2}-[0-9]{4})");
                    if (mDMYdash.Success && DateTime.TryParseExact(mDMYdash.Groups[1].Value, "dd-MM-yyyy", null, System.Globalization.DateTimeStyles.None, out DateTime d))
                        createTime = d;
                    else
                    {
                        var mRuMonth = Regex.Match(dhtml, @"([0-9]{1,2}\s+[А-ЯЁа-яё]+?\s+[0-9]{4})");
                        if (mRuMonth.Success)
                            createTime = tParse.ParseCreateTime(mRuMonth.Groups[1].Value, "dd.MM.yyyy");
                    }

                    int sid = 0, pir = 0;
                    int.TryParse(Regex.Match(dhtml, @"Раздают[^0-9]*([0-9]+)\s*<", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value, out sid);
                    int.TryParse(Regex.Match(dhtml, @"Качают[^0-9]*([0-9]+)\s*<", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value, out pir);

                    var ids = Regex.Matches(dhtml, @"engine/download\.php\?id=([0-9]+)", RegexOptions.IgnoreCase)
                                   .Cast<Match>()
                                   .Select(m => m.Groups[1].Value)
                                   .Distinct()
                                   .ToList();

                    if (ids.Count == 0)
                    {
                        // предупреждение по конкретному посту без торрентов
                        sb.AppendLine($"    [warn] no torrents on post: {postUrl}");
                        continue;
                    }

                    foreach (string id in ids)
                    {
                        string q = null;
                        var around = takeAround(dhtml, $"engine/download.php?id={id}", 300);
                        var mq = Regex.Match(around, @"\b([0-9]{3,4}p)\b", RegexOptions.IgnoreCase);
                        if (mq.Success) q = mq.Groups[1].Value;

                        string qDigits = (q ?? "0").ToLower().Replace("p", "");
                        string url = $"{postUrl}?q={qDigits}&id={id}";

                        string title = (!string.IsNullOrWhiteSpace(nameEn) ? $"{nameRu} / {nameEn}" : nameRu) + (relased > 0 ? $" {relased}" : "");
                        if (!string.IsNullOrWhiteSpace(q))
                            title += $" [{q}]";

                        torrents.Add(new TorrentDetails
                        {
                            trackerName = "anidub",
                            types = cat == "dorama" ? new[] { "serial" } : new[] { "anime" },
                            url = url,
                            title = title,
                            sid = sid,
                            pir = pir,
                            createTime = createTime,
                            name = nameRu,
                            originalname = nameEn,
                            relased = relased
                        });

                        torrentsOnPage++;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"    [post-error] {postUrl} :: {ex.GetType().Name}: {ex.Message}");
                }
            }

            st.TorrentsDiscovered += torrentsOnPage;

            // метрики внутри колбэка
            await FileDB.AddOrUpdate(torrents, async (t, db) =>
            {
                try
                {
                    if (db.TryGetValue(t.url, out TorrentDetails cached) && cached.title == t.title)
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

                    byte[] tor = await HttpClient.Download($"{AppInit.conf.Anidub.rqHost()}/engine/download.php?id={id}", referer: AppInit.conf.Anidub.rqHost(), useproxy: AppInit.conf.Anidub.useproxy);
                    if (tor == null || tor.Length == 0)
                    {
                        st.TorrentsFailed++;
                        return false;
                    }

                    string magnet = BencodeTo.Magnet(tor);
                    if (magnet == null)
                    {
                        st.TorrentsFailed++;
                        return false;
                    }

                    t.magnet = magnet;
                    t.sizeName = BencodeTo.SizeName(tor);
                    st.TorrentsSaved++;
                    return true;
                }
                catch
                {
                    st.TorrentsFailed++;
                    return false;
                }
            });

            return (true, postsCount, torrentsOnPage);
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

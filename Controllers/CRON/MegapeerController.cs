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

namespace JacRed.Controllers.CRON
{
    [Route("/cron/megapeer/[action]")]
    public class MegapeerController : BaseController
    {
        static Dictionary<string, List<TaskParse>> taskParse = new Dictionary<string, List<TaskParse>>();

        // Человеческие названия категорий
        static readonly Dictionary<string, string> CatNames = new Dictionary<string, string>
        {
            {"80", "Зарубежные фильмы"},
            {"79", "Наши фильмы"},
            {"5",  "Наши сериалы"},
            {"6",  "Зарубежные сериалы"},
            {"55", "Документалистика"},
            {"57", "Телепередачи"},
            {"76", "Мультипликация"},
        };

        static string CatTitle(string code)
            => CatNames.TryGetValue(code, out var name) ? name : $"Категория {code}";

        static MegapeerController()
        {
            try
            {
                if (IO.File.Exists("Data/temp/megapeer_taskParse.json"))
                {
                    var raw = IO.File.ReadAllText("Data/temp/megapeer_taskParse.json", Encoding.UTF8);
                    taskParse = JsonConvert.DeserializeObject<Dictionary<string, List<TaskParse>>>(raw) ?? new();
                }
            }
            catch { taskParse = new(); }
        }

        #region Parse
        static bool _workParse = false;

        /// <summary>
        /// /cron/megapeer/parse?page=0
        /// Лог: ok; found=N; processed=M + многострочная разбивка по категориям
        /// </summary>
        async public Task<string> Parse(int page)
        {
            if (_workParse)
                return "work";

            _workParse = true;

            int totalFound = 0;
            int totalProcessed = 0;
            var cats = new List<string>() { "80", "79", "6", "5", "55", "57", "76" };
            var perCat = new Dictionary<string, (int found, int processed)>();

            try
            {
                foreach (string cat in cats)
                {
                    var (ok, found, processed) = await parsePage(cat, page);
                    totalFound += found;
                    totalProcessed += processed;

                    if (!perCat.ContainsKey(cat)) perCat[cat] = (0, 0);
                    perCat[cat] = (perCat[cat].found + found, perCat[cat].processed + processed);

                    await Task.Delay(AppInit.conf.Megapeer.parseDelay);
                }
            }
            catch (Exception ex)
            {
                _workParse = false;
                return $"error: {ex.Message}";
            }

            _workParse = false;
            return BuildLog(totalFound, totalProcessed, perCat, cats);
        }
        #endregion

        #region UpdateTasksParse
        /// <summary>
        /// /cron/megapeer/updatetasksparse?limit_page=5
        /// limit_page <= 0 — все доступные страницы
        /// </summary>
        async public Task<string> UpdateTasksParse()
        {
            int limitPage = 0;
            int.TryParse(Request?.Query["limit_page"], out limitPage);

            foreach (string cat in new List<string>() { "80", "79", "6", "5", "55", "57", "76" })
            {
                string url0 = $"{AppInit.conf.Megapeer.rqHost()}/browse.php?cat={cat}&page=0";

                string html = await HttpClient.Get(
                    url0,
                    encoding: Encoding.GetEncoding(1251),
                    useproxy: AppInit.conf.Megapeer.useproxy,
                    addHeaders: new List<(string name, string val)>()
                    {
                        ("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"),
                        ("referer", url0),
                        ("pragma", "no-cache"),
                        ("cache-control", "no-cache"),
                        ("sec-fetch-mode", "navigate"),
                        ("sec-fetch-site", "same-origin"),
                        ("upgrade-insecure-requests", "1")
                    });

                if (string.IsNullOrWhiteSpace(html))
                    continue;

                int maxPageIdx = 0;
                foreach (Match m in Regex.Matches(html, @"[?&]page=(\d+)", RegexOptions.IgnoreCase))
                    if (int.TryParse(m.Groups[1].Value, out int n))
                        maxPageIdx = Math.Max(maxPageIdx, n);

                int pagesCount = maxPageIdx + 1; // 0..N
                int pagesToTake = (limitPage > 0) ? Math.Min(limitPage, pagesCount) : pagesCount;

                var list = new List<TaskParse>(pagesToTake);
                for (int i = 0; i < pagesToTake; i++)
                    list.Add(new TaskParse(i) { updateTime = DateTime.MinValue });

                taskParse[cat] = list;
            }

            try
            {
                IO.Directory.CreateDirectory("Data/temp");
                IO.File.WriteAllText("Data/temp/megapeer_taskParse.json", JsonConvert.SerializeObject(taskParse));
            }
            catch { }

            return "ok";
        }
        #endregion

        #region ParseAllTask
        static bool _parseAllTaskWork = false;

        /// <summary>
        /// /cron/megapeer/parsealltask
        /// Лог: ok; found=N; processed=M + многострочная разбивка по категориям
        /// </summary>
        async public Task<string> ParseAllTask()
        {
            if (_parseAllTaskWork)
                return "work";

            _parseAllTaskWork = true;

            int totalFound = 0;
            int totalProcessed = 0;
            var cats = new List<string>() { "80", "79", "6", "5", "55", "57", "76" };
            var perCat = new Dictionary<string, (int found, int processed)>();

            try
            {
                foreach (var task in taskParse.ToArray())
                {
                    foreach (var val in task.Value.ToArray())
                    {
                        if (DateTime.Today == val.updateTime)
                            continue;

                        await Task.Delay(AppInit.conf.Megapeer.parseDelay);

                        var (ok, found, processed) = await parsePage(task.Key, val.page);
                        totalFound += found;
                        totalProcessed += processed;

                        if (!perCat.ContainsKey(task.Key)) perCat[task.Key] = (0, 0);
                        perCat[task.Key] = (perCat[task.Key].found + found, perCat[task.Key].processed + processed);

                        if (ok)
                            val.updateTime = DateTime.Today;
                    }
                }

                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    IO.File.WriteAllText("Data/temp/megapeer_taskParse.json", JsonConvert.SerializeObject(taskParse));
                }
                catch { }
            }
            catch (Exception ex)
            {
                _parseAllTaskWork = false;
                return $"error: {ex.Message}";
            }

            _parseAllTaskWork = false;
            return BuildLog(totalFound, totalProcessed, perCat, cats);
        }
        #endregion

        #region parsePage (internal)
        async Task<(bool ok, int found, int processed)> parsePage(string cat, int page)
        {
            string url = $"{AppInit.conf.Megapeer.rqHost()}/browse.php?cat={cat}&page={page}";

            string html = await HttpClient.Get(
                url,
                encoding: Encoding.GetEncoding(1251),
                useproxy: AppInit.conf.Megapeer.useproxy,
                addHeaders: new List<(string name, string val)>()
                {
                    ("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"),
                    ("referer", url),
                    ("pragma", "no-cache"),
                    ("cache-control", "no-cache"),
                    ("sec-fetch-mode", "navigate"),
                    ("sec-fetch-site", "same-origin"),
                    ("upgrade-insecure-requests", "1")
                });

            if (string.IsNullOrWhiteSpace(html))
                return (false, 0, 0);

            // основной шаблон строк списка
            var rows = Regex.Matches(html, @"<tr class=""table_fon""[\s\S]*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // fallback: любые tr
            if (rows.Count == 0)
                rows = Regex.Matches(html, @"<tr[\s\S]*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var torrents = new List<MegapeerDetails>();

            foreach (Match row in rows)
            {
                string r = row.Value;

                // title + href (возможны перестановки class="url")
                var a = Regex.Match(r, @"<a[^>]*class=""url""[^>]*href=""([^""]+)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
                if (!a.Success)
                    a = Regex.Match(r, @"<a[^>]*href=""([^""]+)""[^>]*class=""[^""]*url[^""]*""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
                if (!a.Success)
                    continue;

                string href = a.Groups[1].Value;
                string title = HtmlDecode(StripTags(a.Groups[2].Value)).Trim();
                string name = title;              // для поиска
                string originalname = null;       // будет обновлён после запроса карточки (если найдём)

                string urlDetails = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? href
                    : $"{AppInit.conf.Megapeer.rqHost()}{(href.StartsWith("/") ? href : "/" + href)}";

                // downloadid (относительный/абсолютный)
                string downloadid = null;
                var dl = Regex.Match(r, @"href=""(?:https?://[^""]+)?/download/(\d+)""", RegexOptions.IgnoreCase);
                if (dl.Success)
                    downloadid = dl.Groups[1].Value;

                // sizeName
                string sizeName = null;
                var msize = Regex.Match(r, @"(\d+[.,]?\d*\s*(?:MB|GB|GiB|MiB))", RegexOptions.IgnoreCase);
                if (msize.Success) sizeName = msize.Groups[1].Value;

                // sid/pir
                int sid = 0, pir = 0;
                var fonts = Regex.Matches(r, @"<font[^>]*>(\d+)</font>", RegexOptions.IgnoreCase);
                if (fonts.Count >= 2)
                {
                    int.TryParse(fonts[0].Groups[1].Value, out sid);
                    int.TryParse(fonts[1].Groups[1].Value, out pir);
                }

                // relased (int)
                int relased = 0;
                var y = Regex.Match(title, @"\(([1-2][0-9]{3})\)");
                if (y.Success) int.TryParse(y.Groups[1].Value, out relased);

                // createTime из колонки "Добавлен" (в листинге)
                DateTime? createTimeMaybe = null;
                var addCol = Regex.Match(r, @"<td>\s*([\d]{1,2}\s*[А-Яа-яA-Za-z]{3}\s*\d{2})\s*</td>");
                if (addCol.Success)
                    createTimeMaybe = ParseAddedDate(HtmlDecode(addCol.Groups[1].Value));
                DateTime createTimeVal = createTimeMaybe ?? DateTime.Now;

                // #region types
                string[] types = Array.Empty<string>();
                switch (cat)
                {
                    case "80":
                    case "79":
                        types = new[] { "movie" };
                        break;
                    case "6":
                    case "5":
                        types = new[] { "serial" };
                        break;
                    case "55":
                        types = new[] { "docuserial", "documovie" };
                        break;
                    case "57":
                        types = new[] { "tvshow" };
                        break;
                    case "76":
                        types = new[] { "multfilm", "multserial" };
                        break;
                }
                // #endregion

                torrents.Add(new MegapeerDetails()
                {
                    trackerName = "megapeer",
                    types = types,
                    url = urlDetails,
                    title = title,
                    sid = sid,
                    pir = pir,
                    sizeName = sizeName,
                    createTime = createTimeVal,     // DateTime (не nullable)
                    name = name,
                    originalname = originalname,    // обновим после карточки
                    relased = relased,
                    downloadId = downloadid
                });
            }

            int found = torrents.Count;
            int processed = 0;

            await FileDB.AddOrUpdate<MegapeerDetails>(torrents, async (t, db) =>
            {
                // тянем magnet и originalname с карточки (с ретраями)
                var info = await GetDetailsInfoWithRetry(t.url, referer: AppInit.conf.Megapeer.rqHost(), attempts: 3);
                if (!string.IsNullOrWhiteSpace(info.magnet))
                {
                    t.magnet = info.magnet;
                    if (!string.IsNullOrWhiteSpace(info.originalname))
                        t.originalname = info.originalname;

                    processed++;
                    return true;
                }
                return false;
            });

            return (found > 0, found, processed);
        }
        #endregion

        #region Helpers
        static string HtmlDecode(string s) => string.IsNullOrEmpty(s) ? s : HttpUtility.HtmlDecode(s);
        static string StripTags(string s) => string.IsNullOrEmpty(s) ? s : Regex.Replace(s, "<.*?>", string.Empty);

        static DateTime? ParseAddedDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            var ru = new CultureInfo("ru-RU");
            string[] fmts = { "dd MMM yy", "d MMM yy", "dd-MMM-yy", "d-MMM-yy" };
            if (DateTime.TryParseExact(s, fmts, ru, DateTimeStyles.None, out var dt))
                return dt;
            if (DateTime.TryParse(s, ru, DateTimeStyles.None, out dt))
                return dt;
            return null;
        }

        // Формирование многострочного лога с человеческими именами категорий
        static string BuildLog(
            int totalFound,
            int totalProcessed,
            Dictionary<string, (int found, int processed)> perCat,
            List<string> order)
        {
            var sb = new StringBuilder();
            sb.Append("ok; found=").Append(totalFound).Append("; processed=").Append(totalProcessed).AppendLine();
            sb.AppendLine("by_cat:");
            foreach (var cat in order)
            {
                perCat.TryGetValue(cat, out var v);
                sb.Append("- ")
                  .Append(CatTitle(cat)).Append(" (").Append(cat).Append("): ")
                  .Append(v.found).Append("/").Append(v.processed)
                  .AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Получить magnet и originalname со страницы карточки (с ретраями)
        /// </summary>
        async Task<(string magnet, string originalname)> GetDetailsInfoWithRetry(string detailsUrl, string referer, int attempts = 3)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    string html = await HttpClient.Get(detailsUrl,
                        encoding: Encoding.GetEncoding(1251),
                        useproxy: AppInit.conf.Megapeer.useproxy,
                        addHeaders: new List<(string name, string val)>()
                        {
                            ("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"),
                            ("referer", referer),
                            ("pragma", "no-cache"),
                            ("cache-control", "no-cache"),
                            ("sec-fetch-mode", "navigate"),
                            ("sec-fetch-site", "same-origin"),
                            ("upgrade-insecure-requests", "1")
                        });

                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        // magnet
                        var mm = Regex.Match(html, @"href\s*=\s*""(magnet:\?xt=urn:btih:[^""]+)""", RegexOptions.IgnoreCase);
                        string magnet = mm.Success ? HtmlDecode(mm.Groups[1].Value) : null;

                        // originalname — несколько вариантов вёрстки
                        string original = null;

                        // Вариант 1: <b>Оригинальное название:</b> The Title
                        var mo1 = Regex.Match(html, @"Оригинальное\s*название\s*:\s*</?(?:b|strong)[^>]*>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
                        if (mo1.Success) original = HtmlDecode(mo1.Groups[1].Value).Trim();

                        // Вариант 2: <td>Оригинальное название:</td><td> ... </td>
                        if (string.IsNullOrWhiteSpace(original))
                        {
                            var mo2 = Regex.Match(html, @"Оригинальное\s*название\s*:\s*</td>\s*<td[^>]*>\s*([^<\r\n]+)\s*<", RegexOptions.IgnoreCase);
                            if (mo2.Success) original = HtmlDecode(mo2.Groups[1].Value).Trim();
                        }

                        // Вариант 3: label + текст без тэгов на той же строке
                        if (string.IsNullOrWhiteSpace(original))
                        {
                            var mo3 = Regex.Match(html, @"Оригинальное\s*название\s*[:\-]\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
                            if (mo3.Success) original = HtmlDecode(mo3.Groups[1].Value).Trim();
                        }

                        return (magnet, original);
                    }
                }
                catch { }

                await Task.Delay(1000);
            }

            return (null, null);
        }
        #endregion
    }
}

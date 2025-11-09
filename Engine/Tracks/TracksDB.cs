using JacRed.Engine.CORE;
using JacRed.Models.Details;
using JacRed.Models.Tracks;
using MonoTorrent;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace JacRed.Engine
{
    public static class TracksDB
    {
        public static void Configuration()
        {
            Console.WriteLine("TracksDB load");

            foreach (var folder1 in Directory.GetDirectories("Data/tracks"))
            {
                foreach (var folder2 in Directory.GetDirectories(folder1))
                {
                    foreach (var file in Directory.GetFiles(folder2))
                    {
                        string infohash = folder1.Substring(12) + folder2.Substring(folder1.Length + 1) + Path.GetFileName(file);

                        try
                        {
                            var res = JsonConvert.DeserializeObject<ffprobemodel>(File.ReadAllText(file));
                            if (res?.streams != null && res.streams.Count > 0)
                                Database.TryAdd(infohash, res);
                        }
                        catch { }
                    }
                }
            }
        }

        static Random random = new Random();

        static ConcurrentDictionary<string, ffprobemodel> Database = new ConcurrentDictionary<string, ffprobemodel>();

        static string pathDb(string infohash, bool createfolder = false)
        {
            string folder = $"Data/tracks/{infohash.Substring(0, 2)}/{infohash[2]}";

            if (createfolder)
                Directory.CreateDirectory(folder);

            return $"{folder}/{infohash.Substring(3)}";
        }

        public static bool theBad(string[] types)
        {
            if (types == null || types.Length == 0)
                return true;

            if (types.Contains("sport") || types.Contains("tvshow") || types.Contains("docuserial"))
                return true;

            return false;
        }

        public static List<ffStream> Get(string magnet, string[] types = null, bool onlydb = false)
        {
            if (types != null && theBad(types))
                return null;

            string infohash = MagnetLink.Parse(magnet).InfoHashes.V1OrV2.ToHex();
            if (Database.TryGetValue(infohash, out ffprobemodel res))
                return res.streams;

            string path = pathDb(infohash);
            if (!File.Exists(path))
                return null;

            try
            {
                res = JsonConvert.DeserializeObject<ffprobemodel>(File.ReadAllText(path));
                if (res?.streams == null || res.streams.Count == 0)
                    return null;
            }
            catch { return null; }

            Database.AddOrUpdate(infohash, res, (k, v) => res);
            return res.streams;
        }


        async public static Task Add(string magnet, string[] types = null)
        {
            if (types != null && theBad(types))
                return;

            if (AppInit.conf.tsuri == null || AppInit.conf.tsuri.Length == 0)
                return;

            string infohash = MagnetLink.Parse(magnet).InfoHashes.V1OrV2.ToHex();
            if (string.IsNullOrEmpty(infohash))
                return;

            ffprobemodel res = null;
            string tsuri = AppInit.conf.tsuri[random.Next(0, AppInit.conf.tsuri.Length)];

            #region ffprobe
            try
            {
                var timeOut = TimeSpan.FromMinutes(3);
                var cancellationTokenSource = new CancellationTokenSource(timeOut);
                var token = cancellationTokenSource.Token;

                string media = $"{tsuri}/stream/file?link={HttpUtility.UrlEncode(magnet)}&index=1&play";

                using (var process = new System.Diagnostics.Process())
                {
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    process.StartInfo.FileName = "ffprobe";
                    process.StartInfo.Arguments = $"-v quiet -print_format json -show_format -show_streams \"{media}\"";
                    process.Start();

                    await process.WaitForExitAsync(token);

                    string outPut = await process.StandardOutput.ReadToEndAsync();
                    res = JsonConvert.DeserializeObject<ffprobemodel>(outPut);
                }
            }
            catch { }

            await HttpClient.Post($"{tsuri}/torrents", "{\"action\":\"rem\",\"hash\":\"" + infohash + "\"}");

            if (res?.streams == null || res.streams.Count == 0)
                return;
            #endregion

            Database.AddOrUpdate(infohash, res, (k, v) => res);

            try
            {
                string path = pathDb(infohash, createfolder: true);
                await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(res, Formatting.Indented));
            }
            catch { }
        }


        public static HashSet<string> Languages(TorrentDetails t, List<ffStream> streams)
        {
            try
            {
                var languages = new HashSet<string>();

                if (t.languages != null)
                {
                    foreach (var l in t.languages)
                        languages.Add(l);
                }

                if (streams != null)
                {
                    foreach (var item in streams)
                    {
                        if (!string.IsNullOrEmpty(item.tags?.language) && item.codec_type == "audio")
                            languages.Add(item.tags.language);
                    }
                }

                if (languages.Count == 0)
                    return null;

                return languages;
            }
            catch { return null; }
        }
    }
}

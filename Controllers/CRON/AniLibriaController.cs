using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JacRed.Engine.CORE;
using Microsoft.AspNetCore.Mvc;
using JacRed.Engine;
using JacRed.Models.Details;
using JacRed.Models.AniLibV1;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace JacRed.Controllers.CRON
{
	[Route("/cron/anilibria/[action]")]
	public class AniLibriaController : BaseController
	{
		static bool workParse = false;

		async public Task<string> Parse(int limit_page)
		{
			if (workParse)
				return "work";

			workParse = true;

			int limit = 70;
			if (limit_page > 0)
			{
				limit = limit_page;
			}

			try
			{
				int page = 1;
				int perPage = 40;
				int fetched = 0;

				while (fetched < limit)
				{
					string urlID = $"{AppInit.conf.Anilibria.rqHost()}/api/v1/anime/torrents?page={page}&limit={perPage}";
					var resp = await HttpClient.Get<TorrentsResponse>(urlID, IgnoreDeserializeObject: true, useproxy: AppInit.conf.Anilibria.useproxy);
					
					int limit2 = resp.meta.pagination.total_pages;
					if (limit2 == 0)
						break;
					if (limit_page == 0)
						if (limit != limit2)
							limit = limit2;
					
					if (resp == null || resp.data == null )
						break;

					var torrents = new List<TorrentBaseDetails>();
					foreach (var item in resp.data)
					{
						if (item.release == null || string.IsNullOrWhiteSpace(item.magnet))
							continue;

						string nameRu = item.release.name?.main;
						string nameEn = item.release.name?.english;
						int year = item.release.year;
						string quality = item.quality?.description;
						string codec = item.codec?.label;

						if (string.IsNullOrWhiteSpace(nameRu) || string.IsNullOrWhiteSpace(nameEn))
							continue;

						// Build unique url id similar to previous scheme, but v1 based
						string url = $"{AppInit.conf.Anilibria.rqHost()}/anime/releases/release/{item.release.alias}/torrents?{item.release.id}&{(quality ?? "")}&{(codec ?? "")}";

						DateTime createTime;

						if (item.updated_at > item.created_at)
						{
							createTime = item.updated_at;
						}
						else
						{
							createTime = item.created_at;
						}
						
						string title = $"{nameRu} / {nameEn} {year} [{quality}{(string.IsNullOrWhiteSpace(codec) ? "" : ", " + codec)}]";

						string sizeName = null;
						if (item.size > 0)
						{
							long bytes = item.size;
							string[] suffix = { "B", "KB", "MB", "GB", "TB" };
							int i = 0;
							double dbl = bytes;
							while (i < suffix.Length && bytes >= 1024)
							{
								dbl = bytes / 1024.0;
								bytes /= 1024;
								i++;
							}
							sizeName = string.Format("{0:N2} {1}", dbl, suffix[i]).Replace(",", ".");
						}

						torrents.Add(new TorrentBaseDetails()
						{
							trackerName = "anilibria",
							types = new string[] { "anime" },
							url = url,
							title = title,
							sid = item.seeders,
							pir = item.leechers,
							createTime = createTime,
							magnet = item.magnet,
							sizeName = sizeName,
							name = tParse.ReplaceBadNames(nameRu),
							originalname = tParse.ReplaceBadNames(nameEn),
							relased = year
						});
					}

					FileDB.AddOrUpdate(torrents);

					fetched ++;
					page++;
					await Task.Delay(200);
				}
			}
			catch { }

			workParse = false;
			return $"{limit} pages ok\n";
		}
	}
}
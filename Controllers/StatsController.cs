using Microsoft.AspNetCore.Mvc;

namespace JacRed.Controllers
{
    [Route("/stats/[action]")]
    public class StatsController : Controller
    {
        public ActionResult Torrents(string trackerName)
        {
            if (!AppInit.conf.openstats)
                return Content(string.Empty);

            if (string.IsNullOrWhiteSpace(trackerName))
            {
                return Content(System.IO.File.ReadAllText("Data/temp/stats.json"));
            }
            else
            {
                return Content(string.Empty);
                //var torrents = tParse.db.Values.Where(i => i.trackerName == trackerName);

                //return Json(new
                //{
                //    nullParse = torrents.Where(i => i.magnet == null).OrderByDescending(i => i.createTime).Select(i =>
                //    {
                //        return new
                //        {
                //            i.trackerName,
                //            i.types,
                //            i.url,

                //            i.title,
                //            i.sid,
                //            i.pir,
                //            i.size,
                //            i.sizeName,
                //            i.createTime,
                //            i.updateTime,
                //            i.magnet,

                //            i.name,
                //            i.originalname,
                //            i.relased
                //        };
                //    }),
                //    lastToday = torrents.Where(i => i.createTime >= DateTime.Today).OrderByDescending(i => i.createTime).Select(i =>
                //    {
                //        return new
                //        {
                //            i.trackerName,
                //            i.types,
                //            i.url,

                //            i.title,
                //            i.sid,
                //            i.pir,
                //            i.size,
                //            i.sizeName,
                //            i.createTime,
                //            i.updateTime,
                //            i.magnet,

                //            i.name,
                //            i.originalname,
                //            i.relased
                //        };
                //    }),
                //    lastCreateTime = torrents.OrderByDescending(i => i.createTime).Take(40).Select(i =>
                //    {
                //        return new
                //        {
                //            i.trackerName,
                //            i.types,
                //            i.url,

                //            i.title,
                //            i.sid,
                //            i.pir,
                //            i.size,
                //            i.sizeName,
                //            i.createTime,
                //            i.updateTime,
                //            i.magnet,

                //            i.name,
                //            i.originalname,
                //            i.relased
                //        };
                //    })
                //});
            }
        }
    }
}

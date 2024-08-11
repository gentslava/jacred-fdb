using System;
using System.Collections.Generic;

namespace JacRed.Models
{
    public class TorrentQuality
    {
        public HashSet<int> qualitys { get; set; } = new HashSet<int>();

        public HashSet<string> types { get; set; } = new HashSet<string>();

        public HashSet<string> languages { get; set; } = new HashSet<string>();


        public DateTime createTime { get; set; }

        public DateTime updateTime { get; set; }
    }
}

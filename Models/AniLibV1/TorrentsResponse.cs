using System;

namespace JacRed.Models.AniLibV1
{
	public class TorrentsResponse
	{
		public System.Collections.Generic.List<TorrentItem> data { get; set; }
		public Meta meta { get; set; }
	}

	public class Meta
	{
		public Pagination pagination { get; set; }
	}
	
	public class Pagination
	{
		public int total { get; set; }
		public int count { get; set; }
		public int per_page { get; set; }
		public int current_page { get; set; }
		public int total_pages { get; set; }
		public Links links { get; set; }
	}
	
	public class Links
	{
		public string next { get; set; }
	}
	
	public class TorrentItem
	{
		public int id { get; set; }
		public string hash { get; set; }
		public long size { get; set; }
		public string label { get; set; }
		public int seeders { get; set; }
		public int leechers { get; set; }
		public string magnet { get; set; }
		public TorrentQuality quality { get; set; }
		public TorrentCodec codec { get; set; }
		public DateTime created_at { get; set; }
		public DateTime updated_at { get; set; }
		public Release release { get; set; }
	}

	public class TorrentQuality
	{
		public string value { get; set; }
		public string description { get; set; }
	}

	public class TorrentCodec
	{
		public string value { get; set; }
		public string label { get; set; }
	}

	public class Release
	{
		public int id { get; set; }
		public int year { get; set; }
		public ReleaseName name { get; set; }
		public string alias { get; set; }
		public ReleaseSeason season { get; set; }
	}

	public class ReleaseName
	{
		public string main { get; set; }
		public string english { get; set; }
	}

	public class ReleaseSeason
	{
		public string value { get; set; }
		public string description { get; set; }
	}
}
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace YTDownloader
{
	public class PlaylistDTO
	{
		[JsonProperty("name", Required = Required.Always)]
		public string Name { get; internal set; }
		[JsonProperty("urls", Required = Required.Always)]
		public string[] VideoUrls { get; set; }
	}
}

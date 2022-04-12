using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using clipr;
using Newtonsoft.Json;

namespace YTDownloader
{
	public class Arguments
	{
		public static readonly string OutTypes = string.Join(", ", Enum.GetNames(typeof(OutputType)));
		[NamedArgument('u', "url", Description = "URL of the Youtube video.")]
		public string Url { get; set; } = null;
		[NamedArgument('p', "playlist", Description = "URL of the Youtube playlist.")]
		public string Playlist { get; set; } = null;
		[NamedArgument('f', "format", Description = "Desired output format. [audio, video, both]")] // how to do this programatically?
		public string Format { get; set; } = "audio";
		[NamedArgument('d', "development", Action = ParseAction.StoreFalse, Const = true, NumArgs = 1, Constraint = NumArgsConstraint.Optional, Description = "Download video over 1080p.")]
		public bool Progressive { get; set; } = true;
		[NamedArgument('m', "max-resolution", Action = ParseAction.Store, Const = 1080, NumArgs = 1, Constraint = NumArgsConstraint.Exactly, Description = "Maximal resolution to download video in.")]
		public int MaxResolution { get; set; } = 1080;
		[NamedArgument("max-quality", Action = ParseAction.StoreTrue, Description = "Download max quality audio (slower download)")]
		public bool MaxQuality { get; set; } = false;
		[NamedArgument("visualization", Action = ParseAction.StoreTrue, Description = "Produce video of audio visualization.")]
		public bool AddVisualization { get; set; } = false;
		public OutputType OutType { get; set; }
		public override string ToString()
		{
			return JsonConvert.SerializeObject(this);
		}
	}
	public enum OutputType
	{
		Audio,
		Video,
		Both
	}
}

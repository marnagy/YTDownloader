using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VideoLibrary;

namespace YTDownloader
{
	public static class YouTubeVideoExtensions
	{
		public static bool IsAudioOnly(this YouTubeVideo video)
			=> video.Format == VideoFormat.Unknown &&
				video.AudioFormat != AudioFormat.Unknown;
		public static bool IsVideoOnly(this YouTubeVideo video)
			=> video.Format == VideoFormat.Mp4
				&& video.AudioFormat == AudioFormat.Unknown;
	}
}

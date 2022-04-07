using System;
using VideoLibrary;
using clipr;
using System.Text;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
//using Konsole;
using Xabe.FFmpeg;
using System.Net.Http;
using VideoLibrary.Exceptions;
using TagLib;
using Newtonsoft.Json;
using Konsole;

namespace YTDownloader
{
	class Program
	{
		const string ApiUrl = "https://ytbes.herokuapp.com";
		const string TestEndpoint = "/test";
		const string PlaylistEndpoint = "/playlist";
		const string ThumbnailEndpoint = "/thumbnail";

		const string AppDirectoryName = "csharp_yt_downloader";
		const string FFmpegWindowsDirectoryName = "ffmpeg_win";
		const string FFmpegLinuxDirectoryName = "ffmpeg_linux";
		const string FFmpegMacDirectoryName = "ffmpeg_mac";

		static int MainProgressBarCounter = 0;
		static readonly string MainProgressBarDescription = "Downloading playlist";

		private static readonly HttpClient client = new HttpClient();
		static async Task Main(string[] args)
		{
			// unpack ffmpeg executables
			UnpackFFmpeg();

			Arguments parsedArgs = getArgs(args);

			//Console.WriteLine("Arguments loaded.");

			if ( ! await IsServerRunning() )
			{
				WriteError("Internet is down or server is not responding.\nPlease, try again later.");
				return;
			}

			//Console.WriteLine("Server is online.");

			var urls = new List<string>();
			if ( parsedArgs.Playlist is not null )
			{
				var playlistData = await GetUrlsFromPlaylist(parsedArgs.Playlist);
				urls.AddRange(playlistData.VideoUrls);
				var currDir = new DirectoryInfo(Directory.GetCurrentDirectory());
				var playlistDir = currDir.CreateSubdirectory($"playlist_{RemoveForbidden(playlistData.Name)}");
				Directory.SetCurrentDirectory(playlistDir.FullName);
				Console.WriteLine($"Downloading playlist {playlistData.Name}");
			}
			else if (parsedArgs.Url is not null)
			{
				urls.Add(parsedArgs.Url);
			}

			//Console.WriteLine("Starting downloads...");

			bool showPlaylistProgressBar = urls.Count > 1;
			ProgressBar progressBar = null;
			if ( showPlaylistProgressBar )
			{
				progressBar = new ProgressBar(urls.Count);
				progressBar.Refresh(MainProgressBarCounter, MainProgressBarDescription);
			}
			var handlers = new List<Task<string>>();
			foreach (var url in urls)
			{
				YouTubeVideo[] streams = new YouTubeVideo[0];
				try
				{
					streams = (await GetStreams(url)).ToArray();
				}
				catch ( UnavailableStreamException e)
				{
					WriteError($"Stream is unavailable for {url}.\n{e.Message}");
				}
				var handler = HandleDownload(streams, parsedArgs, url, progressBar);
				handlers.Add(handler);
			}

			Task.WaitAll(handlers.ToArray());

			var errorMsgs = new List<string>();
			foreach (var handler in handlers)
			{
				var errorMsg = await handler;
				if ( !string.IsNullOrEmpty(errorMsg) )
					errorMsgs.Add(errorMsg);
			}

			if (errorMsgs.Count > 0)
			{
				foreach (var msg in errorMsgs)
				{
					Console.Error.WriteLine(msg);
				}
			}

			Console.WriteLine("Download completed.");
		}

		private static async Task<string> HandleDownload(YouTubeVideo[] streams, Arguments parsedArgs, string videoUrl, ProgressBar progressBar)
		{
			string result = null;
			try
			{
				switch (parsedArgs.OutFormat)
				{
					case OutputType.Audio:
						await DownloadAudio(streams, videoUrl);
						break;
					case OutputType.Video:
						await DownloadVideo(streams, parsedArgs);
						break;
					case OutputType.Both:
						//var audioTask = DownloadAudio(streams, videoUrl);
						await DownloadVideo(streams, parsedArgs);
						Console.WriteLine("Extracting audio from video");
						await ExtractAudio(streams, parsedArgs);
						break;
					default:
						WriteError($"Unsupported output type: {parsedArgs.OutFormat}");
						break;
				}
			}
			catch (Exception e)
			{
				result = $"Youtube url: {streams[0].Info.Title}, message: {e.Message}";
			}

			if ( progressBar is not null )
				progressBar.Refresh(++MainProgressBarCounter, MainProgressBarDescription);

			return result;
		}

		private static async Task ExtractAudio(YouTubeVideo[] streams, Arguments parsedArgs)
		{
			var videoOutputFileName = GetVideoOutputFileName(streams);
			var audioOutputFileName = Path.ChangeExtension(GetAudioOutputFileName(streams), "mp3");

			Console.WriteLine($"Extracting audio from video...");

			var conversion = await FFmpeg.Conversions.FromSnippet.ExtractAudio(videoOutputFileName, audioOutputFileName);
			await conversion.Start();
		}

		private async static Task<IEnumerable<YouTubeVideo>> GetStreams(string url)
		{
			IEnumerable<YouTubeVideo> streams;
			try
			{
				streams = await YouTube.Default.GetAllVideosAsync(url);
				//streams = YouTube.Default.GetAllVideos(url).ToArray();
				return streams;
			}
			catch (TimeoutException)
			{
				WriteError("Your internet connection timed out. Please, try again.");
				return Array.Empty<YouTubeVideo>();
			}
			catch (UnavailableStreamException)
			{
				WriteError("This video is not publicly accessible.");
				return Array.Empty<YouTubeVideo>();
			}
		}

		private static async Task<bool> IsServerRunning()
		{
			try
			{
				string url = $"{ApiUrl}{TestEndpoint}";
				var response = await client.GetAsync(url);
				return response.IsSuccessStatusCode;
			}
			catch
			{
				return false;
			}
		}

		private static async Task<PlaylistDTO> GetUrlsFromPlaylist(string playlistUrl)
		{
			string url = $"{ApiUrl}{PlaylistEndpoint}?url={playlistUrl}";
			string responseText = await client.GetStringAsync(url);
			var playlistData = JsonConvert.DeserializeObject<PlaylistDTO>(responseText);
			return playlistData;
		}

		private static void UnpackFFmpeg()
		{
			const string ffmpeg_win_file_name = "ffmpeg.exe";
			const string ffprobe_win_file_name = "ffprobe.exe";

			// binary files
			const string ffmpeg_mac_file_name = "ffmpeg"; 
			const string ffprobe_mac_file_name = "ffprobe";

			//const string ffmpeg_linux_file_name = "ffmpeg.exe";
			//const string ffprobe_linux_file_name = "ffprobe.exe";

			var homeDir = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
			//if ( homeDir.GetDirectories().Select(dirInfo => dirInfo.Name).Contains(AppDirectoryName) )
			//	return;
			var appDir = homeDir.CreateSubdirectory(AppDirectoryName);

			// How can I change this to a switch?
			if ( OperatingSystem.IsWindows() )
			{
				//Console.WriteLine($"Unpacking ffmpeg files to application directory {homeDir}");
				var ffmpegDir = appDir.CreateSubdirectory(FFmpegWindowsDirectoryName);
				if ( !(ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffmpeg_win_file_name) &&
					ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffprobe_win_file_name) ))
				{
					Console.WriteLine($"Unpacking ffmpeg files to application directory {ffmpegDir}");
					System.IO.File.WriteAllBytes(Path.Combine(ffmpegDir.FullName, ffmpeg_win_file_name), Properties.Resources.ffmpeg_win);
					System.IO.File.WriteAllBytes(Path.Combine(ffmpegDir.FullName, ffprobe_win_file_name), Properties.Resources.ffprobe_win);

					FFmpeg.SetExecutablesPath(ffmpegDir.FullName);
				}
			}
			// TODO: different builds for different Distros: Ubuntu, Fedora, Arch,...
			if ( OperatingSystem.IsLinux() )
			{
				WriteError("Essential feature missing for this platform: ffmpeg");
			}
			if ( OperatingSystem.IsMacOS() )
			{
				//WriteError("Essential feature missing for this platform: ffmpeg");

				Console.WriteLine("This part has not been properly tested due to development taking place on Windows.");
				Console.WriteLine("Proceed anyway? [y/n]");
				ConsoleKeyInfo resp = Console.ReadKey(intercept: false);
				if ( resp.KeyChar != 'y' )
					WriteError("You have chosen to NOT proceed.");

				var ffmpegDir = appDir.CreateSubdirectory(FFmpegMacDirectoryName);
				if ( !(ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffmpeg_mac_file_name) &&
					ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffprobe_mac_file_name) ))
				{
					Console.WriteLine($"Unpacking ffmpeg files to application directory {ffmpegDir}");

					System.IO.File.WriteAllBytes(Path.Combine(ffmpegDir.FullName, ffmpeg_mac_file_name), Properties.Resources.ffmpeg_mac);
					System.IO.File.WriteAllBytes(Path.Combine(ffmpegDir.FullName, ffprobe_mac_file_name), Properties.Resources.ffprobe_mac);

					FFmpeg.SetExecutablesPath(ffmpegDir.FullName);
				}
			}
		}

		private static string GetVideoOutputFileName(YouTubeVideo[] streams)
			=> GetVideoOutputFileName(streams.Where(s => s.IsVideoOnly()).First());

		private static string GetVideoOutputFileName(YouTubeVideo youtubeStream)
			=> Path.Combine( Directory.GetCurrentDirectory(), GetFileName(youtubeStream, OutputType.Video) );

		private static string GetAudioOutputFileName(YouTubeVideo[] streams)
			=> GetAudioOutputFileName(streams.Where(s => s.IsAudioOnly()).First());

		private static string GetAudioOutputFileName(YouTubeVideo youtubeStream)
			=> Path.Combine( Directory.GetCurrentDirectory(), GetFileName(youtubeStream, OutputType.Audio) );

		private static async Task DownloadVideo(YouTubeVideo[] streams, Arguments parsedArgs)
		{
			const int FullHD = 1080;
			if ( parsedArgs.Progressive || parsedArgs.MaxResolution < FullHD )
			{
				await DownloadProgressiveVideo(streams);
				return;
			}

			string SubDirName() => $"temp_{Environment.ProcessId}";

			// create temp directory in current directory and move into it
			var currDir = new DirectoryInfo(Directory.GetCurrentDirectory());
			var subDir = currDir.CreateSubdirectory(SubDirName());
			Console.WriteLine($"SubDirectory name: {SubDirName()}");
			try{
				//Directory.SetCurrentDirectory(subDir.FullName);
				// download audio
				var audioStream = streams.Where(s => s.IsAudioOnly())
					.OrderByDescending(s => s.AudioBitrate)
					.First();
				var audioFileName = Path.Combine(".", subDir.Name, $"audio.{audioStream.AudioFormat.ToString().ToLower()}");
				//Console.WriteLine($"Downloading audio with bitrate {audioStream.AudioBitrate} ...");
				var audioDownloading = Download(audioStream, audioFileName);
				// download video
				var videoStream = streams.Where(s => s.IsVideoOnly() && s.Resolution <= parsedArgs.MaxResolution)
					.OrderByDescending(s => s.Resolution)
					.First();
				var videoFileName = Path.Combine(".", subDir.Name, $"video.{videoStream.Format.ToString().ToLower()}");
				//Console.WriteLine($"Downloading video with resolution {videoStream.Resolution} ...");
				var videoDownloading = Download(videoStream, videoFileName);
				
				await audioDownloading;
				await videoDownloading;

				// merge using FFmpeg
				string outputPath = GetVideoOutputFileName(videoStream);
				IMediaInfo videoMediaInfo = await FFmpeg.GetMediaInfo(videoFileName);
				IStream videoMediaStream = videoMediaInfo.VideoStreams.FirstOrDefault()
					?.SetCodec(VideoCodec.h264);

				IMediaInfo audioMediaInfo = await FFmpeg.GetMediaInfo(audioFileName);
				IStream audioMediaStream = audioMediaInfo.AudioStreams.FirstOrDefault()
					?.SetCodec(AudioCodec.mp3);

				//var cut_video_title = videoStream.Info.Title.Length > 20 ? videoStream.Info.Title.Substring(0, 20) : videoStream.Info.Title;
				var conversion_desc = $"Converting to MP4";
				var progressBar = new ProgressBarSlim((int)videoMediaInfo.Size);
				progressBar.Refresh(0, conversion_desc);
				var conversion = FFmpeg.Conversions.New()
					.AddStream(
						videoMediaStream,
						audioMediaStream
					)
					.SetOutputFormat(Format.mp4)
					.SetOutput( outputPath );
				var conversionResult = await conversion.Start();

				//var conversion = await FFmpeg.Conversions.FromSnippet.AddAudio(videoFileName, audioFileName, outputPath);
				//IConversionResult result = await conversion
				//	.Start();
			}
			catch
			{
				
			}
			finally
			{
				subDir.Delete(recursive: true);
			}
		}

		private static async Task DownloadProgressiveVideo(YouTubeVideo[] streams)
		{
			var bestProgressive = streams.Where(s => IsProgressive(s))
					.OrderByDescending(s => s.Resolution)
					.First();
			var fileName = GetFileName(bestProgressive, OutputType.Video);
			await Download(bestProgressive, fileName);
		}

		private static async Task DownloadAudio(YouTubeVideo[] streams, string youtubeUrl)
		{
			var stream = streams.Where(s => s.IsAudioOnly())
				.OrderByDescending(s => s.AudioBitrate)
				.First();
			var fileName = GetFileName(stream, OutputType.Audio);
			await Download(stream, fileName);
			// convert to mp3
			var convertedFileName = await ConvertAudio(fileName);
			// remove previous version
			var previousFile = new FileInfo(fileName);
			previousFile.Delete();
			// add metadata from Youtube
			//Console.WriteLine("Writing metadata...");
			var author = stream.Info.Author;
			var title = stream.Info.Title;
			var tfile = TagLib.File.Create(convertedFileName);
			try
			{
				tfile.Tag.Performers = new string[] { author };
				tfile.Tag.Title = title;
				byte[] thumbnailArr = await GetThumbnail( youtubeUrl );
				//System.IO.File.WriteAllBytes("thumbnail.jpg", thumbnailArr);
				ByteVector pictureData = new ByteVector( thumbnailArr );
				var cover = new TagLib.Id3v2.AttachedPictureFrame
				{
					Type = PictureType.FrontCover,
					Description = "Cover",
					MimeType = System.Net.Mime.MediaTypeNames.Image.Jpeg,
					Data = pictureData,
					TextEncoding = StringType.UTF16
				};
				tfile.Tag.Pictures = new[] { cover };
			}
			finally
			{
				tfile.Save();
			}
		}

		static async Task<byte[]> GetThumbnail(string youtubeUri)
		{
			var url = $"{ApiUrl}{ThumbnailEndpoint}?url={youtubeUri}";
			//var thumbnailUrl = JsonConvert.DeserializeObject<string[]>( await client.GetStringAsync(url) );
			//Console.WriteLine($"Thumbnail url: {thumbnailUrl[0]}");
			var pictureData = await client.GetByteArrayAsync(url);
			return pictureData;
		}

		static async Task<string> ConvertAudio(string fileName)
		{
			//FFmpeg.SetExecutablesPath(@"C:\Users\mnagy\Projects\C#\YTDownloader\YTDownloader\ffmpeg-2022-01-27-git-3c831847a8-full_build\bin");
			IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(fileName);
			string outputPath = Path.ChangeExtension(fileName, ".mp3");

			IStream audioStream = mediaInfo.AudioStreams.FirstOrDefault()
				?.SetCodec(AudioCodec.mp3);

			var conversionResult = await FFmpeg.Conversions.New()
				.AddStream(new[] {audioStream })
				.SetOutputFormat(Format.mp3)
				.SetOutput( Path.Combine(Directory.GetCurrentDirectory(), outputPath))
				.SetOverwriteOutput(overwrite: true)
				.Start();
			
			return outputPath;
		}

		static async Task Download(YouTubeVideo stream, string fileName)
		{
			var contentLength = stream.ContentLength ?? int.MaxValue;

			int descConst = 30;
			var fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);
			var desc = $"{fileNameNoExt.Substring(0, fileNameNoExt.Length < descConst ? fileNameNoExt.Length : descConst)}";
			var konsoleProgressBar = new ProgressBarSlim((int)contentLength);
			konsoleProgressBar.Refresh(0, desc);
			const int KB = (1 << 10);
			const int MB = (1 << 20);
			int bufferSize = 2 * MB;
			byte[] buffer = new byte[bufferSize];
			using ( var YTStream = await stream.StreamAsync() )
			using ( var outFileStream = System.IO.File.OpenWrite(fileName))
			{
				int bytesRead;
				while ( (bytesRead = await YTStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
				{
					await outFileStream.WriteAsync(buffer, 0, bytesRead);
					konsoleProgressBar.Refresh( konsoleProgressBar.Current + bytesRead, desc );
				}
			}
		}

		static string RemoveForbidden(string name)
		{
			char[] forbidden = new[] { '/', '\\', '|' };
			var sb = new StringBuilder();
			foreach (var c in name.Select(c => c <= 126 && c >= 32 && !forbidden.Contains(c) ? c : '_' ))
			{
				sb.Append(c);
			}
			return sb.ToString();
		}

		static string GetFileName(YouTubeVideo video, OutputType outType)
		{
			switch (outType)
			{
				case OutputType.Audio:
					return RemoveForbidden(video.FullName) + '.' + video.AudioFormat.ToString().ToLower();
				case OutputType.Video:
					return RemoveForbidden(video.FullName);
				default:
					throw new ArgumentException("Invalid format wanted.");
			}
		}

		//static bool IsAudioOnly(YouTubeVideo video) =>
		//	video.Format == VideoFormat.Unknown &&
		//		video.AudioFormat != AudioFormat.Unknown;
		
		//static bool IsVideoOnly(YouTubeVideo video) =>
		//	video.Format == VideoFormat.Mp4 //video.Format != VideoFormat.Unknown 
		//		&& video.AudioFormat == AudioFormat.Unknown;

		static YouTubeVideo GetStream(YouTubeVideo[] streams, Arguments args)
		{
			switch (args.OutFormat)
			{
				case OutputType.Audio:
					return streams.Where(s => s.IsAudioOnly())
						.OrderByDescending(v => v.AudioBitrate)
						.First();
				case OutputType.Video:
					if (args.Progressive)
						return streams.Where(s => IsProgressive(s))
							.OrderByDescending(v => v.Resolution)
							.First();

					return streams.Where(s => s.Format == VideoFormat.Mp4)
						.OrderByDescending(s => s.Resolution)
						.First(s => s.Resolution <= args.MaxResolution);
					//WriteError("This part of the program is not yet completed. Use flag -p, please.");
					//// unreachable code
					//return null;
				default:
					throw new ArgumentException($"Invalid format: {args.OutFormat}.");
			}
		}

		/// <summary>
		/// Contains both audio AND video.
		/// </summary>
		/// <param name="video">Youtube Video</param>
		/// <returns></returns>
		static bool IsProgressive(YouTubeVideo video) => 
			(int)video.AudioFormat < 3 && (int)video.Format < 2;

		static Arguments getArgs(string[] args)
		{
			var parser = new CliParser<Arguments>();

			var argsObj = new Arguments();

			if ( parser.TryParse(args, argsObj))
			{
				var formats = (IList<OutputType>)Enum.GetValues(typeof(OutputType));
				var dict = formats.ToDictionary(outType => outType.ToString().ToLower());
				//.Select(f => f.ToLower())

				//foreach (var kvPair in dict)
				//{
				//	Console.WriteLine($"{kvPair.Key} -> {kvPair.Value}");
				//}
					
				// custom constraints
				if ( !dict.ContainsKey(argsObj.Format.ToLower()))
				{
					WriteError($"Invalid format: '{argsObj.Format}'");
					// unreachable
					return null;
				}

				argsObj.OutFormat = dict[argsObj.Format];
				
				if ( ( argsObj.Url is null && argsObj.Playlist is null ) || 
					( !(argsObj.Url is null) && !(argsObj.Playlist is null) )) // --help (if required arg is null)
				{
					WriteError("Exactly one of flags -u/--url and -p/--playlist has to contain value.\nPlease, try again.");
					return null;
				}

				//try
				//{
				//	// test if it has correct Uri format
				//	if ( argsObj.Url is not null ) 
				//		var _ = new Uri(argsObj.Url);
				//}
				//catch (UriFormatException)
				//{
				//	WriteError("Failed to parse URL (invalid format of the URL).");
				//}

				return argsObj;
			}
			else
			{
				WriteError("Unable to parse command line arguments. Use --help for more information.");
				// unreachable
				return null;
			}
		}

		/// <summary>
		/// Exits program with code 1.
		/// </summary>
		/// <param name="errorMessage"></param>
		static void WriteError(string errorMessage)
		{
			Console.Error.WriteLine(errorMessage);
			Environment.Exit(1);
		}
	}
}

﻿using System;
using VideoLibrary;
using clipr;
using System.Text;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
//using Konsole;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using System.Net.Http;
using VideoLibrary.Exceptions;
using TagLib;
using Newtonsoft.Json;
using Konsole;
using System.Diagnostics;

namespace YTDownloader
{
	class Program
	{
		const string ApiUrl = "http://nas.lan";
		const string TestEndpoint = "/test";
		const string PlaylistEndpoint = "/playlist";
		const string ThumbnailEndpoint = "/thumbnail";

		const string AppDirectoryName = "csharp_yt_downloader";
		const string FFmpegWindowsDirectoryName = "ffmpeg_win";
		const string FFmpegLinuxDirectoryName = "ffmpeg_linux";
		const string FFmpegMacDirectoryName = "ffmpeg_mac";

		const string AudioOutputFormat = "mp3";

		static int MainProgressBarCounter = 0;
		static readonly string MainProgressBarDescription = "Downloading playlist";

		private static readonly HttpClient client = new HttpClient();
		static async Task Main(string[] args)
		{
			// unpack ffmpeg executables
			await UnpackFFmpeg();

			Arguments parsedArgs = getArgs(args);

			//Console.WriteLine("Arguments loaded.");

			// if ( ! await IsServerRunning() )
			// {
			// 	WriteError("Internet is down or server is not responding.\nPlease, try again later.");
			// 	return;
			// }

			//Console.WriteLine("Server is online.");

			IList<string> urls = await GetUrls(parsedArgs);

			Console.WriteLine("Loading streams");

			List<Task<IEnumerable<YouTubeVideo>>> streamGetters = urls.Select( url => GetStreams(url) ).ToList();
			Task.WaitAll(streamGetters.ToArray());

			IList<(string url, IList<YouTubeVideo> streams)> data = await GetAllStreams(urls, streamGetters);

			bool showPlaylistProgressBar = data.Count > 1;
			ProgressBar progressBar = null;
			if ( showPlaylistProgressBar )
			{
				progressBar = new ProgressBar(data.Count);
				progressBar.Refresh(MainProgressBarCounter, MainProgressBarDescription);
			}

			// start downloading
			Console.WriteLine("Starting downloading");
			IList<Task<string>> handlers = data
				.Select(data_pair => HandleDownload(data_pair.streams, parsedArgs, data_pair.url, progressBar))
				.ToArray();

			// wait for all downloads to end
			Task.WaitAll(handlers.ToArray());

			await HandleErrors(handlers);

			Console.WriteLine("Download completed.");
		}

		private static async Task<IList<(string urls,IList<YouTubeVideo> streams)>> GetAllStreams(IList<string> urls, List<Task<IEnumerable<YouTubeVideo>>> streamGetters)
		{
			var data = new List<(string url, IList<YouTubeVideo> streams)>();

			for (int i = 0; i < urls.Count; i++)
			{
				var url = urls[i];
				try
				{
					IList<YouTubeVideo> streams = (await streamGetters[i]).ToArray();
					if ( streams.Any() )
						data.Add( (url, streams) );
				}
				catch (Exception)
				{
					Console.Error.WriteLine($"Stream is unavailable for {url}.");
					//WriteError();
					continue;
				}
			}
			return data;
		}

		private static async Task<IList<string>> GetUrls(Arguments args)
		{
			IList<string> urls;
			if ( args.Playlist is not null )
			{
				var playlistData = await GetUrlsFromPlaylist(args.Playlist);
				urls = playlistData.VideoUrls ?? Array.Empty<string>();
				var currDir = new DirectoryInfo(Directory.GetCurrentDirectory());
				var playlistDir = currDir.CreateSubdirectory($"playlist_{RemoveForbidden(playlistData.Name)}");
				Directory.SetCurrentDirectory(playlistDir.FullName);
				Console.WriteLine($"Downloading playlist {playlistData.Name}");
			}
			else if (args.Url is not null)
			{
				urls = new[] { args.Url };
			}
			else
			{
				throw new InvalidOperationException("Arguments must contain exactly one of Url (-u/--url) or Playlist (-p/--playlist).");
			}
			return urls;
		}

		private static async Task HandleErrors(IList<Task<string>> downloadHandlers)
		{
			var errorMsgs = new List<string>();
			foreach (var handler in downloadHandlers)
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
		}

		private static async Task<string> HandleDownload(IList<YouTubeVideo> streams, Arguments parsedArgs, string videoUrl, ProgressBar progressBar)
		{
			string result = null;
			bool masterProgressBarPresent = progressBar != null;
			try
			{
				switch (parsedArgs.OutType)
				{
					case OutputType.Audio:
						//await DownloadAudio(streams, videoUrl, masterProgressBarPresent);
						{
							var audioFile = new FileInfo(
								Path.ChangeExtension(
									GetFileName( GetStream(streams, parsedArgs), OutputType.Audio),
									AudioOutputFormat)
								);
							if ( parsedArgs.MaxQuality )
							{
								(var audioFileName, var stream) = await DownloadAudioOnly(streams, videoUrl, parsedArgs, masterProgressBarPresent);
								audioFile = new FileInfo(audioFileName);
								await AddMetadata(videoUrl, audioFileName, stream, masterProgressBarPresent);
							}
							else
							{
								var audioArgs = new Arguments()
								{
									Progressive = true
								};
								await DownloadVideo(streams, audioArgs, masterProgressBarPresent);
								await ExtractAudio(streams, masterProgressBarPresent);
								var videoFile = new FileInfo(GetFileName(GetStream(streams, new Arguments(){ OutType = OutputType.Video }), OutputType.Video));
								Console.WriteLine($"Removing {videoFile.FullName}");
								videoFile.Delete();
								await AddMetadata(videoUrl, audioFile.FullName, streams[0], masterProgressBarPresent);
							}

							if ( parsedArgs.AddVisualization)
							{
								await Visualize(audioFile, masterProgressBarPresent);
							}
						}
						break;
					case OutputType.Video:
						await DownloadVideo(streams, parsedArgs, masterProgressBarPresent);
						break;
					case OutputType.Both:
						{
							//var audioTask = DownloadAudio(streams, videoUrl, masterProgressBarPresent);
							var videoTask = DownloadVideo(streams, parsedArgs, masterProgressBarPresent);

							await videoTask;
							//Task.WaitAll(new[] { audioTask, videoTask });
							await ExtractAudio(streams, masterProgressBarPresent);
							var audioFile = new FileInfo(
								Path.ChangeExtension(
									GetFileName(streams[0], OutputType.Audio),
									AudioOutputFormat)
								);
							await AddMetadata(videoUrl, audioFile.FullName, streams[0], masterProgressBarPresent);

							if ( parsedArgs.AddVisualization)
							{
								await Visualize(audioFile, masterProgressBarPresent);
							}
						}
						break;
					default:
						WriteError($"Unsupported output type: {parsedArgs.OutType}");
						break;
				}
			}
			catch (Exception e)
			{
				//throw e;
				result = $"Unknown error for url {videoUrl}\nMessage:\n{e.Message}\nStacktrace:\n{e.StackTrace}";
			}

			if ( progressBar is not null )
				progressBar.Refresh(++MainProgressBarCounter, MainProgressBarDescription);

			return result;
		}

		private static async Task Visualize(FileInfo audioFile, bool masterProgressBarPresent)
		{
			//Console.WriteLine("Creating visualization video...");
			var visualize = //Path.Combine(Directory.GetCurrentDirectory(), Path.ChangeExtension(Path.GetTempFileName(), "mp4"));
				Path.Combine(
				audioFile.Directory.FullName,
				$"visualization-{ Path.GetFileNameWithoutExtension(audioFile.Name) }.mp4"
			);
			var conversion = await FFmpeg.Conversions.FromSnippet
				.VisualiseAudio(audioFile.FullName, visualize, VideoSize.Hd720,
				pixelFormat: PixelFormat.rgb24,
				mode: VisualisationMode.line, amplitudeScale: AmplitudeScale.log, frequencyScale: FrequencyScale.log);
			conversion.SetFrameRate(60f)
				.SetOverwriteOutput(true)
				//.UseMultiThread(true)
				.SetOutputFormat(Format.mp4);
			if ( !masterProgressBarPresent)
			{
				conversion.OnProgress += async (sender, args) =>
				{
					//Show all output from FFmpeg to console
					Console.CursorLeft = 0;
					await Console.Out.WriteAsync($"Creating visualization [{args.Duration}/{args.TotalLength}][{args.Percent}%]");
				};
			}
			//Console.WriteLine($"Build command: { conversion.Build() }");
			await conversion.Start();

			if ( !masterProgressBarPresent )
			await Console.Out.WriteLineAsync();

			if ( !masterProgressBarPresent )
				Console.WriteLine($"Result in {visualize}");
		}

		private static async Task AddMetadata(string youtubeUrl, string fullName, YouTubeVideo stream, bool masterProgressBarPresent)
		{
			if ( !masterProgressBarPresent )
				Console.WriteLine("Adding metadata");

			var author = stream.Info.Author;
			var title = stream.Info.Title;
			var tfile = TagLib.File.Create(fullName);
			try
			{
				tfile.Tag.Performers = new string[] { author };
				tfile.Tag.Title = title;
				byte[] thumbnailArr = await GetThumbnail( youtubeUrl );
				//System.IO.File.WriteAllBytes("thumbnail.jpg", thumbnailArr);
				var pictureData = new ByteVector( thumbnailArr );
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

		private static async Task ExtractAudio(IList<YouTubeVideo> streams, bool masterProgressBarPresent)
		{
			var videoOutputFileName = Path.Combine(Directory.GetCurrentDirectory(), GetFileName(streams[0], OutputType.Video));
			var audioOutputFileName = Path.Combine(Directory.GetCurrentDirectory(), GetFileName(streams[0], OutputType.Audio));
			audioOutputFileName = Path.ChangeExtension(audioOutputFileName, AudioOutputFormat);

			if ( !masterProgressBarPresent )
				Console.WriteLine($"Extracting audio from video...");

			// this needs FULL PATH, not a relative one
			//var conversion = await FFmpeg.Conversions.FromSnippet.ExtractAudio(videoOutputFileName, audioOutputFileName);

			var mediaInfo = await FFmpeg.GetMediaInfo(videoOutputFileName);
			var audioInfo = mediaInfo.AudioStreams.FirstOrDefault();

			IConversion conversion = FFmpeg.Conversions.New()
				.AddStream(audioInfo)
				.SetOutput(audioOutputFileName)
				.SetOverwriteOutput(overwrite: true)
				.SetAudioBitrate(256 * (1L << 10))
				.SetOutputFormat(Format.mp3);

			var  _ = await conversion.Start();
		}

		private async static Task<IEnumerable<YouTubeVideo>> GetStreams(string url)
		{
			IEnumerable<YouTubeVideo> streams;
			try
			{
				streams = await YouTube.Default.GetAllVideosAsync(url);
				return streams;
			}
			catch (TimeoutException)
			{
				Console.Error.WriteLine("Your internet connection timed out. Please, try again.");
				return Enumerable.Empty<YouTubeVideo>();
			}
			catch (UnavailableStreamException)
			{
				Console.Error.WriteLine("This video is not publicly accessible.");
				return Enumerable.Empty<YouTubeVideo>();
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"Error \"{e.Message}\" occurred on getting streams from \"{url}\"");
			}
			return Enumerable.Empty<YouTubeVideo>();
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

		private static async Task UnpackFFmpeg()
		{
			const string ffmpeg_win_file_name = "ffmpeg.exe";
			const string ffprobe_win_file_name = "ffprobe.exe";

			//// binary files
			const string ffmpeg_mac_file_name = "ffmpeg";
			const string ffprobe_mac_file_name = "ffprobe";

			const string ffmpeg_linux_file_name = "ffmpeg";
			const string ffprobe_linux_file_name = "ffprobe";

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
					ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffprobe_win_file_name)) )
				{
					Console.WriteLine($"Unpacking ffmpeg files to application directory {ffmpegDir}");
					var currDir = Directory.GetCurrentDirectory();
					Directory.SetCurrentDirectory(ffmpegDir.FullName);
					await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
					Directory.SetCurrentDirectory(currDir);

				}
				FFmpeg.SetExecutablesPath(ffmpegDir.FullName);
			}
			// TODO: different builds for different Distros: Ubuntu, Fedora, Arch,...
			if ( OperatingSystem.IsLinux() )
			{
				Console.WriteLine("This part has not been properly tested on Linux due to development taking place on Windows.");
				Console.WriteLine("Proceed anyway? [y/n]");
				ConsoleKeyInfo resp = Console.ReadKey(intercept: false);
				if ( resp.KeyChar != 'y' )
					WriteError("You have chosen to NOT proceed.");

				//Console.WriteLine($"Unpacking ffmpeg files to application directory {homeDir}");
				var ffmpegDir = appDir.CreateSubdirectory(FFmpegWindowsDirectoryName);
				if ( !(ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffmpeg_linux_file_name) &&
					ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffprobe_linux_file_name)) )
				{
					Console.WriteLine($"Unpacking ffmpeg files to application directory {ffmpegDir}");
					var currDir = Directory.GetCurrentDirectory();
					Directory.SetCurrentDirectory(ffmpegDir.FullName);
					await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
					Directory.SetCurrentDirectory(currDir);

				}
				FFmpeg.SetExecutablesPath(ffmpegDir.FullName);
			}
			if ( OperatingSystem.IsMacOS() )
			{
				//WriteError("Essential feature missing for this platform: ffmpeg");

				Console.WriteLine("This part has not been properly tested on MacOS due to development taking place on Windows.");
				Console.WriteLine("Proceed anyway? [y/n]");
				ConsoleKeyInfo resp = Console.ReadKey(intercept: false);
				if ( resp.KeyChar != 'y' )
					WriteError("You have chosen to NOT proceed.");

				var ffmpegDir = appDir.CreateSubdirectory(FFmpegMacDirectoryName);
				if ( !(ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffmpeg_mac_file_name) &&
					ffmpegDir.GetFiles().Select(fi => fi.Name).Contains(ffprobe_mac_file_name) ))
				{
					Console.WriteLine($"Unpacking ffmpeg files to application directory {ffmpegDir}");
					await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);

				}
				FFmpeg.SetExecutablesPath(ffmpegDir.FullName);
			}
		}

		private static async Task DownloadVideo(IList<YouTubeVideo> streams, Arguments parsedArgs, bool masterProgressBarPresent)
		{
			const int FullHD = 1080;
			if ( parsedArgs.Progressive || parsedArgs.MaxResolution < FullHD )
			{
				await DownloadProgressiveVideo(streams, masterProgressBarPresent);
				return;
			}

			string SubDirName = $"temp_{Environment.ProcessId}";

			// create temp directory in current directory and move into it
			var currDir = new DirectoryInfo(Directory.GetCurrentDirectory());
			var subDir = currDir.CreateSubdirectory(SubDirName);
			Debug.WriteLine($"SubDirectory name: {SubDirName}");
			try{
				// download audio
				var audioStream = GetStream(streams, parsedArgs);
				var audioFileName = Path.Combine(subDir.FullName, $"audio.{audioStream.AudioFormat.ToString().ToLower()}");
				Debug.WriteLine($"Downloading audio with bitrate {audioStream.AudioBitrate} ...");
				var audioDownloading = Download(audioStream, audioFileName, masterProgressBarPresent);
				// download video
				var videoStream = streams.Where(s => s.IsVideoOnly() && s.Resolution <= parsedArgs.MaxResolution)
					.OrderByDescending(s => s.Resolution)
					.First();
				var videoFileName = Path.Combine(subDir.FullName, $"video.{videoStream.Format.ToString().ToLower()}");
				Debug.WriteLine($"Downloading video with resolution {videoStream.Resolution} ...");
				var videoDownloading = Download(videoStream, videoFileName, masterProgressBarPresent);
				
				await audioDownloading;
				await videoDownloading;

				// merge using FFmpeg
				string outputPath = GetFileName(videoStream, OutputType.Video);
				IMediaInfo videoMediaInfo = await FFmpeg.GetMediaInfo(videoFileName);
				IStream videoMediaStream = videoMediaInfo.VideoStreams.FirstOrDefault()
					?.SetCodec(VideoCodec.h264);

				IMediaInfo audioMediaInfo = await FFmpeg.GetMediaInfo(audioFileName);
				IStream audioMediaStream = audioMediaInfo.AudioStreams.FirstOrDefault()
					?.SetCodec(AudioCodec.mp3);

				//var cut_video_title = videoStream.Info.Title.Length > 20 ? videoStream.Info.Title.Substring(0, 20) : videoStream.Info.Title;
				var conversion_desc = $"Converting to MP4";
				//ProgressBarSlim progressBar = null;
				//if ( !masterProgressBarPresent )
				//{
				//	// why am I creating this progress bar..?
				//	progressBar = new ProgressBarSlim((int)videoMediaInfo.Size);
				//	progressBar.Refresh(0, conversion_desc);
				//}
				var conversion = FFmpeg.Conversions.New()
					.AddStream(
						videoMediaStream,
						audioMediaStream
					)
					.SetOutputFormat(Format.mp4)
					.SetOverwriteOutput(overwrite: true)
					.SetOutput( outputPath );

				var conversionResult = await conversion.Start();

				//var conversion = await FFmpeg.Conversions.FromSnippet.AddAudio(videoFileName, audioFileName, outputPath);
				//IConversionResult result = await conversion
				//	.Start();
			}
			//catch
			//{
				
			//}
			finally
			{
				subDir.Delete(recursive: true);
			}
		}

		private static async Task DownloadProgressiveVideo(IList<YouTubeVideo> streams, bool masterProgressBarPresent)
		{
			var bestProgressive = streams.Where(s => s.IsProgressive())
					.Where(s => s.Format == VideoFormat.Mp4)
					.OrderByDescending(s => s.Resolution)
					.First();
			var fileName = GetFileName(bestProgressive, OutputType.Video);
			await Download(bestProgressive, fileName, masterProgressBarPresent);
		}

		private static async Task<(string filename, YouTubeVideo stream)> DownloadAudioOnly(IList<YouTubeVideo> streams, string youtubeUrl, Arguments parsedArgs, bool masterProgressBarPresent)
		{
			var stream = GetStream(streams, parsedArgs);

			var audioFileName = GetFileName(stream, OutputType.Audio).Replace(' ', '_');
			await Download(stream, audioFileName, masterProgressBarPresent);
			var finalAudioFileName = Path.ChangeExtension(audioFileName, AudioOutputFormat);

			// convert to mp3
			var convertedFileName = await ConvertAudio(audioFileName);

			// remove original audio file
			{
				var previousFile = new FileInfo(audioFileName);
				previousFile.Delete();
			}

			return (convertedFileName, stream);

			// add metadata from Youtube
			//var author = stream.Info.Author;
			//var title = stream.Info.Title;
			//var tfile = TagLib.File.Create(convertedFileName);
			//try
			//{
			//	tfile.Tag.Performers = new string[] { author };
			//	tfile.Tag.Title = title;
			//	byte[] thumbnailArr = await GetThumbnail( youtubeUrl );
			//	//System.IO.File.WriteAllBytes("thumbnail.jpg", thumbnailArr);
			//	var pictureData = new ByteVector( thumbnailArr );
			//	var cover = new TagLib.Id3v2.AttachedPictureFrame
			//	{
			//		Type = PictureType.FrontCover,
			//		Description = "Cover",
			//		MimeType = System.Net.Mime.MediaTypeNames.Image.Jpeg,
			//		Data = pictureData,
			//		TextEncoding = StringType.UTF16
			//	};
			//	tfile.Tag.Pictures = new[] { cover };
			//}
			//finally
			//{
			//	tfile.Save();
			//}
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

		static async Task Download(YouTubeVideo stream, string fileName, bool masterProgressBarPresent)
		{
			var contentLength = stream.ContentLength ?? int.MaxValue;
			contentLength = contentLength <= 0 ? int.MaxValue : contentLength;

			int descConst = 30;
			var fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);
			var desc = $"{fileNameNoExt.Substring(0, fileNameNoExt.Length < descConst ? fileNameNoExt.Length : descConst)}";
			ProgressBarSlim konsoleProgressBar = null;
			if ( !masterProgressBarPresent )
			{
				konsoleProgressBar = new ProgressBarSlim((int)contentLength);
				konsoleProgressBar.Refresh(0, desc);
			}

			const int KB = (1 << 10);
			const int MB = (1 << 20);
			int bufferSize = 2 * MB;
			byte[] buffer = new byte[bufferSize];
			using ( var YTStream = await stream.StreamAsync() )
			using ( var outFileStream = System.IO.File.OpenWrite(fileName))
			{
				int bytesRead;
				while ( (bytesRead = await YTStream.ReadAsync(buffer)) > 0)
				{
					await outFileStream.WriteAsync(buffer.AsMemory(0, bytesRead));

					if ( !masterProgressBarPresent )
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
			string name;
			switch (outType)
			{
				case OutputType.Audio:
					name = RemoveForbidden(video.FullName);
					return Path.ChangeExtension(name, video.AudioFormat.ToString().ToLower()).Replace(' ', '_');
				case OutputType.Video:
					name = RemoveForbidden(video.FullName);
					return name.Replace(' ', '_');
				default:
					throw new ArgumentException("Invalid format wanted.");
			}
		}

		static YouTubeVideo GetStream(IList<YouTubeVideo> streams, Arguments args)
		{
			switch (args.OutType)
			{
				case OutputType.Audio:
					return streams.Where(s => s.IsAudioOnly())
						.OrderByDescending(v => v.AudioBitrate)
						.First();
				case OutputType.Video:
					if (args.Progressive)
						return streams.Where(s => s.IsProgressive())
							.OrderByDescending(v => v.Resolution)
							.First();

					return streams.Where(s => s.Format == VideoFormat.Mp4)
						.OrderByDescending(s => s.Resolution)
						.First(s => s.Resolution <= args.MaxResolution);
					//WriteError("This part of the program is not yet completed. Use flag -p, please.");
					//// unreachable code
					//return null;
				default:
					throw new ArgumentException($"Invalid format: {args.OutType}.");
			}
		}

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

				argsObj.OutType = dict[argsObj.Format];
				
				if ( !(argsObj.Url is not null ^ argsObj.Playlist is not null) ) // --help (if required arg is null)
				{
					WriteError("Exactly one of flags -u/--url and -p/--playlist has to contain value.\nPlease, try again.");
					// unreachable
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

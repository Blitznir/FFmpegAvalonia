using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Extensions.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MessageBox.Avalonia.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ExtensionMethods;
using System.Threading;
using FFmpegAvalonia.ViewModels;

namespace FFmpegAvalonia
{
    class FFmpeg
    {
        private FFmpegProcess _FFProcess;
        private readonly string _FFmpegPath;
        private readonly ConcurrentDictionary<string, int> _FilesDict; //maybe not need to be concurrentdict yet
        private int _LastFrame;
        private long _CancelQ = 0;
        public bool CancelQ
        {
            get => Interlocked.Read(ref _CancelQ) == 1;
            set => Interlocked.Exchange(ref _CancelQ, Convert.ToInt64(value));
        }
        private int _TotalPrevFrameProgress;
        private int _TotalDirFrames;
        private IProgress<double>? _UIProgress;
        //private bool _IsDisposed;
        private readonly object _DisposeLock = new();
        private MainWindowViewModel? _ViewModel;
        public FFmpeg(string ffmpegdir)
        {
            _FFmpegPath = ffmpegdir;
            _FFProcess = new FFmpegProcess(_FFmpegPath) ///////////////////////////////////////////////////////////////////////////////////////////////////////////////
            {
                StartInfo = DefaultStartInfo(),
                EnableRaisingEvents = true,
            };
            _FilesDict = new ConcurrentDictionary<string, int>();
        }
        private static ProcessStartInfo DefaultStartInfo()
        {
            return new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }
        private void NewFFProcess()
        {
            _FFProcess = new FFmpegProcess(_FFmpegPath)
            {
                StartInfo = DefaultStartInfo(),
                EnableRaisingEvents = true
            };
        }
        public string GetFrameCountApproximate(string dir, string searchPattern, string args)
        {
            bool skipFrameRateCalc = false;
            decimal frameRate = 0;
            Regex re = new("(?:\\s+-r\\s+)(\\S+)");
            Match match = re.Match(args);
            if (match.Success)
            {
                skipFrameRateCalc = true;
                Trace.TraceInformation("Skipping per file framerate calculation because -r arg exists");
                string frameRateToParse = match.Groups[1].Value;
                var split = frameRateToParse.Split("/");
                decimal numerator = Decimal.Parse(split[0]);
                decimal denominator = Decimal.Parse(split[1]);
                frameRate = numerator / denominator;
                Trace.TraceInformation("Framerate: " + frameRate);
            }
            NewFFProcess();
            var dirInfo = new DirectoryInfo(dir);
            var files = dirInfo.EnumerateFiles(searchPattern);
            var sb = new StringBuilder();
            foreach (var file in files)
            {
                sb.Append(file.FullName);

                if (!skipFrameRateCalc)
                {
                    _FFProcess.StartProbe($"-v 0 -of csv=p=0 -select_streams v:0 -show_entries stream=r_frame_rate \"{file.FullName}\"");
                    string output = _FFProcess.StandardOutput.ReadToEnd().Trim();
                    frameRate = Decimal.Parse(output.Split(@"/")[0]) / Decimal.Parse(output.Split(@"/")[1]);

                    Trace.TraceInformation("Framerate: " + frameRate);
                    _FFProcess.WaitForExit();

                    Trace.TraceInformation("Process Exit Code: " + _FFProcess.ExitCode);
                }

                _FFProcess.StartProbe($"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{file.FullName}\"");
                decimal totalSeconds = Decimal.Parse(_FFProcess.StandardOutput.ReadToEnd().Trim());

                Trace.TraceInformation("Total Seconds: " + totalSeconds);
                _FFProcess.WaitForExit();

                Trace.TraceInformation("Process Exit Code: " + _FFProcess.ExitCode);

                decimal totalFrames = frameRate * totalSeconds;
                _FilesDict.TryAdd(file.FullName, (int)totalFrames); //rounds down
                _TotalDirFrames += (int)totalFrames;
                sb.Append(" -- " + (int)totalFrames + " -- " + totalFrames + System.Environment.NewLine);
            }
            _FFProcess.Dispose();
            return sb.ToString();
        }
        public string GetFrameCountFromPackets(string dir, string searchPattern) 
        {
            NewFFProcess();
            var dirInfo = new DirectoryInfo(dir);
            var files = dirInfo.EnumerateFiles(searchPattern);
            var sb = new StringBuilder();
            foreach (var file in files)
            {
                sb.Append(file.FullName);
                _FFProcess.StartInfo.Arguments = $"-v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets \"{file.FullName}\"";
                _FFProcess.Start();
                int totalFrames = Int32.Parse(_FFProcess.StandardOutput.ReadToEnd().Split("=")[1].Split("[")[0].Trim());
                _FFProcess.WaitForExit();
                _FilesDict.TryAdd(file.FullName, totalFrames);
                sb.Append(" -- " + totalFrames + System.Environment.NewLine);
            }
            _FFProcess.Dispose();
            return sb.ToString();
        }
        public string GetFrameCount(string dir, string searchPattern)
        {
            NewFFProcess();
            var dirInfo = new DirectoryInfo(dir);
            var files = dirInfo.EnumerateFiles(searchPattern);
            var sb = new StringBuilder();
            foreach (var file in files)
            {
                sb.Append(file.FullName);
                _FFProcess.StartInfo.Arguments = $"-v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames \"{file.FullName}\"";
                _FFProcess.Start();
                int totalFrames = Int32.Parse(_FFProcess.StandardOutput.ReadToEnd().Split("=")[1].Split("[")[0].Trim());
                _FFProcess.WaitForExit();
                _FilesDict.TryAdd(file.FullName, totalFrames);
                sb.Append(" -- " + totalFrames + System.Environment.NewLine);
            }
            _FFProcess.Dispose();
            return sb.ToString();
        }
        public async Task<string> RunProfile(string args, string outputDir, string ext, IProgress<double> progress, MainWindowViewModel viewModel)
        {
            //Start out having progress bar show prog of entire dir
            //Progress would be current progress plus the sum of the files already done
            _ViewModel = viewModel;
            _UIProgress = progress;
            //NewFFProcess("ffmpeg");
            //Trace.TraceInformation(_FFProcess.StartInfo.FileName);
            //_FFProcess.OutputDataReceived += new DataReceivedEventHandler(StdOutHandler);

            foreach (var filePath in _FilesDict.Keys)
            {
                NewFFProcess();
                _FFProcess.OutputDataReceived += new DataReceivedEventHandler(StdOutHandler);
                //Path.GetFileName(filePath);
                if (CancelQ)
                {
                    _FFProcess.Dispose();
                    CancelQ = false;
                    return filePath;
                }
                _FFProcess.StartMpeg($"-i \"{filePath}\" -progress pipe:1 {args} \"{Path.Combine(outputDir, Path.GetFileNameWithoutExtension(filePath) + ext)}\"");
                _FFProcess.BeginOutputReadLine();
                await ReadStdErr();
                await _FFProcess.WaitForExitAsync();
                Trace.TraceInformation("Process Exit Code: " + _FFProcess.ExitCode);
                if (CancelQ)
                {
                    _FFProcess.Dispose();
                    CancelQ = false;
                    return filePath;
                }
                _TotalPrevFrameProgress += _FilesDict[filePath];
                lock (_DisposeLock)
                {
                    _FFProcess.Dispose();
                }
            }
            //_FFProcess.Dispose();
            return "0";
        }
        public void StopProfile()
        {
            CancelQ = true;
            lock (_DisposeLock)
            {
                try
                {
                    _FFProcess.Refresh();
                    if (!_FFProcess.HasExited)
                    {
                        _FFProcess.StandardInput.WriteLine("q");
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        }
        public static bool CheckFFmpegExecutable(string location)
        {
            string ffprobe;
            string ffmpeg;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                ffprobe = "ffprobe";
                ffmpeg = "ffmpeg";
            }
            else
            {
                ffprobe = "ffprobe.exe";
                ffmpeg = "ffmpeg.exe";
            }
            if (File.Exists(Path.Combine(location, ffmpeg)) && File.Exists(Path.Combine(location, ffprobe)))
            {
                var proc = new Process
                {
                    StartInfo = DefaultStartInfo()
                };
                proc.StartInfo.FileName = Path.Combine(location, ffmpeg); //only runs ffmpeg not ffprobe
                proc.Start();
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(1000);
                if (!proc.HasExited)
                {
                    proc.Close();
                }
                proc.Dispose();
                if (String.IsNullOrEmpty(stdout) && String.IsNullOrEmpty(stderr))
                {
                    return false;
                }
                if (stderr.StartsWith("ffprobe") || stderr.StartsWith("ffmpeg"))
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }
        private async Task ReadStdErr()
        {
            var sr = _FFProcess.StandardError;
            var sb = new StringBuilder();
            while (!sr.EndOfStream)
            {
                var inputChar = (char)sr.Read();
                sb.Append(inputChar);
                if (sb.EndsWith(Environment.NewLine))
                {
                    var line = sb.ToString();
                    sb = new StringBuilder();
                    Trace.TraceInformation(line);
                }
                else if (sb.EndsWith("already exists. Overwrite? [y/N] "))
                {
                    Trace.TraceInformation("Overwrite prompt found");
                    var line = sb.ToString();
                    if (_ViewModel!.AutoOverwriteCheck)
                    {
                        await _FFProcess.StandardInput.WriteLineAsync("y");
                    }
                    else
                    {
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            var msgBox = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow(new MessageBox.Avalonia.DTO.MessageBoxStandardParams
                            {
                                ButtonDefinitions = ButtonEnum.YesNo,
                                ContentTitle = "Overwrite",
                                ContentHeader = "Overwrite?",
                                ContentMessage = $"The file \"{line.Split(@"'")[1].Split(@"'")[0]}\" already exists, would you like to overwrite it?"
                            });
                            var app = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                            var result = await msgBox.ShowDialog(app.MainWindow);
                            if (result == ButtonResult.Yes)
                            {
                                await _FFProcess.StandardInput.WriteLineAsync("y");
                            }
                            else
                            {
                                await _FFProcess.StandardInput.WriteLineAsync("n");
                            }
                        }, DispatcherPriority.MaxValue);
                        sb = new StringBuilder();
                    }
                    Trace.TraceInformation(line);
                }
            }
        }
        private void StdOutHandler(object sendingProcess, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                Trace.TraceInformation("STDOUT**--" + e.Data);
                if (e.Data.Contains("frame="))
                {
                    _LastFrame = Int32.Parse(e.Data.Split("=")[1].Trim()); //trim
                    Trace.TraceInformation(_LastFrame.ToString());
                    var progress = ((double)_LastFrame + _TotalPrevFrameProgress) / _TotalDirFrames;
                    Trace.TraceInformation(progress.ToString());
                    _UIProgress!.Report(((double)_LastFrame + _TotalPrevFrameProgress) / _TotalDirFrames);
                }
            }
        }
    }
}
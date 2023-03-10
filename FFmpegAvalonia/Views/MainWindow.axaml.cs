using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Rendering;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using Avalonia.Extensions.Controls;
using System.Collections.Generic;
using System.Collections;
using System.Drawing.Printing;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MessageBox.Avalonia.Enums;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Path = System.IO.Path;
using Avalonia.Layout;
using CyberFileUtils;
using ExtensionMethods;
using PCLUntils.IEnumerables;
using Avalonia.Media;
using System.Reflection;
using MessageBox.Avalonia.DTO;
using System.Linq;
using System.Text;
using FFmpegAvalonia.ViewModels;
using FFmpegAvalonia.AppSettingsX;
using Avalonia.VisualTree;

namespace FFmpegAvalonia
{
    public partial class MainWindow : Window
    {
        private FFmpeg? FFmp;
        private ProgressFileCopier? Copier;
        private ListViewData? CurrentItemInProgress;
        private bool _IsQueueRunning;
        private readonly ObservableCollection<ListViewData> ListViewItems = new();
        private readonly ObservableCollection<string> ProfileBoxItems = new();
        public AppSettings AppSettings = new();
        public MainWindowViewModel ViewModel = new();

        public MainWindow()
        {
            //Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(AppContext.BaseDirectory, "debug.log")));
            //MIGHT WANT TO CHECK IF DEBUG FILE IS GETTING TOO BIG AND CLEAR IT UP
            var textWriter = new TextWriterTraceListener(Path.Combine(AppContext.BaseDirectory, "debug.log"));
            var listener = new ConsoleTraceListener()
            {
                Writer = textWriter.Writer,
                TraceOutputOptions = TraceOptions.DateTime// | TraceOptions.Timestamp | TraceOptions.Callstack, ///MAYBE USE THIS WITH PROGRESS ENUM???
            };
            Trace.Listeners.Add(listener);
            Trace.AutoFlush = true;
            InitializeComponent();
            AddHandler(DragDrop.DropEvent, Drop!);
            AddHandler(DragDrop.DragOverEvent, DragOver!);
            ProgListView.Items = ListViewItems;
            DataContext = ViewModel;
            Title = "CyberAva ffmpeg " + Assembly.GetExecutingAssembly().GetName().Version!.ToString();
        }
        private void DragOver(object sender, DragEventArgs e)
        {
            Debug.WriteLine("DragOver");

            Point pt = e.GetPosition((IVisual)sender);
            var test = SourceDirBox.HitTestCustom(pt);
            Debug.WriteLine(test);

            // Only allow Copy or Link as Drop Operations.
            e.DragEffects = e.DragEffects & (DragDropEffects.Copy | DragDropEffects.Link);

            // Only allow if the dragged data contains text or filenames.
            if (!e.Data.Contains(DataFormats.Text) && !e.Data.Contains(DataFormats.FileNames))
                e.DragEffects = DragDropEffects.None;
        }
        private void Drop(object sender, DragEventArgs e)
        {
            Debug.WriteLine("Drop");
            Point pt = e.GetPosition((IVisual)sender);
            TextBox textBox;
            IInputElement inputElement = MainGrid.InputHitTest(pt)!;
            try
            {
                TextBlock textBlock = (TextBlock)inputElement.GetVisualDescendants().Where(x => x is TextBlock).Single();
                //TextBlock textBlock = (TextBlock)inputElement.GetVisualChildren().ToList()[0].GetVisualChildren().ToList()[0];
                textBox = (TextBox)textBlock.TemplatedParent!;
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.Message);
                return;
            }
            if (e.Data.Contains(DataFormats.Text))
                textBox.Text = e.Data.GetText();
            else if (e.Data.Contains(DataFormats.FileNames) && e.Data.GetFileNames()!.Count() == 1)
                textBox.Text = string.Join(Environment.NewLine, e.Data.GetFileNames()!);
        }
        private async void MainWindow_Opened(object? sender, EventArgs e)
        {
            Trace.TraceInformation("MainWindow Opened");
            if (AppSettings.Settings.FFmpegPath == String.Empty)
            {
                var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Help!", $"We could not find your ffmpeg path please select it", ButtonEnum.Ok);
                var result = await msgBoxError.ShowDialog(this);
                if (result == ButtonResult.Ok)
                {
                    var dialog = new OpenFolderDialog();
                    var path = await dialog.ShowAsync(this);
                    if (path is not null)
                    {
                        AppSettings.Settings.FFmpegPath = path;
                    }
                }
                else
                {
                    this.Close();
                }
            }
            if (AppSettings.Profiles.Count > 0)
            {
                foreach (var profile in AppSettings.Profiles.Keys)
                {
                    ProfileBoxItems.Add(profile);
                }
                ProfileBox.Items = ProfileBoxItems;
            }
            ViewModel.AutoOverwriteCheck = AppSettings.Settings.AutoOverwriteCheck;
#if DEBUG
            var buttonGrid = this.FindControl<Grid>("ButtonSec");
            var testButton = new Button();
            buttonGrid.Children.Add(testButton);
            testButton.Content = "Test";
            testButton.HorizontalAlignment = HorizontalAlignment.Right;
            testButton.Click += Test_Click!;
#endif
        }
        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            FFmp?.StopProfile();
            Copier?.Stop();
            AppSettings.Settings.AutoOverwriteCheck = ViewModel.AutoOverwriteCheck;
            AppSettings.Save();
        }
        private async void Browse_Click(object sender, RoutedEventArgs e)
        {
            var control = e.Source as Control;
            var dialog = new OpenFolderDialog();
            var result = await dialog.ShowAsync(this);
            if (result is not null)
            {
                var textBox = this.FindControl<TextBox>(Regex.Replace(control!.Name!, "Browse.*", "Box"));
                textBox.Text = result;
            }
        }
        private async void ListViewItem_Remove(object sender, RoutedEventArgs e)
        {
            //CHECK IF QUEUE IS STARTED
            if (_IsQueueRunning)
            {
                var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow(new MessageBox.Avalonia.DTO.MessageBoxStandardParams
                {
                    ButtonDefinitions = ButtonEnum.Ok,
                    ContentTitle = "Error",
                    ContentHeader = "The queue is currently running",
                    ContentMessage = "If you would like, you can stop the queue"
                });
                await msgBoxError.ShowDialog(this);
                return;
            }
            //CHECK IF THAT ITEM IS CURRENTLY PROGRESSING
            /*if (CurrentItemInProgress is not null && CurrentItemInProgress.Name == itemName)
            {
                var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow(new MessageBox.Avalonia.DTO.MessageBoxStandardParams
                {
                    ButtonDefinitions = ButtonEnum.Ok,
                    ContentTitle = "Error",
                    ContentHeader = "That item is currently being processed",
                    ContentMessage = "If you would like, you can stop the queue"
                });
                await msgBoxError.ShowDialog(this);
                return;
            }*/
            //CHECK IF ITEM HAS BEEN PROCESSED

            Control control = (Control)sender;
            ListViewData data = (ListViewData)control.DataContext!;
            ListViewItems.Remove(data);
        }
#if DEBUG
        private void Test_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.ReadProfiles(@"G:\OBS\10 Transfer Orders\import profiles");
        }
#endif
        private void ListViewItem_Select(object sender, RoutedEventArgs e)//maybe remove?
        {
            /*var control = e.Source as Control;
            var sourceParent = control.Parent.Parent.Parent as DockPanel;
            var sourceParentName = (string)sourceParent.Tag;

            foreach (ListViewData item in ProgListView.Items)
            {
                if (item.Name == sourceParentName)
                {
                    ProgListView.SelectedItem = item;
                    break;
                }
            }*/
            /*ListViewData test = (ListViewData)ProgListView.SelectedItem;
            Trace.TraceInformation(test.Name + "STYP");*/
            //FFmpeg.Yeet(OutputDirBox.Text, "*" + ExtBox.Text);
        }
        private async void AddToQueue_Click(object sender, RoutedEventArgs e)
        {
            if (String.IsNullOrWhiteSpace(SourceDirBox.Text) || String.IsNullOrWhiteSpace(OutputDirBox.Text))
            {
                goto MsgBoxError;
            }
            var fileCount = new DirectoryInfo(SourceDirBox.Text).EnumerateFiles($"*{ExtBox.Text}").Count();
            if (fileCount == 0)
            {
                var msgBox = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "No files matching that extension exist");
                await msgBox.Show(this);
                return;
            }
            if (CopySourceCheck.IsChecked.NullIsFalse())
            {
                ListViewItems.Add(new ListViewData()
                {
                    Name = Path.GetFileName(SourceDirBox.Text),
                    Label = Path.GetFileName(SourceDirBox.Text),
                    Description = new DescriptionData()
                    {
                        SourceDir = SourceDirBox.Text,
                        OutputDir = OutputDirBox.Text,
                        FileCount = fileCount,
                        State = ItemState.Awaiting,
                        Type = ItemTask.Copy,
                        LabelProgressType = ItemLabelProgressType.TotalFileCount, //have as setting
                        ProgressBarType = ItemProgressBarType.File, //have as setting
                    },
                    Background = Brushes.AliceBlue,
                });
                return;
            }
            if (ProfileBox.SelectedItem is null)
            {
                if (!String.IsNullOrEmpty(ProfileBox.Text))
                {
                    foreach (var item in ProfileBox.Items)
                    {
                        if (ProfileBox.Text == item.ToString())
                        {
                            goto NextCheck;
                        }
                    }
                }
                var msgBoxProfileError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "The profile box entry is invalid", ButtonEnum.Ok);
                await msgBoxProfileError.ShowDialog(this);
                return;
            }
            NextCheck:
            if (String.IsNullOrWhiteSpace(ExtBox.Text))
            {
                goto MsgBoxError;
            }
            ListViewItems.Add(new ListViewData() {
                Name = Path.GetFileName(SourceDirBox.Text),
                Label = Path.GetFileName(SourceDirBox.Text),
                Description = new DescriptionData()
                {
                    SourceDir = SourceDirBox.Text,
                    OutputDir = OutputDirBox.Text,
                    FileExt = ExtBox.Text.StartsWith(".") ? ExtBox.Text : $".{ExtBox.Text}",
                    FileCount = fileCount,
                    Profile = AppSettings.Profiles[ProfileBox.Text],
                    State = ItemState.Awaiting,
                    Type = ItemTask.Transcode,
                    LabelProgressType = ItemLabelProgressType.None, //have as setting
                    ProgressBarType = ItemProgressBarType.Directory, //have as setting
                },
                //blanchedalmond //mintcream
                Background = Brushes.LightYellow,
            });
            return;

            MsgBoxError:
            var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "Required textboxes are not all filled out", ButtonEnum.Ok);
            await msgBoxError.ShowDialog(this);
        }
        private async void StartQueue_Click(object sender, RoutedEventArgs e)
        {
            //checkffmpegexe
            /*if (!FFmpeg.CheckFFmpegExecutable(AppSettings.Settings.FFmpegPath))
            {
                var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "The ffmpeg/ffprobe processes could not be found");
                await msgBoxError.ShowDialog(this);
                return;
            }*/

            if (String.IsNullOrWhiteSpace(AppSettings.Settings.FFmpegPath))
            {
                var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "The ffmpeg directory setting is blank");
                await msgBoxError.ShowDialog(this);
            }

            _IsQueueRunning = true;

            AddToQueueBtn.IsEnabled = false;
            StartQueueBtn.IsEnabled = false;
            CopySourceCheck.IsEnabled = false;
            //AutoOverwriteCheck.IsEnabled = false;
            StopQueueBtn.IsEnabled = true;

            string response = String.Empty;
            foreach (ListViewData item in ListViewItems)
            {
                CurrentItemInProgress = item;
                item.Description.State = ItemState.Progressing;
                if (item.Description.Type == ItemTask.Copy)
                {
                    Copier = new(new Progress<double>(x => item.Progress = x), item, ViewModel);
                    response = await Task.Run(() => Copier.CopyDirectory(item.Description.SourceDir, item.Description.OutputDir));
                }
                else if (item.Description.Type == ItemTask.Transcode)
                {
                    FFmp = new FFmpeg(AppSettings.Settings.FFmpegPath);
                    Trace.TraceInformation(await Task.Run(() => FFmp.GetFrameCountApproximate(
                        dir: item.Description.SourceDir,
                        searchPattern: "*" + item.Description.FileExt,
                        args: item.Description.Profile.Arguments
                    )));
                    response = await Task.Run(() => FFmp.RunProfile(
                        args: item.Description.Profile.Arguments,
                        outputDir: item.Description.OutputDir,
                        ext: item.Description.Profile.OutputExtension,
                        progress: new Progress<double>(x => item.Progress = x),
                        viewModel: ViewModel
                    ));
                }
                else
                {
                    throw new Exception("Internal Item error: ItemTask enum not properly assigned to Type property of Item Description property");
                }
                if (Int32.TryParse(response, out int result) && result == 0)
                {
                    item.Check = true;
                    item.Description.State = ItemState.Complete;
                }
                else if (response != String.Empty)
                {
                    item.Description.State = ItemState.Stopped;
                    var msgBoxCancel = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Queue Canceled", $"Your queue was canceled on file {response}", ButtonEnum.Ok);
                    await msgBoxCancel.ShowDialog(this);
                    goto SKIPDIALOG;
                }
                else
                {
                    throw new Exception($"Internal task response error: string not valid: \"{response}\"");
                }
            }

            var msgBox = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Queue Completed", "Your queue has finished");
            await msgBox.ShowDialog(this);

            SKIPDIALOG:
            CurrentItemInProgress = null;
            FFmp = null; //this will stop the method StopProfile() in MainWindow_Closed() from firing
            Copier = null;
            _IsQueueRunning = false;
            AddToQueueBtn.IsEnabled = true;
            StartQueueBtn.IsEnabled = true;
            CopySourceCheck.IsEnabled = true;
            //AutoOverwriteCheck.IsEnabled = true;
            StopQueueBtn.IsEnabled = false;
        }
        private void StopQueue_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentItemInProgress!.Description.Type == ItemTask.Transcode)
            {
                if (FFmp is not null)
                {
                    FFmp.StopProfile();
                }
                else
                {
                    var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "There does not appear to be an FFmpeg instance running", ButtonEnum.Ok);
                    msgBoxError.ShowDialog(this);
                }
            }
            else if (CurrentItemInProgress!.Description.Type == ItemTask.Copy)
            {
                if (Copier is not null)
                {
                    //Copier.CancelFlag = true;
                    Copier.Stop();
                }
                else
                {
                    var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "There does not appear to be an copier instance running", ButtonEnum.Ok);
                    msgBoxError.ShowDialog(this);
                }
            }
            else
            {
                if (FFmp is null && Copier is null)
                {
                    var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "There does not appear to be a queue in progress", ButtonEnum.Ok);
                    msgBoxError.ShowDialog(this);
                }
                else
                {
                    FFmp?.StopProfile();
                    Copier?.Stop();
                    var msgBoxError = MessageBox.Avalonia.MessageBoxManager.GetMessageBoxStandardWindow("Error", "There do be was a problem plz tell how?!?!?", ButtonEnum.Ok);
                    msgBoxError.ShowDialog(this);
                }
            }
        }
    }
}

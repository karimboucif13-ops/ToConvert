using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Application = System.Windows.Application;
using ColorDialog = System.Windows.Forms.ColorDialog;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using FormsColor = System.Drawing.Color;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace ToConvert;

public partial class MainWindow : Window
{
    private string? _selectedFile;
    private List<string> _selectedFiles = [];
    private readonly MediaPlayer _musicPlayer = new();
    private string? _currentMusicFile;
    private bool _isMusicPlaying;
    private bool _autoPlayOnOpen;
    private bool _isConverting;
    private bool _isCheckingForUpdates;
    private bool _isApplyingUpdate;
    private Window? _licenseWindow;
    private static readonly string BackgroundColorPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreeGameX", "Converter", "background-color.txt");

    private static readonly Dictionary<string, string> ImageFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "PNG",
        [".jpg"] = "JPEG",
        [".jpeg"] = "JPEG",
        [".bmp"] = "BMP",
        [".gif"] = "GIF",
        [".tif"] = "TIFF",
        [".tiff"] = "TIFF"
    };

    private static readonly Dictionary<string, string> TextFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "JSON",
        [".xml"] = "XML",
        [".csv"] = "CSV",
        [".txt"] = "Plain text",
        [".md"] = "Markdown",
        [".html"] = "HTML"
    };

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {GitHubUpdateService.CurrentVersion.ToString(3)}";
        LoadSavedBackgroundColor();
        _musicPlayer.Volume = MusicVolumeSlider.Value;
        _musicPlayer.MediaFailed += MusicPlayer_MediaFailed;
        _musicPlayer.MediaEnded += MusicPlayer_MediaEnded;
        _musicPlayer.MediaOpened += MusicPlayer_MediaOpened;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
        LoadBundledMusic();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var updateError = GitHubUpdateService.ConsumeUpdateError();
        if (!string.IsNullOrWhiteSpace(updateError))
        {
            MessageBox.Show(this,
                $"The update could not be applied. The previous version was restored.\n\n{updateError}",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        await CheckForUpdatesAsync(isManual: false);
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(isManual: true);
    }

    private async Task CheckForUpdatesAsync(bool isManual)
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        if (_isConverting)
        {
            UpdateStatusText.Text = "Wait until the current operation finishes before updating.";
            return;
        }

        _isCheckingForUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "Checking...";
        UpdateStatusText.Text = "Checking GitHub for updates...";
        try
        {
            var release = await GitHubUpdateService.GetLatestReleaseAsync();
            if (release is null)
            {
                UpdateStatusText.Text = "No GitHub release is published yet.";
                if (isManual)
                {
                    MessageBox.Show(this,
                        "There are no published releases in the configured GitHub repository yet.",
                        "No release available", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            if (_isConverting)
            {
                UpdateStatusText.Text = "Wait until the current operation finishes before installing updates.";
                return;
            }

            var currentVersion = GitHubUpdateService.CurrentVersion;
            if (release.Version.CompareTo(currentVersion) <= 0)
            {
                UpdateStatusText.Text = $"You're up to date (v{currentVersion.ToString(3)}).";
                if (isManual)
                {
                    MessageBox.Show(this, "You're using the latest available version.",
                        "No update available", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            UpdateStatusText.Text = $"Update v{release.Version.ToString(3)} is available.";
            var notes = string.IsNullOrWhiteSpace(release.Notes)
                ? "No release notes were provided."
                : release.Notes.Trim();
            if (notes.Length > 1600)
            {
                notes = notes[..1600] + "...";
            }

            var result = MessageBox.Show(this,
                $"Version {release.Version.ToString(3)} is available. Install it and restart now?\n\n{notes}",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result != MessageBoxResult.Yes)
            {
                UpdateStatusText.Text = $"Update v{release.Version.ToString(3)} is ready when you are.";
                return;
            }

            _isApplyingUpdate = true;
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesButton.Content = "Downloading...";
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The running app's executable path could not be determined.");
            GitHubUpdateService.EnsureInstallationIsWritable(executablePath);
            UpdateStatusText.Text = $"Downloading v{release.Version.ToString(3)}...";
            var downloadPath = await GitHubUpdateService.DownloadAndVerifyAsync(release);
            GitHubUpdateService.StartUpdater(downloadPath);
            MessageBox.Show(this, "The verified update is ready. The app will close and restart.",
                "Applying update", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Could not check or install updates. Select Check for updates to retry.";
            if (isManual)
            {
                MessageBox.Show(this, $"The update operation failed:\n{ex.Message}",
                    "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isCheckingForUpdates = false;
            if (!Application.Current.Dispatcher.HasShutdownStarted)
            {
                _isApplyingUpdate = false;
                CheckUpdatesButton.IsEnabled = true;
                CheckUpdatesButton.Content = "Check for updates  →";
            }
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select files to convert",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            SetSelectedFiles(dialog.FileNames);
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        SetDropZoneHighlighted(e.Data.GetDataPresent(DataFormats.FileDrop));
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        SetDropZoneHighlighted(false);
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        SetDropZoneHighlighted(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            SetSelectedFiles(files);
        }
    }

    private void SetDropZoneHighlighted(bool isHighlighted)
    {
        DropZone.Background = new SolidColorBrush(isHighlighted
            ? Color.FromRgb(239, 238, 255)
            : Color.FromRgb(248, 248, 255));
        DropZone.BorderBrush = new SolidColorBrush(isHighlighted
            ? Color.FromRgb(101, 88, 245)
            : Color.FromRgb(207, 203, 255));
        DropZone.BorderThickness = new Thickness(isHighlighted ? 2.5 : 1.5);
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        _selectedFile = null;
        _selectedFiles.Clear();
        SelectedFileCard.Visibility = Visibility.Collapsed;
        SourceFormatCombo.ItemsSource = null;
        TargetFormatCombo.ItemsSource = null;
        FileDetailsText.ToolTip = null;
        ConvertButton.IsEnabled = false;
        CreateZipButton.Visibility = Visibility.Collapsed;
        ExtractZipButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "Choose a file to get started.";
    }

    private void SetSelectedFiles(IEnumerable<string> paths)
    {
        if (_isConverting || _isApplyingUpdate)
        {
            StatusText.Text = "Please wait for the current operation to finish.";
            return;
        }

        var candidates = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = candidates.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            MessageBox.Show(this, "Select one or more files. Folders cannot be converted directly.",
                "No files selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (files.Length != candidates.Length)
        {
            MessageBox.Show(this, "Folders and unavailable files were skipped. Only files can be processed.",
                "Some items were skipped", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _selectedFiles = files.ToList();
        SetSelectedFile(files[0]);
        CreateZipButton.Visibility = Visibility.Visible;
        var canExtractZip = files.Length == 1 &&
                            Path.GetExtension(files[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase);
        ExtractZipButton.Visibility = canExtractZip ? Visibility.Visible : Visibility.Collapsed;

        if (files.Length == 1)
        {
            return;
        }

        var extensions = files.Select(path => Path.GetExtension(path) ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        FileNameText.Text = $"{files.Length} files selected";
        FileDetailsText.Text = $"{FormatFileSize(files.Sum(path => new FileInfo(path).Length))}  ·  " +
                               string.Join(", ", extensions.Select(extension => extension.ToUpperInvariant()));
        FileDetailsText.ToolTip = string.Join(Environment.NewLine, files);

        if (extensions.Length > 1)
        {
            SourceFormatCombo.ItemsSource = new[]
            {
                new FormatOption($"{files.Length} mixed-format files", string.Empty)
            };
            SourceFormatCombo.SelectedIndex = 0;
            TargetFormatCombo.ItemsSource = null;
            TargetFormatCombo.IsEnabled = false;
            ConvertButton.IsEnabled = false;
            ConvertButton.Content = "Convert all files   →";
            StatusText.Text = "Batch conversion needs files with the same format. Create ZIP can combine mixed files.";
            return;
        }

        if (TargetFormatCombo.Items.Count == 0 || TargetFormatCombo.SelectedItem is not FormatOption)
        {
            TargetFormatCombo.IsEnabled = false;
            ConvertButton.IsEnabled = false;
            ConvertButton.Content = "Convert all files   →";
            StatusText.Text = $"No conversion is available for {extensions[0].ToUpperInvariant()} files.";
            return;
        }

        TargetFormatCombo.IsEnabled = true;
        ConvertButton.Content = "Convert all files   →";
        ConvertButton.IsEnabled = true;
        StatusText.Text = $"{files.Length} files selected. The same output format will be used for every file.";
    }

    private void SetSelectedFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "The selected file could not be found.", "File not found",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedFile = path;
        TargetFormatCombo.IsEnabled = true;
        ConvertButton.Content = "Convert file   →";
        var fileInfo = new FileInfo(path);
        var extension = fileInfo.Extension.ToLowerInvariant();
        FileNameText.Text = fileInfo.Name;
        FileDetailsText.Text = $"{FormatFileSize(fileInfo.Length)}  ·  {extension}";
        FileDetailsText.ToolTip = path;
        SelectedFileCard.Visibility = Visibility.Visible;

        if (ImageFormats.TryGetValue(extension, out var imageFormat))
        {
            var source = new FormatOption($"{imageFormat}  ({extension})", extension);
            SourceFormatCombo.ItemsSource = new[] { source };
            SourceFormatCombo.SelectedIndex = 0;
            var targets = new[]
            {
                new FormatOption("PNG  (.png)", ".png"),
                new FormatOption("JPEG  (.jpg)", ".jpg"),
                new FormatOption("BMP  (.bmp)", ".bmp"),
                new FormatOption("GIF  (.gif)", ".gif"),
                new FormatOption("TIFF  (.tiff)", ".tiff")
            };
            TargetFormatCombo.ItemsSource = FfmpegConverter.IsAvailable
                ? targets.Concat(FfmpegConverter.ExtendedImageFormats).ToArray()
                : targets;
            TargetFormatCombo.SelectedValue = NormalizeImageExtension(extension);
            ConvertButton.IsEnabled = true;
            StatusText.Text = "FFmpeg handles extended formats. Unsupported animation is reduced to the first frame.";
            return;
        }

        if (FfmpegConverter.IsExtendedImageExtension(extension))
        {
            var source = new FormatOption($"Image  ({extension})", extension);
            SourceFormatCombo.ItemsSource = new[] { source };
            SourceFormatCombo.SelectedIndex = 0;
            TargetFormatCombo.ItemsSource = new[]
            {
                new FormatOption("PNG  (.png)", ".png"),
                new FormatOption("JPEG  (.jpg)", ".jpg"),
                new FormatOption("BMP  (.bmp)", ".bmp"),
                new FormatOption("GIF  (.gif)", ".gif"),
                new FormatOption("TIFF  (.tiff)", ".tiff")
            }.Concat(FfmpegConverter.ExtendedImageFormats).ToArray();
            TargetFormatCombo.SelectedValue = extension;
            ConvertButton.IsEnabled = FfmpegConverter.IsAvailable;
            StatusText.Text = FfmpegConverter.IsAvailable
                ? "Choose an output image format."
                : "The extended image conversion engine is unavailable.";
            return;
        }

        if (FfmpegConverter.IsAudioExtension(extension) || FfmpegConverter.IsVideoExtension(extension))
        {
            var mediaType = FfmpegConverter.IsAudioExtension(extension) ? "Audio" : "Video";
            SourceFormatCombo.ItemsSource = new[] { new FormatOption($"{mediaType}  ({extension})", extension) };
            SourceFormatCombo.SelectedIndex = 0;
            var targets = FfmpegConverter.IsAudioExtension(extension)
                ? FfmpegConverter.AudioFormats
                : FfmpegConverter.AudioFormats.Concat(FfmpegConverter.VideoFormats).ToArray();
            TargetFormatCombo.ItemsSource = targets;
            TargetFormatCombo.SelectedValue = extension;
            ConvertButton.IsEnabled = FfmpegConverter.IsAvailable;
            StatusText.Text = FfmpegConverter.IsAvailable
                ? "Audio and video are converted locally with FFmpeg."
                : "FFmpeg is unavailable. Reinstall the app to restore its bundled tools.";
            return;
        }

        if (extension == ".docx")
        {
            SourceFormatCombo.ItemsSource = new[] { new FormatOption("Word Document (.docx)", extension) };
            SourceFormatCombo.SelectedIndex = 0;
            TargetFormatCombo.ItemsSource = new[]
            {
                new FormatOption("Plain Text (.txt)", ".txt"),
                new FormatOption("Rich Text (.rtf)", ".rtf")
            };
            TargetFormatCombo.SelectedValue = ".txt";
            ConvertButton.IsEnabled = true;
            StatusText.Text = "Text extraction keeps paragraph breaks; images and page layout are not preserved.";
            return;
        }

        FormatOption[] archiveTargets = extension switch
        {
            ".zip" => new[]
            {
                new FormatOption("TAR Archive (.tar)", ".tar")
            },
            ".tar" => new[]
            {
                new FormatOption("ZIP Archive (.zip)", ".zip"),
                new FormatOption("GZIP Compressed TAR (.gz)", ".gz")
            },
            ".gz" => new[]
            {
                new FormatOption("TAR Archive (.tar)", ".tar")
            },
            _ => Array.Empty<FormatOption>()
        };
        if (archiveTargets.Length > 0)
        {
            SourceFormatCombo.ItemsSource = new[]
            {
                new FormatOption($"{extension[1..].ToUpperInvariant()} Archive ({extension})", extension)
            };
            SourceFormatCombo.SelectedIndex = 0;
            TargetFormatCombo.ItemsSource = archiveTargets;
            TargetFormatCombo.SelectedIndex = 0;
            ConvertButton.IsEnabled = true;
            StatusText.Text = "ZIP, TAR, and GZIP conversions are supported. RAR and 7Z need external tools.";
            return;
        }

        if (TextFormats.TryGetValue(extension, out var textFormat))
        {
            var source = new FormatOption($"{textFormat}  ({extension})", extension);
            SourceFormatCombo.ItemsSource = new[] { source };
            SourceFormatCombo.SelectedIndex = 0;
            var targets = GetTextTargets(extension);
            TargetFormatCombo.ItemsSource = targets;
            TargetFormatCombo.SelectedValue = extension;
            ConvertButton.IsEnabled = targets.Length > 1;
            StatusText.Text = targets.Length > 1
                ? "Choose the output format."
                : "This file can be read, but no alternate output format is available.";
            return;
        }

        SourceFormatCombo.ItemsSource = new[] { new FormatOption("Unsupported format", extension) };
        SourceFormatCombo.SelectedIndex = 0;
        TargetFormatCombo.ItemsSource = null;
        ConvertButton.IsEnabled = false;
        StatusText.Text = "This format is not supported. See the supported formats in the sidebar.";
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingUpdate || _selectedFiles.Count == 0 ||
            TargetFormatCombo.SelectedItem is not FormatOption target)
        {
            return;
        }

        var files = _selectedFiles.ToArray();
        var extension = target.Extension;
        string? singleOutput = null;
        string outputFolder;
        if (files.Length == 1)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save the converted file",
                Filter = $"{target.Label}|*{extension}|All files (*.*)|*.*",
                DefaultExt = extension,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"{Path.GetFileNameWithoutExtension(files[0])}-converted{extension}",
                FilterIndex = 1
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            singleOutput = dialog.FileName;
            outputFolder = Path.GetDirectoryName(singleOutput)!;
            if (string.Equals(Path.GetFullPath(files[0]), Path.GetFullPath(singleOutput),
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Choose a different path so the original file is not overwritten.",
                    "Same file selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose a folder for the converted files",
                SelectedPath = Path.GetDirectoryName(files[0]) ?? Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments),
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() != FormsDialogResult.OK)
            {
                return;
            }

            outputFolder = dialog.SelectedPath;
        }

        _isConverting = true;
        ConvertButton.IsEnabled = false;
        TargetFormatCombo.IsEnabled = false;
        CreateZipButton.IsEnabled = false;
        ExtractZipButton.IsEnabled = false;
        BatchProgressBar.Visibility = Visibility.Visible;
        BatchProgressBar.IsIndeterminate = files.Length == 1;
        BatchProgressBar.Minimum = 0;
        BatchProgressBar.Maximum = files.Length;
        BatchProgressBar.Value = 0;

        try
        {
            var convertedCount = 0;
            var failures = new List<string>();
            var sourcePaths = new HashSet<string>(
                files.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < files.Length; index++)
            {
                var source = files[index];
                var output = files.Length == 1
                    ? singleOutput!
                    : GetBatchOutputPath(source, outputFolder, extension, sourcePaths);
                ConvertButton.Content = files.Length == 1
                    ? "Converting..."
                    : $"Converting {index + 1} of {files.Length}...";
                StatusText.Text = $"Converting {Path.GetFileName(source)} ({index + 1}/{files.Length})...";

                try
                {
                    await Task.Run(() => ConvertFile(source, output, extension));
                    convertedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(source)}: {ex.Message}");
                }

                BatchProgressBar.Value = index + 1;
            }

            if (failures.Count == 0)
            {
                StatusText.Text = files.Length == 1
                    ? $"Conversion complete: {Path.GetFileName(singleOutput)}"
                    : $"Converted all {convertedCount} files to {outputFolder}.";
                MessageBox.Show(this,
                    files.Length == 1
                        ? $"Your file was converted successfully.\n\n{singleOutput}"
                        : $"All {convertedCount} files were converted successfully.\n\n{outputFolder}",
                    "Conversion complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusText.Text = $"Converted {convertedCount} of {files.Length} files; {failures.Count} failed.";
                var failureDetails = string.Join(Environment.NewLine, failures.Take(5));
                if (failures.Count > 5)
                {
                    failureDetails += $"{Environment.NewLine}...and {failures.Count - 5} more.";
                }

                MessageBox.Show(this,
                    $"Converted {convertedCount} of {files.Length} files. Failed files:\n\n{failureDetails}",
                    "Some conversions failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _isConverting = false;
            BatchProgressBar.Visibility = Visibility.Collapsed;
            BatchProgressBar.IsIndeterminate = false;
            ConvertButton.Content = files.Length > 1 ? "Convert all files   →" : "Convert file   →";
            TargetFormatCombo.IsEnabled = _selectedFiles.Count > 0 &&
                                         TargetFormatCombo.Items.Count > 0 &&
                                         (_selectedFiles.Count == 1 ||
                                          _selectedFiles.Select(Path.GetExtension)
                                              .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1);
            ConvertButton.IsEnabled = _selectedFiles.Count > 0 &&
                                      TargetFormatCombo.IsEnabled &&
                                      TargetFormatCombo.SelectedItem is FormatOption;
            CreateZipButton.IsEnabled = _selectedFiles.Count > 0;
            ExtractZipButton.IsEnabled = _selectedFiles.Count == 1 &&
                                         Path.GetExtension(_selectedFiles[0]).Equals(
                                             ".zip", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void ConvertFile(string source, string destination, string extension)
    {
        if (FfmpegConverter.CanConvert(Path.GetExtension(source), extension))
        {
            FfmpegConverter.Convert(source, destination, extension);
        }
        else
        {
            FileConverter.Convert(source, destination, extension);
        }
    }

    private static string GetBatchOutputPath(
        string source,
        string outputFolder,
        string extension,
        HashSet<string> sourcePaths)
    {
        var stem = $"{Path.GetFileNameWithoutExtension(source)}-converted";
        var candidate = Path.Combine(outputFolder, stem + extension);
        var suffix = 2;
        while (File.Exists(candidate) || sourcePaths.Contains(Path.GetFullPath(candidate)))
        {
            candidate = Path.Combine(outputFolder, $"{stem} ({suffix++}){extension}");
        }

        return candidate;
    }

    private async void CreateZip_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFiles.Count == 0 || _isConverting || _isApplyingUpdate)
        {
            return;
        }

        var sources = _selectedFiles.ToArray();
        var dialog = new SaveFileDialog
        {
            Title = "Create a ZIP archive",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"{Path.GetFileNameWithoutExtension(sources[0])}-archive.zip"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var output = Path.GetFullPath(dialog.FileName);
        if (sources.Select(Path.GetFullPath).Contains(output, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The ZIP archive cannot replace one of the selected files.",
                "Choose a different path", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetOperationBusy(true);
        BatchProgressBar.IsIndeterminate = true;
        BatchProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = $"Creating a ZIP archive from {sources.Length} file(s)...";
        try
        {
            await Task.Run(() => CreateZipArchive(sources, output));
            StatusText.Text = $"ZIP archive created: {Path.GetFileName(output)}";
            MessageBox.Show(this, $"The ZIP archive was created successfully.\n\n{output}",
                "ZIP created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "The ZIP archive could not be created.";
            MessageBox.Show(this, $"An error occurred while creating the ZIP archive:\n{ex.Message}",
                "ZIP creation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BatchProgressBar.Visibility = Visibility.Collapsed;
            BatchProgressBar.IsIndeterminate = false;
            SetOperationBusy(false);
        }
    }

    private async void ExtractZip_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFiles.Count != 1 || _isConverting || _isApplyingUpdate)
        {
            return;
        }

        var archivePath = _selectedFiles[0];
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a destination folder for the extracted files",
            SelectedPath = Path.GetDirectoryName(archivePath) ?? Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != FormsDialogResult.OK)
        {
            return;
        }

        var outputFolder = Path.Combine(dialog.SelectedPath, Path.GetFileNameWithoutExtension(archivePath));
        if (Directory.Exists(outputFolder))
        {
            MessageBox.Show(this, "A folder with this archive's name already exists. Choose another destination.",
                "Destination already exists", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetOperationBusy(true);
        BatchProgressBar.IsIndeterminate = true;
        BatchProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = $"Extracting {Path.GetFileName(archivePath)}...";
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, outputFolder, overwriteFiles: false));
            StatusText.Text = $"ZIP extracted to {outputFolder}.";
            MessageBox.Show(this, $"The archive was extracted successfully.\n\n{outputFolder}",
                "ZIP extracted", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "The ZIP archive could not be extracted.";
            MessageBox.Show(this, $"An error occurred while extracting the ZIP archive:\n{ex.Message}",
                "ZIP extraction failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BatchProgressBar.Visibility = Visibility.Collapsed;
            BatchProgressBar.IsIndeterminate = false;
            SetOperationBusy(false);
        }
    }

    private static void CreateZipArchive(IEnumerable<string> sourceFiles, string destinationPath)
    {
        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceFiles)
        {
            var fileName = Path.GetFileName(source);
            var extension = Path.GetExtension(fileName);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var entryName = fileName;
            var suffix = 2;
            while (!entryNames.Add(entryName))
            {
                entryName = $"{stem} ({suffix++}){extension}";
            }

            archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
        }
    }

    private void SetOperationBusy(bool isBusy)
    {
        _isConverting = isBusy;
        TargetFormatCombo.IsEnabled = !isBusy && _selectedFiles.Count > 0 &&
                                     TargetFormatCombo.Items.Count > 0 &&
                                     (_selectedFiles.Count == 1 ||
                                      _selectedFiles.Select(Path.GetExtension)
                                          .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1);
        ConvertButton.IsEnabled = !isBusy && _selectedFiles.Count > 0 &&
                                  TargetFormatCombo.IsEnabled &&
                                  TargetFormatCombo.SelectedItem is FormatOption;
        CreateZipButton.IsEnabled = !isBusy && _selectedFiles.Count > 0;
        ExtractZipButton.IsEnabled = !isBusy && _selectedFiles.Count == 1 &&
                                     Path.GetExtension(_selectedFiles[0]).Equals(
                                         ".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static FormatOption[] GetTextTargets(string sourceExtension)
    {
        var extensions = sourceExtension.ToLowerInvariant() switch
        {
            ".json" => new[] { ".json", ".xml", ".csv" },
            ".xml" => new[] { ".xml", ".json" },
            ".csv" => new[] { ".csv", ".json" },
            ".txt" => new[] { ".txt", ".md", ".html" },
            ".md" => new[] { ".md", ".txt", ".html" },
            ".html" => new[] { ".html", ".txt" },
            _ => Array.Empty<string>()
        };

        return extensions.Select(extension => new FormatOption(
            $"{TextFormats[extension]}  ({extension})", extension)).ToArray();
    }

    private static string NormalizeImageExtension(string extension) =>
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" :
        extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ? ".tiff" : extension;

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024d):0.#} MB"
    };

    private void LoadBundledMusic()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreeGameX", "Converter");
        Directory.CreateDirectory(directory);
        _currentMusicFile = Path.Combine(directory, "music.mp3");

        var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("ToConvert.Assets.music.mp3")
            ?? throw new FileNotFoundException("The bundled music track is missing.");
        using (resource)
        {
            using var output = File.Create(_currentMusicFile);
            resource.CopyTo(output);
        }

        _autoPlayOnOpen = true;
        _musicPlayer.Open(new Uri(_currentMusicFile));
        MusicPlayerStatus.Text = "Loading soundtrack...";
    }

    private void MusicToggle_Click(object sender, RoutedEventArgs e)
    {
        _autoPlayOnOpen = false;
        if (_isMusicPlaying)
        {
            _musicPlayer.Pause();
            _isMusicPlaying = false;
            MusicPlayerButton.Content = "▶  Play";
            MusicPlayerStatus.Text = "Paused";
            return;
        }

        _musicPlayer.Play();
        _isMusicPlaying = true;
        MusicPlayerButton.Content = "Ⅱ  Pause";
        MusicPlayerStatus.Text = Path.GetFileNameWithoutExtension(_currentMusicFile);
    }

    private void MusicStop_Click(object sender, RoutedEventArgs e)
    {
        _autoPlayOnOpen = false;
        _musicPlayer.Stop();
        _isMusicPlaying = false;
        MusicPlayerButton.Content = "▶  Play";
        MusicPlayerStatus.Text = "Stopped";
    }

    private void ChooseMusic_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a music track",
            Filter = "Audio files|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac;*.ogg|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _autoPlayOnOpen = false;
        _currentMusicFile = dialog.FileName;
        _musicPlayer.Open(new Uri(_currentMusicFile));
        _musicPlayer.Play();
        _isMusicPlaying = true;
        MusicPlayerButton.Content = "Ⅱ  Pause";
        MusicPlayerStatus.Text = Path.GetFileNameWithoutExtension(_currentMusicFile);
    }

    private void MusicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _musicPlayer.Volume = e.NewValue;
    }

    private void MusicPlayer_MediaEnded(object? sender, EventArgs e)
    {
        _musicPlayer.Position = TimeSpan.Zero;
        _isMusicPlaying = false;
        MusicPlayerButton.Content = "▶  Play";
        MusicPlayerStatus.Text = "Track finished";
    }

    private void MusicPlayer_MediaOpened(object? sender, EventArgs e)
    {
        if (!_autoPlayOnOpen)
        {
            return;
        }

        _autoPlayOnOpen = false;
        _musicPlayer.Play();
        _isMusicPlaying = true;
        MusicPlayerButton.Content = "Ⅱ  Pause";
        MusicPlayerStatus.Text = "FreeGameX soundtrack";
    }

    private void MusicPlayer_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        _isMusicPlaying = false;
        MusicPlayerButton.Content = "▶  Play";
        MusicPlayerStatus.Text = "Unable to play this track.";
        MessageBox.Show(this, $"The selected track could not be played:\n{e.ErrorException.Message}",
            "Playback failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ViewLicense_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseWindow?.IsVisible == true)
        {
            _licenseWindow.Activate();
            return;
        }

        var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("ToConvert.FFmpeg-LICENSE.txt")
            ?? throw new FileNotFoundException("The bundled FFmpeg license is missing.");
        using var reader = new StreamReader(resource);
        _licenseWindow = new Window
        {
            Owner = this,
            Title = "FFmpeg License",
            Width = 760,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
            Content = new TextBox
            {
                Text = reader.ReadToEnd(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(16),
                BorderThickness = new Thickness(0)
            }
        };
        _licenseWindow.Closed += (_, _) => _licenseWindow = null;
        _licenseWindow.Show();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _musicPlayer.Close();
    }

    private void LoadSavedBackgroundColor()
    {
        if (!File.Exists(BackgroundColorPath))
        {
            return;
        }

        try
        {
            var value = File.ReadAllText(BackgroundColorPath).Trim();
            if (ColorConverter.ConvertFromString(value) is not Color color)
            {
                throw new FormatException("The saved color is not valid.");
            }

            ApplyBackgroundColor(color);
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, $"The saved background color could not be loaded:\n{ex.Message}",
                "Invalid background color", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The saved background color could not be read:\n{ex.Message}",
                "Settings unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChangeBackground_Click(object sender, RoutedEventArgs e)
    {
        var current = Background is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(244, 246, 252);
        using var dialog = new ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            Color = FormsColor.FromArgb(current.R, current.G, current.B)
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var selected = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackgroundColorPath)!);
            File.WriteAllText(BackgroundColorPath, selected.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The background color could not be saved:\n{ex.Message}",
                "Settings could not be saved", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ApplyBackgroundColor(selected);
    }

    private void ApplyBackgroundColor(Color color)
    {
        Background = new SolidColorBrush(color);
        var isLight = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B >= 155;
        var titleColor = isLight ? Color.FromRgb(23, 34, 59) : Colors.White;
        var mutedColor = isLight ? Color.FromRgb(120, 132, 155) : Color.FromRgb(220, 224, 235);
        WelcomeTitle.Foreground = new SolidColorBrush(titleColor);
        WelcomeSubtitle.Foreground = new SolidColorBrush(mutedColor);
        StatusText.Foreground = new SolidColorBrush(mutedColor);
    }

}

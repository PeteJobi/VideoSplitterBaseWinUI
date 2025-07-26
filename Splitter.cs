using DraggerResizer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;
using Windows.UI;
using Orientation = DraggerResizer.Orientation;
using Path = System.IO.Path;

namespace VideoSplitter
{
    public class Splitter<T> where T : SplitRange, new()
    {
        private Canvas canvas;
        private Canvas progressCanvas;
        private StackPanel scenePreviewPanel;
        private FrameworkElement seeker;
        private TimeSpan duration;
        private const double minimumScale = 5;
        private const double linesOffset = 0.5;
        private const double units = 5;
        private const double incrementScaleBy = 0.5;
        private const double scenePreviewPanelHeight = 70;
        private double scale = minimumScale;
        private readonly int[] scaleIncrementCounts = [10, 15, 20];
        private readonly int[] labelIntervals = [4, 2, 1];
        private readonly TimeSpan[] spans =
        [
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(15),

            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(2.5),

            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(30),

            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5),

            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1),
        ];
        //e.g scale is incremented by 0.5 (incrementScaleBy) 10 (scaleIncrementCounts) times while the label intervals is 4 (labelIntervals) * 5 units. Then the label intervals
        //change to 2 and the scale now has to increment 20 times before the label interval changes to 1, after which scale has to be increment by 30 and then it repeats.
        private int currentSpanIndex;
        private int currentLabelPosIndex;
        private DraggerResizer.DraggerResizer dragger;
        private MediaPlayer mediaPlayer;
        private double prevTimelineScale;
        private TimeSpan prevVideoProgress;
        private DispatcherQueue dispatcher;
        private SplitViewModel<T> model;
        private DataTemplate sectionTemplate;
        private string videoPath;
        private double previewImageWidth;
        private CancellationTokenSource previewsTokenSource;
        private Process? ffmpegProcess;
        private string? currentPreviewsFolder;
        private static readonly Color Transparent = Color.FromArgb(0, 255, 255, 255);
        private Action<FrameworkElement, T>? actionForEachSection;
        private Dictionary<T, SplitRangeExtras> splitRangeExtras = new();

        public Splitter(SplitViewModel<T> model, Canvas canvas, MediaPlayer mediaPlayer, DispatcherQueue dispatcher, Action<FrameworkElement, T>? actionForEachSection = null, string? ffmpegPath = null, string? videoPath = null)
        {
            dragger = new DraggerResizer.DraggerResizer();
            sectionTemplate = (DataTemplate)Application.Current.Resources["SectionTemplate"];

            this.canvas = canvas;
            this.mediaPlayer = mediaPlayer;
            this.dispatcher = dispatcher;
            this.model = model;
            this.actionForEachSection = actionForEachSection;
            PlaybackSessionOnNaturalDurationChanged(mediaPlayer.PlaybackSession, null);
            mediaPlayer.PlaybackSession.NaturalDurationChanged += PlaybackSessionOnNaturalDurationChanged;
            mediaPlayer.PlaybackSession.NaturalVideoSizeChanged += PlaybackSessionOnNaturalVideoSizeChanged;
            mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSessionOnPlaybackStateChanged;
            mediaPlayer.PlaybackSession.PositionChanged += (s, e) =>
            {
                dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    PositionSeeker(s.Position);
                    model.VideoProgress = prevVideoProgress = s.Position;
                });
            };
            model.PropertyChanged += ModelOnPropertyChanged;
            if(model.SplitRanges == null) model.SplitRanges = new ObservableCollection<T>();
            model.SplitRanges.CollectionChanged += SplitRangesOnCollectionChanged;

            if (!string.IsNullOrWhiteSpace(ffmpegPath) && !string.IsNullOrWhiteSpace(videoPath))
            {
                ffmpegProcess = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                    },
                    EnableRaisingEvents = true
                };
                this.videoPath = videoPath;
                previewsTokenSource = new CancellationTokenSource();
            }

            seeker = (FrameworkElement)canvas.Children[0];
            canvas.Children.Remove(seeker);
            canvas.Children.Add(new Canvas());
            scenePreviewPanel = new StackPanel
            {
                Height = scenePreviewPanelHeight, 
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Background = new SolidColorBrush(Transparent)
            };
            progressCanvas = new Canvas();
            progressCanvas.Children.Add(scenePreviewPanel);
            progressCanvas.Children.Add(seeker);
            progressCanvas.Tapped += ProgressCanvasOnTapped;
            canvas.Children.Add(progressCanvas);
            seeker.UpdateLayout(); //This sets the ActualWidth of seeker
            dragger.InitDraggerResizer(seeker, [Orientation.Horizontal], 
                new HandlingParameters{ DontChangeZIndex = true, Boundary = Boundary.BoundedAtCenter}, dragged: SeekerDragged);
            dragger.SetElementZIndex(seeker, 100);
            Canvas.SetTop(progressCanvas, 30);
        }

        private double GetInitialScale()
        {
            var availableWidth = (canvas.Parent as ScrollPresenter).ActualWidth;
            var c = 0;
            var unitRanges = scaleIncrementCounts.Select(si =>
            {
                var lastScaleInc = scaleIncrementCounts.Take(c).Sum();
                var first = availableWidth / ((minimumScale + (incrementScaleBy * lastScaleInc)) *
                                 labelIntervals[c] * units);
                var last = availableWidth / ((minimumScale + (incrementScaleBy * (si + lastScaleInc))) * labelIntervals[c] * units);
                c++;
                return (first, last);
            }).ToArray();

            var segments = spans.Length / scaleIncrementCounts.Length;
            var percPerSegment = 1 / (double)segments * 100;
            var percCovered = 0d;
            var cv = new List<(TimeSpan a, TimeSpan b)>();
            for (var i = 0; i < spans.Length; i++)
            {
                var span = spans[i];
                var unitRangesIndex = i % unitRanges.Length;
                var unitRange = unitRanges[unitRangesIndex];
                var spanStart = span * unitRange.first;
                var spanEnd = span * unitRange.last;
                cv.Add((spanStart, spanEnd));
                if (spanStart >= duration && spanEnd <= duration)
                {
                    var incCount = IncCount(unitRangesIndex, spans[i]);
                    var percRatioRemainder = (double)incCount / scaleIncrementCounts.Sum() * percPerSegment;
                    return percCovered + percRatioRemainder;
                }
                if (spanStart <= duration && spanEnd <= duration)
                {
                    return percCovered - 1d / scaleIncrementCounts.Sum() * percPerSegment;
                }

                if (unitRangesIndex == unitRanges.Length - 1) percCovered += percPerSegment;
            }
            throw new Exception("Something went wrong");

            int IncCount(int labelPosIndex, TimeSpan span)
            {
                var spanDifference = TimeSpan.MaxValue;
                var start = scaleIncrementCounts.Take(labelPosIndex).Sum();
                var end = start + scaleIncrementCounts[labelPosIndex];
                var labelInt = labelIntervals[labelPosIndex];
                var result = -1;
                for (var i = start; i <= end; i++)
                {
                    var unit = availableWidth / ((minimumScale + (incrementScaleBy * i)) * labelInt * units);
                    var total = unit * span;
                    if((total - duration).Duration() < spanDifference)
                    {
                        spanDifference = (total - duration).Duration();
                        result = i;
                    }
                }
                return result;
            }
        }

        private void PopulateTimeline()
        {
            var rulerCanvas = (Canvas)canvas.Children[0];
            rulerCanvas.Children.Clear();
            var currentLabelInt = labelIntervals[currentLabelPosIndex];
            var currentSpan = spans[currentSpanIndex];
            var numOfLabels = Math.Ceiling(duration / currentSpan);
            var numOfLines = numOfLabels * currentLabelInt * units;
            canvas.Width = numOfLines * scale + linesOffset + 40;
            var singleSpanWidth = currentLabelInt * units * scale;
            progressCanvas.Width = scenePreviewPanel.Width = duration / currentSpan * singleSpanWidth + linesOffset;
            Debug.WriteLine(scenePreviewPanel.Width);
            progressCanvas.UpdateLayout();
            foreach (var range in model.SplitRanges)
            {
                SetSectorWidthAndPosition(range.Start, range.End, splitRangeExtras[range].Section, false);
            }
            PositionSeeker(mediaPlayer.PlaybackSession.Position);
            for (var i = 1; i <= numOfLines; i++)
            {
                var line = new Line
                {
                    X1 = Math.Round(i * scale) + linesOffset,
                    Y1 = 0,
                    Y2 = i % (units * currentLabelInt) == 0 ? 12 : i % units == 0 ? 7 : 4
                };
                line.X2 = line.X1;
                line.Stroke = (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                rulerCanvas.Children.Add(line);
            }

            for (var i = 1; i <= numOfLabels; i++)
            {
                var pos = i * scale * units * currentLabelInt;
                var textBlock = new TextBlock
                {
                    Text = (i * currentSpan).ToString(),
                    FontSize = 10,
                    Width = 60,
                    HorizontalTextAlignment = TextAlignment.Center
                };
                rulerCanvas.Children.Add(textBlock);
                Canvas.SetTop(textBlock, 10);
                Canvas.SetLeft(textBlock, pos - textBlock.Width / 2);
            }

            if (previewImageWidth > 0)
            {
                _ = SetUpPreviews();
            }
        }

        private void SetScale(double percent)
        {
            var segments = spans.Length / scaleIncrementCounts.Length;
            var percPerSegment = 1 / (double)segments * 100;
            var chosenSegment = (int)(percent / percPerSegment);
            var remainder = percent % percPerSegment;
            var chosenIncrementIndex = -1;
            var howManyIncrements = -1;
            var s = 0;
            var sum = scaleIncrementCounts.Sum();
            var lastPercRatio = 0d;
            for (var i = 0; i < scaleIncrementCounts.Length; i++)
            {
                var scaleIncrement = scaleIncrementCounts[i];
                s += scaleIncrement;
                var incRatio = (double)s / sum * percPerSegment;
                if (remainder > incRatio)
                {
                    lastPercRatio = incRatio;
                    continue;
                }

                chosenIncrementIndex = i;
                var equ = incRatio - lastPercRatio;
                howManyIncrements = (int)((remainder - lastPercRatio) / equ * scaleIncrement);
                break;
            }

            scale = minimumScale + (scaleIncrementCounts.Take(chosenIncrementIndex).Sum() + howManyIncrements) * incrementScaleBy;
            currentLabelPosIndex = chosenIncrementIndex;
            currentSpanIndex = (chosenSegment * scaleIncrementCounts.Length) + chosenIncrementIndex;
            currentSpanIndex = Math.Min(currentSpanIndex, spans.Length - 1);
            PopulateTimeline();
        }

        private void PositionSeeker(double distance) => dragger.PositionElementLeft(seeker, distance - seeker.ActualWidth / 2);
        private void PositionSeeker(TimeSpan position) => dragger.PositionElementLeft(seeker, position / duration * progressCanvas.Width - seeker.ActualWidth / 2);

        private void PositionSeekerAndPlayer(double distance)
        {
            mediaPlayer.PlaybackSession.Position = distance / progressCanvas.Width * duration;
            PositionSeeker(distance);
        }
        private void PositionSeekerAndPlayer(TimeSpan position)
        {
            mediaPlayer.PlaybackSession.Position = position;
            PositionSeeker(position);
        }

        private async Task AnimateSeeker(MediaPlaybackSession session)
        {
            const int frameTime24Fps = 1000 / 24;
            while (session.PlaybackState == MediaPlaybackState.Playing)
            {
                dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    PositionSeeker(session.Position);
                    model.VideoProgress = prevVideoProgress = session.Position;
                    //Debug.WriteLine($"{session.Position}/{duration}/{session.NaturalDuration}");
                });
                await Task.Delay(frameTime24Fps);
            }
        }

        private void PlaybackSessionOnNaturalDurationChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.NaturalDuration == TimeSpan.Zero) return;
            duration = sender.NaturalDuration;
            dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                model.TimelineScaleOf100 = GetInitialScale();
                if (Math.Abs(prevTimelineScale - model.TimelineScaleOf100) > 0.005)
                {
                    prevTimelineScale = model.TimelineScaleOf100;
                    SetScale(model.TimelineScaleOf100);
                }
            });
        }

        private async void PlaybackSessionOnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.PlaybackState == MediaPlaybackState.Playing) await AnimateSeeker(sender);
        }

        private void PlaybackSessionOnNaturalVideoSizeChanged(MediaPlaybackSession sender, object args)
        {
            previewImageWidth = sender.NaturalVideoWidth / (double)sender.NaturalVideoHeight * scenePreviewPanelHeight;
            if (duration == TimeSpan.Zero) return;
            dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () => _ = SetUpPreviews());
        }

        public async Task PlaySection(TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)
        {
            PositionSeekerAndPlayer(start);
            mediaPlayer.Play();
            await Task.Delay(end - start, cancellationToken);
            mediaPlayer.Pause();
        }

        private void ProgressCanvasOnTapped(object sender, TappedRoutedEventArgs e)
        {
            var distance = e.GetPosition(progressCanvas).X;
            PositionSeekerAndPlayer(distance);
            model.VideoProgress = mediaPlayer.Position;
        }

        private void SeekerDragged()
        {
            var distance = dragger.GetElementLeft(seeker) + seeker.ActualWidth / 2;
            model.VideoProgress = prevVideoProgress = mediaPlayer.PlaybackSession.Position = distance / progressCanvas.Width * duration;
        }

        public void SplitSection()
        {
            var position = mediaPlayer.PlaybackSession.Position;
            if (!model.SplitRanges.Any())
            {
                model.SplitRanges.Add(new T { Start = TimeSpan.Zero, End = position });
                model.SplitRanges.Add(new T { Start = position, End = duration });
                return;
            }

            var selectedRange = model.SplitRanges.FirstOrDefault(r => position > r.Start && position < r.End);
            if(selectedRange != null)
            {
                model.SplitRanges.Add(new T { Start = position, End = selectedRange.End });
                selectedRange.End = position;
                return;
            }

            var nearestEndBeforeMark = TimeSpan.Zero;
            foreach (var splitRange in model.SplitRanges)
            {
                if (splitRange.End < position && splitRange.End > nearestEndBeforeMark) nearestEndBeforeMark = splitRange.End;
            }

            model.SplitRanges.Add(new T { Start = nearestEndBeforeMark, End = position });
        }

        public void SplitIntervals(TimeSpan interval)
        {
            model.SplitRanges.Clear();
            var start = TimeSpan.Zero;
            var end = interval;
            while (end < duration)
            {
                model.SplitRanges.Add(new T{ Start = start, End = end });
                start = end;
                end += interval;
            }
            model.SplitRanges.Add(new T { Start = start, End = duration });
        }

        public void JoinSections(params T[] ranges)
        {
            if (ranges.Length < 2) return;

            var firstSelectedRange = ranges.First();
            var lastSelectedRange = ranges.Last();
            var rangesToRemove = ranges.Skip(1);
            foreach (var r in rangesToRemove)
            {
                model.SplitRanges.Remove(r);
            }

            if (firstSelectedRange.End > lastSelectedRange.End) return;
            firstSelectedRange.End = lastSelectedRange.End;
        }

        public Task Dispose() => previewsTokenSource.CancelAsync();

        public void BringSectionHandleToTop(T range)
        {
            if (!splitRangeExtras.TryGetValue(range, out var extras)) return;
            dragger.SetElementZIndexTopmost(extras.Section);
        }

        private void ValidateAndFixRangeSpan(T range)
        {
            if (range.Start > range.End)
            {
                (range.Start, range.End) = (range.End, range.Start);
            }
            if(range.Start < TimeSpan.Zero) range.Start = TimeSpan.Zero;
            if (range.End > duration) range.End = duration;
        }

        private void SortList(IList newAdditions, int index)
        {
            for (var i = 0; i < newAdditions.Count; i++)
            {
                var addition = (T)newAdditions[i]!;
                for (var j = 0; j < model.SplitRanges.Count; j++)
                {
                    var range = model.SplitRanges[j];
                    if (range == addition) continue;
                    if ((addition.Start < range.Start && index + i > j) || (addition.Start > range.Start && index + i < j))
                    {
                        model.SplitRanges.Move(j, index + i);
                        break;
                    }
                }
            }
        }

        private void SplitRangesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e)
            {
                case { Action: NotifyCollectionChangedAction.Reset }:
                {
                    foreach (var extras in splitRangeExtras.Values)
                    {
                        dragger.RemoveElement(extras.Section);
                    }
                    splitRangeExtras.Clear();
                    break;
                }
                case { Action: NotifyCollectionChangedAction.Remove, OldItems: not null }:
                {
                    foreach (T item in e.OldItems)
                    {
                        if(splitRangeExtras.TryGetValue(item, out var extras)) dragger.RemoveElement(extras.Section);
                    }
                    break;
                }
                case { Action: NotifyCollectionChangedAction.Add, NewItems: not null }:
                {
                    foreach (T item in e.NewItems)
                    {
                        ValidateAndFixRangeSpan(item);
                        CreateNewRange(item);
                        item.PropertyChanged += (o, args) =>
                        {
                            if (args.PropertyName is not (nameof(SplitRange.Start) or nameof(SplitRange.End))) return;
                            ValidateAndFixRangeSpan(item);
                            SortList(new List<T>{item}, model.SplitRanges.IndexOf(item));
                            var extras = splitRangeExtras[item];
                            SetSectorWidthAndPosition(item.Start, item.End, extras.Section, false, true);
                        };
                    }
                    Task.Run(async () =>
                    {
                        dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () => SortList(e.NewItems, e.NewStartingIndex));
                    });

                    break;
                }
            }
        }

        private void ModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SplitViewModel<T>.TimelineScaleOf100):
                    if (Math.Abs(prevTimelineScale - model.TimelineScaleOf100) > 0.005)
                    {
                        prevTimelineScale = model.TimelineScaleOf100;
                        SetScale(model.TimelineScaleOf100);
                    }
                    break;
                case nameof(SplitViewModel<T>.VideoProgress):
                    if (model.VideoProgress != prevVideoProgress)
                    {
                        prevVideoProgress = model.VideoProgress;
                        PositionSeekerAndPlayer(prevVideoProgress);
                    }
                    break;
            }
        }

        private void CreateNewRange(T range)
        {
            var section = (FrameworkElement)sectionTemplate.LoadContent();
            progressCanvas.Children.Add(section);
            SetSectorWidthAndPosition(range.Start, range.End, section, true);
            section.UpdateLayout();
            //var range = new SplitRange { Start = start, End = end, InMultiSelectMode = model.InMultiSelectMode, CancellationTokenSource = new CancellationTokenSource() };
            //range.InMultiSelectMode = model.InMultiSelectMode;
            section.DataContext = range;
            var orientations = new Dictionary<Orientation, Appearance>
            {
                {
                    Orientation.Horizontal, new Appearance
                    {
                        AtRest = (SolidColorBrush?)TryGetResource("SectionHandleAtRest"),
                        Hover = (SolidColorBrush?)TryGetResource("SectionHandleHover"),
                        Pressed = (SolidColorBrush?)TryGetResource("SectionHandlePressed"),
                        CursorShape = InputSystemCursorShape.Hand
                    }
                },
                {
                    Orientation.Left, new Appearance
                    {
                        AtRest = (SolidColorBrush?)TryGetResource("SectionLeftHandleAtRest"),
                        Hover = (SolidColorBrush?)TryGetResource("SectionLeftHandleHover"),
                        Pressed = (SolidColorBrush?)TryGetResource("SectionLeftHandlePressed"),
                        HandleThickness = 10
                    }
                },
                {
                    Orientation.Right, new Appearance
                    {
                        AtRest = (SolidColorBrush?)TryGetResource("SectionRightHandleAtRest"),
                        Hover = (SolidColorBrush?)TryGetResource("SectionRightHandleHover"),
                        Pressed = (SolidColorBrush?)TryGetResource("SectionRightHandlePressed"),
                        HandleThickness = 10
                    }
                }
            };
            var extras = new SplitRangeExtras
            {
                Section = section
            };
            dragger.InitDraggerResizer(section, orientations, ended: UpdateContext);
            splitRangeExtras.Add(range, extras);
            actionForEachSection?.Invoke(section, range);

            void UpdateContext()
            {
                var left = dragger.GetElementLeft(extras.Section);
                range.SetStartAndEndAtOnce(left / progressCanvas.Width * duration, (left + extras.Section.Width) / progressCanvas.Width * duration);
                //Debug.WriteLine($"{range.Start}   {range.End}    {range.Duration}   {xx},{yy}");
                //Debug.WriteLine($"{a}   {b}    {a == range.Start}   {b == range.End}");
            }
        }

        private async Task SetUpPreviews()
        {
            if (ffmpegProcess == null) return;
            await previewsTokenSource.CancelAsync();
            previewsTokenSource = new CancellationTokenSource();
            currentPreviewsFolder = Path.Join(Path.GetTempPath(), Path.GetRandomFileName()) + "/";
            Directory.CreateDirectory(currentPreviewsFolder);
            scenePreviewPanel.Children.Clear();
            var numOfPreviews = scenePreviewPanel.Width / previewImageWidth;
            var previewInterval = 1 / numOfPreviews * duration;
            var currentTimePoint = TimeSpan.Zero;
            var token = previewsTokenSource.Token;
            var previewsFolder = currentPreviewsFolder;
            for (var i = 0; i < numOfPreviews; i++)
            {
                await SetPreviewImage(currentTimePoint, i, previewsFolder, token);
                if (token.IsCancellationRequested) break;
                currentTimePoint += previewInterval;
            }
            Directory.Delete(previewsFolder, true);
        }

        private async Task SetPreviewImage(TimeSpan previewTimePoint, int index, string outputFolder, CancellationToken token)
        {
            await StartProcess($"-ss {previewTimePoint} -i \"{videoPath}\"  -frames:v 1 -vf scale=w=-1:h={scenePreviewPanelHeight} \"{outputFolder}{index}.png\"", token);
            if (token.IsCancellationRequested) return;
            var image = new Image();
            image.Name = index.ToString();
            image.Source = new BitmapImage(new Uri($"{outputFolder}{index}.png"));
            image.Stretch = Stretch.Uniform;
            scenePreviewPanel.Children.Add(image);
        }

        private void SetSectorWidthAndPosition(TimeSpan start, TimeSpan end, FrameworkElement section, bool isNew, bool bounded = false)
        {
            var left = start / duration * progressCanvas.Width;
            var width = (end - start) / duration * progressCanvas.Width;
            if (isNew)
            {
                Canvas.SetLeft(section, left);
                section.Width = width;
            }
            else
            {
                dragger.PositionElementLeft(section, left, new HandlingParameters{ Boundary = bounded ? Boundary.BoundedAtEdges : Boundary.NoBounds });
                dragger.ResizeElementWidth(section, width, parameters: new HandlingParameters{ Boundary = bounded ? Boundary.BoundedAtEdges : Boundary.NoBounds });
            }
        }

        private object? TryGetResource(string resourceName) => !Application.Current.Resources.TryGetValue(resourceName, out var value) ? null : value;

        private bool once;
        async Task StartProcess(string arguments, CancellationToken token)
        {
            var finished = false;
            token.Register(() =>
            {
                if (finished) return;
                ffmpegProcess.CancelErrorRead();
                ffmpegProcess.CancelOutputRead();
                finished = true;
            });
            ffmpegProcess.StartInfo.Arguments = arguments;
            ffmpegProcess.Start();
            ffmpegProcess.BeginErrorReadLine();
            ffmpegProcess.BeginOutputReadLine();
            try
            {
                await ffmpegProcess.WaitForExitAsync(token);
                if (finished) return;
                ffmpegProcess.CancelErrorRead();
                ffmpegProcess.CancelOutputRead();
                finished = true;
            }
            catch (Exception e) { }
            //ffmpeg.Dispose();
        }
    }

    public class Splitter: Splitter<SplitRange>
    {
        public Splitter(SplitViewModel<SplitRange> model, Canvas canvas, MediaPlayer mediaPlayer, DispatcherQueue dispatcher, Action<FrameworkElement,
            SplitRange>? actionForEachSection = null, string? ffmpegPath = null, string? videoPath = null) 
            : base(model, canvas, mediaPlayer, dispatcher, actionForEachSection, ffmpegPath, videoPath)
        {
        }
    }

    public class SplitViewModel<T>: INotifyPropertyChanged where T : SplitRange, new()
    {
        public ObservableCollection<T> SplitRanges { get; set; }
        private double _timelinescaleof100;
        public double TimelineScaleOf100
        {
            get => _timelinescaleof100;
            set
            {
                if(_timelinescaleof100 == value) return;
                _timelinescaleof100 = value;
                OnPropertyChanged();
            }
        }
        private TimeSpan _videoprogress;
        public TimeSpan VideoProgress
        {
            get => _videoprogress;
            set
            {
                if(_videoprogress == value) return;
                _videoprogress = value;
                OnPropertyChanged();
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class SplitViewModel: SplitViewModel<SplitRange>{}

    public class SplitRange : INotifyPropertyChanged
    {
        private TimeSpan _start;
        public TimeSpan Start
        {
            get => _start;
            set
            {
                if(_start == value) return;
                _start = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Duration));
            }
        }

        private TimeSpan _end;

        public TimeSpan End
        {
            get => _end;
            set
            {
                if(_end == value) return;
                _end = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Duration));
            }
        }

        public string Duration
        {
            get
            {
                var duration = End - Start;
                if (duration.TotalHours > 1) return $"{duration.TotalHours:F1}h";
                if (duration.TotalMinutes > 1) return $"{duration.TotalMinutes:F1}m";
                return $"{duration.TotalSeconds:F1}s";
            }
        }

        public void SetStartAndEndAtOnce(TimeSpan start, TimeSpan end)
        {
            _start = start;
            _end = end;
            OnPropertyChanged(nameof(Start));
            OnPropertyChanged(nameof(End));
            OnPropertyChanged(nameof(Duration));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    class SplitRangeExtras
    {
        public FrameworkElement Section { get; set; }
    }
}

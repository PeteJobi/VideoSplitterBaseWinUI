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
        private readonly Canvas canvas;
        private readonly Canvas progressCanvas;
        private readonly StackPanel scenePreviewPanel;
        private readonly FrameworkElement seeker;
        private TimeSpan duration;
        private const double MinimumScale = 5;
        private const double LinesOffset = 0.5;
        private const double Units = 5;
        private const double IncrementScaleBy = 0.5;
        private const double ScenePreviewPanelHeight = 70;
        private const double SpaceForLines = 30;
        private double scale = MinimumScale;
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
        //change to 2 and the scale now has to increment 20 times before the label interval changes to 1, after which scale has to be incremented by 30 and then it repeats.
        private int currentSpanIndex;
        private int currentLabelPosIndex;
        private readonly DraggerResizer.DraggerResizer dragger;
        private readonly MediaPlayer mediaPlayer;
        private double prevTimelineScale;
        private TimeSpan prevVideoProgress;
        private bool prevIsPlaying;
        private readonly DispatcherQueue dispatcher;
        private readonly SplitViewModel<T> model;
        private readonly DataTemplate sectionTemplate;
        private readonly string? videoPath;
        private double previewImageWidth;
        private CancellationTokenSource previewsTokenSource;
        private readonly Process? ffmpegProcess;
        private string? currentPreviewsFolder;
        private readonly Color Transparent = Color.FromArgb(0, 255, 255, 255);
        private readonly Action<FrameworkElement, T>? actionForEachSection;
        private readonly Dictionary<T, SplitRangeExtras> splitRangeExtras = new();
        private readonly HandlingParameters noBounds = new() { Boundary = Boundary.NoBounds };

        public Splitter(SplitViewModel<T> model, Canvas canvas, MediaPlayer mediaPlayer, Action<FrameworkElement, T>? actionForEachSection = null, string? ffmpegPath = null, string? videoPath = null)
        {
            dragger = new DraggerResizer.DraggerResizer();
            sectionTemplate = (DataTemplate)Application.Current.Resources["SectionTemplate"];

            this.canvas = canvas;
            this.mediaPlayer = mediaPlayer;
            this.model = model;
            this.actionForEachSection = actionForEachSection;
            dispatcher = canvas.DispatcherQueue;
            PlaybackSessionOnNaturalDurationChanged(mediaPlayer.PlaybackSession, null);
            mediaPlayer.PlaybackSession.NaturalDurationChanged += PlaybackSessionOnNaturalDurationChanged;
            mediaPlayer.PlaybackSession.NaturalVideoSizeChanged += PlaybackSessionOnNaturalVideoSizeChanged;
            mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSessionOnPlaybackStateChanged;
            mediaPlayer.PlaybackSession.PositionChanged += PlaybackSessionOnPositionChanged;
            model.PropertyChanged += ModelOnPropertyChanged;
            model.SplitRanges.CollectionChanged += SplitRangesOnCollectionChanged;

            if (!string.IsNullOrWhiteSpace(ffmpegPath) && !string.IsNullOrWhiteSpace(videoPath))
            {
                ffmpegProcess = new Process
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
                Height = ScenePreviewPanelHeight, 
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            };
            progressCanvas = new Canvas
            {
                Background = new SolidColorBrush(Transparent),
                Height = canvas.ActualHeight
            };
            progressCanvas.Children.Add(scenePreviewPanel);
            progressCanvas.Children.Add(seeker);
            progressCanvas.Tapped += ProgressCanvasOnTapped;
            canvas.Children.Add(progressCanvas);
            Canvas.SetTop(scenePreviewPanel, SpaceForLines);
            seeker.UpdateLayout(); //This sets the ActualWidth of seeker
            dragger.InitDraggerResizer(seeker, [Orientation.Horizontal], 
                new HandlingParameters{ DontChangeZIndex = true, Boundary = Boundary.BoundedAtCenter}, new HandlingCallbacks{ Dragging = SeekerDragged });
            dragger.SetElementZIndex(seeker, 100);
        }

        public void SplitSection()
        {
            var position = model.VideoProgress;
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

        public async Task PlaySection(TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)
        {
            PositionSeekerAndPlayer(start);
            model.IsPlaying = true;
            await Task.Delay(end - start, cancellationToken);
            model.IsPlaying = false;
        }

        public void BringSectionHandleToTop(T range)
        {
            if (!splitRangeExtras.TryGetValue(range, out var extras)) return;
            dragger.SetElementZIndexTopmost(extras.Section);
        }

        public Task Dispose() => previewsTokenSource.CancelAsync();

        private double GetInitialScale()
        {
            var availableWidth = (canvas.Parent as ScrollPresenter).ActualWidth;
            var c = 0;
            var unitRanges = scaleIncrementCounts.Select(si =>
            {
                var lastScaleInc = scaleIncrementCounts.Take(c).Sum();
                var first = availableWidth / ((MinimumScale + (IncrementScaleBy * lastScaleInc)) *
                                 labelIntervals[c] * Units);
                var last = availableWidth / ((MinimumScale + (IncrementScaleBy * (si + lastScaleInc))) * labelIntervals[c] * Units);
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
            return percCovered - 1d / scaleIncrementCounts.Sum() * percPerSegment;

            int IncCount(int labelPosIndex, TimeSpan span)
            {
                var spanDifference = TimeSpan.MaxValue;
                var start = scaleIncrementCounts.Take(labelPosIndex).Sum();
                var end = start + scaleIncrementCounts[labelPosIndex];
                var labelInt = labelIntervals[labelPosIndex];
                var result = -1;
                for (var i = start; i <= end; i++)
                {
                    var unit = availableWidth / ((MinimumScale + (IncrementScaleBy * i)) * labelInt * Units);
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
            var numOfLines = numOfLabels * currentLabelInt * Units;
            canvas.Width = numOfLines * scale + LinesOffset + 40;
            var singleSpanWidth = currentLabelInt * Units * scale;
            progressCanvas.Width = scenePreviewPanel.Width = duration / currentSpan * singleSpanWidth + LinesOffset;
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
                    X1 = Math.Round(i * scale) + LinesOffset,
                    Y1 = 0,
                    Y2 = i % (Units * currentLabelInt) == 0 ? 12 : i % Units == 0 ? 7 : 4
                };
                line.X2 = line.X1;
                line.Stroke = (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                rulerCanvas.Children.Add(line);
            }

            for (var i = 1; i <= numOfLabels; i++)
            {
                var pos = i * scale * Units * currentLabelInt;
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

            scale = MinimumScale + (scaleIncrementCounts.Take(chosenIncrementIndex).Sum() + howManyIncrements) * IncrementScaleBy;
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
            dispatcher.TryEnqueue(DispatcherQueuePriority.Normal,
                () => model.IsPlaying = prevIsPlaying = sender.PlaybackState == MediaPlaybackState.Playing);
            if (sender.PlaybackState == MediaPlaybackState.Playing) await AnimateSeeker(sender);
        }

        private void PlaybackSessionOnNaturalVideoSizeChanged(MediaPlaybackSession sender, object args)
        {
            previewImageWidth = sender.NaturalVideoWidth / (double)sender.NaturalVideoHeight * ScenePreviewPanelHeight;
            if (duration == TimeSpan.Zero) return;
            dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () => _ = SetUpPreviews());
        }

        private void PlaybackSessionOnPositionChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.Position == prevVideoProgress) return;
            dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                PositionSeeker(sender.Position);
                model.VideoProgress = prevVideoProgress = sender.Position;
            });
        }

        private void ProgressCanvasOnTapped(object sender, TappedRoutedEventArgs e)
        {
            var distance = e.GetPosition(progressCanvas).X;
            PositionSeekerAndPlayer(distance);
            model.VideoProgress = prevVideoProgress = mediaPlayer.Position;
        }

        private void SeekerDragged()
        {
            var distance = dragger.GetElementLeft(seeker) + seeker.ActualWidth / 2;
            model.VideoProgress = prevVideoProgress = mediaPlayer.PlaybackSession.Position = distance / progressCanvas.Width * duration;
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
                        model.SplitRanges.Move(index + i, j);
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
                            SetSectorWidthAndPosition(item.Start, item.End, extras.Section, false);
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
                case nameof(SplitViewModel<T>.IsPlaying):
                    if (model.IsPlaying != prevIsPlaying)
                    {
                        prevIsPlaying = model.IsPlaying;
                        if (model.IsPlaying)
                        {
                            mediaPlayer.Play();
                        }
                        else
                        {
                            mediaPlayer.Pause();
                        }
                    }
                    break;
            }
        }

        private void CreateNewRange(T range)
        {
            var section = (FrameworkElement)sectionTemplate.LoadContent();
            progressCanvas.Children.Add(section);
            Canvas.SetTop(section, SpaceForLines);
            SetSectorWidthAndPosition(range.Start, range.End, section, true);
            section.UpdateLayout();
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
            dragger.InitDraggerResizer(section, orientations, callbacks: new HandlingCallbacks{ DragCompleted = UpdateContext, ResizeCompleted = _ => UpdateContext() });
            splitRangeExtras.Add(range, extras);
            actionForEachSection?.Invoke(section, range);

            void UpdateContext()
            {
                var left = dragger.GetElementLeft(extras.Section);
                range.SetStartAndEndAtOnce(left / progressCanvas.Width * duration, (left + extras.Section.Width) / progressCanvas.Width * duration);
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
            await DeletePreviewFolder(previewsFolder, token);
        }

        private async Task SetPreviewImage(TimeSpan previewTimePoint, int index, string outputFolder, CancellationToken token)
        {
            await StartProcess($"-ss {previewTimePoint} -i \"{videoPath}\" -frames:v 1 -vf scale=w=-1:h={ScenePreviewPanelHeight} \"{outputFolder}{index}.png\"", token);
            if (token.IsCancellationRequested) return;
            var image = new Image();
            image.Name = index.ToString();
            image.Source = new BitmapImage(new Uri($"{outputFolder}{index}.png"));
            image.Stretch = Stretch.Uniform;
            scenePreviewPanel.Children.Add(image);
        }

        private static async Task DeletePreviewFolder(string previewFolder, CancellationToken token)
        {
            try
            {
                await Task.Delay(500, token);
            }
            catch(TaskCanceledException){}
            finally
            {
                Directory.Delete(previewFolder, true);
            }
        }

        private void SetSectorWidthAndPosition(TimeSpan start, TimeSpan end, FrameworkElement section, bool isNew)
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
                dragger.PositionElementLeft(section, left, noBounds);
                dragger.ResizeElementWidth(section, width, parameters: noBounds);
            }
        }

        private static object? TryGetResource(string resourceName) => !Application.Current.Resources.TryGetValue(resourceName, out var value) ? null : value;

        private async Task StartProcess(string arguments, CancellationToken token)
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
        public Splitter(SplitViewModel model, Canvas canvas, MediaPlayer mediaPlayer, Action<FrameworkElement,
            SplitRange>? actionForEachSection = null, string? ffmpegPath = null, string? videoPath = null) 
            : base(model, canvas, mediaPlayer, actionForEachSection, ffmpegPath, videoPath)
        {
        }
    }

    public class SplitViewModel<T>: INotifyPropertyChanged where T : SplitRange, new()
    {
        public ObservableCollection<T> SplitRanges { get; set; } = [];
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

        private bool _isplaying;
        public bool IsPlaying
        {
            get => _isplaying;
            set
            {
                if (_isplaying == value) return;
                _isplaying = value;
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

    public class SplitViewModel: SplitViewModel<SplitRange>;

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

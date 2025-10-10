using DraggerResizer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Timeline;
using VideoSplitter;
using Windows.Media.Playback;
using Orientation = DraggerResizer.Orientation;

namespace VideoSplitterBase
{
    public class MediaSplitter<T> : MediaTimeline where T : SplitRange, new()
    {
        private readonly DraggerResizer.DraggerResizer dragger;
        private readonly DataTemplate sectionTemplate;
        private readonly SplitViewModel<T> model;
        private readonly Action<FrameworkElement, T>? actionForEachSection;
        private readonly Dictionary<T, SplitRangeExtras> splitRangeExtras = new();
        private readonly HandlingParameters noBounds = new() { Boundary = Boundary.NoBounds };
        private readonly DispatcherQueue dispatcher;

        public MediaSplitter(SplitViewModel<T> model, Canvas canvas, MediaPlayer mediaPlayer, Action<FrameworkElement, T>? actionForEachSection = null, string? ffmpegPath = null,
            string? videoPath = null) : base(model, canvas, mediaPlayer, ffmpegPath, videoPath)
        {
            this.model = model;
            this.actionForEachSection = actionForEachSection;
            dispatcher = canvas.DispatcherQueue;
            dragger = new DraggerResizer.DraggerResizer();
            sectionTemplate = (DataTemplate)Application.Current.Resources["SectionTemplate"];

            model.PropertyChanged += ModelOnPropertyChanged;
            model.SplitRanges.CollectionChanged += SplitRangesOnCollectionChanged;
        }

        public void SplitSection()
        {
            var position = model.Progress;
            if (!model.SplitRanges.Any())
            {
                model.SplitRanges.Add(new T { Start = TimeSpan.Zero, End = position });
                model.SplitRanges.Add(new T { Start = position, End = model.Duration });
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

        public void SplitIntervals(TimeSpan interval, T? range = null)
        {
            var errorMargin = TimeSpan.FromMilliseconds(50); //margin of error to cater for floating point inaccuracies
            var rangeDuration = range?.End - range?.Start ?? model.Duration;
            if (interval >= rangeDuration - errorMargin) return;
            if (range == null) model.SplitRanges.Clear();
            else model.SplitRanges.Remove(range);
            var start = range?.Start ?? TimeSpan.Zero;
            var end = start + interval;
            var dur = range?.End ?? model.Duration;
            while (end < dur - errorMargin)
            {
                model.SplitRanges.Add(new T{ Start = start, End = end });
                start = end;
                end += interval;
            }
            model.SplitRanges.Add(new T { Start = start, End = dur });
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
            model.Progress = start;
            model.IsPlaying = true;
            await Task.Delay(end - start, cancellationToken);
            model.IsPlaying = false;
        }

        public void BringSectionHandleToTop(T range)
        {
            if (!splitRangeExtras.TryGetValue(range, out var extras)) return;
            dragger.SetElementZIndexTopmost(extras.Section);
        }

        private void ModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SplitViewModel<T>.TimelineWidth):
                    foreach (var range in model.SplitRanges)
                    {
                        SetSectorWidthAndPosition(range.Start, range.End, splitRangeExtras[range].Section, false);
                    }
                    break;
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

        private void ValidateAndFixRangeSpan(T range)
        {
            if (range.Start > range.End)
            {
                (range.Start, range.End) = (range.End, range.Start);
            }
            if(range.Start < TimeSpan.Zero) range.Start = TimeSpan.Zero;
            if (range.End > model.Duration) range.End = model.Duration;
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

        private void CreateNewRange(T range)
        {
            var section = (FrameworkElement)sectionTemplate.LoadContent();
            progressCanvas.Children.Add(section);
            Canvas.SetTop(section, MediaTimeline.SpaceForLines);
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
                range.SetStartAndEndAtOnce(left / progressCanvas.Width * model.Duration, (left + extras.Section.Width) / progressCanvas.Width * model.Duration);
            }
        }

        private void SetSectorWidthAndPosition(TimeSpan start, TimeSpan end, FrameworkElement section, bool isNew)
        {
            var left = start / model.Duration * progressCanvas.Width;
            var width = (end - start) / model.Duration * progressCanvas.Width;
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
    }

    public class SplitViewModel<T> : MediaViewModel where T : SplitRange, new()
    {
        public ObservableCollection<T> SplitRanges { get; set; } = [];
    }

    public class SplitViewModel : SplitViewModel<SplitRange>;

    public class SplitRange : INotifyPropertyChanged
    {
        private TimeSpan _start;
        public TimeSpan Start
        {
            get => _start;
            set => SetProperty(ref _start, value, alsoNotify: nameof(Duration));
        }

        private TimeSpan _end;
        public TimeSpan End
        {
            get => _end;
            set => SetProperty(ref _end, value, alsoNotify: nameof(Duration));
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
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null, params string[] alsoNotify)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            foreach (var dep in alsoNotify) OnPropertyChanged(dep);
            return true;
        }
    }

    class SplitRangeExtras
    {
        public FrameworkElement Section { get; set; }
    }
}

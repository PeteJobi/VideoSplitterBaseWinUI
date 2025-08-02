# VideoSplitterBase
This is a single-class WinUI 3 C# library that provides a video timeline interface for users to easily create ranges representing sections of video.

![VideoSplitter 2025-07-30 18-54-28000_CROPPED](https://github.com/user-attachments/assets/92b30578-71d9-4040-9bb3-228ce2f7a6cb)

# How to use
This library depends on [DraggerResizerWinUI](https://github.com/PeteJobi/DraggerResizerWinUI). Include both libraries into your WinUI solution and reference both in your WinUI project. The below shows the minimum code required to use this library: <br>
XAML
```
<MediaPlayerElement Name="VideoPlayer" Source="Assets/path/to/video.mp4"/>
<ScrollView Width="1000" Height="150">
    <Canvas Name="TimelineCanvas" HorizontalAlignment="Left">
        <Rectangle Height="150" Width="20"/>
    </Canvas>
</ScrollView>
```

Code-Behind
```
private SplitViewModel viewModel;
...
viewModel = new SplitViewModel();
VideoPlayer.MediaPlayer.PlaybackSession.NaturalVideoSizeChanged += PlaybackSessionOnNaturalVideoSizeChanged;
...
private void PlaybackSessionOnNaturalDurationChanged(MediaPlaybackSession sender, object args)
{
    var splitter = new Splitter(viewModel, TimelineCanvas, VideoPlayer.MediaPlayer);
}
```
Some things to note:
- The canvas you supply becomes the timeline, and it should contain exactly one child: the element that will be used for seeking, the Playhead. Both the canvas and mediaplayerelement should be loaded before you initialize the splitter object.
- The canvas should have at least a height of 100 if you want preview thumbnails, otherwise, it should be at least 30 (which is the height of the ruler marks).

# API Documentation
- Instance creation: There are two versions of the Splitter class. One's generic and the other isn't. If you wish to extend the view model's class to add properties other than the Start and End times of a video section, use the generic version for that. If the Start and End properties of the view model is enough, use the non-generic version.
  - **Splitter(SplitViewModel model, Canvas canvas, MediaPlayer mediaPlayer, Action<FrameworkElement, SplitRange>? actionForEachSection = null, string? ffmpegPath = null, string? videoPath = null)** <br>
    - The _model_ is a plain **SplitViewModel** object you create, with properties that the Splitter object modifies and can be bound to in your XAML. Read more about **SplitViewModel** class in the [Classes](#classes) section.
    - The _canvas_ is going to contain everything that constitutes the timeline. As mentioned earlier, it should contain exactly one element: the playhead. This element can have its own children.
    - The _mediaPlayer_ is gotten from the MediaPlayerElement. The Splitter uses if for various things like playback control.
    - Specify an _actionForEachSection_, if you want to perform operations on the elements that are created when a range is added to the model. Useful if you want to attach click handlers. This action receives the **FrameworkElement** and **SplitRange** representing the section. Read more about **SplitRange** class in the [Classes](#classes) section.
    - Splitter uses FFMPEG to generate preview thumbnails. If you want those, pass in the path in the _ffmpegPath_ parameter.
    - FFMPEG will need the path to the video to generate preview thumbnails. You'll have to specify _videoPath_ as well if you want those.
  - **Splitter(SplitViewModel<T> model, Canvas canvas, MediaPlayer mediaPlayer, Action<FrameworkElement, T>? actionForEachSection = null, string? ffmpegPath = null, string? videoPath = null)** <br>
    The generic version. _T_ must be a class that extends **SplitRange**. Read more about **SplitRange** class in the [Classes](#classes) section.
- **SplitSection()** <br>
  If there are no existing ranges, callling this method creates 2 ranges split at the current position of the video. Otherwise, if the playhead is within a range, the range is split into two at the video's current positon, else a range is created starting from the end time of the closest range whose end is before the video's current position, and the video's current position itself.
- **void SplitIntervals(TimeSpan interval)** <br>
  Creates as many _interval_-long ranges as can fit into the video and an additional range filling what's left over if any. This will clear any ranges that existed before the call.
- **void JoinSections(params SplitRange[] ranges)** <br>
  Joins the ranges provided into one single range whose Start time corresponds with the Start time of the first specified range and End time corresponds to the End time of the last specified range.
- **async Task PlaySection(TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)** <br>
  Plays the video at the specified _start_ position, and waits for a duration of _end - start_ to pause the video. You can pass a cancellation token with which you can interrupt the wait.
- **void BringSectionHandleToTop(SplitRange range)** <br>
  All range sections lie along the same axis and can overlap each other. Automatically, any section that's clicked or dragged or resized is placed on top of the others, but if you want to programmatically place anyone on top, call this method with the corresponding _range_.
- **Task Dispose()** <br>
  Stops any ongoing processes. Right now, all it does is interrupt preview thumbnails generation.

# Required Application Resources
The library depends on Apllication Resources to allow for some customization of the drag/resize handles of sections, and the section itself.


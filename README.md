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
    - The _model_ is a plain SplitViewModel object you create, with properties that the Splitter object modifies and can be bound to in your XAML. Read more about SplitViewModel class in the [Classes](#classes) section.
    - The _canvas_ is going to contain everything that constitutes the timeline. As mentioned earlier, it should contain exactly one element: the playhead. This element can have its own children.
    - The _mediaPlayer_ is gotten from the MediaPlayerElement. The Splitter uses if for various things like playback control.
    - Specify an _actionForEachSection_, if you want to perform operations on the elements that are created when a range is added to the model. Useful if you want to attach click handlers. This action receives the FrameworkElement and SplitRange representing the section.
    - Splitter uses FFMPEG to generate preview thumbnails. If you want those, pass in the path in the _ffmpegPath_ parameter.
    - FFMPEG will need the path to the video to generate preview thumbnails. You'll have to specify _videoPath_ as well if you want those.


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
viewModel = new SplitViewModel { SplitRanges = new ObservableCollection<SplitRange>() };
VideoPlayer.MediaPlayer.PlaybackSession.NaturalVideoSizeChanged += PlaybackSessionOnNaturalVideoSizeChanged;
...
private void PlaybackSessionOnNaturalDurationChanged(MediaPlaybackSession sender, object args)
{
    var splitter = new Splitter(viewModel, TimelineCanvas, VideoPlayer.MediaPlayer);
}
```
Some things to note:
- The canvas you supply becomes the timeline, and it should contain exactly one child: the element that will be used for seeking. Both the canvas and mediaplayerelement should be loaded before you initialize the splitter object.
- The canvas should have at least a height of 100 if you want preview thumbnails, otherwise, it should be at least 30 (which is the height of the ruler marks).




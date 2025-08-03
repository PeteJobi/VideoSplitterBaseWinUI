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
  - **Splitter(SplitViewModel\<T> model, Canvas canvas, MediaPlayer mediaPlayer, Action<FrameworkElement, T>? actionForEachSection = null, string? ffmpegPath = null, string? videoPath = null)** <br>
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
The library depends on Application Resources to allow for some customization of the drag/resize handles of sections, and the section itself.
- **SectionLeftHandleAtRest**, **SectionLeftHandleHover** and **SectionLeftHandlePressed**: The colours for the left resize handles when they're not interacted with, hovered, or pressed respectively. They're all optional.
- **SectionRightHandleAtRest**, **SectionRightHandleHover** and **SectionRightHandlePressed**: The colours for the right resize handles when they're not interacted with, hovered, or pressed respectively. They're all optional.
- **SectionHandleAtRest**, **SectionHandleHover** and **SectionHandlePressed**: The colours for the drag handles when they're not interacted with, hovered, or pressed respectively. They're all optional. Note that if you do provide colours for the drag handles, the section template will need to have a transparent background for them to be seen.
- **SectionTemplate**: This is the template used to form sections. The datatype of the template should be SplitRange, and you can bind to the Start, End and Duration properties of the Class. If you're using the generic version, the datatype will be the type T. <br>
  Example:
  ```
  //The drag handle will be #44000000 when it's not being interacted with. This colour will also apply when it's being hovered, since SectionHandleHover isn't declared
  <SolidColorBrush Color="#44000000" x:Key="SectionHandleAtRest"></SolidColorBrush>
  
  //The drag handle will be #66000000 when it's being pressed
  <SolidColorBrush Color="#66000000" x:Key="SectionHandlePressed"></SolidColorBrush>
  
  //The left handle will be transparent when it's not being interacted with, since SectionLeftHandleAtRest isn't declared. It's going to be black when hovered and pressed
  <SolidColorBrush Color="Black" x:Key="SectionLeftHandleHover"></SolidColorBrush>
  
  //The right handle will be transparent when it's not being interacted with, since SectionRightHandleAtRest isn't declared. It's going to be black when hovered and pressed
  <SolidColorBrush Color="Black" x:Key="SectionRightHandleHover"></SolidColorBrush>
  
  //The range sections will be made up of a simple border with a stroke, containing a TextBlock that will reflect the duration of the range.
  //The background is transparent so the drag handle colours will show through. The height is 70 so it matches the height of the preview thumbnails
  <DataTemplate x:Key="SectionTemplate" x:DataType="videoSplitter:SplitRange">
     <Border BorderBrush="AntiqueWhite" BorderThickness="3" Height="70" Background="Transparent">
        <TextBlock HorizontalTextAlignment="Center" Text="{x:Bind Duration, Mode=OneWay}" Foreground="Yellow"/>
     </Border>
  </DataTemplate>
  ```

# Classes
- **SplitViewModel<T>**: This is the view model you pass into the Splitter constructor when instantiating it.
  ```
  public class SplitViewModel<T>: INotifyPropertyChanged where T : SplitRange, new()
  {
      public ObservableCollection<T> SplitRanges { get; set; } = [];
      public double TimelineScaleOf100 { get; set; }
      public bool IsPlaying { get; set; }
      public TimeSpan VideoProgress { get; set; }
  }
  ```
  - **SplitRanges**: This is a collection of the ranges created. You can add to this collection to create new ranges (provided they are valid) or remove from the collection to delete. This collection is modified by the Splitter when you call any of the Split or Join functions.
    ```
    var viewModel = new SplitViewModel();
    var splitter = new Splitter(viewModel, TimelineCanvas, VideoPlayer.MediaPlayer);

    //Create a range that starts at 5 seconds and ends at 15. This also creates the section in the timeline
    var newRange = new SplitRange{ Start = TimeSpan.Parse("00:00:05.000"), End = TimeSpan.Parse("00:00:15.000") };
    viewModel.SplitRanges.Add(newRange);

    //Deletes the range and its section
    viewModel.SplitRanges.Remove(newRange);
    ```
  - **TimelineScaleOf100**: This represents a scale of the timeline from 1 to 100, 100 meaning the timeline spans seconds and 1 meaning the timeline spans hours. This property is also modified by the Splitter and the scale that it is initially set to depends on the duration of the video - the Splitter tries to fit the entire span of the video into the full width of the timeline.
    When you change the scale from your end, it is reflected in the timeline. Best way to use this is to bind it to a slider with a maximum value of 100. The user can use this to zoom in and out of the timeline.
    ```
    var viewModel = new SplitViewModel();
    var splitter = new Splitter(viewModel, TimelineCanvas, VideoPlayer.MediaPlayer);
    
    //In XAML
    <Slider Value="{x:Bind viewModel.TimelineScaleOf100, Mode=TwoWay}" Maximum="100"/>
    ```
  - **IsPlaying**: This property reflects the current playback state of the video. If the video is playing, this value is true, otherwise it's false. You can use this property to show a pause or play icon. You can also use it to control playback: set it to true to play the video or set it to false to pause.
    ```
    private void PlayPause(object sender, RoutedEventArgs e)
    {
        viewModel.IsPlaying = !viewModel.IsPlaying;
    }
    
    //In XAML
    <Button Click="PlayPause">
        <FontIcon Glyph="{x:Bind viewModel.IsPlaying, Mode=OneWay, Converter={StaticResource PlayPauseGlyphConverter}}"/>
    </Button>
    ```
  - **VideoProgress**: This represents the current position of the video. The timeline itself acts as a video progress bar, but if you need a simpler progress bar that synchronizes with the timeline's current position, you can bind this property to a slider, using a Timespan-to-Double Converter.
- **SplitViewModel**: The non-generic version.
  ```
  public class SplitViewModel: SplitViewModel<SplitRange>;
  ```
- **SplitRange**: This class represents a range, defined by a Start and End time.
  ```
  public class SplitRange : INotifyPropertyChanged
  {
      public TimeSpan Start { get; set; }
      public TimeSpan End { get; set; }
      public string Duration { get; }
      public void SetStartAndEndAtOnce(TimeSpan start, TimeSpan end);
  }
  ```
  - **Start** and **End**: Self-explanatory. A range is only valid if the Start and End properties are not less than Timespan.Zero and not greater than the duration of the video and the Start is not greater than the End. If an invalid range is added, it will be corrected by the Splitter to be made valid.
    ```
    //Create a range that starts at 15 seconds and ends at 5. Obviously invalid
    var newRange = new SplitRange{ Start = TimeSpan.Parse("00:00:15.000"), End = TimeSpan.Parse("00:00:05.000") };
    viewModel.SplitRanges.Add(newRange);

    //Start and End gets swapped by the Splitter
    var start = newRange.Start; //start is 00:00:05.000
    var end = newRange.End; //end is 00:00:15.000
    ```
  - **Duration**: This returns the difference between the Start and End of the range expressed in total seconds, minutes or hours with a single fixed point.
    ```
    //Continuing from the previous example
    var duration = newRange.Duration; //duration is 10.0s
    ```
  - **void SetStartAndEndAtOnce(TimeSpan start, TimeSpan end)**: Setting either the Start or End properties will trigger a PropertyChanged event and set off some actions that acts on both properties. If you wanted to set both properties, these actions would be called twice, and the first time they're called, one of the properties (the second property to be set) will have an outdated value.
    With this method, you can set both properties, and then trigger the PropertyChanged event for both properties AFTER they're both set.
    ```
    //Continuing from the previous example
    newRange.Start = Timespan.FromSeconds(20); //When PropertyChanged event is called for Start, Start is 00:00:20 and End is 00:00:15. Invalid.
    newRange.End = Timespan.FromSeconds(25); //When PropertyChanged event is called for End, Start is 00:00:20 and End is 00:00:25. As it should be

    newRange.SetStartAndEndAtOnce(Timespan.FromSeconds(30), Timespan.FromSeconds(35)); //When PropertyChanged event is called for Start and End, Start is 00:00:30 and End is 00:00:35
    ```

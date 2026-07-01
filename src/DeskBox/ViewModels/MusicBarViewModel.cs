using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskBox.ViewModels;

public sealed partial class MusicBarViewModel : ObservableObject
{
    private double _height;
    private double _opacity = 1;
    private double _dotSize = 4;

    public MusicBarViewModel(double height)
    {
        _height = height;
    }

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }

    public double DotSize
    {
        get => _dotSize;
        set => SetProperty(ref _dotSize, value);
    }
}

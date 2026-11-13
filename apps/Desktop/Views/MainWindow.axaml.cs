using System;
using Avalonia.Controls;

namespace DuplicateVideoRemover.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // The window opens 1716 wide; on a smaller screen it opens as large as fits (it is centred on the screen)
        if (Screens.Primary is { } screen)
        {
            double scale = screen.Scaling > 0 ? screen.Scaling : 1;
            double room = screen.WorkingArea.Width / scale, roomHigh = screen.WorkingArea.Height / scale;
            Width = Math.Max(MinWidth, Math.Min(Width, room - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, roomHigh - 24));
        }
    }
}

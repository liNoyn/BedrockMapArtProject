using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BMAG.Imaging;

namespace BMAG.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(() => { execute(); return Task.CompletedTask; }, canExecute) { }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public static class BitmapHelper
{
    public static WriteableBitmap ToBitmap(RgbaImage img)
    {
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        for (int y = 0; y < img.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(img.Pixels, y * img.Width * 4, fb.Address + y * fb.RowBytes, img.Width * 4);
        return bmp;
    }
}

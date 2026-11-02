using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LightWeaver.ViewModels;

/// <summary>Hand-rolled INPC base — no MVVM package dependency yet (CommunityToolkit.Mvvm can replace later).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    protected ObservableObject()
        => Localization.LiveText.Observe(this, OnLanguageChanged);

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(null);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

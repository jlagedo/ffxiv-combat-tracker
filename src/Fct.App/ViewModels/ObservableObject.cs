using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Fct.App.ViewModels;

// Minimal INotifyPropertyChanged base for the control panel's view models. The net10
// side deliberately avoids a heavyweight MVVM framework dependency.
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

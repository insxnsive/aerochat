namespace Aerochat.Presentation;

public abstract class ObservableObject : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this,
            new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Notify([System.Runtime.CompilerServices.CallerMemberName]
        string? propertyName = null) => PropertyChanged?.Invoke(this,
            new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ScreenFlow.App.Services;

public enum NoticeKind { Update, Firewall, Network, Info }

/// <summary>Um aviso do sininho (atualização nova, firewall, Radmin...).</summary>
public sealed class Notice(string id, NoticeKind kind, string title, string message, string? actionText = null, Action? action = null) : INotifyPropertyChanged
{
    private bool _isRead;

    public string Id { get; } = id;
    public NoticeKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string? ActionText { get; } = actionText;
    public Action? Action { get; } = action;
    public DateTime Time { get; } = DateTime.Now;
    public bool HasAction => ActionText is not null;

    public string Glyph => Kind switch
    {
        NoticeKind.Update => "",
        NoticeKind.Firewall => "",
        NoticeKind.Network => "",
        _ => "",
    };

    public string TimeText
    {
        get
        {
            var age = DateTime.Now - Time;
            return age.TotalMinutes < 1 ? "agora" : age.TotalHours < 1 ? $"há {(int)age.TotalMinutes} min" : age.TotalDays < 1 ? $"há {(int)age.TotalHours} h" : Time.ToString("dd/MM HH:mm");
        }
    }

    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value) return;
            _isRead = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRead)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Central de avisos mostrada no sininho da barra superior. Usar só na thread da interface.</summary>
public static class Notifications
{
    public static ObservableCollection<Notice> Items { get; } = [];

    public static int Unread => Items.Count(n => !n.IsRead);

    public static event Action? Changed;

    /// <summary>Publica (ou substitui) um aviso com o mesmo Id.</summary>
    public static void Post(Notice notice)
    {
        var existing = Items.FirstOrDefault(n => n.Id == notice.Id);
        if (existing is not null)
        {
            if (existing.Title == notice.Title && existing.Message == notice.Message) return;
            Items.Remove(existing);
        }
        Items.Insert(0, notice);
        Changed?.Invoke();
    }

    public static void Remove(string id)
    {
        var existing = Items.FirstOrDefault(n => n.Id == id);
        if (existing is null) return;
        Items.Remove(existing);
        Changed?.Invoke();
    }

    public static bool Contains(string id) => Items.Any(n => n.Id == id);

    public static void MarkAllRead()
    {
        foreach (var notice in Items) notice.IsRead = true;
        Changed?.Invoke();
    }
}

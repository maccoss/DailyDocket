using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DailyDocket.App.Services;
using DailyDocket.Core.Todos;

namespace DailyDocket.App.ViewModels;

/// <summary>A tag chip, colored by the artifact's palette.</summary>
public sealed record TagView(string Name, int Count, Brush Foreground, Brush Background, bool IsOn)
{
    /// <summary>The filter bar shows how many open tasks carry the tag; a task's own chips show the name alone.</summary>
    public string Label => Count > 0 ? $"{Name} {Count}" : Name;
}

/// <summary>One task in the To do tab, with the fields its edit form uses.</summary>
public sealed partial class TodoView : ObservableObject
{
    public TodoView(TodoItem item, IReadOnlyList<TagView> tags, string due, bool late, string? source)
    {
        Item = item;
        Tags = tags;
        DueText = due;
        Late = late;
        Source = source;
    }

    public TodoItem Item { get; }

    public string Id => Item.Id;

    public string Title => Item.Title;

    public string Notes => Item.Notes;

    public bool Done => Item.Done;

    public IReadOnlyList<TagView> Tags { get; }

    public string DueText { get; }

    public bool Late { get; }

    /// <summary>"From email: subject" when the task was captured from a message.</summary>
    public string? Source { get; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EditNotes { get; set; } = "";

    [ObservableProperty]
    public partial string EditTags { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? EditDue { get; set; }
}

/// <summary>
/// The To do tab: the artifact's list (dragged order, tags, due labels, Done below), kept in the
/// data repository through <see cref="TodoBook"/>.
/// </summary>
public sealed partial class TodoViewModel : ObservableObject
{
    // The artifact's ten tag colors: text, then background.
    private static readonly (string Fg, string Bg)[] Palette =
    [
        ("#0E7C86", "#DDF0F2"), ("#4A54C4", "#E5E7FA"), ("#8E3B9E", "#F4E4F7"), ("#BE2E5B", "#FBE2EA"), ("#8A5A07", "#FAEDD6"),
        ("#2C7A3F", "#E0F1E4"), ("#1F63B8", "#E1ECFB"), ("#7A5230", "#F1E8DF"), ("#4A5C6B", "#E7ECF0"), ("#B0357F", "#FAE3F1"),
    ];

    private readonly TodoBook _book;
    private readonly ConfigSource _config;

    public TodoViewModel(TodoBook book, ConfigSource config)
    {
        _book = book;
        _config = config;
        _book.Changed += () => OnUi(Refresh);
    }

    public ObservableCollection<TodoView> Open { get; } = [];

    public ObservableCollection<TodoView> Done { get; } = [];

    public ObservableCollection<TagView> Tags { get; } = [];

    [ObservableProperty]
    public partial string NewTitle { get; set; } = "";

    [ObservableProperty]
    public partial string NewNotes { get; set; } = "";

    [ObservableProperty]
    public partial string NewTags { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? NewDue { get; set; }

    [ObservableProperty]
    public partial string? Filter { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool HasDone { get; set; }

    /// <summary>Every tag in use, for suggestions.</summary>
    public IReadOnlyList<string> KnownTags => [.. _book.Items.SelectMany(t => t.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    [RelayCommand]
    private void Add()
    {
        if (string.IsNullOrWhiteSpace(NewTitle))
        {
            return;
        }

        _book.Add(NewTitle, NewNotes, NewDue is { } d ? DateOnly.FromDateTime(d) : null, TodoRules.ParseTags(NewTags));
        NewTitle = "";
        NewNotes = "";
        NewTags = "";
        NewDue = null;
    }

    [RelayCommand]
    private void Toggle(TodoView? view)
    {
        if (view is not null)
        {
            _book.SetDone(view.Id, !view.Done);
        }
    }

    [RelayCommand]
    private void Remove(TodoView? view)
    {
        if (view is not null)
        {
            _book.Delete(view.Id);
        }
    }

    [RelayCommand]
    private void Edit(TodoView? view)
    {
        if (view is null)
        {
            return;
        }

        view.EditTitle = view.Title;
        view.EditNotes = view.Notes;
        view.EditTags = string.Join(", ", view.Item.Tags);
        view.EditDue = view.Item.Due?.ToDateTime(TimeOnly.MinValue);
        view.IsEditing = true;
    }

    [RelayCommand]
    private void Save(TodoView? view)
    {
        if (view is null)
        {
            return;
        }

        view.IsEditing = false;
        _book.Update(view.Item with
        {
            Title = string.IsNullOrWhiteSpace(view.EditTitle) ? view.Title : view.EditTitle.Trim(),
            Notes = view.EditNotes.Trim(),
            Tags = TodoRules.ParseTags(view.EditTags),
            Due = view.EditDue is { } d ? DateOnly.FromDateTime(d) : null,
        });
    }

    [RelayCommand]
    private static void Cancel(TodoView? view)
    {
        if (view is not null)
        {
            view.IsEditing = false;
        }
    }

    [RelayCommand]
    private void Up(TodoView? view)
    {
        if (view is not null)
        {
            _book.Nudge(view.Id, -1);
        }
    }

    [RelayCommand]
    private void Down(TodoView? view)
    {
        if (view is not null)
        {
            _book.Nudge(view.Id, 1);
        }
    }

    /// <summary>Drops <paramref name="dragged"/> just before <paramref name="target"/>, or at the end.</summary>
    public void Drop(TodoView dragged, TodoView? target) => _book.Move(dragged.Id, target?.Id);

    [RelayCommand]
    private void FilterBy(string? tag)
    {
        Filter = tag is null || string.Equals(Filter, tag, StringComparison.OrdinalIgnoreCase) ? null : tag;
        Refresh();
    }

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Now, _config.Zone).DateTime);
        var items = _book.Items;

        var counts = items.Where(t => !t.Done).SelectMany(t => t.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Tags.Clear();
        foreach (var g in counts)
        {
            Tags.Add(Tag(g.Key, g.Count(), string.Equals(Filter, g.Key, StringComparison.OrdinalIgnoreCase)));
        }

        var shown = Filter is null ? items : [.. items.Where(t => t.Tags.Contains(Filter, StringComparer.OrdinalIgnoreCase))];
        var editing = Open.Concat(Done).Where(v => v.IsEditing).ToDictionary(v => v.Id);

        // A card whose task and due label have not changed is kept as it is: rebuilding every card's
        // template on each change (a drop, a check) made the list visibly slow to settle.
        var current = Open.Concat(Done).GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());

        TodoView View(TodoItem t)
        {
            if (editing.TryGetValue(t.Id, out var kept) && kept.Item == t)
            {
                return kept; // a form being typed in is never replaced by a refresh
            }

            var (due, late) = TodoRules.DueLabel(t.Due, today);
            if (current.TryGetValue(t.Id, out var same) && same.Item == t && same.DueText == due && same.Late == (late && !t.Done))
            {
                return same;
            }

            var source = t.Source is { } s ? $"From {(s.Kind == "slack" ? "Slack" : "email")}{(s.Subject is { Length: > 0 } subj ? $": {subj}" : "")}" : null;
            return new TodoView(t, [.. t.Tags.Select(tag => Tag(tag, 0, false))], due, late && !t.Done, source);
        }

        Replace(Open, [.. TodoRules.OpenInOrder(shown).Select(View)]);
        Replace(Done, [.. TodoRules.DoneRecentFirst(shown).Take(15).Select(View)]);
        HasDone = Done.Count > 0;

        var open = items.Count(t => !t.Done);
        var lateCount = items.Count(t => !t.Done && t.Due < today);
        Summary = open == 0 ? "Clear." : $"{open} open" + (lateCount > 0 ? $" · {lateCount} late" : "");
        OnPropertyChanged(nameof(KnownTags));
    }

    // Frozen and shared: a brush per tag per refresh added up once the list grew.
    private static readonly Dictionary<string, Brush> BrushCache = [];

    private static Brush FromHex(string hex)
    {
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            BrushCache[hex] = brush;
        }

        return brush;
    }

    private static TagView Tag(string name, int count, bool on)
    {
        var (fg, bg) = Palette[TodoRules.TagHue(name)];
        return new TagView(name, count, on ? Brushes.White : FromHex(fg), on ? FromHex(fg) : FromHex(bg), on);
    }

    /// <summary>
    /// Makes <paramref name="target"/> equal <paramref name="items"/> by moving, inserting and removing,
    /// so a card already shown keeps its container instead of being drawn again.
    /// </summary>
    private static void Replace(ObservableCollection<TodoView> target, IReadOnlyList<TodoView> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var at = target.IndexOf(items[i]);
            if (at == i)
            {
                continue;
            }

            if (at > i)
            {
                target.Move(at, i);
            }
            else
            {
                target.Insert(i, items[i]);
            }
        }

        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }
}

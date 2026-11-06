using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MagnaCarta.Data;

namespace MagnaCarta.Editor;

/// <summary>
/// 필드 사건 보기 — fld 끝의 사건(깃발 · 조건 · 명령)을 이름 붙여 읽는다. 고치지는 않는다(읽기 전용).
/// 명령 이름은 EventNames(story.py ACT), 인자는 i32×14 를 끝의 0 을 떼고 보인다.
/// </summary>
public partial class EventsWindow : Window
{
    private sealed record FieldItem(int Number, string Name, int EventCount)
    {
        public override string ToString() => $"{Number:0000}  {(Name.Length > 0 ? Name : "(이름 없음)")}  · 사건 {EventCount}";
    }

    private sealed record EventItem(int Index, FieldEvent Ev)
    {
        public override string ToString()
        {
            string conds = Ev.Conditions.Count == 0 ? "조건 없음" : string.Join(" · ", Ev.Conditions.Select(CondText));
            return $"#{Index,-3} 깃발 {Ev.Trigger,-3} 명령 {Ev.Actions.Count,-3} {conds}";
        }
    }

    private List<FieldItem> _all = [];
    private Field? _field;

    public EventsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _all = await Task.Run(ScanFields);
            FieldCount.Text = $"사건이 있는 필드 {_all.Count}개";
            ApplyFilter();
        };
    }

    private static List<FieldItem> ScanFields()
    {
        var list = new List<FieldItem>();
        var dir = System.IO.Path.Combine(McFiles.Dir, "Field");
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.fld").OrderBy(x => x))
        {
            if (!int.TryParse(System.IO.Path.GetFileNameWithoutExtension(f), out int n)) continue;
            try
            {
                var fld = Field.Load(n);
                if (fld.Events.Count > 0) list.Add(new FieldItem(n, fld.PlaceName, fld.Events.Count));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        }
        return list;
    }

    private void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = Filter.Text.Trim();
        Fields.ItemsSource = q.Length == 0 ? _all : _all.Where(f => f.Number.ToString().Contains(q) || f.Name.Contains(q)).ToList();
    }

    private void FieldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Fields.SelectedItem is not FieldItem fi) return;
        _field = Field.Load(fi.Number);
        FieldTitle.Text = $"필드 {fi.Number} {_field.PlaceName} — 사건 {_field.Events.Count}개";
        Events.ItemsSource = _field.Events.Select((ev, i) => new EventItem(i, ev)).ToList();
        if (Events.Items.Count > 0) Events.SelectedIndex = 0;
    }

    private void EventChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Events.SelectedItem is not EventItem it) return;
        var ev = it.Ev;
        EventTitle.Text = $"사건 #{it.Index} — 깃발 {ev.Trigger} · 조건 {ev.Conditions.Count} · 명령 {ev.Actions.Count}";
        var sb = new StringBuilder();
        sb.AppendLine("── 조건 ──");
        if (ev.Conditions.Count == 0) sb.AppendLine("  (없음 — 늘 도는 사건)");
        foreach (var c in ev.Conditions) sb.AppendLine("  " + CondText(c) + "   " + Args(c));
        sb.AppendLine().AppendLine("── 명령 ──");
        for (int i = 0; i < ev.Actions.Count; i++)
        {
            var a = ev.Actions[i];
            string name = EventNames.Act.TryGetValue(a[0], out var nm) && nm.Length > 0 ? nm : "?";
            sb.AppendLine($"{i,4}  {a[0],4}  {name,-22} {Args(a)}");
        }
        Body.Text = sb.ToString();
    }

    private static string CondText(int[] c) =>
        (EventNames.Cond.TryGetValue(c[0], out var nm) ? nm : $"조건{c[0]}") + (c[0] switch
        {
            100 or 101 => $"[{c[1]}]=={c[2]}",
            304 => $" {c[1]}",
            302 => $" {c[1]}",
            _ => "",
        });

    private static string Args(int[] c)
    {
        int last = c.Length - 1;
        while (last > 0 && c[last] == 0) last--;
        return last == 0 ? "" : "(" + string.Join(", ", c.Skip(1).Take(last)) + ")";
    }

    private void RunField(object sender, RoutedEventArgs e)
    {
        if (_field != null) MainWindow.Launch($"--field {_field.Number}");
    }
}

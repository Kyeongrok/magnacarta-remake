using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MagnaCarta.Data;

namespace MagnaCarta.Editor;

/// <summary>
/// 컷씬 카메라 — fld F 묶음의 카메라 컷(사건 명령 500)을 위에서 본 길로 그리고, 틀을 넘겨 보며
/// 속도 · 부드럽게 · 시야각을 고쳐 cuts.txt(CutTuning — 게임 F7 창과 같은 표)에 적는다.
/// </summary>
public partial class CutsceneWindow : Window
{
    private sealed record FieldItem(int Number, string Name, int CutCount)
    {
        public override string ToString() => $"{Number:0000}  {(Name.Length > 0 ? Name : "(이름 없음)")}  · 컷 {CutCount}";
    }

    private sealed record CutItem(CameraCut Cut)
    {
        public override string ToString() => $"컷 {Cut.Id}  · {Cut.Length}틀 · 키 {Cut.Keys.Length}";
    }

    private List<FieldItem> _all = [];
    private Field? _field;
    private CameraCut? _raw, _tuned;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool _loading;

    public CutsceneWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (_tuned == null) return;
            double t = Tick.Value + CutTuning.Get(_field!.Number, _raw!.Id).Speed;
            Tick.Value = t > _tuned.Length ? 0 : t;
        };
        Loaded += async (_, _) =>
        {
            _all = await Task.Run(ScanFields);
            FieldCount.Text = $"카메라 컷이 있는 필드 {_all.Count}개";
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
                if (fld.Cuts.Count > 0) list.Add(new FieldItem(n, fld.PlaceName, fld.Cuts.Count));
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
        Cuts.ItemsSource = _field.Cuts.Values.OrderBy(c => c.Id).Select(c => new CutItem(c)).ToList();
        if (Cuts.Items.Count > 0) Cuts.SelectedIndex = 0;
    }

    private void CutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Cuts.SelectedItem is not CutItem ci || _field == null) return;
        _raw = ci.Cut;
        var t = CutTuning.Get(_field.Number, _raw.Id);
        _loading = true;
        Speed.Value = t.Speed; Smooth.Value = t.Smooth; Fov.Value = t.Fov;
        _loading = false;
        Retune();
        Tick.Maximum = Math.Max(1, _tuned!.Length);
        Tick.Value = 0;
        CutTitle.Text = $"필드 {_field.Number} {_field.PlaceName} — 컷 {_raw.Id}";
        var k0 = _raw.Keys.FirstOrDefault();
        CutInfo.Text = $"{_raw.Length}틀(≈ {_raw.Length / 30.3:0.0}초) · 키 {_raw.Keys.Length}개 · 키 사이 {_raw.Step}틀 · 원본 시야각 {k0?.Fov:0} · near {k0?.Near:0.#} · far {k0?.Far:0}";
        Draw();
    }

    private void TuneChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SpeedText.Text = $"x{Speed.Value:0.0}";
        SmoothText.Text = $"{Smooth.Value:0.00}";
        FovText.Text = Fov.Value > 0 ? $"{Fov.Value:0}°" : "원본";
        if (_loading || _raw == null || _field == null) return;
        CutTuning.Set(_field.Number, _raw.Id, new CutTuning.Tune((float)Speed.Value, (float)Smooth.Value, (float)Fov.Value));
        Retune();
        SaveInfo.Text = "바뀜 — 저장을 눌러야 게임에 남는다";
        Draw();
    }

    private void Retune() => _tuned = _raw == null || _field == null ? null : CutTuning.Apply(_raw, CutTuning.Get(_field.Number, _raw.Id));

    private void TickChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Draw();
    private void PlotSizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private void Play(object sender, RoutedEventArgs e)
    {
        _timer.IsEnabled = !_timer.IsEnabled;
        PlayButton.Content = _timer.IsEnabled ? "멈춤" : "재생";
    }

    private void Save(object sender, RoutedEventArgs e) => SaveInfo.Text = "저장: " + CutTuning.Save();

    private void Reset(object sender, RoutedEventArgs e)
    {
        _loading = true; Speed.Value = 1; Smooth.Value = 0; Fov.Value = 0; _loading = false;
        if (_raw != null && _field != null) CutTuning.Set(_field.Number, _raw.Id, CutTuning.Tune.Default);
        Retune(); Draw();
        SaveInfo.Text = "원래대로 — 저장을 눌러야 게임에 남는다";
    }

    private void Preview(object sender, RoutedEventArgs e)
    {
        if (_raw == null || _field == null) return;
        CutTuning.Save();   // 게임은 켤 때 읽으니 먼저 저장
        MainWindow.Launch($"--field {_field.Number}", new() { ["MC_CUT_PREVIEW"] = _raw.Id.ToString() });
        SaveInfo.Text = $"저장하고 게임을 띄움 — 필드 {_field.Number} 컷 {_raw.Id} 를 바로 튼다(F7 창 열림)";
    }

    // ── 위에서 본 그림: 원본 길(회색) · 다듬은 길(노랑) · 지금 틀의 카메라와 보는 쪽 · 배우(파랑) ──
    private void Draw()
    {
        Plot.Children.Clear();
        if (_raw == null || _tuned == null || _field == null || Plot.ActualWidth < 10) return;
        float tick = (float)Tick.Value;
        var (eye, fwd, up, fov) = _tuned.At(tick);
        TickText.Text = $"틀 {tick:0}/{_tuned.Length}";
        KeyInfo.Text = $"카메라 ({eye.X:0.0}, {eye.Y:0.0}, {eye.Z:0.0})  앞 ({fwd.X:0.00}, {fwd.Y:0.00}, {fwd.Z:0.00})  위 ({up.X:0.00}, {up.Y:0.00}, {up.Z:0.00})  시야각 {fov:0}";

        var pts = new List<Vector3>();
        for (float t = 0; t <= _raw.Length; t += Math.Max(1, _raw.Length / 200f)) { pts.Add(_raw.At(t).Eye); pts.Add(_tuned.At(t).Eye); }
        foreach (var a in _field.Actors) pts.Add(a.Position);
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        float span = MathF.Max(MathF.Max(maxX - minX, maxY - minY), 50) * 1.15f;
        float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        double W = Plot.ActualWidth, H = Plot.ActualHeight, s = Math.Min(W, H) / span;
        Point P(Vector3 v) => new(W / 2 + (v.X - cx) * s, H / 2 - (v.Y - cy) * s);

        Line(_raw, Brushes.Gray, 1);
        Line(_tuned, Brushes.Gold, 2);
        foreach (var a in _field.Actors)
        {
            var p = P(a.Position);
            Dot(p, 6, Brushes.DeepSkyBlue);
            var lbl = new TextBlock { Text = $"배우 {a.Id}", Foreground = Brushes.LightSkyBlue, FontSize = 10 };
            Canvas.SetLeft(lbl, p.X + 4); Canvas.SetTop(lbl, p.Y - 6); Plot.Children.Add(lbl);
        }
        var ep = P(eye);
        Dot(ep, 9, Brushes.OrangeRed);
        var look = new Vector3(fwd.X, fwd.Y, 0);
        if (look.LengthSquared() > 1e-6f)
        {
            look = Vector3.Normalize(look) * (span * 0.12f);
            Plot.Children.Add(new System.Windows.Shapes.Line { X1 = ep.X, Y1 = ep.Y, X2 = P(eye + look).X, Y2 = P(eye + look).Y, Stroke = Brushes.OrangeRed, StrokeThickness = 2 });
        }

        void Line(CameraCut cut, Brush b, double th)
        {
            var poly = new Polyline { Stroke = b, StrokeThickness = th };
            for (float t = 0; t <= cut.Length; t += Math.Max(1, cut.Length / 300f)) poly.Points.Add(P(cut.At(t).Eye));
            Plot.Children.Add(poly);
        }
        void Dot(Point p, double r, Brush b)
        {
            var el = new Ellipse { Width = r, Height = r, Fill = b };
            Canvas.SetLeft(el, p.X - r / 2); Canvas.SetTop(el, p.Y - r / 2); Plot.Children.Add(el);
        }
    }
}

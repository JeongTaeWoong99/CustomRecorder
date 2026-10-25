using System.Windows;
using System.Windows.Interop;
using CustomRecorder.App.Services;

namespace CustomRecorder.App.Windows;

public partial class ScheduleWindow : Window
{
    private static readonly (int Minutes, string Label)[] Lengths =
        { (1, "1분"), (5, "5분"), (10, "10분"), (30, "30분"), (60, "1시간"), (90, "1시간 30분"), (120, "2시간"), (180, "3시간") };

    public DateTime Start { get; private set; }
    public TimeSpan Length { get; private set; }

    public ScheduleWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this, 76);
        SourceInitialized += (_, _) => CaptureExclusion.Apply(new WindowInteropHelper(this).Handle);

        DayCombo.Items.Add("오늘");
        DayCombo.Items.Add("내일");
        DayCombo.SelectedIndex = 0;
        for (int h = 0; h < 24; h++) HourCombo.Items.Add(h.ToString("00"));
        for (int m = 0; m < 60; m += 5) MinuteCombo.Items.Add(m.ToString("00"));
        var next = DateTime.Now.AddMinutes(10);
        HourCombo.SelectedIndex = next.Hour;
        MinuteCombo.SelectedIndex = next.Minute / 5;
        foreach (var l in Lengths) LengthCombo.Items.Add(l.Label);
        LengthCombo.SelectedIndex = 3;

        foreach (var c in new[] { DayCombo, HourCombo, MinuteCombo, LengthCombo })
            c.SelectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    private DateTime Chosen() => DateTime.Today.AddDays(DayCombo.SelectedIndex)
        .AddHours(HourCombo.SelectedIndex).AddMinutes(MinuteCombo.SelectedIndex * 5);

    private void UpdateSummary()
    {
        if (SummaryText == null) return;
        var start = Chosen();
        var len = TimeSpan.FromMinutes(Lengths[Math.Max(0, LengthCombo.SelectedIndex)].Minutes);
        SummaryText.Text = start <= DateTime.Now
            ? "이미 지난 시각입니다. 시각을 다시 골라 주세요."
            : $"{start:M월 d일 HH:mm}에 시작해서 {start + len:HH:mm}에 끝납니다. (지금부터 {Until(start)} 뒤)";
    }

    private static string Until(DateTime t)
    {
        var d = t - DateTime.Now;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}시간 {d.Minutes}분" : $"{Math.Max(1, (int)Math.Ceiling(d.TotalMinutes))}분";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var start = Chosen();
        if (start <= DateTime.Now) { UpdateSummary(); return; }
        Start = start;
        Length = TimeSpan.FromMinutes(Lengths[Math.Max(0, LengthCombo.SelectedIndex)].Minutes);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

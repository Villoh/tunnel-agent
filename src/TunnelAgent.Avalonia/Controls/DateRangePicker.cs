using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Calendar = Avalonia.Controls.Calendar;

namespace TunnelAgent.Controls;

/// <summary>
/// A single Fluent-style date-range picker that mirrors <c>CalendarDatePicker</c>:
/// an editable text box showing the "start – end" span plus a calendar icon button
/// that opens a popup with a <see cref="Calendar"/> in range-selection mode.
/// <see cref="Start"/>/<see cref="End"/> are two-way bindable inclusive day bounds.
/// </summary>
public class DateRangePicker : TemplatedControl
{
    public static readonly StyledProperty<DateTime?> StartProperty =
        AvaloniaProperty.Register<DateRangePicker, DateTime?>(nameof(Start), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<DateTime?> EndProperty =
        AvaloniaProperty.Register<DateRangePicker, DateTime?>(nameof(End), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<DateRangePicker, string>(nameof(Watermark), defaultValue: "Select range");

    public DateTime? Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public DateTime? End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public string Watermark { get => GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }

    private TextBox? _textBox;
    private Button? _button;
    private Popup? _popup;
    private Calendar? _calendar;
    private bool _syncing;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_button != null) _button.Click -= OnButtonClick;
        if (_calendar != null) _calendar.SelectedDatesChanged -= OnCalendarSelectionChanged;
        if (_textBox != null)
        {
            _textBox.LostFocus -= OnTextCommitted;
            _textBox.KeyDown -= OnTextKeyDown;
        }

        _textBox = e.NameScope.Find<TextBox>("PART_TextBox");
        _button = e.NameScope.Find<Button>("PART_Button");
        _popup = e.NameScope.Find<Popup>("PART_Popup");
        _calendar = e.NameScope.Find<Calendar>("PART_Calendar");

        if (_button != null) _button.Click += OnButtonClick;
        if (_calendar != null) _calendar.SelectedDatesChanged += OnCalendarSelectionChanged;
        if (_textBox != null)
        {
            _textBox.LostFocus += OnTextCommitted;
            _textBox.KeyDown += OnTextKeyDown;
        }

        UpdateText();
        SyncCalendarFromProps();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StartProperty || change.Property == EndProperty)
        {
            UpdateText();
            SyncCalendarFromProps();
        }
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_popup == null) return;
        SyncCalendarFromProps();
        _popup.IsOpen = !_popup.IsOpen;
    }

    private void OnTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitText();
    }

    private void OnTextCommitted(object? sender, RoutedEventArgs e) => CommitText();

    private void CommitText()
    {
        if (_textBox == null) return;
        var text = _textBox.Text?.Trim() ?? string.Empty;
        var culture = CultureInfo.CurrentCulture;

        if (string.IsNullOrEmpty(text))
        {
            SetRange(null, null);
        }
        else
        {
            var parts = text.Split('–', '-'); // en-dash or hyphen between the two dates
            if (parts.Length == 2
                && DateTime.TryParse(parts[0].Trim(), culture, DateTimeStyles.None, out var a)
                && DateTime.TryParse(parts[1].Trim(), culture, DateTimeStyles.None, out var b))
            {
                SetRange(a.Date <= b.Date ? a.Date : b.Date, a.Date <= b.Date ? b.Date : a.Date);
            }
            else if (DateTime.TryParse(text, culture, DateTimeStyles.None, out var single))
            {
                SetRange(single.Date, single.Date);
            }
        }

        UpdateText(); // normalise (or revert on parse failure)
        SyncCalendarFromProps();
    }

    private void OnCalendarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _calendar == null) return;
        var dates = _calendar.SelectedDates;
        if (dates.Count == 0) return;
        SetRange(dates.Min().Date, dates.Max().Date);
        UpdateText();
    }

    private void SetRange(DateTime? start, DateTime? end)
    {
        _syncing = true;
        Start = start;
        End = end;
        _syncing = false;
    }

    private void SyncCalendarFromProps()
    {
        if (_calendar == null) return;
        _syncing = true;
        _calendar.SelectedDates.Clear();
        if (Start is { } s && End is { } en)
        {
            _calendar.SelectedDates.AddRange(s.Date, en.Date);
            _calendar.DisplayDate = s.Date;
        }
        else if (Start is { } only)
        {
            _calendar.SelectedDates.Add(only.Date);
            _calendar.DisplayDate = only.Date;
        }
        _syncing = false;
    }

    private void UpdateText()
    {
        if (_textBox == null) return;
        var culture = CultureInfo.CurrentCulture;
        _textBox.Text = (Start, End) switch
        {
            ({ } s, { } e) when s.Date == e.Date => s.ToString("d", culture),
            ({ } s, { } e) => $"{s.ToString("d", culture)} – {e.ToString("d", culture)}",
            _ => string.Empty,
        };
    }
}

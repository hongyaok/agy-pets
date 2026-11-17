using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgyPetsOverlay;

public partial class MainWindow : Window
{
    private const int CellWidth = 192;
    private const int CellHeight = 208;
    private const double BaseWalkSpeed = 75.0; // DIPs per second

    private enum PetState
    {
        Idle,
        WalkRight,
        WalkLeft,
        Waving,
        Jumping
    }

    private readonly string _baseUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Random _rng = new();

    private readonly DispatcherTimer _animTimer;
    private readonly DispatcherTimer _configTimer;
    private readonly DispatcherTimer _bubbleTimer;
    private readonly DispatcherTimer _windowTrackTimer;
    private readonly Stopwatch _stopwatch = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    private IntPtr _ownHwnd;
    private IntPtr _agyHwnd;
    private Rect _agyBoundsDip;
    private bool _hasAgyBounds;
    private uint _lastCheckedPid;
    private bool _lastCheckedPidIsAgy;

    private Dictionary<PetState, BitmapSource[]> _animations = new();
    private string? _loadedSpritesheet;
    private string _petDisplayName = "Pet";
    private double _petSize = 96.0;
    private double _speed = 1.0;
    private string _behavior = "roam";

    private PetState _state = PetState.Idle;
    private int _frameIndex;
    private double _frameElapsed;
    private double _stateRemaining = 2.0;

    private double _petCenterX;
    private double _petBottomY;
    private bool _positionInitialized;

    private bool _isMouseDown;
    private bool _isDragging;
    private Point _dragStartDip;
    private Point _lastDragDip;

    private bool _isPollingConfig;
    private bool _isFetchingUsage;

    public MainWindow(int port)
    {
        InitializeComponent();
        _baseUrl = $"http://127.0.0.1:{port}";

        _animTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _animTimer.Tick += AnimTimer_Tick;

        _configTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _configTimer.Tick += async (_, _) => await PollConfigAsync();

        _bubbleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(12)
        };
        _bubbleTimer.Tick += (_, _) => HideSpeechBubble();

        _windowTrackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _windowTrackTimer.Tick += (_, _) => UpdateAntigravityWindowTracking();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ownHwnd = new WindowInteropHelper(this).Handle;
        UpdateAntigravityWindowTracking();

        var bounds = GetActiveBoundsDip();
        _petCenterX = bounds.Right - Math.Max(_petSize, 120);
        _petBottomY = bounds.Bottom - 12;
        _positionInitialized = true;

        ApplyPetSize(_petSize);
        SyncWindowPosition();

        _stopwatch.Start();
        _animTimer.Start();
        _configTimer.Start();
        _windowTrackTimer.Start();

        await PollConfigAsync();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_positionInitialized)
        {
            SyncWindowPosition();
        }
    }

    private void ApplyPetSize(double size)
    {
        _petSize = Math.Clamp(size, 48.0, 220.0);
        PetImage.Width = _petSize;
        PetImage.Height = _petSize * CellHeight / CellWidth;
    }

    private Rect GetActiveBoundsDip()
    {
        return _hasAgyBounds && _agyBoundsDip.Width > 0 && _agyBoundsDip.Height > 0
            ? _agyBoundsDip
            : SystemParameters.WorkArea;
    }

    private void SyncWindowPosition()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var bounds = GetActiveBoundsDip();
        double halfPetWidth = PetImage.Width / 2.0;
        double minCenterX = bounds.Left + halfPetWidth;
        double maxCenterX = Math.Max(minCenterX, bounds.Right - halfPetWidth);
        double minBottomY = bounds.Top + PetImage.Height;
        double maxBottomY = Math.Max(minBottomY, bounds.Bottom);

        _petCenterX = Math.Clamp(_petCenterX, minCenterX, maxCenterX);
        _petBottomY = Math.Clamp(_petBottomY, minBottomY, maxBottomY);

        double petOffsetX = ActualWidth / 2.0;
        double petBottomOffsetY = ActualHeight - 10.0; // matches Grid Margin="10"

        double targetLeft = _petCenterX - petOffsetX;
        double targetTop = _petBottomY - petBottomOffsetY;

        Left = Math.Clamp(targetLeft, bounds.Left - 10, Math.Max(bounds.Left - 10, bounds.Right - ActualWidth + 10));
        Top = Math.Clamp(targetTop, bounds.Top - 10, Math.Max(bounds.Top - 10, bounds.Bottom - ActualHeight + 10));
    }

    private void AnimTimer_Tick(object? sender, EventArgs e)
    {
        double dt = Math.Min(_stopwatch.Elapsed.TotalSeconds, 0.1);
        _stopwatch.Restart();

        if (Visibility != Visibility.Visible)
        {
            return;
        }

        UpdateBehavior(dt);
        UpdateAnimationFrame(dt);
    }

    private void UpdateBehavior(double dt)
    {
        if (_isDragging)
        {
            return;
        }

        if (SpeechBubble.Visibility == Visibility.Visible)
        {
            if (_state is PetState.WalkLeft or PetState.WalkRight)
            {
                SetState(PetState.Idle, 1.5);
            }
            else if (_state is PetState.Waving or PetState.Jumping)
            {
                _stateRemaining -= dt;
                if (_stateRemaining <= 0)
                {
                    SetState(PetState.Idle, 1.5);
                }
            }
            return;
        }

        var bounds = GetActiveBoundsDip();
        double halfPetWidth = PetImage.Width / 2.0;
        double minX = bounds.Left + halfPetWidth;
        double maxX = Math.Max(minX, bounds.Right - halfPetWidth);

        if (_behavior.Equals("idle", StringComparison.OrdinalIgnoreCase))
        {
            _stateRemaining -= dt;
            if (_state != PetState.Idle && _state != PetState.Waving)
            {
                SetState(PetState.Idle, 3.0);
            }
            else if (_stateRemaining <= 0)
            {
                SetState(_rng.NextDouble() < 0.25 ? PetState.Waving : PetState.Idle, 1.5 + _rng.NextDouble() * 3.0);
            }
            return;
        }

        if (_behavior.Equals("follow", StringComparison.OrdinalIgnoreCase))
        {
            var cursor = GetCursorDipPosition();
            double dx = cursor.X - _petCenterX;
            if (Math.Abs(dx) > 48.0)
            {
                var dir = dx > 0 ? PetState.WalkRight : PetState.WalkLeft;
                if (_state != dir)
                {
                    SetState(dir, 0.5);
                }

                _petCenterX = Math.Clamp(_petCenterX + Math.Sign(dx) * BaseWalkSpeed * _speed * dt, minX, maxX);
                SyncWindowPosition();
            }
            else if (_state == PetState.WalkLeft || _state == PetState.WalkRight)
            {
                SetState(PetState.Idle, 1.0);
            }
            else
            {
                _stateRemaining -= dt;
                if (_stateRemaining <= 0)
                {
                    SetState(PetState.Idle, 2.0);
                }
            }
            return;
        }

        // Default: "roam"
        if (_state == PetState.WalkRight)
        {
            _petCenterX += BaseWalkSpeed * _speed * dt;
            if (_petCenterX >= maxX)
            {
                _petCenterX = maxX;
                SetState(PetState.WalkLeft, 2.5 + _rng.NextDouble() * 3.0);
            }
            SyncWindowPosition();
        }
        else if (_state == PetState.WalkLeft)
        {
            _petCenterX -= BaseWalkSpeed * _speed * dt;
            if (_petCenterX <= minX)
            {
                _petCenterX = minX;
                SetState(PetState.WalkRight, 2.5 + _rng.NextDouble() * 3.0);
            }
            SyncWindowPosition();
        }

        _stateRemaining -= dt;
        if (_stateRemaining <= 0)
        {
            double roll = _rng.NextDouble();
            if (roll < 0.45)
            {
                var dir = _rng.Next(2) == 0 ? PetState.WalkLeft : PetState.WalkRight;
                if (_petCenterX <= minX + 10) dir = PetState.WalkRight;
                if (_petCenterX >= maxX - 10) dir = PetState.WalkLeft;
                SetState(dir, 2.5 + _rng.NextDouble() * 3.5);
            }
            else if (roll < 0.75)
            {
                SetState(PetState.Idle, 1.5 + _rng.NextDouble() * 2.5);
            }
            else if (roll < 0.90)
            {
                SetState(PetState.Waving, 1.4 + _rng.NextDouble() * 0.8);
            }
            else
            {
                SetState(PetState.Jumping, 1.1 + _rng.NextDouble() * 0.6);
            }
        }
    }

    private void SetState(PetState next, double durationSeconds)
    {
        if (_state != next)
        {
            _state = next;
            _frameIndex = 0;
            _frameElapsed = 0;
        }
        _stateRemaining = durationSeconds;
    }

    private void UpdateAnimationFrame(double dt)
    {
        if (!_animations.TryGetValue(_state, out var frames) || frames.Length == 0)
        {
            if (!_animations.TryGetValue(PetState.Idle, out frames) || frames.Length == 0)
            {
                return;
            }
        }

        double frameInterval = (_state is PetState.WalkLeft or PetState.WalkRight)
            ? 0.11 / Math.Clamp(_speed, 0.5, 3.0)
            : 0.14;

        _frameElapsed += dt;
        if (_frameElapsed >= frameInterval)
        {
            _frameElapsed = 0;
            _frameIndex = (_frameIndex + 1) % frames.Length;
            PetImage.Source = frames[_frameIndex];
        }
        else if (PetImage.Source is null)
        {
            PetImage.Source = frames[0];
        }
    }

    private async Task PollConfigAsync()
    {
        if (_isPollingConfig)
        {
            return;
        }

        _isPollingConfig = true;
        try
        {
            string json = await _http.GetStringAsync($"{_baseUrl}/api/config");
            var cfg = JsonSerializer.Deserialize<PetConfig>(json, _jsonOptions);
            if (cfg is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(cfg.DisplayName))
            {
                _petDisplayName = cfg.DisplayName;
            }

            if (cfg.Size > 0 && Math.Abs(cfg.Size - _petSize) > 0.5)
            {
                ApplyPetSize(cfg.Size);
                SyncWindowPosition();
            }

            if (cfg.Speed > 0)
            {
                _speed = Math.Clamp(cfg.Speed, 0.5, 3.0);
            }

            if (!string.IsNullOrWhiteSpace(cfg.Behavior))
            {
                _behavior = cfg.Behavior;
            }

            if (!string.IsNullOrWhiteSpace(cfg.Spritesheet) &&
                !string.Equals(cfg.Spritesheet, _loadedSpritesheet, StringComparison.Ordinal))
            {
                await LoadSpritesheetAsync(cfg.Spritesheet);
            }
        }
        catch
        {
            // Ignore transient polling errors while sidecar starts/restarts
        }
        finally
        {
            _isPollingConfig = false;
        }
    }

    private async Task LoadSpritesheetAsync(string spritesheetPath)
    {
        try
        {
            string url = $"{_baseUrl}/api/sprite?path={Uri.EscapeDataString(spritesheetPath)}";
            byte[] bytes = await _http.GetByteArrayAsync(url);

            var sliced = await Task.Run(() => DecodeAndSliceSpritesheet(bytes));
            if (sliced.Count > 0)
            {
                _animations = sliced;
                _loadedSpritesheet = spritesheetPath;
                _frameIndex = 0;
                if (_animations.TryGetValue(_state, out var frames) && frames.Length > 0)
                {
                    PetImage.Source = frames[0];
                }
                else if (_animations.TryGetValue(PetState.Idle, out var idle) && idle.Length > 0)
                {
                    PetImage.Source = idle[0];
                }
            }
        }
        catch
        {
            // Keep existing sprite if download/decode fails
        }
    }

    private static Dictionary<PetState, BitmapSource[]> DecodeAndSliceSpritesheet(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(
            ms,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var sheet = NormalizeToPremultipliedPbgra32(decoder.Frames[0]);

        var result = new Dictionary<PetState, BitmapSource[]>
        {
            [PetState.Idle] = SliceRow(sheet, row: 0, frameCount: 6),
            [PetState.WalkRight] = SliceRow(sheet, row: 1, frameCount: 8),
            [PetState.WalkLeft] = SliceRow(sheet, row: 2, frameCount: 8),
            [PetState.Waving] = SliceRow(sheet, row: 3, frameCount: 4),
            [PetState.Jumping] = SliceRow(sheet, row: 4, frameCount: 5)
        };

        return result;
    }

    internal static BitmapSource NormalizeToPremultipliedPbgra32(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        // Enforce strict premultiplied alpha invariants for WPF layered windows:
        // when A == 0, RGB must be 0 to prevent magenta/black artifacts under AllowsTransparency="True".
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
            else if (a < 255)
            {
                if (pixels[i] > a) pixels[i] = a;
                if (pixels[i + 1] > a) pixels[i + 1] = a;
                if (pixels[i + 2] > a) pixels[i + 2] = a;
            }
        }

        double dpiX = converted.DpiX > 0 ? converted.DpiX : 96.0;
        double dpiY = converted.DpiY > 0 ? converted.DpiY : 96.0;
        var clean = BitmapSource.Create(
            width,
            height,
            dpiX,
            dpiY,
            PixelFormats.Pbgra32,
            null,
            pixels,
            stride);
        clean.Freeze();
        return clean;
    }

    private static BitmapSource[] SliceRow(BitmapSource sheet, int row, int frameCount)
    {
        int y = row * CellHeight;
        if (y + CellHeight > sheet.PixelHeight)
        {
            return [];
        }

        var frames = new List<BitmapSource>(frameCount);
        for (int col = 0; col < frameCount; col++)
        {
            int x = col * CellWidth;
            if (x + CellWidth > sheet.PixelWidth)
            {
                break;
            }

            var crop = new CroppedBitmap(sheet, new Int32Rect(x, y, CellWidth, CellHeight));
            crop.Freeze();
            frames.Add(crop);
        }

        return [.. frames];
    }

    private void PetImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isMouseDown = true;
        _isDragging = false;
        _dragStartDip = GetCursorDipPosition();
        _lastDragDip = _dragStartDip;
        PetImage.CaptureMouse();
        e.Handled = true;
    }

    private void PetImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDown)
        {
            return;
        }

        var current = GetCursorDipPosition();
        if (!_isDragging)
        {
            if (Math.Abs(current.X - _dragStartDip.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(current.Y - _dragStartDip.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;
                SetState(PetState.Idle, 1.0);
            }
        }

        if (_isDragging)
        {
            _petCenterX += current.X - _lastDragDip.X;
            _petBottomY += current.Y - _lastDragDip.Y;
            _lastDragDip = current;
            SyncWindowPosition();
        }
    }

    private async void PetImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMouseDown)
        {
            return;
        }

        _isMouseDown = false;
        if (PetImage.IsMouseCaptured)
        {
            PetImage.ReleaseMouseCapture();
        }

        bool wasDragging = _isDragging;
        _isDragging = false;

        if (!wasDragging)
        {
            SetState(PetState.Waving, 1.6);
            await ShowModelUsageAsync();
        }

        e.Handled = true;
    }

    private void SpeechBubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideSpeechBubble();
        e.Handled = true;
    }

    private async void CheckUsageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetState(PetState.Waving, 1.6);
        await ShowModelUsageAsync();
    }

    private void CloseOverlayMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private static readonly Regex ThinkingLabelRegex = new(
        @"\s*(?:\((?:x-?high|extra\s*high|ultra\s*high|high|medium|med|low|minimal|min|max|none|thinking|non-thinking|extended\s*thinking|standard)(?:\s+thinking)?\)|[-–—]\s*(?:x-?high|extra\s*high|high|medium|med|low|minimal|thinking))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThinkingIdRegex = new(
        @"-(?:x-?high|extra-?high|ultra-?high|high|medium|med|low|minimal|min|max|none|thinking|non-thinking)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripThinkingLevel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "";
        }

        return ThinkingLabelRegex.Replace(label, "").Trim();
    }

    private static string StripModelIdThinkingSuffix(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return "";
        }

        return ThinkingIdRegex.Replace(modelId, "").Trim();
    }

    private static List<UsageModel> GroupUsageModels(List<UsageModel> rawModels)
    {
        bool hasNumericQuota = rawModels.Any(m => m.RemainingFraction is not null);
        var active = rawModels.Where(m =>
        {
            if (m.Disabled && m.RemainingFraction is null)
            {
                return false;
            }
            if (hasNumericQuota && m.RemainingFraction is null)
            {
                return false;
            }
            return true;
        });

        var grouped = new List<UsageModel>();
        var byKey = new Dictionary<string, UsageModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in active)
        {
            string rawLabel = string.IsNullOrWhiteSpace(item.Label) ? (item.ModelId ?? "Unknown") : item.Label!;
            string baseLabel = StripThinkingLevel(rawLabel);
            if (string.IsNullOrWhiteSpace(baseLabel))
            {
                baseLabel = rawLabel.Trim();
            }

            string baseId = StripModelIdThinkingSuffix(item.ModelId);
            string key = !string.IsNullOrWhiteSpace(baseLabel)
                ? baseLabel
                : (!string.IsNullOrWhiteSpace(baseId) ? baseId : "unknown");

            if (!byKey.TryGetValue(key, out var existing))
            {
                var entry = new UsageModel
                {
                    Label = baseLabel,
                    ModelId = baseId,
                    RemainingFraction = item.RemainingFraction,
                    ResetTime = item.ResetTime,
                    Disabled = false
                };
                byKey[key] = entry;
                grouped.Add(entry);
            }
            else
            {
                if (item.RemainingFraction is double frac)
                {
                    existing.RemainingFraction = existing.RemainingFraction is double prev
                        ? Math.Min(prev, frac)
                        : frac;
                }

                if (string.IsNullOrWhiteSpace(existing.ResetTime) && !string.IsNullOrWhiteSpace(item.ResetTime))
                {
                    existing.ResetTime = item.ResetTime;
                }
            }
        }

        return grouped;
    }

    private async Task ShowModelUsageAsync()
    {
        if (_isFetchingUsage)
        {
            return;
        }

        _isFetchingUsage = true;
        try
        {
            BubbleTitle.Text = "Usage limits";
            BubbleSub.Text = $"{_petDisplayName} · Checking…";
            BubbleStatus.Text = "Checking remaining usage…";
            BubbleStatus.Visibility = Visibility.Visible;
            ModelsList.ItemsSource = null;
            ShowSpeechBubble();

            string json = await _http.GetStringAsync($"{_baseUrl}/api/usage");
            var usage = JsonSerializer.Deserialize<UsageResponse>(json, _jsonOptions);

            if (usage is null || usage.Models is null || usage.Models.Count == 0)
            {
                BubbleSub.Text = "No active quotas";
                BubbleStatus.Text = usage?.Error ?? "No model usage data returned.";
                BubbleStatus.Visibility = Visibility.Visible;
                ShowSpeechBubble();
                return;
            }

            var groupedModels = GroupUsageModels(usage.Models)
                .OrderBy(m => m.RemainingFraction ?? 2.0)
                .ToList();

            if (groupedModels.Count == 0)
            {
                BubbleSub.Text = "No active quotas";
                BubbleStatus.Text = "No active model quota returned.";
                BubbleStatus.Visibility = Visibility.Visible;
                ModelsList.ItemsSource = null;
                ShowSpeechBubble();
                return;
            }

            var resetDates = new List<DateTimeOffset>();
            foreach (var m in groupedModels)
            {
                if (!string.IsNullOrWhiteSpace(m.ResetTime) && DateTimeOffset.TryParse(m.ResetTime, out var dto))
                {
                    resetDates.Add(dto);
                }
            }

            DateTimeOffset? soonest = resetDates.Count > 0 ? resetDates.Min() : null;
            bool sharedReset = soonest is not null
                && resetDates.Count == groupedModels.Count
                && resetDates.All(r => Math.Abs((r - soonest.Value).TotalMinutes) < 2.0);

            string countLabel = groupedModels.Count == 1 ? "1 model" : $"{groupedModels.Count} models";
            if (soonest is not null)
            {
                string resetPhrase = FormatResetTime(soonest.Value.ToString("O"));
                BubbleSub.Text = string.IsNullOrEmpty(resetPhrase)
                    ? countLabel
                    : $"{countLabel} · {(sharedReset ? "Resets" : "Next reset")} {resetPhrase}";
            }
            else
            {
                BubbleSub.Text = countLabel;
            }

            var items = groupedModels.Select(m =>
            {
                bool hasFrac = m.RemainingFraction is not null;
                double frac = hasFrac ? Math.Clamp(m.RemainingFraction!.Value, 0.0, 1.0) : 0.0;
                int pct = (int)Math.Round(frac * 100.0);

                Brush barBrush = !hasFrac
                    ? new SolidColorBrush(Color.FromRgb(111, 111, 120))
                    : pct > 50
                        ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
                        : pct > 20
                            ? new SolidColorBrush(Color.FromRgb(252, 211, 77))
                            : new SolidColorBrush(Color.FromRgb(248, 113, 113));
                barBrush.Freeze();

                Brush textBrush = !hasFrac
                    ? new SolidColorBrush(Color.FromRgb(155, 155, 164))
                    : pct > 50
                        ? new SolidColorBrush(Color.FromRgb(243, 243, 244))
                        : pct > 20
                            ? new SolidColorBrush(Color.FromRgb(252, 211, 77))
                            : new SolidColorBrush(Color.FromRgb(248, 113, 113));
                textBrush.Freeze();

                string pctText = !hasFrac
                    ? "—"
                    : pct <= 20
                        ? $"Low · {pct}%"
                        : pct <= 50
                            ? $"Warn · {pct}%"
                            : $"{pct}%";

                return new ModelUsageItem(
                    Label: string.IsNullOrWhiteSpace(m.Label) ? (m.ModelId ?? "Unknown") : m.Label,
                    PercentText: pctText,
                    ResetText: sharedReset ? "" : FormatResetTime(m.ResetTime),
                    BarBrush: barBrush,
                    TextBrush: textBrush,
                    BarWidth: hasFrac ? Math.Round(frac * 262.0, 1) : 0.0);
            }).ToList();

            BubbleStatus.Visibility = Visibility.Collapsed;
            ModelsList.ItemsSource = items;
            ShowSpeechBubble();
        }
        catch (Exception ex)
        {
            BubbleSub.Text = "Unavailable";
            BubbleStatus.Text = $"Unable to load usage limits ({ex.Message}).";
            BubbleStatus.Visibility = Visibility.Visible;
            ModelsList.ItemsSource = null;
            ShowSpeechBubble();
        }
        finally
        {
            _isFetchingUsage = false;
        }
    }

    private void ShowSpeechBubble()
    {
        bool wasCollapsed = SpeechBubble.Visibility != Visibility.Visible;
        SpeechBubble.Visibility = Visibility.Visible;
        if (wasCollapsed && SystemParameters.ClientAreaAnimation)
        {
            var dur = new Duration(TimeSpan.FromMilliseconds(220));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SpeechBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = ease });
            BubbleTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6.0, 0.0, dur) { EasingFunction = ease });
        }
        else if (wasCollapsed)
        {
            SpeechBubble.Opacity = 1.0;
            BubbleTranslate.Y = 0.0;
        }
        _bubbleTimer.Stop();
        _bubbleTimer.Start();
    }

    private void HideSpeechBubble()
    {
        _bubbleTimer.Stop();
        SpeechBubble.Visibility = Visibility.Collapsed;
    }

    private static string FormatResetTime(string? resetTime)
    {
        if (string.IsNullOrWhiteSpace(resetTime))
        {
            return "";
        }

        if (!DateTimeOffset.TryParse(resetTime, out var dto))
        {
            return "";
        }

        var remaining = dto - DateTimeOffset.UtcNow;
        if (remaining.TotalMinutes < 1)
        {
            return "soon";
        }

        if (remaining.TotalHours < 1)
        {
            return $"in {(int)remaining.TotalMinutes}m";
        }

        if (remaining.TotalHours < 24)
        {
            return $"in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return dto.LocalDateTime.ToString("MMM d HH:mm");
    }

    private void UpdateAntigravityWindowTracking()
    {
        IntPtr fgHwnd = GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero)
        {
            SetOverlayVisible(false);
            return;
        }

        GetWindowThreadProcessId(fgHwnd, out uint fgPid);
        bool fgIsOwnProcess = fgHwnd == _ownHwnd || fgPid == _ownPid;

        if (!fgIsOwnProcess)
        {
            if (IsAntigravityMainWindow(fgHwnd))
            {
                _agyHwnd = fgHwnd;
            }
            else
            {
                SetOverlayVisible(false);
                return;
            }
        }
        else if (_agyHwnd == IntPtr.Zero || !IsWindow(_agyHwnd))
        {
            _agyHwnd = FindAntigravityMainWindow();
        }

        if (_agyHwnd == IntPtr.Zero || !IsWindow(_agyHwnd) || !IsWindowVisible(_agyHwnd) || IsIconic(_agyHwnd))
        {
            SetOverlayVisible(false);
            return;
        }

        if (!TryGetWindowBoundsDip(_agyHwnd, out var boundsDip) || boundsDip.Width < 100 || boundsDip.Height < 100)
        {
            SetOverlayVisible(false);
            return;
        }

        bool boundsChanged = !_hasAgyBounds || _agyBoundsDip != boundsDip;
        _agyBoundsDip = boundsDip;
        _hasAgyBounds = true;

        SetOverlayVisible(true);

        if (boundsChanged && _positionInitialized)
        {
            SyncWindowPosition();
        }
    }

    private void SetOverlayVisible(bool visible)
    {
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (Visibility != target)
        {
            Visibility = target;
        }
    }

    private IntPtr FindAntigravityMainWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (IsAntigravityMainWindow(hWnd))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private bool IsAntigravityMainWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd) || IsIconic(hWnd))
        {
            return false;
        }

        if (GetWindowTextLength(hWnd) <= 0)
        {
            return false;
        }

        GetWindowThreadProcessId(hWnd, out uint pid);
        if (!IsAntigravityProcess(pid))
        {
            return false;
        }

        var className = new StringBuilder(128);
        if (GetClassName(hWnd, className, className.Capacity) <= 0)
        {
            return false;
        }

        if (!string.Equals(className.ToString(), "Chrome_WidgetWin_1", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryGetNativeWindowRect(hWnd, out var rect))
        {
            return false;
        }

        return (rect.Right - rect.Left) >= 200 && (rect.Bottom - rect.Top) >= 200;
    }

    private bool IsAntigravityProcess(uint pid)
    {
        if (pid == 0)
        {
            return false;
        }

        if (pid == _lastCheckedPid)
        {
            return _lastCheckedPidIsAgy;
        }

        bool isAgy = false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            isAgy = proc.ProcessName.Equals("Antigravity", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            isAgy = false;
        }

        _lastCheckedPid = pid;
        _lastCheckedPidIsAgy = isAgy;
        return isAgy;
    }

    private bool TryGetWindowBoundsDip(IntPtr hWnd, out Rect boundsDip)
    {
        if (!TryGetNativeWindowRect(hWnd, out var rect))
        {
            boundsDip = default;
            return false;
        }

        var topLeft = DeviceToDip(rect.Left, rect.Top);
        var bottomRight = DeviceToDip(rect.Right, rect.Bottom);
        boundsDip = new Rect(
            topLeft.X,
            topLeft.Y,
            Math.Max(0, bottomRight.X - topLeft.X),
            Math.Max(0, bottomRight.Y - topLeft.Y));
        return true;
    }

    private static bool TryGetNativeWindowRect(IntPtr hWnd, out NativeRect rect)
    {
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<NativeRect>()) == 0)
        {
            return true;
        }
        return GetWindowRect(hWnd, out rect);
    }

    private Point DeviceToDip(int x, int y)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            return source.CompositionTarget.TransformFromDevice.Transform(new Point(x, y));
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    private Point GetCursorDipPosition()
    {
        GetCursorPos(out var pt);
        return DeviceToDip(pt.X, pt.Y);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class PetConfig
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("spritesheet")]
        public string? Spritesheet { get; set; }

        [JsonPropertyName("size")]
        public double Size { get; set; }

        [JsonPropertyName("speed")]
        public double Speed { get; set; }

        [JsonPropertyName("behavior")]
        public string? Behavior { get; set; }
    }

    private sealed class UsageResponse
    {
        [JsonPropertyName("models")]
        public List<UsageModel>? Models { get; set; }

        [JsonPropertyName("planStatus")]
        public PlanStatus? PlanStatus { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class UsageModel
    {
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }

        [JsonPropertyName("remainingFraction")]
        public double? RemainingFraction { get; set; }

        [JsonPropertyName("resetTime")]
        public string? ResetTime { get; set; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; set; }
    }

    private sealed class PlanStatus
    {
        [JsonPropertyName("availablePromptCredits")]
        public double? AvailablePromptCredits { get; set; }

        [JsonPropertyName("availableFlowCredits")]
        public double? AvailableFlowCredits { get; set; }
    }

    private sealed record ModelUsageItem(
        string Label,
        string PercentText,
        string ResetText,
        Brush BarBrush,
        Brush TextBrush,
        double BarWidth);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;

namespace PngGridViewer;

/// <summary>
/// 16の倍数サイズのPNGを最近傍補間で表示する。16x16セルを白⇔緑で塗る。
/// 鉛筆（ドラッグで通過したセルを切り替え）と範囲フィル（ドラッグした矩形を一括で塗る）を切り替えられる。
/// 画像パスと緑セルの状態は .png16 ファイルに保存・読込できる。境界線は1秒ごとに黒/白で切り替わる。
/// </summary>
public sealed partial class MainPage : Page
{
    private enum PaintTool
    {
        Pencil,
        RangeFill,
    }

    private const int CellSize = 16;
    private const double ZoomStep = 1.25;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 16.0;
    private const double WheelPixelsPerNotch = 60.0;

    private static readonly Color BackgroundColor = Color.FromArgb(255, 32, 32, 32);
    private static readonly Color HighlightColor = Color.FromArgb(110, 0, 200, 80);
    private static readonly Color RangePreviewFill = Color.FromArgb(70, 255, 255, 255);

    private readonly DispatcherTimer _blinkTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<CellPos> _greenCells = new();

    private PaintTool _tool = PaintTool.Pencil;

    // 鉛筆: 現在のドラッグで既に切り替えたセル（同じセルを1回のドラッグで何度も反転させないため）
    private readonly HashSet<CellPos> _strokeCells = new();
    private bool _isPainting;

    // 範囲フィル: ドラッグ中の矩形（セル座標）
    private bool _isRangeDragging;
    private CellPos? _rangeStart;
    private CellPos? _rangeEnd;

    // 元に戻す／やり直し: 緑セル集合のスナップショット（1回のドラッグ＝1操作）
    private readonly Stack<HashSet<CellPos>> _undoStack = new();
    private readonly Stack<HashSet<CellPos>> _redoStack = new();
    private HashSet<CellPos> _strokeStartCells = new();

    private CanvasBitmap? _bitmap;
    private StorageFile? _imageFile;
    private StorageFile? _projectFile;
    private Color _gridColor = Microsoft.UI.Colors.Black;
    private bool _isWhite;
    private int _imageWidth;
    private int _imageHeight;
    private double _zoom = 1.0;

    public MainPage()
    {
        InitializeComponent();

        _blinkTimer.Tick += (_, _) => ToggleGridColor();
        _blinkTimer.Start();

        // Ctrl+- / Ctrl+= (メインキーボードとテンキー) / Ctrl+O / Ctrl+S
        RegisterAccelerator(VirtualKey.Subtract, ZoomOut);
        RegisterAccelerator((VirtualKey)189, ZoomOut);   // OEM_MINUS
        RegisterAccelerator(VirtualKey.Add, ZoomIn);
        RegisterAccelerator((VirtualKey)187, ZoomIn);    // OEM_PLUS
        RegisterAccelerator(VirtualKey.O, () => _ = OpenFileAsync());
        RegisterAccelerator(VirtualKey.S, () => _ = SaveProjectAsync(saveAs: false));
        RegisterAccelerator(VirtualKey.Z, Undo);
        RegisterAccelerator(VirtualKey.Z, Redo, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        RegisterAccelerator(VirtualKey.Y, Redo);

        // ScrollViewer の標準ホイール処理は無効にし（XAML で Disabled）、
        // ホイール入力はすべて Viewer_PointerWheelChanged で処理する
        Viewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Viewer_PointerWheelChanged), true);

        Viewer.PointerPressed += Viewer_PointerPressed;
        Viewer.PointerMoved += Viewer_PointerMoved;
        Viewer.PointerReleased += (_, e) =>
        {
            EndStroke(commit: true);
            Viewer.ReleasePointerCapture(e.Pointer);
        };
        Viewer.PointerCanceled += (_, _) => EndStroke(commit: false);
        Viewer.PointerCaptureLost += (_, _) => EndStroke(commit: true);

        SetTool(PaintTool.Pencil);

        Loaded += (_, _) => Viewer.Focus(FocusState.Programmatic);
    }

    private void RegisterAccelerator(VirtualKey key, Action action, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers,
        };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void ToggleGridColor()
    {
        _isWhite = !_isWhite;
        _gridColor = _isWhite ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        ImageCanvas.Invalidate();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e) => await OpenFileAsync();

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(saveAs: false);

    private async void SaveAsButton_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(saveAs: true);

    private void PencilButton_Click(object sender, RoutedEventArgs e) => SetTool(PaintTool.Pencil);

    private void RangeFillButton_Click(object sender, RoutedEventArgs e) => SetTool(PaintTool.RangeFill);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ZoomOut();

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ZoomIn();

    private void SetTool(PaintTool tool)
    {
        _tool = tool;
        PencilButton.IsChecked = tool == PaintTool.Pencil;
        RangeFillButton.IsChecked = tool == PaintTool.RangeFill;
        UpdateStatus();
    }

    private void ZoomIn() => SetZoom(_zoom * ZoomStep);

    private void ZoomOut() => SetZoom(_zoom / ZoomStep);

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyContentSize();
        ImageCanvas.Invalidate();
        UpdateStatus();
    }

    /// <summary>
    /// ビューア上の指定位置(ポインタ)が同じ画像上の点に留まるように拡大縮小する。
    /// </summary>
    private void ZoomAtPointer(double factor, Point viewerPoint)
    {
        double oldZoom = _zoom;
        double newZoom = Math.Clamp(oldZoom * factor, MinZoom, MaxZoom);
        if (newZoom == oldZoom)
        {
            return;
        }

        // ポインタ下の画像上の座標（ズーム前の座標系）
        double contentX = (Viewer.HorizontalOffset + viewerPoint.X) / oldZoom;
        double contentY = (Viewer.VerticalOffset + viewerPoint.Y) / oldZoom;

        _zoom = newZoom;
        ApplyContentSize();
        Viewer.UpdateLayout();
        Viewer.ChangeView(
            contentX * newZoom - viewerPoint.X,
            contentY * newZoom - viewerPoint.Y,
            null,
            true);

        ImageCanvas.Invalidate();
        UpdateStatus();
    }

    /// <summary>
    /// スクロール範囲を現在の表示倍率に合わせる。
    /// </summary>
    private void ApplyContentSize()
    {
        if (_imageWidth == 0)
        {
            return;
        }

        ImageHost.Width = _imageWidth * _zoom;
        ImageHost.Height = _imageHeight * _zoom;
    }

    /// <summary>
    /// Shift + マウスホイールで水平、通常で垂直、Ctrl + ホイールで拡大縮小する。
    /// </summary>
    private void Viewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewer);
        int wheelDelta = point.Properties.MouseWheelDelta;
        bool shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        bool ctrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (ctrlDown)
        {
            ZoomAtPointer(Math.Pow(ZoomStep, wheelDelta / 120.0), point.Position);
            e.Handled = true;
            return;
        }

        // 標準の1ノッチ(120)あたりのスクロール量を画面上のpxに換算
        double step = wheelDelta * WheelPixelsPerNotch / 120.0;

        if (shiftDown)
        {
            Viewer.ChangeView(Viewer.HorizontalOffset - step, null, null, false);
        }
        else
        {
            Viewer.ChangeView(null, Viewer.VerticalOffset - step, null, false);
        }

        e.Handled = true;
    }

    private void Viewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ImageCanvas.Invalidate();
    }

    /// <summary>
    /// 左ボタンを押すと、現在のツールで操作を開始する。
    /// </summary>
    private void Viewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewer);
        if (!point.Properties.IsLeftButtonPressed || _imageWidth == 0)
        {
            return;
        }

        if (!TryGetCell(point.Position, clamp: false, out var cell))
        {
            return;
        }

        // このドラッグ操作の開始時点の状態（履歴・キャンセル用）
        _strokeStartCells = new HashSet<CellPos>(_greenCells);
        Viewer.CapturePointer(e.Pointer);

        if (_tool == PaintTool.RangeFill)
        {
            _isRangeDragging = true;
            _rangeStart = cell;
            _rangeEnd = cell;
            ImageCanvas.Invalidate();
        }
        else
        {
            _isPainting = true;
            _strokeCells.Clear();
            PaintCell(cell);
        }

        e.Handled = true;
    }

    private void Viewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewer);
        bool pressed = point.Properties.IsLeftButtonPressed;

        if (_isRangeDragging)
        {
            // 範囲の終点はビューア外でも画像内に収める
            if (pressed && TryGetCell(point.Position, clamp: true, out var rangeCell))
            {
                _rangeEnd = rangeCell;
                ImageCanvas.Invalidate();
            }
            return;
        }

        if (!_isPainting)
        {
            return;
        }

        if (!pressed)
        {
            EndStroke(commit: true);
            return;
        }

        if (TryGetCell(point.Position, clamp: false, out var cell))
        {
            PaintCell(cell);
        }
    }

    /// <summary>
    /// ドラッグ操作を終了する。commit が true の場合、範囲フィルを確定する。
    /// </summary>
    private void EndStroke(bool commit)
    {
        bool wasStroke = _isRangeDragging || _isPainting;

        if (_isRangeDragging && commit)
        {
            CommitRangeFill();
        }

        if (wasStroke)
        {
            if (commit)
            {
                PushUndo(_strokeStartCells);
            }
            else
            {
                // キャンセル時は操作前の状態に戻す
                ReplaceGreenCells(_strokeStartCells);
            }
        }

        _isRangeDragging = false;
        _rangeStart = null;
        _rangeEnd = null;
        _isPainting = false;
        _strokeCells.Clear();

        ImageCanvas.Invalidate();
        UpdateStatus();
    }

    /// <summary>
    /// 操作前の状態を元に戻す履歴に積む。内容が変わっていない場合は積まない。
    /// </summary>
    private void PushUndo(HashSet<CellPos> before)
    {
        if (before.SetEquals(_greenCells))
        {
            return;
        }

        _undoStack.Push(before);
        _redoStack.Clear();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        _redoStack.Push(new HashSet<CellPos>(_greenCells));
        ReplaceGreenCells(_undoStack.Pop());
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Push(new HashSet<CellPos>(_greenCells));
        ReplaceGreenCells(_redoStack.Pop());
    }

    private void ReplaceGreenCells(HashSet<CellPos> cells)
    {
        _greenCells.Clear();
        _greenCells.UnionWith(cells);
        ImageCanvas.Invalidate();
        UpdateStatus();
    }

    /// <summary>
    /// 鉛筆：セルを白⇔緑で切り替える（1回のドラッグで同じセルは1回だけ）。
    /// </summary>
    private void PaintCell(CellPos cell)
    {
        if (!_strokeCells.Add(cell))
        {
            return;
        }

        if (!_greenCells.Remove(cell))
        {
            _greenCells.Add(cell);
        }

        ImageCanvas.Invalidate();
        UpdateStatus();
    }

    /// <summary>
    /// 範囲フィル：ドラッグした矩形を一括で塗る。
    /// 開始セルが白なら範囲を緑に、緑なら範囲を白にする。
    /// </summary>
    private void CommitRangeFill()
    {
        if (_rangeStart is not { } start || _rangeEnd is not { } end)
        {
            return;
        }

        GetRange(start, end, out int minColumn, out int maxColumn, out int minRow, out int maxRow);
        bool fillGreen = !_greenCells.Contains(start);

        for (int row = minRow; row <= maxRow; row++)
        {
            for (int column = minColumn; column <= maxColumn; column++)
            {
                var cell = new CellPos(column, row);
                if (fillGreen)
                {
                    _greenCells.Add(cell);
                }
                else
                {
                    _greenCells.Remove(cell);
                }
            }
        }
    }

    private static void GetRange(
        CellPos a,
        CellPos b,
        out int minColumn,
        out int maxColumn,
        out int minRow,
        out int maxRow)
    {
        minColumn = Math.Min(a.Column, b.Column);
        maxColumn = Math.Max(a.Column, b.Column);
        minRow = Math.Min(a.Row, b.Row);
        maxRow = Math.Max(a.Row, b.Row);
    }

    /// <summary>
    /// ビューア上の位置が含まれるセル番号を求める。clamp が true なら画像の範囲内に収める。
    /// </summary>
    private bool TryGetCell(Point viewerPoint, bool clamp, out CellPos cell)
    {
        cell = default;
        if (_imageWidth == 0)
        {
            return false;
        }

        // ビューア上の位置 → 画像上の座標（ピクセル）
        double imageX = (Viewer.HorizontalOffset + viewerPoint.X) / _zoom;
        double imageY = (Viewer.VerticalOffset + viewerPoint.Y) / _zoom;
        if (!clamp && (imageX < 0 || imageY < 0 || imageX >= _imageWidth || imageY >= _imageHeight))
        {
            return false;
        }

        int maxColumn = _imageWidth / CellSize - 1;
        int maxRow = _imageHeight / CellSize - 1;
        int column = Math.Clamp((int)Math.Floor(imageX / CellSize), 0, maxColumn);
        int row = Math.Clamp((int)Math.Floor(imageY / CellSize), 0, maxRow);

        cell = new CellPos(column, row);
        return true;
    }

    /// <summary>
    /// 最近傍補間で画像を描き、緑セルを塗り、範囲フィルのプレビューを描き、その上に境界線を引く。
    /// </summary>
    private void ImageCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(BackgroundColor);

        if (_bitmap == null)
        {
            return;
        }

        float offsetX = (float)Viewer.HorizontalOffset;
        float offsetY = (float)Viewer.VerticalOffset;
        float drawWidth = (float)(_imageWidth * _zoom);
        float drawHeight = (float)(_imageHeight * _zoom);
        float cellSize = (float)(CellSize * _zoom);

        ds.DrawImage(
            _bitmap,
            new Rect(-offsetX, -offsetY, drawWidth, drawHeight),
            new Rect(0, 0, _imageWidth, _imageHeight),
            1f,
            CanvasImageInterpolation.NearestNeighbor);

        // 緑のセルを塗る（境界線より下に描く）
        foreach (var cell in _greenCells)
        {
            ds.FillRectangle(
                cell.Column * cellSize - offsetX,
                cell.Row * cellSize - offsetY,
                cellSize,
                cellSize,
                HighlightColor);
        }

        for (int x = 0; x <= _imageWidth; x += CellSize)
        {
            float px = (float)(x * _zoom) - offsetX;
            ds.DrawLine(px, -offsetY, px, drawHeight - offsetY, _gridColor, 1f);
        }

        for (int y = 0; y <= _imageHeight; y += CellSize)
        {
            float py = (float)(y * _zoom) - offsetY;
            ds.DrawLine(-offsetX, py, drawWidth - offsetX, py, _gridColor, 1f);
        }

        // 範囲フィルのドラッグ中プレビュー
        if (_isRangeDragging && _rangeStart is { } start && _rangeEnd is { } end)
        {
            GetRange(start, end, out int minColumn, out int maxColumn, out int minRow, out int maxRow);
            var rect = new Rect(
                minColumn * cellSize - offsetX,
                minRow * cellSize - offsetY,
                (maxColumn - minColumn + 1) * cellSize,
                (maxRow - minRow + 1) * cellSize);

            ds.FillRectangle(rect, RangePreviewFill);
            ds.DrawRectangle(rect, Microsoft.UI.Colors.Yellow, 2f);
        }
    }

    private IntPtr GetWindowHandle()
    {
        return WindowNative.GetWindowHandle(App.MainAppWindow!);
    }

    private async Task OpenFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".png16");
        InitializeWithWindow.Initialize(picker, GetWindowHandle());

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null)
        {
            return;
        }

        if (file.FileType.Equals(".png16", StringComparison.OrdinalIgnoreCase))
        {
            await LoadProjectAsync(file);
        }
        else
        {
            _projectFile = null;
            _greenCells.Clear();
            await LoadPngAsync(file);
        }
    }

    /// <summary>
    /// PNG画像を読み込む。成功したら true を返す。
    /// </summary>
    private async Task<bool> LoadPngAsync(StorageFile file)
    {
        uint width;
        uint height;
        bool isPng;

        using (var stream = await file.OpenReadAsync())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            width = decoder.PixelWidth;
            height = decoder.PixelHeight;
            isPng = decoder.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId;
        }

        if (!isPng)
        {
            await ShowMessageAsync("PNGファイルではありません。");
            return false;
        }

        if (width % CellSize != 0 || height % CellSize != 0)
        {
            await ShowMessageAsync($"画像サイズが16の倍数ではありません（{width} x {height}）。");
            return false;
        }

        CanvasBitmap bitmap;
        using (var stream = await file.OpenReadAsync())
        {
            bitmap = await CanvasBitmap.LoadAsync(ImageCanvas, stream);
        }

        _bitmap?.Dispose();
        _bitmap = bitmap;
        _imageFile = file;
        _imageWidth = (int)width;
        _imageHeight = (int)height;
        _greenCells.Clear();
        _undoStack.Clear();
        _redoStack.Clear();

        SetZoom(1.0);
        Viewer.ChangeView(0, 0, null, true);
        Viewer.Focus(FocusState.Programmatic);
        return true;
    }

    /// <summary>
    /// .png16 ファイルを読み込み、記録された画像と緑セルの状態を復元する。
    /// </summary>
    private async Task LoadProjectAsync(StorageFile file)
    {
        Png16Project? project;
        try
        {
            string json = await FileIO.ReadTextAsync(file);
            project = JsonSerializer.Deserialize<Png16Project>(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            await ShowMessageAsync("プロジェクトファイルを読み込めません。");
            return;
        }

        if (project == null || string.IsNullOrEmpty(project.ImagePath))
        {
            await ShowMessageAsync("プロジェクトファイルに画像パスがありません。");
            return;
        }

        StorageFile imageFile;
        try
        {
            imageFile = await StorageFile.GetFileFromPathAsync(project.ImagePath);
        }
        catch (Exception)
        {
            await ShowMessageAsync($"画像ファイルが見つかりません：\n{project.ImagePath}");
            return;
        }

        if (!await LoadPngAsync(imageFile))
        {
            return;
        }

        _projectFile = file;
        foreach (var cell in project.GreenCells)
        {
            _greenCells.Add(cell);
        }

        ImageCanvas.Invalidate();
        UpdateStatus();
    }

    /// <summary>
    /// 画像パスと緑セルの状態を .png16 ファイルに保存する。
    /// </summary>
    private async Task SaveProjectAsync(bool saveAs)
    {
        if (_imageFile == null)
        {
            await ShowMessageAsync("先にPNG画像を開いてください。");
            return;
        }

        StorageFile? file = saveAs ? null : _projectFile;
        if (file == null)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(_imageFile.Name),
            };
            picker.FileTypeChoices.Add("PNG16 プロジェクト", new List<string> { ".png16" });
            InitializeWithWindow.Initialize(picker, GetWindowHandle());

            file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }
        }

        var project = new Png16Project
        {
            ImagePath = _imageFile.Path,
            GreenCells = _greenCells.ToList(),
        };
        string json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
        await FileIO.WriteTextAsync(file, json);

        _projectFile = file;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_imageWidth == 0)
        {
            return;
        }

        string name = _projectFile?.Name ?? _imageFile?.Name ?? "";
        string toolName = _tool == PaintTool.Pencil ? "鉛筆（ドラッグで白⇔緑）" : "範囲フィル（ドラッグで矩形を一括塗り）";
        StatusText.Text =
            $"{name}  /  {_imageWidth} x {_imageHeight}  /  表示倍率 {_zoom * 100:0}%  /  緑 {_greenCells.Count} セル  /  {toolName}" +
            "  （Ctrl+S で保存、Ctrl+- / Ctrl+= / Ctrl+ホイールで拡大縮小、Shift+ホイールで横移動）";
    }

    private async Task ShowMessageAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "エラー",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}

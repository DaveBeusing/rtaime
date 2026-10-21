// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public partial class SequenceTimelineControl : UserControl
{
    private bool _gestureActive;
    private bool _initialized;
    private SequenceTimelineEditorViewModel? Editor => DataContext as SequenceTimelineEditorViewModel;

    public SequenceTimelineControl() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateRulerViewport();
        if (_initialized) return;
        _initialized = true;
        if (Editor is { } model) await model.RefreshAsync();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRulerViewport();

    private void UpdateRulerViewport()
    {
        // The ruler owns the same horizontal coordinate system as the clips.
        // Parent widths include the track label column and may include dock chrome.
        if (Editor is { } editor && SequenceRuler.ActualWidth > 0)
            editor.SetViewport(SequenceRuler.ActualWidth);
    }

    private void OnRulerClick(object sender, MouseButtonEventArgs e)
    {
        if (Editor is not { HasSequence: true } model ||
            FindParent<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
            return;
        var cursor = e.GetPosition(SequenceRuler);
        if (cursor.X < 0 || cursor.X > SequenceRuler.ActualWidth) return;
        model.PlaceCursor(cursor.X, SequenceRuler.ActualWidth);
        Focus();
        e.Handled = true;
    }

    private void OnClipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Editor is not { HasSequence: true } model || sender is not Border border ||
            border.DataContext is not SequenceEditorClip clip)
            return;
        Focus();
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        model.SelectClip(clip.Id, toggle: ctrl, extend: shift);
        if (ctrl || shift || !model.CanEdit) { e.Handled = true; return; }

        var local = e.GetPosition(border).X;
        var kind = local <= 9 ? SequenceEditorGestureKind.TrimStart :
            local >= border.ActualWidth - 9 ? SequenceEditorGestureKind.TrimEnd :
            SequenceEditorGestureKind.Move;
        var absoluteX = e.GetPosition(SequenceRuler).X;
        var pointer = SequenceTimelineGeometry.ToTick(
            Math.Clamp(absoluteX, 0, SequenceRuler.ActualWidth),
            Math.Max(1, SequenceRuler.ActualWidth),
            model.VisibleStart, model.VisibleEnd,
            model.Sequence!.FrameRate);
        if (!model.BeginGesture(clip.Id, kind, pointer)) return;
        _gestureActive = CaptureMouse();
        if (!_gestureActive) model.CancelGesture();
        e.Handled = true;
    }

    private void OnCapturedMouseMove(object sender, MouseEventArgs e)
    {
        if (!_gestureActive || e.LeftButton != MouseButtonState.Pressed ||
            Editor is not { HasSequence: true } model)
            return;
        var x = e.GetPosition(SequenceRuler).X;
        var tick = SequenceTimelineGeometry.ToTick(
            Math.Clamp(x, 0, SequenceRuler.ActualWidth),
            Math.Max(1, SequenceRuler.ActualWidth),
            model.VisibleStart, model.VisibleEnd, model.Sequence!.FrameRate);
        model.UpdateGesture(tick);
        e.Handled = true;
    }

    private async void OnCapturedMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gestureActive || e.ChangedButton != MouseButton.Left) return;
        _gestureActive = false;
        ReleaseMouseCapture();
        if (Editor is { } model) await model.CompleteGestureAsync();
        e.Handled = true;
    }

    private void OnCapturedMouseCaptureLost(object sender, MouseEventArgs e)
    {
        if (!_gestureActive) return;
        _gestureActive = false;
        Editor?.CancelGesture();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Editor is not { } model) return;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape)
        {
            model.CancelGesture();
            _gestureActive = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        ICommand? command = e.Key switch
        {
            Key.Z when ctrl => model.UndoCommand,
            Key.Y when ctrl => model.RedoCommand,
            Key.C when ctrl => model.CopyCommand,
            Key.V when ctrl => model.PasteCommand,
            Key.Delete when !ctrl => model.DeleteCommand,
            Key.Add or Key.OemPlus when ctrl => model.ZoomInCommand,
            Key.Subtract or Key.OemMinus when ctrl => model.ZoomOutCommand,
            _ => null
        };
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Editor is not { HasSequence: true } model) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (ctrl) model.Zoom = Math.Clamp(model.Zoom * (e.Delta > 0 ? 1.25 : 0.8), 1, 64);
        else if (shift) model.Scroll(e.Delta > 0 ? -0.35 : 0.35);
        else model.FirstTrack += e.Delta > 0 ? -1 : 1;
        e.Handled = true;
    }

    private void OnTrackDragOver(object sender, DragEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as SequenceEditorRow;
        var drag = e.Data.GetDataPresent(typeof(MediaAssetDragPayload))
            ? e.Data.GetData(typeof(MediaAssetDragPayload)) as MediaAssetDragPayload : null;
        var item = drag?.Primary;
        e.Effects = Editor?.CanEdit == true &&
            row is not null && row.Kind is (SequenceMediaKind.Video or SequenceMediaKind.Audio) &&
            item is { Kind: MediaPoolItemKind.Clip, IsOnline: true } &&
            Guid.TryParse(item.ReferenceId, out var asset) && asset != Guid.Empty
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTrackDrop(object sender, DragEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as SequenceEditorRow;
        var drag = e.Data.GetDataPresent(typeof(MediaAssetDragPayload))
            ? e.Data.GetData(typeof(MediaAssetDragPayload)) as MediaAssetDragPayload : null;
        var item = drag?.Primary;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (Editor is not { CanEdit: true } editor ||
            row is null || row.Kind is not (SequenceMediaKind.Video or SequenceMediaKind.Audio) ||
            item is not { Kind: MediaPoolItemKind.Clip, IsOnline: true } ||
            !Guid.TryParse(item.ReferenceId, out var asset) || asset == Guid.Empty)
            return;
        await editor.InsertCatalogueAssetAsync(row.Id, asset);
        e.Effects = DragDropEffects.Copy;
    }

    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T value) return value;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}

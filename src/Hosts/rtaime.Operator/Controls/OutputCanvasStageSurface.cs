// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using rtaime.Control.Contracts;

namespace rtaime.Operator.Controls;

/// <summary>
/// Geometry-only WPF authoring canvas. Never renders or replaces Program pixels.
/// Changes are previewed during drag and committed to local draft once on release.
/// </summary>
public sealed class OutputCanvasStageSurface : FrameworkElement
{
    private enum DragKind { None, Pan, Move, Rotate, Corner }
    private DragKind _drag;
    private int _corner;
    private Point _pressView;
    private Point _previousView;
    private OutputCanvasPoint _pressCanvas;
    private OutputCanvasQuad? _originalQuad;
    private Guid _surfaceId;
    private OutputCanvasEditorViewModel? _attached;
    private OutputCanvasEditorViewModel? _gestureEditor;

    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(OutputCanvasEditorViewModel), typeof(OutputCanvasStageSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, EditorChanged));

    public OutputCanvasEditorViewModel? Editor
    {
        get => (OutputCanvasEditorViewModel?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public OutputCanvasStageSurface()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
        Loaded += OnStageLoaded;
        Unloaded += OnStageUnloaded;
    }

    private static void EditorChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var surface = (OutputCanvasStageSurface)obj;
        surface.CancelDrag();
        if (surface.IsMouseCaptured) surface.ReleaseMouseCapture();
        surface.DetachEditor();
        surface.AttachEditor();
        surface.InvalidateVisual();
    }

    private void OnStageLoaded(object sender, RoutedEventArgs e) => AttachEditor();

    private void OnStageUnloaded(object sender, RoutedEventArgs e)
    {
        CancelDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
        DetachEditor();
    }

    private void AttachEditor()
    {
        if (!IsLoaded || Editor is null || ReferenceEquals(_attached, Editor)) return;
        DetachEditor();
        _attached = Editor;
        _attached.PropertyChanged += ModelChanged;
        InvalidateVisual();
    }

    private void DetachEditor()
    {
        if (_attached is null) return;
        _attached.PropertyChanged -= ModelChanged;
        _attached = null;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Health, save state and unrelated numeric-field notifications must
        // not rebuild the WPF stage when its visible geometry has not changed.
        if (e.PropertyName is null or nameof(OutputCanvasEditorViewModel.SelectedCanvas)
            or nameof(OutputCanvasEditorViewModel.SelectedSurfaceId)
            or nameof(OutputCanvasEditorViewModel.CanvasWidth)
            or nameof(OutputCanvasEditorViewModel.CanvasHeight)
            or nameof(OutputCanvasEditorViewModel.VisibleSurfaces)
            or nameof(OutputCanvasEditorViewModel.PreviewQuad)
            or nameof(OutputCanvasEditorViewModel.Zoom)
            or nameof(OutputCanvasEditorViewModel.PanX)
            or nameof(OutputCanvasEditorViewModel.PanY)
            or nameof(OutputCanvasEditorViewModel.SnapEnabled)
            or nameof(OutputCanvasEditorViewModel.ShowGuides))
            InvalidateVisual();
    }

    private OutputCanvasViewport View => new(
        ActualWidth,ActualHeight, Editor?.CanvasWidth ?? 0, Editor?.CanvasHeight ?? 0,
        Editor?.Zoom ?? 1, Editor?.PanX ?? 0, Editor?.PanY ?? 0);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brush("OperatorAlternateSurfaceBrush", Brushes.Black),
            null, new Rect(RenderSize));
        var editor = Editor;
        if (editor is null || View.Scale <= 0)
        {
            Label(dc,"CANVAS UNAVAILABLE",new Point(18,20),Brushes.LightGray,13);
            return;
        }

        var view = View;
        var upper = view.ToView(new(0,0));
        var lower = view.ToView(new(editor.CanvasWidth,editor.CanvasHeight));
        var bounds = new Rect(upper,lower);
        var outline = Brush("OperatorBorderBrush", Brushes.Gray);
        var accent = Brush("OperatorAccentBrush", Brushes.Cyan);
        var text = Brush("OperatorTextBrush", Brushes.White);
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15,20,26)),new Pen(outline,1),bounds);
        if (editor.ShowGuides)
        {
            var gridStep = view.Scale > .4 ? 100 : 250;
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(65,90,125,148)),1);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (var x=gridStep;x<editor.CanvasWidth;x+=gridStep)
            {
                var a=OutputCanvasViewport.Align(view.ToView(new(x,0)),dpi);
                var b=OutputCanvasViewport.Align(view.ToView(new(x,editor.CanvasHeight)),dpi);
                dc.DrawLine(gridPen,a,b);
            }
            for (var y=gridStep;y<editor.CanvasHeight;y+=gridStep)
            {
                var a=OutputCanvasViewport.Align(view.ToView(new(0,y)),dpi);
                var b=OutputCanvasViewport.Align(view.ToView(new(editor.CanvasWidth,y)),dpi);
                dc.DrawLine(gridPen,a,b);
            }
            var midpoint=view.ToView(new(editor.CanvasWidth/2d,editor.CanvasHeight/2d));
            var guidePen=new Pen(new SolidColorBrush(Color.FromArgb(100,80,179,204)),1);
            dc.DrawLine(guidePen,new(midpoint.X-11,midpoint.Y),new(midpoint.X+11,midpoint.Y));
            dc.DrawLine(guidePen,new(midpoint.X,midpoint.Y-11),new(midpoint.X,midpoint.Y+11));
        }

        foreach (var surface in editor.VisibleSurfaces)
        {
            var selected=surface.SurfaceId==editor.SelectedSurfaceId;
            var quad=selected ? editor.PreviewQuad ?? surface.Destination : surface.Destination;
            var points=Points(quad,view);
            var geometry=Shape(points);
            var fill=new SolidColorBrush(selected ? Color.FromArgb(52,40,191,214) :
                Color.FromArgb(36,120,165,179));
            dc.DrawGeometry(fill,new Pen(selected?accent:outline,selected?2:1),geometry);
            if (selected)
            {
                var handlePen=new Pen(accent,1);
                foreach (var p in points)
                    dc.DrawRectangle(Brush("OperatorPanelBrush",Brushes.Black),handlePen,
                        new Rect(p.X-4.5,p.Y-4.5,9,9));
                Label(dc,$"SURFACE · Z{surface.ZOrder}",points[0]+new Vector(8,-23),text,10);
            }
        }

        dc.Pop();
        Label(dc,"GEOMETRY DRAFT · NOT PROGRAM PIXELS",new Point(12,12),text,10);
        Label(dc,$"{editor.CanvasWidth} × {editor.CanvasHeight}  ·  {editor.Zoom*100:0}%  ·  " +
            (editor.SnapEnabled?"SNAP 10":"FREE"),new Point(12,Math.Max(30,ActualHeight-23)),text,10);
    }

    private Brush Brush(string resource, Brush fallback) => TryFindResource(resource) as Brush ?? fallback;

    private void Label(DrawingContext context,string value,Point point,Brush brush,double size)
    {
        var pixelsPerDip=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text=new FormattedText(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),size,brush,pixelsPerDip)
        { MaxTextWidth=Math.Max(1,ActualWidth-20) };
        context.DrawText(text,point);
    }

    private static Point[] Points(OutputCanvasQuad q,OutputCanvasViewport view) =>
        [view.ToView(q.TopLeft),view.ToView(q.TopRight),view.ToView(q.BottomRight),view.ToView(q.BottomLeft)];

    private static StreamGeometry Shape(IReadOnlyList<Point> points)
    {
        var shape=new StreamGeometry();
        using (var g=shape.Open())
        {
            g.BeginFigure(points[0],true,true);
            g.PolyLineTo(points.Skip(1).ToList(),true,true);
        }
        shape.Freeze();
        return shape;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton==MouseButton.Middle)
        {
            _drag=DragKind.Pan;
            _previousView=e.GetPosition(this);
            if (!CaptureMouse()) CancelDrag();
            e.Handled=true;
        }
        else if (e.ChangedButton==MouseButton.Left && Editor is { CanEdit:true } editor)
        {
            var pos=e.GetPosition(this);
            _pressView=pos;
            _pressCanvas=View.ToCanvas(pos);
            _originalQuad=null;
            _drag=DragKind.None;
            var selected=editor.SelectedSurfaceId;
            foreach (var surface in editor.VisibleSurfaces.Reverse())
            {
                var points=Points(surface.Destination,View);
                var corner=Array.FindIndex(points,p=>(p-pos).Length<=12);
                if (corner<0 && !Inside(_pressCanvas,surface.Destination)) continue;
                editor.SelectSurface(surface.SurfaceId);
                _surfaceId=surface.SurfaceId;
                _originalQuad=surface.Destination;
                _gestureEditor=editor;
                _corner=corner;
                _drag=corner>=0?DragKind.Corner:
                    (Keyboard.Modifiers&ModifierKeys.Alt)!=0?DragKind.Rotate:DragKind.Move;
                break;
            }
            if (_drag!=DragKind.None)
            {
                Focus();
                if (!CaptureMouse()) CancelDrag();
                e.Handled=true;
            }
        }
    }

    private static bool Inside(OutputCanvasPoint p,OutputCanvasQuad q)
    {
        OutputCanvasPoint[] a=[q.TopLeft,q.TopRight,q.BottomRight,q.BottomLeft];
        for(var i=0;i<4;i++)
        {
            var from=a[i]; var to=a[(i+1)%4];
            if ((to.X-from.X)*(p.Y-from.Y)-(to.Y-from.Y)*(p.X-from.X)<0) return false;
        }
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Editor is not { } editor || _drag==DragKind.None) return;
        if (!IsMouseCaptured)
        {
            CancelDrag();
            return;
        }
        var pointer=e.GetPosition(this);
        if (_drag==DragKind.Pan)
        {
            editor.Pan(pointer.X-_previousView.X,pointer.Y-_previousView.Y);
            _previousView=pointer;
            e.Handled=true;
            return;
        }
        if (_originalQuad is null) return;
        var canvas=View.ToCanvas(pointer);
        var dx=canvas.X-_pressCanvas.X;
        var dy=canvas.Y-_pressCanvas.Y;
        if ((Keyboard.Modifiers&ModifierKeys.Shift)!=0 && _drag==DragKind.Move)
        {
            if(Math.Abs(dx)>Math.Abs(dy))dy=0;else dx=0;
        }
        OutputCanvasPoint[] points=[
            _originalQuad.TopLeft,_originalQuad.TopRight,_originalQuad.BottomRight,_originalQuad.BottomLeft];
        if(_drag==DragKind.Move)
        {
            for(var i=0;i<4;i++)
                points[i]=OutputCanvasViewport.Snap(new(points[i].X+dx,points[i].Y+dy),
                    editor.SnapEnabled,(Keyboard.Modifiers&ModifierKeys.Control)!=0);
        }
        else if(_drag==DragKind.Corner)
        {
            points[_corner]=OutputCanvasViewport.Snap(canvas,
                editor.SnapEnabled,(Keyboard.Modifiers&ModifierKeys.Control)!=0);
        }
        else if(_drag==DragKind.Rotate)
        {
            var center=new OutputCanvasPoint(points.Average(p=>p.X),points.Average(p=>p.Y));
            var from=Math.Atan2(_pressCanvas.Y-center.Y,_pressCanvas.X-center.X);
            var to=Math.Atan2(canvas.Y-center.Y,canvas.X-center.X);
            var angle=to-from;
            var cos=Math.Cos(angle);var sin=Math.Sin(angle);
            for(var i=0;i<4;i++)
            {
                var x=points[i].X-center.X;var y=points[i].Y-center.Y;
                points[i]=new(center.X+x*cos-y*sin,center.Y+x*sin+y*cos);
            }
        }
        var draft=new OutputCanvasQuad(points[0],points[1],points[2],points[3]);
        editor.PreviewSurfaceQuad(_surfaceId,draft);
        e.Handled=true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag==DragKind.None) return;
        if (_drag==DragKind.Pan && e.ChangedButton!=MouseButton.Middle)return;
        if (_drag!=DragKind.Pan && e.ChangedButton!=MouseButton.Left)return;
        if (Editor is { } editor && _originalQuad is not null &&
            editor.PreviewQuad is { } preview)
            editor.CommitSurfaceQuad(_surfaceId,preview);
        CancelDrag();
        ReleaseMouseCapture();
        e.Handled=true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelDrag();
    }

    private void CancelDrag()
    {
        if (_originalQuad is not null) _gestureEditor?.PreviewSurfaceQuad(_surfaceId,null);
        _originalQuad=null;
        _gestureEditor=null;
        _drag=DragKind.None;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if(Editor is not { } editor || View.Scale<=0)return;
        var point=e.GetPosition(this);
        var logical=View.ToCanvas(point);
        editor.Zoom*=e.Delta>0?1.15:1/1.15;
        var moved=View.ToView(logical);
        editor.Pan(point.X-moved.X,point.Y-moved.Y);
        e.Handled=true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if(Editor is not { } editor)return;
        if(e.Key==Key.Escape)
        {
            CancelDrag(); if(IsMouseCaptured)ReleaseMouseCapture();
            e.Handled=true;return;
        }
        var step=(Keyboard.Modifiers&ModifierKeys.Shift)!=0?10d:1d;
        (double x,double y) delta=e.Key switch
        {
            Key.Left => (-step,0),Key.Right=>(step,0),
            Key.Up=>(0,-step),Key.Down=>(0,step),_=>(0,0)
        };
        if(delta!=(0d,0d))
        {
            editor.NudgeSelected(delta.x,delta.y);
            e.Handled=true;
        }
        else if(e.Key==Key.Home){editor.Fit();e.Handled=true;}
    }
}

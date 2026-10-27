// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using Xunit;
using System.Windows;
using rtaime.Control.Contracts;
using rtaime.Operator;

namespace rtaime.Tests.Operator;

public sealed class OutputCanvasViewportTests
{
    [Theory]
    [InlineData(960,540,1,0,0,1)]
    [InlineData(1280,720,.75,38,-22,1.25)]
    [InlineData(1600,900,2.6,-75,130,1.5)]
    [InlineData(1920,1080,.16,200,-155,2)]
    public void Canvas_pointer_and_numeric_roundtrip_is_invariant_across_zoom_pan_and_dpi(
        double width,double height,double zoom,double panX,double panY,double dpi)
    {
        var view=new OutputCanvasViewport(width,height,1920,1080,zoom,panX,panY);
        Assert.True(view.Scale>0);
        foreach(var point in new[]
            {new OutputCanvasPoint(0,0),new OutputCanvasPoint(960,540),
                new OutputCanvasPoint(1900,1060),new OutputCanvasPoint(201,376)})
        {
            var screen=view.ToView(point);
            var roundtrip=view.ToCanvas(screen);
            Assert.InRange(Math.Abs(point.X-roundtrip.X),0,1e-8);
            Assert.InRange(Math.Abs(point.Y-roundtrip.Y),0,1e-8);
            var aligned=OutputCanvasViewport.Align(screen,dpi);
            Assert.InRange(Math.Abs(aligned.X-screen.X),0,0.5/dpi+1e-8);
            Assert.InRange(Math.Abs(aligned.Y-screen.Y),0,0.5/dpi+1e-8);
        }
    }

    [Fact]
    public void Snap_is_opt_in_and_fine_mode_never_loses_fractional_coordinates()
    {
        var p=new OutputCanvasPoint(43.6,97.2);
        Assert.Equal(new(40d,100d),OutputCanvasViewport.Snap(p,true));
        Assert.Equal(p,OutputCanvasViewport.Snap(p,false));
        Assert.Equal(p,OutputCanvasViewport.Snap(p,true,fine:true));
    }

    [Fact]
    public void Rulers_and_safe_areas_use_the_identical_pan_zoom_pixel_transform()
    {
        var view = new OutputCanvasViewport(1180, 560, 1920, 1080, 1.3, -45, 33);
        var horizontal = view.Ruler(horizontal: true);
        var vertical = view.Ruler(horizontal: false);
        Assert.NotEmpty(horizontal);
        Assert.NotEmpty(vertical);
        Assert.InRange(horizontal.Count, 1, 64);
        Assert.InRange(vertical.Count, 1, 64);
        Assert.All(horizontal, mark =>
        {
            Assert.InRange(mark.LogicalPixel, 0, 1920);
            Assert.InRange(mark.ViewPosition, 0, view.Width);
            Assert.Equal(view.ToView(new(mark.LogicalPixel, 0)).X, mark.ViewPosition, 8);
        });
        Assert.All(vertical, mark =>
        {
            Assert.InRange(mark.LogicalPixel, 0, 1080);
            Assert.InRange(mark.ViewPosition, 0, view.Height);
            Assert.Equal(view.ToView(new(0, mark.LogicalPixel)).Y, mark.ViewPosition, 8);
        });
        var safe = view.SafeAreaBounds(.10);
        Assert.False(safe.IsEmpty);
        Assert.Equal(view.ToView(new(192, 108)).X, safe.Left, 8);
        Assert.Equal(view.ToView(new(1728, 972)).Y, safe.Bottom, 8);
        Assert.Equal(new OutputCanvasPoint(.25, .75),
            view.Normalize(new OutputCanvasPoint(480, 810)));
        // Warp corners may legitimately be outside the physical canvas extent.
        Assert.Equal(new OutputCanvasPoint(-.1, 1.2),
            view.Normalize(new OutputCanvasPoint(-192, 1296)));
        Assert.Empty((view with { Zoom = 0 }).Ruler(true));
        Assert.True((view with { Zoom = 0 }).SafeAreaBounds(.10).IsEmpty);
    }

    [Fact]
    public void Body_drag_grid_snap_translates_all_warped_vertices_by_exactly_one_offset()
    {
        var quad = new OutputCanvasQuad(new(13, 17), new(303, 20),
            new(270, 192), new(35, 180));
        var snapped = OutputCanvasViewport.TranslateRigid(quad, 4.6, 3.2, true);
        Assert.Equal(new OutputCanvasPoint(20, 20), snapped.TopLeft);
        var original = new[] { quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft };
        var moved = new[] { snapped.TopLeft, snapped.TopRight, snapped.BottomRight, snapped.BottomLeft };
        for (var i = 0; i < original.Length; i++)
        {
            Assert.Equal(7, moved[i].X - original[i].X, 8);
            Assert.Equal(3, moved[i].Y - original[i].Y, 8);
        }
        Assert.True(OutputCanvasDraftSession.IsValidQuad(snapped));
        var free = OutputCanvasViewport.TranslateRigid(quad, 4.6, 3.2, true, fine: true);
        Assert.Equal(quad.TopLeft.X + 4.6, free.TopLeft.X, 8);
        Assert.Equal(quad.BottomRight.Y + 3.2, free.BottomRight.Y, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutputCanvasViewport.TranslateRigid(quad, double.NaN, 1, true));
    }

    [Fact]
    public void Zoom_about_pointer_preserves_same_logical_point_when_pan_is_adjusted()
    {
        var original=new OutputCanvasViewport(1200,600,1920,1080,.8,22,-16);
        var cursor=new Point(380,230);
        var canvas=original.ToCanvas(cursor);
        var enlarged=original with { Zoom=original.Zoom*1.15 };
        var displaced=enlarged.ToView(canvas);
        enlarged=enlarged with
        {
            PanX=enlarged.PanX+cursor.X-displaced.X,
            PanY=enlarged.PanY+cursor.Y-displaced.Y
        };
        var stable=enlarged.ToView(canvas);
        Assert.InRange(Math.Abs(stable.X-cursor.X),0,1e-9);
        Assert.InRange(Math.Abs(stable.Y-cursor.Y),0,1e-9);
    }
}

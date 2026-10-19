// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using rtaime.Control.Contracts;
using rtaime.Operator;

using Xunit;

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

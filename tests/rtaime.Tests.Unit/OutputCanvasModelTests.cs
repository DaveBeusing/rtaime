// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control;
using rtaime.Control.Contracts;
namespace rtaime.Tests.Unit;

public sealed class OutputCanvasModelTests
{
    [Fact]
    public void Identity_mapping_preserves_pixel_centres()
    {
        var q = OutputCanvasQuad.Rectangle(0, 0, 1920, 1080);
        Assert.Equal(new OutputCanvasPoint(0.5, 0.5), OutputCanvasGeometry.Map(q, 0.5 / 1920, 0.5 / 1080));
        Assert.Equal(new OutputCanvasPoint(1919.5, 1079.5), OutputCanvasGeometry.Map(q, 1919.5 / 1920, 1079.5 / 1080));
    }

    [Fact]
    public void Perspective_quad_uses_projective_expected_pixel_mapping()
    {
        var q = new OutputCanvasQuad(new(0,0), new(200,0), new(150,100), new(50,100));
        Assert.Equal(new OutputCanvasPoint(100, 200d / 3), OutputCanvasGeometry.Map(q, .5, .5));
        Assert.Equal(q.BottomRight, OutputCanvasGeometry.Map(q, 1, 1));
    }

    [Fact]
    public void Invalid_and_degenerate_quads_fail_closed()
    {
        OutputCanvasQuad[] invalid = [
            OutputCanvasQuad.Rectangle(0, 0, 0, 100),
            OutputCanvasQuad.Rectangle(0, 0, -100, 100),
            new(new(0,0), new(100,100), new(100,0), new(0,100)),
            new(new(double.NaN,0), new(100,0), new(100,100), new(0,100)),
            OutputCanvasQuad.Rectangle(0, 0, double.MaxValue, 100),
            new(new(0,0), new(100,0), new(20,20), new(0,100))
        ];
        Assert.All(invalid, q => Assert.False(OutputCanvasGeometry.IsValid(q)));
    }

    [Fact]
    public void Multiple_sizes_crop_and_surfaces_are_valid_without_claiming_targets()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var model = new OutputCanvasConfiguration("1.0", Guid.NewGuid(), 0,
            [new(a,"Wide",1920,1080), new(b,"Square",1024,1024)],
            Enumerable.Range(0,8).Select(i => new OutputCanvasSurface(Guid.NewGuid(), i % 2 == 0 ? a : b,
                new(BusRoleId:"program"), i, new(.1,.2,.5,.6), OutputCanvasQuad.Rectangle(-10,0,500,500))).ToArray(), []);
        Assert.True(OutputCanvasModel.Validate(model, [], ["program"], []).IsValid);
        var bad = model with { Canvases = [new(a,"Overflow",int.MaxValue,int.MaxValue)] };
        Assert.Contains(OutputCanvasModel.Validate(bad).Issues, i => i.Code == "canvas.size");
        var badCrop = model with { Surfaces = [model.Surfaces[0] with { Crop = new(0,0,double.PositiveInfinity,1) }] };
        Assert.Contains(OutputCanvasModel.Validate(badCrop).Issues, i => i.Code == "canvas.crop");
    }

    [Fact]
    public void Unknown_sources_profiles_and_monitoring_roles_are_rejected()
    {
        var c = Guid.NewGuid();
        var model = new OutputCanvasConfiguration("1.0",Guid.NewGuid(),0,[new(c,"Test",100,100)],
            [new(Guid.NewGuid(),c,new(BusRoleId:"preview"),0,new(0,0,1,1),OutputCanvasQuad.Rectangle(0,0,100,100))],
            [new(Guid.NewGuid(),c,"provider","target","unknown")]);
        var validation = OutputCanvasModel.Validate(model, [], ["program"], [new("provider","target","production",100,100,"configured")]);
        Assert.Contains(validation.Issues, i => i.Code == "canvas.bus");
        Assert.Contains(validation.Issues, i => i.Code == "canvas.target_unsupported");
        model = model with { Surfaces = [model.Surfaces[0] with { Input = new(SourceId:Guid.NewGuid().ToString()) }] };
        Assert.Contains(OutputCanvasModel.Validate(model, [], ["program"], []).Issues, i => i.Code == "canvas.source");
    }
}

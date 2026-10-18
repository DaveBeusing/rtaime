// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using System.Text;
using rtaime.Control.Contracts;
namespace rtaime.ControlHost;

public static class OutputCanvasLegacy
{
    public static OutputCanvasConfiguration Reconstruct(ProductionId productionId,
        IReadOnlyList<ProductionOutputRoleState> roles, int width, int height, OutputCanvasTarget[] targets)
    {
        Guid Id(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"output.canvas/1.0/{productionId}/{key}")).AsSpan(0,16));
        var canvases = new List<OutputCanvasDefinition>(); var surfaces = new List<OutputCanvasSurface>();
        var bindings = new List<OutputCanvasBinding>();
        foreach (var role in roles.OrderBy(r => r.RoleId.ToString(), StringComparer.Ordinal))
        {
            var id = role.RoleId.ToString();
            var canvasId = Id("canvas/" + id);
            canvases.Add(new(canvasId, id, width, height));
            surfaces.Add(new(Id("surface/" + id), canvasId, new(BusRoleId:id), 0,
                new(0,0,1,1), OutputCanvasQuad.Rectangle(0,0,width,height), role.Enabled));
            var target = targets.SingleOrDefault(t => t.TargetId == role.TargetId);
            bindings.Add(new(Id("output/" + id), canvasId, target?.ProviderId ?? role.ProviderSelector,
                role.TargetId, target?.ProfileId ?? "production", role.Enabled));
        }
        return new(OutputCanvasContract.Version, Id("configuration"), 0, canvases.ToArray(), surfaces.ToArray(), bindings.ToArray());
    }
}

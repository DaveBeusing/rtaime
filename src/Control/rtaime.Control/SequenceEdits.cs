// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
namespace rtaime.Control;

public static class SequenceEdits
{
    public static SequenceDefinition Apply(SequenceDefinition model, SequenceEdit[] edits)
    {
        if (edits is null || edits.Length is < 1 or > SequenceContract.MaximumEdits || edits.Any(e=>e is null))
            throw new ArgumentException("A bounded nonempty typed edit batch is required.");
        var tracks=model.Tracks.ToList(); var clips=model.Clips.ToList(); var transitions=model.Transitions.ToList();
        SequenceClip Clip(Guid id) => clips.SingleOrDefault(c=>c.ClipId==id) ?? throw new ArgumentException("Clip does not exist.");
        SequenceClip[] Linked(SequenceClip c) => c.LinkGroupId is null ? [c] :
            clips.Where(x=>x.LinkGroupId==c.LinkGroupId).OrderBy(x=>x.ClipId.ToString(),StringComparer.Ordinal).ToArray();
        void Replace(SequenceClip clip) { clips.RemoveAll(c=>c.ClipId==clip.ClipId); clips.Add(clip); }
        void Close(long start,long length)
        {
            if(start<0 || length<=0 || start>SequenceContract.MaximumTime-length) throw new ArgumentException("Invalid gap interval.");
            var end=checked(start+length);
            if(clips.Any(c=>c.TimelineStart<end && c.TimelineStart+c.Duration>start))
                throw new ArgumentException("Gap/ripple cannot implicitly trim clips crossing the removed interval.");
            foreach(var c in clips.ToArray()) if(c.TimelineStart>=end) Replace(c with { TimelineStart=c.TimelineStart-length });
            for(var i=0;i<transitions.Count;i++)
            {
                var t=transitions[i];
                if(t.Start<end && t.Start+t.Duration>start) throw new ArgumentException("Remove crossing transitions explicitly.");
                if(t.Start>=end) transitions[i]=t with { Start=t.Start-length };
            }
        }
        foreach(var e in edits)
        {
            if(e.TargetId==Guid.Empty && e.Kind!=SequenceEditKind.CloseGap) throw new ArgumentException("Edit target identity is required.");
            var present=new List<string>();
            if(e.Clip is not null) present.Add("clip"); if(e.Track is not null) present.Add("track");
            if(e.Transition is not null) present.Add("transition"); if(e.Position is not null) present.Add("position");
            if(e.SourceIn is not null) present.Add("sourceIn"); if(e.SourceOut is not null) present.Add("sourceOut");
            if(e.Duration is not null) present.Add("duration"); if(e.TrackId is not null) present.Add("trackId");
            if(e.Ids is not null) present.Add("ids"); if(e.LinkGroupId is not null) present.Add("linkGroupId");
            void Fields(string[] required,params string[] optional)
            {
                if(required.Any(p=>!present.Contains(p)) || present.Any(p=>!required.Contains(p) && !optional.Contains(p)))
                    throw new ArgumentException("Typed edit payload contains missing or inapplicable fields.");
            }
            switch(e.Kind)
            {
                case SequenceEditKind.InsertClip:
                    Fields(["clip"]);
                    if(e.Clip!.ClipId!=e.TargetId || clips.Any(c=>c.ClipId==e.TargetId)) throw new ArgumentException("Clip insert identity conflict.");
                    clips.Add(e.Clip); break;
                case SequenceEditKind.MoveClip:
                    Fields(["position"],"trackId");
                    var moving=Clip(e.TargetId); var delta=checked(e.Position!.Value-moving.TimelineStart);
                    foreach(var c in Linked(moving)) Replace(c with { TimelineStart=checked(c.TimelineStart+delta),
                        TrackId=c.ClipId==moving.ClipId ? e.TrackId??c.TrackId : c.TrackId });
                    break;
                case SequenceEditKind.DeleteClip:
                case SequenceEditKind.RippleDelete:
                    Fields([]);
                    var deleted=Clip(e.TargetId); var removing=Linked(deleted);
                    clips.RemoveAll(c=>removing.Any(r=>r.ClipId==c.ClipId));
                    if(e.Kind==SequenceEditKind.RippleDelete) Close(deleted.TimelineStart,deleted.Duration);
                    break;
                case SequenceEditKind.SplitClip:
                    Fields(["position","ids"],"linkGroupId");
                    var split=Clip(e.TargetId); var group=Linked(split);
                    var offset=checked(e.Position!.Value-split.TimelineStart);
                    if(offset<=0 || offset>=split.Duration || e.Ids!.Length!=group.Length ||
                        (split.LinkGroupId is not null ? e.LinkGroupId is null || e.LinkGroupId==split.LinkGroupId || e.LinkGroupId==Guid.Empty : e.LinkGroupId is not null))
                        throw new ArgumentException("Split requires an interior time, one explicit new clip ID per linked member and a distinct right-side group.");
                    for(var i=0;i<group.Length;i++)
                    {
                        var c=group[i];
                        Replace(c with { SourceOut=checked(c.SourceIn+offset),Duration=offset });
                        clips.Add(c with { ClipId=e.Ids[i],SourceIn=checked(c.SourceIn+offset),
                            TimelineStart=e.Position.Value,Duration=c.Duration-offset,LinkGroupId=e.LinkGroupId });
                    }
                    break;
                case SequenceEditKind.TrimClip:
                    Fields(["sourceIn","sourceOut"]);
                    var trimmed=Clip(e.TargetId);
                    var left=checked(e.SourceIn!.Value-trimmed.SourceIn); var right=checked(e.SourceOut!.Value-trimmed.SourceOut);
                    foreach(var c in Linked(trimmed)) Replace(c with { SourceIn=checked(c.SourceIn+left),
                        SourceOut=checked(c.SourceOut+right),TimelineStart=checked(c.TimelineStart+left),
                        Duration=checked(c.Duration+right-left) });
                    break;
                case SequenceEditKind.CloseGap:
                    Fields(["position","duration"]);
                    if(e.TargetId!=Guid.Empty) throw new ArgumentException("Gap close is a document-wide interval, not an entity.");
                    Close(e.Position!.Value,e.Duration!.Value); break;
                case SequenceEditKind.Link:
                    Fields(["ids"]);
                    if(e.Ids!.Length<2 || e.Ids.Distinct().Count()!=e.Ids.Length || clips.Any(c=>c.LinkGroupId==e.TargetId))
                        throw new ArgumentException("Link requires distinct clips and a new group identity.");
                    foreach(var id in e.Ids)
                    {
                        var c=Clip(id); if(c.LinkGroupId is not null) throw new ArgumentException("Unlink existing groups explicitly.");
                        Replace(c with { LinkGroupId=e.TargetId });
                    }
                    break;
                case SequenceEditKind.Unlink:
                    Fields([]);
                    var linked=clips.Where(c=>c.LinkGroupId==e.TargetId).ToArray();
                    if(linked.Length<2) throw new ArgumentException("Link group does not exist.");
                    foreach(var c in linked) Replace(c with { LinkGroupId=null }); break;
                case SequenceEditKind.AddTrack:
                case SequenceEditKind.UpdateTrack:
                    Fields(["track"]);
                    if(e.Track!.TrackId!=e.TargetId || tracks.Any(t=>t.TrackId==e.TargetId)!=(e.Kind==SequenceEditKind.UpdateTrack))
                        throw new ArgumentException("Track edit identity/existence mismatch.");
                    tracks.RemoveAll(t=>t.TrackId==e.TargetId); tracks.Add(e.Track); break;
                case SequenceEditKind.DeleteTrack:
                    Fields([]);
                    if(clips.Any(c=>c.TrackId==e.TargetId) || tracks.RemoveAll(t=>t.TrackId==e.TargetId)!=1)
                        throw new ArgumentException("Remove clips explicitly before their track.");
                    break;
                case SequenceEditKind.AddTransition:
                    Fields(["transition"]);
                    if(e.Transition!.TransitionId!=e.TargetId || transitions.Any(t=>t.TransitionId==e.TargetId))
                        throw new ArgumentException("Transition identity conflict.");
                    transitions.Add(e.Transition); break;
                case SequenceEditKind.DeleteTransition:
                    Fields([]);
                    if(transitions.RemoveAll(t=>t.TransitionId==e.TargetId)!=1) throw new ArgumentException("Transition does not exist.");
                    break;
                default: throw new ArgumentException("Unknown future edit operation.");
            }
        }
        return model with { Tracks=tracks.ToArray(),Clips=clips.ToArray(),Transitions=transitions.ToArray() };
    }
}

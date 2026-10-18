// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
namespace rtaime.Control;

public static class SequenceModel
{
    public static SequenceValidation Validate(SequenceDefinition model, ulong revision = 0,
        IReadOnlyCollection<SequenceAsset>? assets = null, IReadOnlyCollection<string>? audioBuses = null)
    {
        var issues = new List<SequenceIssue>();
        void Error(string code,string path,string message) { if (issues.Count < 128) issues.Add(new(code,path,message)); }
        if (model.Version != SequenceContract.Version) Error("sequence.version","version","Unsupported sequence capability version.");
        if (model.SequenceId == Guid.Empty) Error("sequence.identity","sequenceId","Sequence identity is required.");
        if (string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 128) Error("sequence.name","name","Name requires 1-128 characters.");
        if (!SequenceTime.ValidRate(model.FrameRate)) Error("sequence.rate","frameRate","Invalid rational frame rate.");
        if (model.Tracks is null || model.Clips is null || model.Transitions is null ||
            model.Tracks.Any(t=>t is null) || model.Clips.Any(c=>c is null) || model.Transitions.Any(t=>t is null))
        {
            Error("sequence.payload","$","Non-null collections and entries are required.");
            return new(model.SequenceId,revision,issues.ToArray());
        }
        if (model.Tracks.Length > SequenceContract.MaximumTracks || model.Clips.Length > SequenceContract.MaximumClips ||
            model.Transitions.Length > SequenceContract.MaximumTransitions)
        {
            Error("sequence.capacity","$","Track, clip or transition count exceeds the finite document cap.");
            return new(model.SequenceId,revision,issues.ToArray());
        }
        var ids = model.Tracks.Select(t=>t.TrackId).Concat(model.Clips.Select(c=>c.ClipId))
            .Concat(model.Transitions.Select(t=>t.TransitionId)).Append(model.SequenceId).ToArray();
        if (ids.Contains(Guid.Empty) || ids.Distinct().Count()!=ids.Length)
        {
            Error("sequence.identity","$","Sequence/track/clip/transition identities must be nonempty and unique.");
            return new(model.SequenceId,revision,issues.ToArray());
        }
        var tracks = model.Tracks.ToDictionary(t=>t.TrackId);
        foreach (var t in model.Tracks)
        {
            var path = $"tracks/{t.TrackId}";
            if (!Enum.IsDefined(t.Kind) || string.IsNullOrWhiteSpace(t.Name) || t.Name.Length>128 ||
                t.LayerOrder is < -100000 or > 100000)
                Error("sequence.track",path,"Invalid track kind, name or layer order.");
            if (t.Kind == SequenceMediaKind.Audio
                ? string.IsNullOrWhiteSpace(t.AudioBusId) || t.AudioBusId.Length>64 ||
                    (audioBuses is not null && !audioBuses.Contains(t.AudioBusId))
                : t.AudioBusId is not null)
                Error("sequence.audio_bus",path,"Audio tracks require an existing bus; non-audio tracks cannot declare audio routing.");
        }
        foreach (var c in model.Clips)
        {
            var path = $"clips/{c.ClipId}";
            if (!tracks.TryGetValue(c.TrackId,out var track)) Error("sequence.track_reference",path,"Track does not exist.");
            if (c.SourceIn<0 || c.SourceOut<=c.SourceIn || c.SourceOut>SequenceContract.MaximumTime ||
                c.TimelineStart<0 || c.Duration<=0 || c.Duration>SequenceContract.MaximumTime ||
                c.TimelineStart>SequenceContract.MaximumTime-c.Duration || c.SourceOut-c.SourceIn!=c.Duration)
                Error("sequence.range",path,"Clip uses positive half-open integral tick ranges at unit rate within the 24-hour cap.");
            var media = c.Media;
            if (media is null || media.AssetId==Guid.Empty || !Enum.IsDefined(media.Kind) ||
                media.FingerprintSha256 is null || media.FingerprintSha256.Length!=64 || media.FingerprintSha256.Any(h=>!Uri.IsHexDigit(h)) ||
                (track is not null && track.Kind!=media.Kind))
                Error("sequence.media",path,"A matching typed asset identity and SHA-256 reference is required.");
            else if (assets is not null)
            {
                var asset = assets.SingleOrDefault(a=>a.AssetId==media.AssetId && a.Kind==media.Kind);
                if (asset is null || asset.FingerprintSha256!=media.FingerprintSha256)
                    Error("sequence.asset_reference",path,"Asset is missing, has another media kind, or has been relinked to different content.");
                else if (c.SourceOut>asset.DurationTicks && media.Kind!=SequenceMediaKind.Graphics)
                    Error("sequence.source_range",path,"Source OUT exceeds the catalogued duration.");
            }
        }
        if (issues.Any(i=>i.Code is "sequence.range" or "sequence.media" or "sequence.track_reference"))
            return new(model.SequenceId,revision,issues.ToArray());
        var clips = model.Clips.ToDictionary(c=>c.ClipId);
        foreach (var group in model.Clips.Where(c=>c.LinkGroupId is not null).GroupBy(c=>c.LinkGroupId!.Value))
        {
            if (group.Key==Guid.Empty || ids.Contains(group.Key) || group.Count()<2 ||
                group.Select(c=>(c.TimelineStart,c.Duration)).Distinct().Count()!=1)
                Error("sequence.link",$"links/{group.Key}","Linked clips require distinct group identity and equal timeline ranges.");
        }
        var pairs = new HashSet<(Guid,Guid)>();
        var fadePairs = model.Transitions.Where(t => t.Kind != SequenceTransitionKind.Cut)
            .Select(t => (t.FromClipId, t.ToClipId)).ToHashSet();
        foreach (var transition in model.Transitions)
        {
            var path = $"transitions/{transition.TransitionId}";
            if (!clips.TryGetValue(transition.FromClipId,out var a) || !clips.TryGetValue(transition.ToClipId,out var b) ||
                a.ClipId==b.ClipId || a.TrackId!=b.TrackId || !Enum.IsDefined(transition.Kind) || !pairs.Add((a.ClipId,b.ClipId)))
            { Error("sequence.transition",path,"Transition endpoints must be unique ordered clips on one track."); continue; }
            var start = Math.Max(a.TimelineStart,b.TimelineStart);
            var end = Math.Min(a.TimelineStart+a.Duration,b.TimelineStart+b.Duration);
            if (transition.Kind==SequenceTransitionKind.Cut)
            {
                if (transition.Duration!=0 || transition.Start!=b.TimelineStart || a.TimelineStart+a.Duration!=b.TimelineStart)
                    Error("sequence.transition_range",path,"CUT requires adjacent clips and a zero-length boundary.");
            }
            else if (a.TimelineStart>=b.TimelineStart || a.TimelineStart+a.Duration>=b.TimelineStart+b.Duration ||
                end<=start || transition.Start!=start || transition.Duration!=end-start ||
                (tracks[a.TrackId].Kind==SequenceMediaKind.Audio) != (transition.Kind==SequenceTransitionKind.Crossfade))
                Error("sequence.transition_range",path,"Dissolve/crossfade must describe exactly the ordered clip overlap and match track kind.");
        }
        foreach (var group in model.Clips.GroupBy(c=>c.TrackId))
        {
            var ordered = group.OrderBy(c=>c.TimelineStart).ThenBy(c=>c.ClipId).ToArray();
            for (var i=0;i<ordered.Length;i++) for(var j=i+1;j<ordered.Length && ordered[j].TimelineStart<ordered[i].TimelineStart+ordered[i].Duration;j++)
                if (!fadePairs.Contains((ordered[i].ClipId, ordered[j].ClipId)))
                    Error("sequence.overlap",$"clips/{ordered[j].ClipId}","Same-track overlaps require an explicit validated transition.");
            var fades = model.Transitions.Where(t=>t.Kind!=SequenceTransitionKind.Cut && clips.TryGetValue(t.FromClipId,out var c) && c.TrackId==group.Key)
                .OrderBy(t=>t.Start).ToArray();
            for(var i=1;i<fades.Length;i++)
                if(fades[i].Start<fades[i-1].Start+fades[i-1].Duration)
                    Error("sequence.transition_overlap","transitions","Transition intervals on one track cannot overlap.");
        }
        return new(model.SequenceId,revision,issues.ToArray());
    }
}

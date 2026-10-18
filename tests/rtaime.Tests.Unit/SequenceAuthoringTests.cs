// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control;
using rtaime.Control.Contracts;
namespace rtaime.Tests.Unit;

public sealed class SequenceAuthoringTests
{
    private static readonly Guid AssetId=Guid.Parse("87000000-0000-0000-0000-000000000001");
    private static readonly string Hash=new('A',64);
    private static SequenceDefinition Model()
    {
        var tracks=Enumerable.Range(0,4).Select(i=>new SequenceTrack(Guid.NewGuid(),"Track "+i,
            i<2?SequenceMediaKind.Video:SequenceMediaKind.Audio,i,AudioBusId:i<2?null:"program")).ToArray();
        return new("1.0",Guid.NewGuid(),"Sequence",new(50,1),tracks,
            tracks.Select(t=>new SequenceClip(Guid.NewGuid(),t.TrackId,new(AssetId,t.Kind,Hash),0,1000,0,1000)).ToArray(),[]);
    }
    private static SequenceAsset[] Assets => [new(AssetId,SequenceMediaKind.Video,10000,Hash),new(AssetId,SequenceMediaKind.Audio,10000,Hash)];

    [Fact]
    public void Rational_25_50_and_5994_boundaries_do_not_accumulate_float_error()
    {
        Assert.Equal(240000L,SequenceTime.FromFrames(25,new(25,1)));
        Assert.Equal(240000L,SequenceTime.FromFrames(50,new(50,1)));
        Assert.Equal(4004L,SequenceTime.FromFrames(1,new(60000,1001)));
        Assert.Equal(240240000L,SequenceTime.FromFrames(60000,new(60000,1001)));
        Assert.Equal(0L,SequenceTime.ToFrames(4003,new(60000,1001)));
        Assert.Equal(1L,SequenceTime.ToFrames(4003,new(60000,1001),true));
        Assert.Throws<OverflowException>(()=>SequenceTime.FromFrames(long.MaxValue,new(1,1)));
    }

    [Fact]
    public void Two_video_and_two_audio_tracks_are_real_validated_asset_references()
    {
        var m=Model();
        Assert.True(SequenceModel.Validate(m,0,Assets,["program"]).IsValid);
        Assert.Equal(2,m.Tracks.Count(t=>t.Kind==SequenceMediaKind.Audio));
        Assert.All(m.Clips,c=>Assert.Equal(c.Duration,c.SourceOut-c.SourceIn));
        Assert.Contains(SequenceModel.Validate(m,0,[],["program"]).Issues,i=>i.Code=="sequence.asset_reference");
        Assert.Contains(SequenceModel.Validate(m,0,Assets,[]).Issues,i=>i.Code=="sequence.audio_bus");
    }

    [Fact]
    public void Linked_move_split_and_trim_preserve_left_ids_and_exact_half_open_ranges()
    {
        var m=Model(); var group=Guid.NewGuid(); var selected=new[]{m.Clips[0].ClipId,m.Clips[2].ClipId};
        m=SequenceEdits.Apply(m,[new(SequenceEditKind.Link,group,Ids:selected)]);
        m=SequenceEdits.Apply(m,[new(SequenceEditKind.MoveClip,selected[0],Position:2000)]);
        Assert.All(m.Clips.Where(c=>c.LinkGroupId==group),c=>Assert.Equal(2000L,c.TimelineStart));
        var rights=new[]{Guid.NewGuid(),Guid.NewGuid()}; var rightGroup=Guid.NewGuid();
        m=SequenceEdits.Apply(m,[new(SequenceEditKind.SplitClip,selected[0],Position:2500,Ids:rights,LinkGroupId:rightGroup)]);
        Assert.All(selected,id=>Assert.Equal(500L,m.Clips.Single(c=>c.ClipId==id).Duration));
        Assert.All(rights,id=>Assert.Equal(500L,m.Clips.Single(c=>c.ClipId==id).SourceIn));
        m=SequenceEdits.Apply(m,[new(SequenceEditKind.TrimClip,rights[0],SourceIn:600,SourceOut:900)]);
        Assert.All(m.Clips.Where(c=>c.LinkGroupId==rightGroup),c=>{
            Assert.Equal(2600L,c.TimelineStart);Assert.Equal(300L,c.Duration);
        });
        Assert.True(SequenceModel.Validate(m,0,Assets,["program"]).IsValid);
        m=SequenceEdits.Apply(m,[new(SequenceEditKind.Unlink,rightGroup)]);
        Assert.All(rights,id=>Assert.Null(m.Clips.Single(c=>c.ClipId==id).LinkGroupId));
    }

    [Fact]
    public void Gap_close_and_ripple_are_document_wide_and_reject_implicit_crossing_trims()
    {
        var m=Model(); var group=Guid.NewGuid();
        m=SequenceEdits.Apply(m,[new(SequenceEditKind.Link,group,Ids:m.Clips.Select(c=>c.ClipId).ToArray())]);
        var extra=new SequenceClip(Guid.NewGuid(),m.Tracks[0].TrackId,m.Clips[0].Media,0,1000,2000,1000);
        m=SequenceEdits.Apply(m,[new(SequenceEditKind.InsertClip,extra.ClipId,Clip:extra),
            new(SequenceEditKind.CloseGap,Guid.Empty,Position:1000,Duration:1000)]);
        Assert.Equal(1000L,m.Clips.Single(c=>c.ClipId==extra.ClipId).TimelineStart);
        m=SequenceEdits.Apply(m,[new(SequenceEditKind.RippleDelete,m.Clips.First(c=>c.LinkGroupId==group).ClipId)]);
        Assert.Equal(0L,Assert.Single(m.Clips).TimelineStart);
        Assert.Throws<ArgumentException>(()=>SequenceEdits.Apply(m,[new(SequenceEditKind.CloseGap,Guid.Empty,Position:0,Duration:100)]));
    }

    [Fact]
    public void Same_track_overlap_requires_an_exact_typed_transition_interval()
    {
        var m=Model(); var a=m.Clips[0];
        var b=a with { ClipId=Guid.NewGuid(),TimelineStart=800 };
        m=m with { Clips=m.Clips.Append(b).ToArray() };
        Assert.Contains(SequenceModel.Validate(m).Issues,i=>i.Code=="sequence.overlap");
        var fade=new SequenceTransition(Guid.NewGuid(),a.ClipId,b.ClipId,SequenceTransitionKind.Dissolve,800,200);
        Assert.True(SequenceModel.Validate(m with { Transitions=[fade] }).IsValid);
        Assert.Contains(SequenceModel.Validate(m with { Transitions=[fade with { Duration=201 }] }).Issues,i=>i.Code=="sequence.transition_range");
    }

    [Fact]
    public void Invalid_future_null_overflow_and_wrong_edit_fields_fail_closed()
    {
        var m=Model();
        Assert.False(SequenceModel.Validate(m with { Version="2.0" }).IsValid);
        Assert.False(SequenceModel.Validate(m with { Clips=null! }).IsValid);
        Assert.Contains(SequenceModel.Validate(m with { Tracks=Enumerable.Repeat(m.Tracks[0],65).ToArray() }).Issues,i=>i.Code=="sequence.capacity");
        Assert.Contains(SequenceModel.Validate(m with { Clips=[m.Clips[0] with { TimelineStart=long.MaxValue }] }).Issues,i=>i.Code=="sequence.range");
        Assert.Throws<ArgumentException>(()=>SequenceEdits.Apply(m,[]));
        Assert.Throws<ArgumentException>(()=>SequenceEdits.Apply(m,[new(SequenceEditKind.MoveClip,m.Clips[0].ClipId,Position:0,SourceIn:0)]));
        Assert.Throws<ArgumentException>(()=>SequenceEdits.Apply(m,[new((SequenceEditKind)99,m.Clips[0].ClipId)]));
        Assert.Throws<InvalidDataException>(()=>SequenceJson.Read<SequenceDefinition>("{\"version\":\"1.0\",\"Version\":\"2.0\"}"));
    }
}

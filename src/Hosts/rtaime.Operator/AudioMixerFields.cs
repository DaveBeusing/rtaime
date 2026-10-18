// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Operator;

// Mixer-only editor metadata reuses the Inspector numeric primitive and existing DSP ranges.
public static class AudioMixerFields
{
    public static InspectorPropertyDefinition LowHz { get; } = new("mixer.eq.low.hz", "Low shelf Hz",20,20000,120,1,"Hz");
    public static InspectorPropertyDefinition MidHz { get; } = new("mixer.eq.mid.hz", "Mid bell Hz",20,20000,1000,1,"Hz");
    public static InspectorPropertyDefinition HighHz { get; } = new("mixer.eq.high.hz", "High shelf Hz",20,20000,8000,1,"Hz");
    public static InspectorPropertyDefinition EqDb { get; } = new("mixer.eq.db","EQ gain dB",-18,18,0,0.1,"dB");
    public static InspectorPropertyDefinition Q { get; } = new("mixer.eq.q","Bell Q",0.1,10,1,0.1,"Q");
    public static InspectorPropertyDefinition Threshold { get; } = new("mixer.comp.threshold","Compressor threshold",-60,0,-18,0.1,"dBFS");
    public static InspectorPropertyDefinition Ratio { get; } = new("mixer.comp.ratio","Compressor ratio",1,20,4,0.1,":1");
    public static InspectorPropertyDefinition Attack { get; } = new("mixer.comp.attack","Attack",0.1,200,5,0.1,"ms");
    public static InspectorPropertyDefinition Release { get; } = new("mixer.dynamics.release","Release",5,5000,100,1,"ms");
    public static InspectorPropertyDefinition Makeup { get; } = new("mixer.comp.makeup","Makeup gain",0,24,0,0.1,"dB");
    public static InspectorPropertyDefinition Ceiling { get; } = new("mixer.limiter.ceiling","Sample-peak ceiling",-24,0,-1,0.1,"dBFS");
}

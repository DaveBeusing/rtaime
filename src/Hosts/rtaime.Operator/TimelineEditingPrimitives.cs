// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Operator;

public enum TimelineSnapKind { Cue, Clip, Playhead, Frame }
public sealed record TimelineSnapTarget(long Frame, TimelineSnapKind Kind);
public sealed record TimelineSnapResult(long Frame, TimelineSnapKind Kind, bool Magnetized);

public static class TimelineSnapPolicy
{
	// Nearest target wins; equal distances prefer cue, clip, then confirmed playhead.
	// Frame quantization is always integral, even with optional magnetic snapping disabled.
	public static TimelineSnapResult SnapFrame(long frame, IEnumerable<TimelineSnapTarget> targets, long threshold, long totalFrames, bool enabled)
	{
		if (totalFrames <= 0) return new(0, TimelineSnapKind.Frame, false);
		frame = Math.Clamp(frame, 0, totalFrames - 1);
		if (!enabled) return new(frame, TimelineSnapKind.Frame, false);
		threshold = Math.Clamp(threshold, 0, 10);
		var nearest = targets.Where(t => t.Frame >= 0 && t.Frame < totalFrames && t.Kind != TimelineSnapKind.Frame)
			.Select(t => (Target: t, Distance: t.Frame >= frame ? t.Frame - frame : frame - t.Frame))
			.Where(t => t.Distance <= threshold)
			.OrderBy(t => t.Distance).ThenBy(t => t.Target.Kind).ThenBy(t => t.Target.Frame).FirstOrDefault();
		return nearest.Target is null ? new(frame, TimelineSnapKind.Frame, false) : new(nearest.Target.Frame, nearest.Target.Kind, true);
	}
}

internal sealed class AuthoringDraftHistory<T>
{
	internal const int Capacity = 32;
	private readonly List<T> _undo = [], _redo = [];
	public bool CanUndo => _undo.Count > 0;
	public bool CanRedo => _redo.Count > 0;
	public int UndoCount => _undo.Count;
	public int RedoCount => _redo.Count;
	public void Remember(T state)
	{
		if (_undo.Count == Capacity) _undo.RemoveAt(0);
		_undo.Add(state); _redo.Clear();
	}
	public T Undo(T current)
	{
		var index = _undo.Count - 1;
		var state = _undo[index]; _undo.RemoveAt(index); _redo.Add(current); return state;
	}
	public T Redo(T current)
	{
		var index = _redo.Count - 1;
		var state = _redo[index]; _redo.RemoveAt(index); _undo.Add(current); return state;
	}
	public void Clear() { _undo.Clear(); _redo.Clear(); }
}

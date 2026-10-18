// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace rtaime.Operator.Controls;

/// <summary>Presentation only. Native input, command routing and production state remain unchanged.</summary>
public static class OperatorControlPresentation
{
	public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
		"Enabled", typeof(bool), typeof(OperatorControlPresentation), new PropertyMetadata(false, EnabledChanged));
	public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.RegisterAttached(
		"ReducedMotion", typeof(bool), typeof(OperatorControlPresentation),
		new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, MotionChanged));
	public static readonly DependencyProperty AnimateEntranceProperty = DependencyProperty.RegisterAttached(
		"AnimateEntrance", typeof(bool), typeof(OperatorControlPresentation), new PropertyMetadata(false, MotionChanged));
	public static readonly DependencyProperty IsPendingProperty = DependencyProperty.RegisterAttached(
		"IsPending", typeof(bool), typeof(OperatorControlPresentation), new PropertyMetadata(false, StateChanged));
	public static readonly DependencyProperty DisabledReasonProperty = DependencyProperty.RegisterAttached(
		"DisabledReason", typeof(string), typeof(OperatorControlPresentation), new PropertyMetadata(string.Empty, StateChanged));
	public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.RegisterAttached(
		"ErrorText", typeof(string), typeof(OperatorControlPresentation), new PropertyMetadata(string.Empty, StateChanged));
	private static readonly DependencyPropertyKey HighContrastKey = DependencyProperty.RegisterAttachedReadOnly(
		"HighContrast", typeof(bool), typeof(OperatorControlPresentation), new PropertyMetadata(false));
	public static readonly DependencyProperty HighContrastProperty = HighContrastKey.DependencyProperty;
	private static readonly DependencyPropertyKey StateDescriptionKey = DependencyProperty.RegisterAttachedReadOnly(
		"StateDescription", typeof(string), typeof(OperatorControlPresentation), new PropertyMetadata(string.Empty));
	public static readonly DependencyProperty StateDescriptionProperty = StateDescriptionKey.DependencyProperty;
	private static readonly DependencyProperty SessionProperty = DependencyProperty.RegisterAttached(
		"Session", typeof(Session), typeof(OperatorControlPresentation));
	public static readonly RoutedUICommand ToggleReducedMotion = new("Reduce motion", "ToggleReducedMotion", typeof(OperatorControlPresentation));

	static OperatorControlPresentation()
	{
		CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(ToggleReducedMotion,
			(sender, args) => { var owner = (Window)sender; SetReducedMotion(owner, !GetReducedMotion(owner)); args.Handled = true; },
			(_, args) => { args.CanExecute = true; args.Handled = true; }));
	}

	public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
	public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
	public static bool GetReducedMotion(DependencyObject value) => (bool)value.GetValue(ReducedMotionProperty);
	public static void SetReducedMotion(DependencyObject value, bool reduced) => value.SetValue(ReducedMotionProperty, reduced);
	public static bool GetAnimateEntrance(DependencyObject value) => (bool)value.GetValue(AnimateEntranceProperty);
	public static void SetAnimateEntrance(DependencyObject value, bool enabled) => value.SetValue(AnimateEntranceProperty, enabled);
	public static bool GetIsPending(DependencyObject value) => (bool)value.GetValue(IsPendingProperty);
	public static void SetIsPending(DependencyObject value, bool pending) => value.SetValue(IsPendingProperty, pending);
	public static string GetDisabledReason(DependencyObject value) => (string)value.GetValue(DisabledReasonProperty);
	public static void SetDisabledReason(DependencyObject value, string reason) => value.SetValue(DisabledReasonProperty, reason);
	public static string GetErrorText(DependencyObject value) => (string)value.GetValue(ErrorTextProperty);
	public static void SetErrorText(DependencyObject value, string error) => value.SetValue(ErrorTextProperty, error);
	public static bool GetHighContrast(DependencyObject value) => (bool)value.GetValue(HighContrastProperty);
	public static string GetStateDescription(DependencyObject value) => (string)value.GetValue(StateDescriptionProperty);
	internal static int ActiveSystemSubscriptions { get; private set; }
	internal static void ApplyPreferenceSnapshot(FrameworkElement element, bool clientAreaAnimation, bool highContrast)
	{
		element.SetValue(HighContrastKey, highContrast);
		if (!clientAreaAnimation || highContrast || GetReducedMotion(element))
			(element.GetValue(SessionProperty) as Session)?.Cancel();
	}
	internal static bool IsAnimating(DependencyObject value) => (value.GetValue(SessionProperty) as Session)?.Animating == true;

	private static void EnabledChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
	{
		if (value is not FrameworkElement element) return;
		(value.GetValue(SessionProperty) as Session)?.Dispose();
		value.ClearValue(SessionProperty);
		if ((bool)args.NewValue) value.SetValue(SessionProperty, new Session(element));
	}
	private static void MotionChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) =>
		(value.GetValue(SessionProperty) as Session)?.Cancel();
	private static void StateChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) => Describe(value);
	private static void Describe(DependencyObject value)
	{
		var disabled = value is UIElement { IsEnabled: false };
		var text = GetErrorText(value) is { Length: > 0 } error ? "Error: " + error :
			disabled ? "Unavailable: " + (GetDisabledReason(value) is { Length: > 0 } reason ? reason : "Command is not currently available.") :
			GetIsPending(value) ? "Pending confirmation" : string.Empty;
		value.SetValue(StateDescriptionKey, text);
	}

	// One loaded-element subscription, no timers, no layout animation and no Program cadence observation.
	private sealed class Session : IDisposable
	{
		private readonly FrameworkElement _element;
		private AnimationClock? _clock;
		private bool _subscribed;
		public bool Animating => _clock is not null;
		public Session(FrameworkElement element)
		{
			_element = element;
			element.Loaded += Loaded;
			element.Unloaded += Unloaded;
			element.IsVisibleChanged += VisibleChanged;
			element.IsEnabledChanged += EnabledStateChanged;
			Describe(element);
			if (element.IsLoaded) Attach();
		}
		private void EnabledStateChanged(object sender, DependencyPropertyChangedEventArgs args) => Describe(_element);
		private void Loaded(object sender, RoutedEventArgs args) { Attach(); Enter(); }
		private void Unloaded(object sender, RoutedEventArgs args) { Cancel(); Detach(); }
		private void VisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
		{
			if ((bool)args.NewValue) Enter(); else Cancel();
		}
		private void Attach()
		{
			if (_subscribed) return;
			_subscribed = true;
			SystemParameters.StaticPropertyChanged += SystemChanged;
			ActiveSystemSubscriptions++;
			Refresh();
		}
		private void Detach()
		{
			if (!_subscribed) return;
			SystemParameters.StaticPropertyChanged -= SystemChanged;
			ActiveSystemSubscriptions--;
			_subscribed = false;
		}
		private void SystemChanged(object? sender, PropertyChangedEventArgs args)
		{
			if (_element.Dispatcher.CheckAccess()) Refresh();
			else _element.Dispatcher.BeginInvoke(new Action(() => { if (_subscribed) Refresh(); }));
		}
		private void Refresh()
		{
			ApplyPreferenceSnapshot(_element, SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);
		}
		private void Enter()
		{
			Cancel();
			if (!_element.IsLoaded || !_element.IsVisible || !GetAnimateEntrance(_element) ||
				GetReducedMotion(_element) || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
			var duration = _element.TryFindResource("OperatorMotionDuration") is TimeSpan configured ? configured : TimeSpan.FromMilliseconds(150);
			duration = TimeSpan.FromMilliseconds(Math.Clamp(duration.TotalMilliseconds, 120, 180));
			var target = (double)_element.GetAnimationBaseValue(UIElement.OpacityProperty);
			var animation = new DoubleAnimation(0, target, new Duration(duration))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
				FillBehavior = FillBehavior.Stop
			};
			_clock = animation.CreateClock();
			_clock.Completed += Completed;
			_element.ApplyAnimationClock(UIElement.OpacityProperty, _clock, HandoffBehavior.SnapshotAndReplace);
		}
		private void Completed(object? sender, EventArgs args) => Cancel();
		public void Cancel()
		{
			if (_clock is null) return;
			_clock.Completed -= Completed;
			_clock.Controller?.Remove();
			_element.ApplyAnimationClock(UIElement.OpacityProperty, null);
			_clock = null;
		}
		public void Dispose()
		{
			Cancel(); Detach();
			_element.Loaded -= Loaded;
			_element.Unloaded -= Unloaded;
			_element.IsVisibleChanged -= VisibleChanged;
			_element.IsEnabledChanged -= EnabledStateChanged;
		}
	}
}

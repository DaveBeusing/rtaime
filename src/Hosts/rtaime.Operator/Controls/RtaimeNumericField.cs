// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Input;

namespace rtaime.Operator.Controls;

public class RtaimeNumericField : RtaimeTextBox
{
	private bool _formatting, _scrubbing, _publishing;
	private Point _origin;
	private double _originValue;

	public static readonly DependencyProperty DefinitionProperty = DependencyProperty.Register(
		nameof(Definition), typeof(InspectorPropertyDefinition), typeof(RtaimeNumericField),
		new FrameworkPropertyMetadata(InspectorProperties.LayerScale, Changed));
	public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
		nameof(Value), typeof(double), typeof(RtaimeNumericField),
		new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
	private static readonly DependencyPropertyKey HasInputErrorKey = DependencyProperty.RegisterReadOnly(
		nameof(HasInputError), typeof(bool), typeof(RtaimeNumericField), new FrameworkPropertyMetadata(false));
	public static readonly DependencyProperty HasInputErrorProperty = HasInputErrorKey.DependencyProperty;
	public static readonly RoutedEvent InputValidityChangedEvent = EventManager.RegisterRoutedEvent(
		nameof(InputValidityChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RtaimeNumericField));

	static RtaimeNumericField() =>
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeNumericField), new FrameworkPropertyMetadata(typeof(RtaimeNumericField)));

	public RtaimeNumericField()
	{
		MaxLength = 64;
		Loaded += (_, _) => { CancelInput(); AnnounceValidity(); };
		IsVisibleChanged += (_, _) => AnnounceValidity();
		DataContextChanged += (_, _) => CancelInput();
	}
	public InspectorPropertyDefinition Definition
	{
		get => (InspectorPropertyDefinition)GetValue(DefinitionProperty);
		set => SetValue(DefinitionProperty, value);
	}
	public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
	public bool HasInputError => (bool)GetValue(HasInputErrorProperty);
	public event RoutedEventHandler InputValidityChanged
	{
		add => AddHandler(InputValidityChangedEvent, value);
		remove => RemoveHandler(InputValidityChangedEvent, value);
	}
	private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
	{
		var field = (RtaimeNumericField)sender;
		if (!field._publishing) field.FormatValue();
	}
	public void CancelInput()
	{
		GetBindingExpression(ValueProperty)?.UpdateTarget();
		FormatValue();
	}
	public void Adjust(double direction, bool fine = false, bool coarse = false)
	{
		if (IsReadOnly || !IsEnabled || !Definition.Accepts(Value)) return;
		Publish(Definition.Adjust(Value, direction, fine, coarse));
		FormatValue();
	}
	public void ResetDraft()
	{
		if (IsReadOnly || !IsEnabled) return;
		Publish(Definition.DefaultValue);
		FormatValue();
	}
	private void Publish(double value)
	{
		_publishing = true;
		try { SetCurrentValue(ValueProperty, value); }
		finally { _publishing = false; }
	}
	protected override void OnTextChanged(System.Windows.Controls.TextChangedEventArgs e)
	{
		base.OnTextChanged(e);
		if (_formatting) return;
		var valid = Definition.TryParse(Text, CultureInfo.CurrentCulture, out var value);
		SetInputError(!valid);
		if (valid) Publish(value);
	}
	private void FormatValue()
	{
		_formatting = true;
		try { SetCurrentValue(TextProperty, Value.ToString("G17", CultureInfo.CurrentCulture)); }
		finally { _formatting = false; }
		AutomationProperties.SetName(this, Definition.Label);
		AutomationProperties.SetHelpText(this, Definition.Help);
		ToolTip = Definition.Help;
		SetInputError(!Definition.Accepts(Value));
	}
	private void SetInputError(bool error)
	{
		var changed = error != HasInputError;
		SetValue(HasInputErrorKey, error);
		if (error)
		{
			ToolTip = $"Invalid {Definition.Label}. Enter a finite value in {Definition.Minimum}..{Definition.Maximum} {Definition.Unit}" +
				(Definition.Integral ? " (whole number)." : ".");
			AutomationProperties.SetHelpText(this, ToolTip.ToString());
		}
		else { ToolTip = Definition.Help; AutomationProperties.SetHelpText(this, Definition.Help); }
		if (changed) AnnounceValidity();
	}
	private void AnnounceValidity() => RaiseEvent(new RoutedEventArgs(InputValidityChangedEvent, this));
	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			if (_scrubbing) { Publish(_originValue); _scrubbing = false; ReleaseMouseCapture(); }
			CancelInput(); e.Handled = true; return;
		}
		if (e.Key is Key.Up or Key.Down)
		{
			Adjust(e.Key == Key.Up ? 1 : -1, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
			e.Handled = true; return;
		}
		if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.Control)
		{ ResetDraft(); e.Handled = true; return; }
		if (e.Key == Key.Return) { e.Handled = true; return; }
		base.OnPreviewKeyDown(e);
	}
	protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && IsEnabled && !IsReadOnly && !HasInputError)
		{
			Focus(); _origin = e.GetPosition(this); _originValue = Value;
			_scrubbing = CaptureMouse(); e.Handled = true; return;
		}
		base.OnPreviewMouseLeftButtonDown(e);
	}
	protected override void OnPreviewMouseMove(MouseEventArgs e)
	{
		if (_scrubbing)
		{
			var delta = e.GetPosition(this).X - _origin.X;
			Publish(Definition.Adjust(_originValue, delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Control)));
			FormatValue(); e.Handled = true; return;
		}
		base.OnPreviewMouseMove(e);
	}
	protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		if (_scrubbing) { _scrubbing = false; ReleaseMouseCapture(); e.Handled = true; return; }
		base.OnPreviewMouseLeftButtonUp(e);
	}
	protected override void OnLostMouseCapture(MouseEventArgs e)
	{
		_scrubbing = false;
		base.OnLostMouseCapture(e);
	}
}

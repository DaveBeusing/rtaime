// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using rtaime.Operator.Controls;
using System.Windows.Controls;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class OperatorInspectorControl : UserControl
{
	public static readonly DependencyProperty InspectorProperty = DependencyProperty.Register(
		nameof(Inspector),
		typeof(MediaPoolInspectorViewModel),
		typeof(OperatorInspectorControl), new PropertyMetadata(null, InspectorChanged));

	public static readonly DependencyProperty ControlStateProperty = DependencyProperty.Register(
		nameof(ControlState),
		typeof(OperatorViewModel),
		typeof(OperatorInspectorControl));

	public static readonly DependencyProperty MediaDeckProperty = DependencyProperty.Register(
		nameof(MediaDeck),
		typeof(MediaDeckViewModel),
		typeof(OperatorInspectorControl));

	public static readonly DependencyProperty QuickControlsProperty = DependencyProperty.Register(
		nameof(QuickControls),
		typeof(OperatorQuickControlsViewModel),
		typeof(OperatorInspectorControl));

	public static readonly DependencyProperty CollapseCommandProperty = DependencyProperty.Register(
		nameof(CollapseCommand),
		typeof(ICommand),
		typeof(OperatorInspectorControl));

	public OperatorInspectorControl()
	{
		InitializeComponent();
		AddHandler(RtaimeNumericField.InputValidityChangedEvent, new RoutedEventHandler((_, _) => UpdateNumericValidity()));
		Loaded += (_, _) => { SubscribeInspector(); UpdateNumericValidity(); };
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        { if (FindEditor(e.NewFocus as DependencyObject) is not null) Inspector?.BeginDraftTransaction(); }));
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        { if (FindEditor(e.OldFocus as DependencyObject) is not null && !ReferenceEquals(FindEditor(e.OldFocus as DependencyObject), FindEditor(e.NewFocus as DependencyObject))) Inspector?.EndDraftTransaction(); }));
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        { if (FindEditor(e.OriginalSource as DependencyObject) is not null) Inspector?.BeginDraftTransaction(); }), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        { if (FindEditor(e.OriginalSource as DependencyObject) is not null) Inspector?.EndDraftTransaction(); }), true);
        PreviewKeyDown += OnDraftKeyDown;
		Unloaded += (_, _) => UnsubscribeInspector();
	}

	public MediaPoolInspectorViewModel? Inspector
	{
		get => (MediaPoolInspectorViewModel?)GetValue(InspectorProperty);
		set => SetValue(InspectorProperty, value);
	}

	public OperatorViewModel? ControlState
	{
		get => (OperatorViewModel?)GetValue(ControlStateProperty);
		set => SetValue(ControlStateProperty, value);
	}

	public MediaDeckViewModel? MediaDeck
	{
		get => (MediaDeckViewModel?)GetValue(MediaDeckProperty);
		set => SetValue(MediaDeckProperty, value);
	}

	public OperatorQuickControlsViewModel? QuickControls
	{
		get => (OperatorQuickControlsViewModel?)GetValue(QuickControlsProperty);
		set => SetValue(QuickControlsProperty, value);
	}

	public ICommand? CollapseCommand
	{
		get => (ICommand?)GetValue(CollapseCommandProperty);
		set => SetValue(CollapseCommandProperty, value);
	}
	private MediaPoolInspectorViewModel? _subscribedInspector;
	private string? _selectionIdentity;
	private static readonly DependencyPropertyKey NumericInputsValidKey = DependencyProperty.RegisterReadOnly(
		nameof(NumericInputsValid), typeof(bool), typeof(OperatorInspectorControl), new PropertyMetadata(true));
	public static readonly DependencyProperty NumericInputsValidProperty = NumericInputsValidKey.DependencyProperty;
	public bool NumericInputsValid => (bool)GetValue(NumericInputsValidProperty);
	private static void InspectorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
	{
		var control = (OperatorInspectorControl)sender;
		control.UnsubscribeInspector();
		if (control.IsLoaded) control.SubscribeInspector();
	}
	private void SubscribeInspector()
	{
		if (ReferenceEquals(_subscribedInspector, Inspector)) return;
		UnsubscribeInspector();
		_subscribedInspector = Inspector;
		_selectionIdentity = Inspector?.EditingSelectionIdentity;
		if (_subscribedInspector is not null) _subscribedInspector.PropertyChanged += InspectorChanged;
	}
	private void UnsubscribeInspector()
	{
        _subscribedInspector?.EndDraftTransaction();
		if (_subscribedInspector is not null) _subscribedInspector.PropertyChanged -= InspectorChanged;
		_subscribedInspector = null;
	}
	private void InspectorChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(MediaPoolInspectorViewModel.EditingSelectionIdentity) ||
			_selectionIdentity == Inspector?.EditingSelectionIdentity) return;
		_selectionIdentity = Inspector?.EditingSelectionIdentity;
		foreach (var field in NumericFields(this)) field.CancelInput();
		UpdateNumericValidity();
	}
	private void UpdateNumericValidity()
	{
		var valid = !NumericFields(this).Any(field => field.IsVisible && field.HasInputError);
		SetValue(NumericInputsValidKey, valid);
		if (Inspector is not null) Inspector.InspectorInputsValid = valid;
	}
    private DependencyObject? FindEditor(DependencyObject? element)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, this);)
        {
            if (current is RtaimeNumericField or RtaimeSlider or RtaimeToggleButton or RtaimeTextBox) return current;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
    private void OnDraftKeyDown(object sender, KeyEventArgs e)
    {
        if (Inspector is not { } model) return;
        var editor = FindEditor(Keyboard.FocusedElement as DependencyObject);
        if (e.Key == Key.Escape && editor is not null) model.CancelDraftTransaction();
        if (editor is not null) model.BeginDraftTransaction();
        if (OperatorKeyboardCommandRegistry.IsTextEntryContext(Keyboard.FocusedElement as DependencyObject) ||
            Keyboard.Modifiers != ModifierKeys.Control) return;
        var command = e.Key == Key.Z ? model.UndoInspectorDraftCommand : e.Key == Key.Y ? model.RedoInspectorDraftCommand : null;
        if (command is null) return;
        if (!e.IsRepeat && command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }
	private static IEnumerable<RtaimeNumericField> NumericFields(DependencyObject root)
	{
		for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			var child = VisualTreeHelper.GetChild(root, index);
			// WPF can expose an empty child slot while an IsVisibleChanged removal event is bubbling.
			if (child is null) continue;
			if (child is RtaimeNumericField field) yield return field;
			else foreach (var descendant in NumericFields(child)) yield return descendant;
		}
	}

}

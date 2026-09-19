// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace rtaime.Operator.Controls;

public class RtaimeTextBox : TextBox
{
	public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
		nameof(IsCompact),
		typeof(bool),
		typeof(RtaimeTextBox),
		new FrameworkPropertyMetadata(false));

	static RtaimeTextBox()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeTextBox), new FrameworkPropertyMetadata(typeof(RtaimeTextBox)));
	}

	public bool IsCompact
	{
		get => (bool)GetValue(IsCompactProperty);
		set => SetValue(IsCompactProperty, value);
	}
}

public class RtaimeComboBoxItem : ComboBoxItem
{
	static RtaimeComboBoxItem()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeComboBoxItem), new FrameworkPropertyMetadata(typeof(RtaimeComboBoxItem)));
	}
}

public class RtaimeComboBox : ComboBox
{
	static RtaimeComboBox()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeComboBox), new FrameworkPropertyMetadata(typeof(RtaimeComboBox)));
	}

	protected override DependencyObject GetContainerForItemOverride()
	{
		return new RtaimeComboBoxItem();
	}

	protected override bool IsItemItsOwnContainerOverride(object item)
	{
		return item is RtaimeComboBoxItem;
	}
}

public class RtaimeCheckBox : CheckBox
{
	static RtaimeCheckBox()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeCheckBox), new FrameworkPropertyMetadata(typeof(RtaimeCheckBox)));
	}
}

public class RtaimeRadioButton : RadioButton
{
	static RtaimeRadioButton()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeRadioButton), new FrameworkPropertyMetadata(typeof(RtaimeRadioButton)));
	}
}

public class RtaimeSlider : Slider
{
	static RtaimeSlider()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeSlider), new FrameworkPropertyMetadata(typeof(RtaimeSlider)));
	}
}

public class RtaimeScrollBar : ScrollBar
{
	static RtaimeScrollBar()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeScrollBar), new FrameworkPropertyMetadata(typeof(RtaimeScrollBar)));
	}
}

public class RtaimeScrollViewer : ScrollViewer
{
	static RtaimeScrollViewer()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeScrollViewer), new FrameworkPropertyMetadata(typeof(RtaimeScrollViewer)));
	}
}

public class RtaimeSplitter : GridSplitter
{
	static RtaimeSplitter()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeSplitter), new FrameworkPropertyMetadata(typeof(RtaimeSplitter)));
	}
}

public class RtaimeListBoxItem : ListBoxItem
{
	static RtaimeListBoxItem()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeListBoxItem), new FrameworkPropertyMetadata(typeof(RtaimeListBoxItem)));
	}
}

public class RtaimeListBox : ListBox
{
	static RtaimeListBox()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeListBox), new FrameworkPropertyMetadata(typeof(RtaimeListBox)));
	}

	protected override DependencyObject GetContainerForItemOverride()
	{
		return new RtaimeListBoxItem();
	}

	protected override bool IsItemItsOwnContainerOverride(object item)
	{
		return item is RtaimeListBoxItem;
	}
}

public class RtaimeTabItem : TabItem
{
	static RtaimeTabItem()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeTabItem), new FrameworkPropertyMetadata(typeof(RtaimeTabItem)));
	}
}

public class RtaimeTabControl : TabControl
{
	static RtaimeTabControl()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeTabControl), new FrameworkPropertyMetadata(typeof(RtaimeTabControl)));
	}

	protected override DependencyObject GetContainerForItemOverride()
	{
		return new RtaimeTabItem();
	}

	protected override bool IsItemItsOwnContainerOverride(object item)
	{
		return item is RtaimeTabItem;
	}
}

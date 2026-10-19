// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;

namespace rtaime.Operator;

public partial class MainWindow
{
    public static readonly DependencyProperty TimelineSequenceVisibilityProperty =
        DependencyProperty.Register(nameof(TimelineSequenceVisibility), typeof(Visibility),
            typeof(MainWindow), new PropertyMetadata(Visibility.Visible));

    public static readonly DependencyProperty TimelineMediaVisibilityProperty =
        DependencyProperty.Register(nameof(TimelineMediaVisibility), typeof(Visibility),
            typeof(MainWindow), new PropertyMetadata(Visibility.Collapsed));

    public Visibility TimelineSequenceVisibility
    {
        get => (Visibility)GetValue(TimelineSequenceVisibilityProperty);
        set => SetValue(TimelineSequenceVisibilityProperty, value);
    }

    public Visibility TimelineMediaVisibility
    {
        get => (Visibility)GetValue(TimelineMediaVisibilityProperty);
        set => SetValue(TimelineMediaVisibilityProperty, value);
    }

    private void OnShowSequenceTimeline(object sender, RoutedEventArgs e)
    {
        TimelineMediaVisibility = Visibility.Collapsed;
        TimelineSequenceVisibility = Visibility.Visible;
    }

    private void OnShowMediaTimeline(object sender, RoutedEventArgs e)
    {
        TimelineSequenceVisibility = Visibility.Collapsed;
        TimelineMediaVisibility = Visibility.Visible;
    }
}

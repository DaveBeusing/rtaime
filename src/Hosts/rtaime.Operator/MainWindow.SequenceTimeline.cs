// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using rtaime.Client;

namespace rtaime.Operator;

public partial class MainWindow
{
    private void ConfigureSequencePlayout(
        NamedPipeOperatorControlTransport controlTransport, OperatorViewModel owner)
    {
        // Reuse the one authenticated ControlHost connection and its authority snapshot.
        // This is presentation composition only; Runtime owns the output clock and sink.
        SequenceTimeline.AttachPlayout(new SequenceOperatorPlayoutViewModel(
            SequenceTimeline, new SequenceAuthoringClient(controlTransport),
            new SequencePreparationClient(controlTransport),
            new SequenceShuttleClient(controlTransport),
            new SequenceOutputClient(controlTransport),
            () => SequenceOperatorAuthority.From(owner), owner));
    }

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

// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorVisualQualificationTests : IDisposable
{
	private const double ShellTopBarHeight = 60;
	private const double ShellStatusBarHeight = 30;
	private const double ShellRegionGap = 6;
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"rtaime-operator-visual-qualification",
		Guid.NewGuid().ToString("N"));
	private string LayoutPath => Path.Combine(_root, "operator-layout.json");

	public OperatorVisualQualificationTests()
	{
		Directory.CreateDirectory(_root);
	}

	public static IEnumerable<object[]> QualificationMatrix()
	{
		yield return ["reference-100", 1920.0, 1080.0, 100, 1.0, false, false, false];
		yield return ["standard-window", 1600.0, 900.0, 100, 5.0 / 6.0, false, false, false];
		yield return ["reference-125", 1536.0, 864.0, 125, 0.8, false, false, false];
		yield return ["reference-150", 1280.0, 720.0, 150, 2.0 / 3.0, false, true, true];
		yield return ["reference-200", 960.0, 540.0, 200, 0.6, true, true, true];
	}

	[Theory]
	[MemberData(nameof(QualificationMatrix))]
	public void Qualification_matrix_preserves_workspace_geometry(
		string caseName,
		double viewportWidth,
		double viewportHeight,
		int dpiPercent,
		double expectedScale,
		bool expectedCompact,
		bool expectedCompactNavigation,
		bool expectedSecondaryMetricsCollapsed)
	{
		var shell = CreateShell();
		shell.UpdateViewportSize(viewportWidth, viewportHeight);

		Assert.True(dpiPercent is >= 100 and <= 200, $"{caseName}: unsupported qualification DPI metadata.");
		Assert.InRange(Math.Abs(shell.WorkspaceScale - expectedScale), 0, 0.002);
		Assert.Equal(expectedCompact, shell.IsCompactViewport);
		Assert.Equal(expectedCompactNavigation ? 68 : 92, shell.NavigationRailWidth);
		Assert.Equal(
			expectedCompactNavigation ? Visibility.Collapsed : Visibility.Visible,
			shell.NavigationLabelVisibility);
		Assert.Equal(
			expectedSecondaryMetricsCollapsed ? Visibility.Collapsed : Visibility.Visible,
			shell.SecondaryMetricVisibility);

		foreach (var workspace in OperatorWorkspaceNames.All)
		{
			shell.SelectWorkspace(workspace);

			AssertFiniteNonNegative(shell.LeftColumnWidth.Value, $"{caseName}/{workspace}: left column");
			AssertFiniteNonNegative(shell.RightColumnWidth.Value, $"{caseName}/{workspace}: right column");
			AssertFiniteNonNegative(shell.LowerRowHeight.Value, $"{caseName}/{workspace}: lower row");
			AssertFiniteNonNegative(shell.LeftSplitterWidth, $"{caseName}/{workspace}: left splitter");
			AssertFiniteNonNegative(shell.RightSplitterWidth, $"{caseName}/{workspace}: right splitter");
			AssertFiniteNonNegative(shell.LowerSplitterHeight, $"{caseName}/{workspace}: lower splitter");
			AssertFiniteNonNegative(shell.CenterWorkspacePrimaryMinWidth, $"{caseName}/{workspace}: center minimum");
			AssertFiniteNonNegative(shell.AuxiliaryWorkspaceColumnMinWidth, $"{caseName}/{workspace}: auxiliary minimum");
			AssertFiniteNonNegative(shell.AuxiliaryWorkspaceGapWidth, $"{caseName}/{workspace}: auxiliary gap");
			AssertFinitePositive(shell.ProductionWorkspaceHeight, $"{caseName}/{workspace}: production workspace");
			AssertFinitePositive(shell.MediaDeckPreviewHeight, $"{caseName}/{workspace}: media preview");
			AssertFinitePositive(shell.LiveLowerPanelHeight, $"{caseName}/{workspace}: live lower panel");
			AssertFinitePositive(shell.CompositingPreviewHeight, $"{caseName}/{workspace}: compositing preview");
			AssertFinitePositive(shell.HealthSidebarWidth, $"{caseName}/{workspace}: health sidebar");
			AssertFinitePositive(shell.HealthOverviewCardWidth, $"{caseName}/{workspace}: health overview");
			AssertFinitePositive(shell.SourceBinThumbnailWidth, $"{caseName}/{workspace}: source thumbnail");
			AssertFinitePositive(shell.QuickControlCardWidth, $"{caseName}/{workspace}: quick control");
			AssertFinitePositive(shell.TimelineHeaderColumnWidth.Value, $"{caseName}/{workspace}: timeline header");

			if (!shell.HasLeftRegion)
			{
				Assert.Equal(0, shell.LeftColumnWidth.Value);
				Assert.Equal(0, shell.LeftSplitterWidth);
			}

			var fixedHorizontal = shell.NavigationRailWidth +
				ShellRegionGap +
				shell.LeftColumnWidth.Value +
				shell.LeftSplitterWidth +
				shell.RightSplitterWidth +
				shell.RightColumnWidth.Value;
			var centerAvailableWidth = viewportWidth - fixedHorizontal;
			Assert.True(
				centerAvailableWidth > 0,
				$"{caseName}/{workspace}: fixed shell geometry consumes the full viewport.");
			Assert.True(
				shell.CenterWorkspacePrimaryMinWidth <= centerAvailableWidth + 0.5,
				$"{caseName}/{workspace}: center minimum exceeds the available center container.");

			if (shell.HasAuxiliaryWorkspaceColumn && !shell.IsCompactViewport)
			{
				Assert.True(shell.AuxiliaryWorkspaceColumnWidth.IsStar);
				Assert.True(
					shell.CenterWorkspacePrimaryMinWidth +
					shell.AuxiliaryWorkspaceGapWidth +
					shell.AuxiliaryWorkspaceColumnMinWidth <= centerAvailableWidth + 0.5,
					$"{caseName}/{workspace}: primary and auxiliary workspace minimums overflow the center container.");
			}
			else
			{
				Assert.Equal(0, shell.AuxiliaryWorkspaceColumnWidth.Value);
				Assert.Equal(0, shell.AuxiliaryWorkspaceColumnMinWidth);
				Assert.Equal(0, shell.AuxiliaryWorkspaceGapWidth);
			}

			var bodyAvailableHeight = viewportHeight - ShellTopBarHeight - ShellStatusBarHeight;
			Assert.True(bodyAvailableHeight > 0, $"{caseName}: shell chrome consumes the full viewport height.");
			Assert.True(
				shell.LowerRowHeight.Value + shell.LowerSplitterHeight <= bodyAvailableHeight + 0.5,
				$"{caseName}/{workspace}: lower workspace region overflows the body.");
			Assert.True(
				shell.ProductionWorkspaceHeight <= bodyAvailableHeight + 0.5,
				$"{caseName}/{workspace}: production workspace exceeds the body height.");

			if (expectedCompact)
			{
				Assert.Equal(Visibility.Collapsed, shell.CompactOptionalVisibility);
				Assert.Equal(0, shell.LeftColumnWidth.Value);
				Assert.Equal(0, shell.RightColumnWidth.Value);
				Assert.Equal(0, shell.LeftSplitterWidth);
				Assert.Equal(0, shell.RightSplitterWidth);
				Assert.Equal(0, shell.LowerSplitterHeight);
				Assert.Equal(0, shell.CenterWorkspacePrimaryMinWidth);
			}
			else
			{
				Assert.Equal(Visibility.Visible, shell.CompactOptionalVisibility);
			}
		}
	}

	[Fact]
	public async Task Viewport_changes_do_not_mutate_persisted_reference_layout()
	{
		var shell = CreateShell();
		shell.UpdateViewportSize(1920, 1080);
		shell.SelectWorkspace(OperatorWorkspaceNames.Edit);
		shell.LeftColumnWidth = new GridLength(500);
		shell.RightColumnWidth = new GridLength(450);
		shell.LowerRowHeight = new GridLength(400);
		await shell.SaveAsync();

		shell.UpdateViewportSize(960, 540);
		Assert.True(shell.IsCompactViewport);
		Assert.Equal(0, shell.LeftColumnWidth.Value);
		Assert.Equal(0, shell.RightColumnWidth.Value);
		Assert.Equal(200, shell.LowerRowHeight.Value);
		await shell.SaveAsync();

		var persisted = new OperatorLayoutStore(LayoutPath).Load();
		var edit = persisted.Workspaces[OperatorWorkspaceNames.Edit];
		Assert.Equal(500, edit.LeftPanelWidth);
		Assert.Equal(450, edit.RightPanelWidth);
		Assert.Equal(400, edit.LowerPanelHeight);
		Assert.False(edit.IsLeftCollapsed);
		Assert.False(edit.IsRightCollapsed);
	}

	[Fact]
	public void Viewer_and_fullscreen_modes_restore_without_changing_authoritative_layout()
	{
		var fullscreenRequests = new List<bool>();
		var shell = CreateShell(fullscreenRequests.Add);
		shell.UpdateViewportSize(1536, 864);
		shell.SelectWorkspace(OperatorWorkspaceNames.Edit);
		var baselineLeft = shell.LeftColumnWidth.Value;
		var baselineRight = shell.RightColumnWidth.Value;
		var baselineLower = shell.LowerRowHeight.Value;

		shell.MaximizePreviewCommand.Execute(null);
		Assert.Equal("PREVIEW", shell.ViewerMode);
		Assert.Equal(Visibility.Visible, shell.PreviewViewerVisibility);
		Assert.Equal(Visibility.Collapsed, shell.ProgramViewerVisibility);

		shell.RestoreViewersCommand.Execute(null);
		Assert.Equal("DUAL", shell.ViewerMode);

		shell.ToggleCenterMaximizeCommand.Execute(null);
		Assert.True(shell.IsCenterMaximized);
		Assert.Equal(0, shell.LeftColumnWidth.Value);
		Assert.Equal(0, shell.RightColumnWidth.Value);
		Assert.Equal(0, shell.LowerRowHeight.Value);

		shell.ToggleCenterMaximizeCommand.Execute(null);
		Assert.False(shell.IsCenterMaximized);
		Assert.Equal(baselineLeft, shell.LeftColumnWidth.Value, 3);
		Assert.Equal(baselineRight, shell.RightColumnWidth.Value, 3);
		Assert.Equal(baselineLower, shell.LowerRowHeight.Value, 3);

		shell.FullscreenPreviewCommand.Execute(null);
		Assert.True(shell.IsCenterMaximized);
		Assert.Equal("PREVIEW", shell.ViewerMode);
		Assert.Equal(true, fullscreenRequests[^1]);

		shell.ExitFullscreenCommand.Execute(null);
		Assert.False(shell.IsCenterMaximized);
		Assert.Equal("DUAL", shell.ViewerMode);
		Assert.Equal(false, fullscreenRequests[^1]);
		Assert.Equal(baselineLeft, shell.LeftColumnWidth.Value, 3);
		Assert.Equal(baselineRight, shell.RightColumnWidth.Value, 3);
	}

	[Fact]
	public void Startup_surface_is_container_driven_and_structurally_bounded()
	{
		var repositoryRoot = FindRepositoryRoot();
		var windowPath = Path.Combine(repositoryRoot, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml");
		var windowCodePath = Path.Combine(repositoryRoot, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml.cs");
		var document = XDocument.Load(windowPath);
		XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
		XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

		var root = document.Root;
		Assert.NotNull(root);
		Assert.Equal("1600", root.Attribute("Width")?.Value);
		Assert.Equal("900", root.Attribute("Height")?.Value);
		Assert.Equal("960", root.Attribute("MinWidth")?.Value);
		Assert.Equal("500", root.Attribute("MinHeight")?.Value);
		Assert.Equal("True", root.Attribute("UseLayoutRounding")?.Value);
		Assert.Equal("True", root.Attribute("SnapsToDevicePixels")?.Value);

		var startupOverlay = document
			.Descendants(presentation + "Border")
			.Single(element => element.Attribute(x + "Name")?.Value == "StartupOverlay");
		Assert.Null(startupOverlay.Attribute("Width"));
		Assert.Null(startupOverlay.Attribute("Height"));
		Assert.Equal("3", startupOverlay.Attribute("Grid.RowSpan")?.Value);

		var stageGrid = startupOverlay
			.Descendants(presentation + "UniformGrid")
			.Single(element => element.Attribute("Columns")?.Value == "7");
		Assert.Equal("1", stageGrid.Attribute("Rows")?.Value);
		Assert.DoesNotContain(
			startupOverlay.Descendants(),
			element => element.Name.LocalName == "ProgressBar");

		var detailsButton = startupOverlay
			.Descendants()
			.Single(element => element.Attribute(x + "Name")?.Value == "StartupDetailsButton");
		Assert.Equal("RtaimeButton", detailsButton.Name.LocalName);

		var windowCode = File.ReadAllText(windowCodePath);
		Assert.Contains("SystemParameters.ClientAreaAnimation", windowCode, StringComparison.Ordinal);
		Assert.Contains("StartStartupBrandAnimation", windowCode, StringComparison.Ordinal);
	}

	[Fact]
	public void Workspace_surface_bindings_use_shell_geometry_instead_of_fixed_reference_render_sizes()
	{
		var repositoryRoot = FindRepositoryRoot();
		var windowPath = Path.Combine(repositoryRoot, "src", "Hosts", "rtaime.Operator", "MainWindow.xaml");
		var window = File.ReadAllText(windowPath);

		foreach (var binding in new[]
		{
			"Shell.NavigationRailWidth",
			"Shell.LeftColumnWidth",
			"Shell.RightColumnWidth",
			"Shell.LowerRowHeight",
			"Shell.CenterWorkspacePrimaryMinWidth",
			"Shell.AuxiliaryWorkspaceColumnWidth",
			"Shell.ProductionWorkspaceHeight"
		})
		{
			Assert.Contains(binding, window, StringComparison.Ordinal);
		}

		Assert.DoesNotContain("Width=\"1920\"", window, StringComparison.Ordinal);
		Assert.DoesNotContain("Height=\"1080\"", window, StringComparison.Ordinal);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private OperatorShellViewModel CreateShell(Action<bool>? setFullscreen = null) =>
		new(new OperatorLayoutStore(LayoutPath), setFullscreen ?? (_ => { }));

	private static void AssertFiniteNonNegative(double value, string message)
	{
		Assert.True(double.IsFinite(value), $"{message} is not finite.");
		Assert.True(value >= 0, $"{message} is negative.");
	}

	private static void AssertFinitePositive(double value, string message)
	{
		Assert.True(double.IsFinite(value), $"{message} is not finite.");
		Assert.True(value > 0, $"{message} is not positive.");
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Repository root containing rtaime.slnx could not be located.");
	}
}

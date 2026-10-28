// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using rtaime.Client;

namespace rtaime.Operator;

// OGraf inspection is explicitly local and has no route to Production authority.
// The static PNG is a package thumbnail, NEVER a rendered Preview or Program frame.
public sealed partial class OperatorViewModel
{
    private Func<string?>? _ografManifestPicker;
    private bool _ografInspectionBusy;
    private OGrafPackageInspection? _ografInspection;
    private string _ografInspectionStatus =
        "UNAVAILABLE · import a local .ograf.json manifest for read-only inspection.";
    private string _ografInspectionDetails =
        "OGraf v1 HTML/JavaScript rendering requires a separately qualified isolated provider. Program admission is disabled.";
    private ImageSource? _ografPackageThumbnail;

    public ICommand InspectOGrafManifestCommand { get; private set; } = null!;

    public string OGrafInspectionStatus
    {
        get => _ografInspectionStatus;
        private set => Set(ref _ografInspectionStatus, value);
    }

    public string OGrafInspectionDetails
    {
        get => _ografInspectionDetails;
        private set => Set(ref _ografInspectionDetails, value);
    }

    public ImageSource? OGrafPackageThumbnail
    {
        get => _ografPackageThumbnail;
        private set => Set(ref _ografPackageThumbnail, value);
    }

    public OGrafPackageInspection? InspectedOGrafPackage => _ografInspection;

    public bool OGrafCanTakeToProgram => false;

    private void InitializeOGrafInspection()
    {
        InspectOGrafManifestCommand = new AsyncRelayCommand(
            InspectOGrafManifestAsync, () => _ografManifestPicker is not null && !_ografInspectionBusy);
    }

    internal void SetOGrafManifestPicker(Func<string?> picker)
    {
        _ografManifestPicker = picker ?? throw new ArgumentNullException(nameof(picker));
        (InspectOGrafManifestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task InspectOGrafManifestAsync()
    {
        if (_ografManifestPicker is null || _ografInspectionBusy)
            return;
        string? manifest;
        try { manifest = _ografManifestPicker(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            OGrafInspectionStatus = "IMPORT REJECTED · local file selection failed.";
            OGrafInspectionDetails = ex.Message;
            return;
        }
        if (string.IsNullOrWhiteSpace(manifest)) return;

        _ografInspectionBusy = true;
        (InspectOGrafManifestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        _ografInspection = null;
        OnPropertyChanged(nameof(InspectedOGrafPackage));
        OGrafPackageThumbnail = null;
        OGrafInspectionStatus = "INSPECTING · local manifest and package resource bounds";
        OGrafInspectionDetails = "No JavaScript is executed. No Program/Preview command is issued.";
        try
        {
            var reviewed = await OGrafPackageInspector.InspectAsync(manifest);
            ImageSource? thumbnail = null;
            if (reviewed.ThumbnailPath is not null)
            {
                try
                {
                    var bytes = await OGrafPackageInspector.ReadThumbnailAsync(reviewed);
                    if (bytes is not null)
                    {
                        using var stream = new MemoryStream(bytes, writable: false);
                        var decoder = new PngBitmapDecoder(stream,
                            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        if (decoder.Frames.Count != 1 ||
                            decoder.Frames[0].PixelWidth is < 1 or > 384 ||
                            decoder.Frames[0].PixelHeight is < 1 or > 384)
                            throw new InvalidDataException("Thumbnail dimensions are outside the 384x384 inspection bound.");
                        var image = decoder.Frames[0];
                        image.Freeze();
                        thumbnail = image;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                {
                    OGrafInspectionDetails = "Package valid; PNG thumbnail could not be safely decoded.";
                }
            }
            _ografInspection = reviewed;
            OnPropertyChanged(nameof(InspectedOGrafPackage));
            OGrafPackageThumbnail = thumbnail;
            OGrafInspectionStatus = "INSPECTED · " + reviewed.Name + " · Program blocked";
            OGrafInspectionDetails = reviewed.GraphicId + " · " +
                (reviewed.Version ?? "unversioned") + " · " +
                reviewed.Parameters.Count + " scalar parameters · " +
                reviewed.CustomActionIds.Count + " custom actions · " +
                reviewed.ActionDurations.Count + " declared durations · " +
                (thumbnail is null ? "no decoded thumbnail" : "static package thumbnail (NOT rendered Preview)") +
                " · source/lifecycle/asset rights UNVERIFIED; isolated renderer UNAVAILABLE.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            OGrafInspectionStatus = "IMPORT REJECTED · no rendering or routing performed";
            OGrafInspectionDetails = ex.Message;
        }
        finally
        {
            _ografInspectionBusy = false;
            (InspectOGrafManifestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}

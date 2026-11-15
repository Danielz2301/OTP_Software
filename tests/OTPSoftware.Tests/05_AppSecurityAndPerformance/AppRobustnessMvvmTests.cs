namespace OTPSoftware.Tests;

using OTPSoftware.App.Services;
using OTPSoftware.App.ViewModels;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;
using Xunit;

public sealed class AppRobustnessMvvmTests
{
    private sealed class BlockingCameraImageService : ICameraImageService
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _firstProbeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _probeCount;
        private CancellationToken _firstProbeToken;

        public Task FirstProbeStarted => _firstProbeStarted.Task;
        public CancellationToken FirstProbeToken
        {
            get { lock (_gate) return _firstProbeToken; }
        }

        public async Task<bool> TestHostReachableAsync(
            string? uncPath,
            int timeoutMs = 1200,
            CancellationToken cancellationToken = default)
        {
            int callIndex = Interlocked.Increment(ref _probeCount);
            if (callIndex == 1)
            {
                lock (_gate)
                {
                    _firstProbeToken = cancellationToken;
                }
                _firstProbeStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        public Task<string?> FindPanelImageFolderAsync(
            string rootPath,
            string panelName,
            string sideCode,
            DateTime? timestamp,
            bool skipHostCheck = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public IReadOnlyList<CameraEndpointDescriptor> GetCameraEndpoints(OtpSettings settings) => [];

        public Task<IReadOnlyList<CameraHealthStatus>> CheckCameraEndpointsHealthAsync(
            IReadOnlyList<CameraEndpointDescriptor> endpoints,
            int timeoutMs = 600,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CameraHealthStatus>>([]);
    }

    private sealed class InstantEmptyMesDetailService : IMesDetailService
    {
        public MesParameterEvaluation EvaluateParameter(string? paramName, string? paramValue, bool groupIsNg = false) =>
            new("-", "OK", MesEvaluationStatus.Ok);

        public IReadOnlyList<MesParameterRow> ConvertFromOtpDefectDetail(string? otpDefectCode, string? otpDefectValue) => [];

        public IReadOnlyList<MesPanelDetailRecord> FindMesPanelDetails(
            string mesLogPath,
            string panelName,
            int maxRotatedFiles = 5,
            long chunkBytes = 4 * 1024 * 1024,
            CancellationToken cancellationToken = default) => [];

        public IReadOnlyList<MesPanelDetailRecord> ExtractRecentPanelRecords(
            string mesLogPath,
            int maxBytes = 8 * 1024 * 1024) => [];
    }

    private static OtpSettings CreateTestSettingsWithCamera(string machineName = "MMAPI120")
    {
        var machine = new MachineConfig
        {
            Name = machineName,
            Images =
            [
                new CameraImageConfig(@"\\192.0.2.10\share", "A1"),
                new CameraImageConfig(@"\\192.0.2.11\share", "A2")
            ]
        };
        return new OtpSettings
        {
            Machines = [machine],
            MachinesByName = new Dictionary<string, MachineConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [machineName] = machine
            }
        };
    }

    [Fact]
    public async Task SpotlightViewModel_WhenInspectUnitCalledRapidly_CancelsPreviousInFlightLookup()
    {
        var cameraStub = new BlockingCameraImageService();
        using var bgMonitor = new BackgroundMonitorService(
            cameraImageService: cameraStub,
            mesDetailService: new InstantEmptyMesDetailService());
        using var vm = new SpotlightViewModel(bgMonitor, CreateTestSettingsWithCamera());

        var unit1 = new UnitRecord
        {
            Source = "MMAPI120",
            PanelName = "PANEL_FIRST",
            BluName = "BLU_1",
            Pmnt = "PMNT1",
            Errors = ["OTP-NG"],
            Timestamp = new DateTime(2026, 11, 15, 10, 0, 0)
        };
        var unit2 = new UnitRecord
        {
            Source = "MMAPI120",
            PanelName = "PANEL_SECOND",
            BluName = "BLU_2",
            Pmnt = "PMNT2",
            Errors = ["OTP-NG"],
            Timestamp = new DateTime(2026, 11, 15, 10, 1, 0)
        };

        Task firstInspection = vm.InspectUnitAsync(unit1, []);
        await cameraStub.FirstProbeStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await vm.InspectUnitAsync(unit2, []);
        await firstInspection;

        Assert.True(cameraStub.FirstProbeToken.IsCancellationRequested);
        Assert.Equal("PANEL_SECOND", vm.PanelName);
        Assert.True(vm.HasSpotlightUnit);
    }

    [Fact]
    public async Task SpotlightViewModel_WhenClearedMidLookup_CancelsInFlightLookupAndResetsState()
    {
        var cameraStub = new BlockingCameraImageService();
        using var bgMonitor = new BackgroundMonitorService(
            cameraImageService: cameraStub,
            mesDetailService: new InstantEmptyMesDetailService());
        using var vm = new SpotlightViewModel(bgMonitor, CreateTestSettingsWithCamera());

        var unit = new UnitRecord
        {
            Source = "MMAPI120",
            PanelName = "PANEL_CLEAR_TEST",
            Errors = ["OTP-NG"],
            Timestamp = new DateTime(2026, 11, 15, 10, 5, 0)
        };

        Task inspection = vm.InspectUnitAsync(unit, []);
        await cameraStub.FirstProbeStarted.WaitAsync(TimeSpan.FromSeconds(5));

        vm.Clear();
        await inspection;

        Assert.True(cameraStub.FirstProbeToken.IsCancellationRequested);
        Assert.False(vm.HasSpotlightUnit);
    }

    [Fact]
    public async Task MesDetailViewModel_WhenDisposedDuringInFlightLookup_CancelsTokensAndSupportsHeadlessFiltering()
    {
        var cameraStub = new BlockingCameraImageService();
        using var bgMonitor = new BackgroundMonitorService(
            cameraImageService: cameraStub,
            mesDetailService: new InstantEmptyMesDetailService());

        var row = new DisplayRowViewModel
        {
            PanelName = "PANEL_MES_VM",
            Source = "MMAPI120",
            PMNT = "PMNT_01",
            BluName = "BLU_01",
            TimeFormatted = "2026-11-15 10:15:00",
            Timestamp = new DateTime(2026, 11, 15, 10, 15, 0),
            Errors = "OTP-White_lv"
        };

        var run1 = new MesPanelDetailRecord
        {
            PanelName = "PANEL_MES_VM",
            LogTime = "2026-11-15 10:10:00.000",
            TestResult = "NG",
            Parameters =
            [
                new MesParameterRow(1, "WHITE", "White_lv", "280.0", ">=300", "NG", MesEvaluationStatus.Ng),
                new MesParameterRow(2, "WHITE", "contrast", "1250", ">=1000", "OK", MesEvaluationStatus.Ok)
            ]
        };
        var run2 = new MesPanelDetailRecord
        {
            PanelName = "PANEL_MES_VM",
            LogTime = "2026-11-15 10:15:00.000",
            TestResult = "OK",
            Parameters =
            [
                new MesParameterRow(1, "WHITE", "White_lv", "335.0", ">=300", "OK", MesEvaluationStatus.Ok)
            ]
        };

        IReadOnlyList<CameraImageConfig> camConfigs =
        [
            new CameraImageConfig(@"\\192.0.2.10\share", "A1"),
            new CameraImageConfig(@"\\192.0.2.11\share", "A2")
        ];

        using var vm = new MesDetailViewModel(row, [run1, run2], "MES_1.log", bgMonitor, camConfigs);

        Assert.True(vm.IsMultiRun);
        Assert.Equal(1, vm.SelectedRunIndex);
        Assert.Single(vm.Parameters);

        vm.SelectedRunIndex = 0;
        Assert.Equal(2, vm.Parameters.Count);

        vm.ShowNgOnly = true;
        Assert.Single(vm.Parameters);
        Assert.Equal("White_lv", vm.Parameters[0].ParamName);

        vm.ShowNgOnly = false;
        vm.ParameterSearchText = "contrast";
        Assert.Single(vm.Parameters);
        Assert.Equal("contrast", vm.Parameters[0].ParamName);

        vm.SelectedCameraIndex = 1;
        Assert.True(vm.IsRightCameraSelected);

        await cameraStub.FirstProbeStarted.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Dispose();

        Assert.True(cameraStub.FirstProbeToken.IsCancellationRequested);
        Assert.Null(vm.LeftThumbnail);
        Assert.Null(vm.RightThumbnail);
    }
}

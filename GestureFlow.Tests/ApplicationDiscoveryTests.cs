using System.IO;
using System.Text.Json;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.Lab.Simulation;
using ApplicationIdentity = GestureFlow.Desktop.Applications.ApplicationIdentity;

namespace GestureFlow.Tests;

public sealed class ApplicationDiscoveryTests
{
    private const string Writer = @"C:\Apps\Writer\writer.exe";
    private const string Portable = @"D:\Portable\Writer\writer.exe";
    private const string Family = "Example.Tools_abcdefgh12345";

    private sealed class FixtureSource(params ApplicationObservation[] rows) : IApplicationDiscoverySource
    {
        public ApplicationObservation[] Rows { get; set; } = rows;

        public Task<DiscoveryBatch> DiscoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DiscoveryBatch(Rows, []));
        }
    }

    private sealed class UnavailableSource : IApplicationDiscoverySource
    {
        public Task<DiscoveryBatch> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromException<DiscoveryBatch>(new UnauthorizedAccessException());
    }

    [Theory]
    [InlineData(DiscoverySource.Desktop | DiscoverySource.Running, true)]
    [InlineData(DiscoverySource.Office | DiscoverySource.SystemRegistry64, true)]
    [InlineData(DiscoverySource.Running, false)]
    [InlineData(DiscoverySource.StartMenu | DiscoverySource.SystemRegistry64, false)]
    [InlineData(DiscoverySource.Appx, false)]
    public async Task ManagementListsUseDesktopAndOfficeSources(DiscoverySource source, bool visible)
    {
        var fixture = new FixtureSource(new ApplicationObservation("writer", "Writer", source, Writer));
        var catalog = await Discover(Create([fixture]));
        Assert.Equal(visible, GestureFlow.Desktop.MainWindow.IsListedApplication(Assert.Single(catalog.Applications)));
    }

    [Fact]
    public async Task AnUnavailableSourceDoesNotDiscardSuccessfulDiscovery()
    {
        var accessible = new FixtureSource(new ApplicationObservation("start:writer", "Writer", DiscoverySource.StartMenu, Writer));
        var catalog = await Discover(Create([new UnavailableSource(), accessible]));
        Assert.Single(catalog.Warnings);
        Assert.Equal("Writer", Assert.Single(catalog.Applications).DisplayName);
    }

    [Fact]
    public async Task StrongIdentityMergesAllMatchingSourcesAndKeepsTheIdWhenOneSourceDisappears()
    {
        var registry = new FixtureSource(
            new("uninstall:writer", "Writer 安装记录", DiscoverySource.SystemRegistry64,
                Icon: new(Writer), ExecutableHint: Writer, InstallDirectory: @"C:\Apps\Writer"),
            new("uninstall:writer", "Writer 安装记录", DiscoverySource.SystemRegistry32));
        var start = new FixtureSource(new ApplicationObservation("lnk:writer", "Writer", DiscoverySource.StartMenu, Writer));
        var desktop = new FixtureSource(new ApplicationObservation("desktop:writer", "桌面 Writer", DiscoverySource.Desktop, Writer));
        var processes = new FixtureSource(new ApplicationObservation("process:43", "WRITER", DiscoverySource.Running, @"c:\apps\writer\WRITER.EXE"));
        var service = Create([registry, start, desktop, processes]);

        var merged = Assert.Single((await Discover(service)).Applications);
        Assert.Equal(ApplicationIdentity.ForPath(Writer), merged.AppId);
        Assert.Equal("桌面 Writer", merged.DisplayName);
        Assert.Equal(IdentificationStatus.Matched, merged.IdentificationStatus);
        Assert.Equal(DiscoverySource.SystemRegistry64 | DiscoverySource.SystemRegistry32 |
            DiscoverySource.StartMenu | DiscoverySource.Desktop | DiscoverySource.Running, merged.DiscoverySource);

        desktop.Rows = [];
        var withoutShortcut = Assert.Single((await Discover(service)).Applications);
        Assert.Equal(merged.AppId, withoutShortcut.AppId);
        Assert.False(withoutShortcut.DiscoverySource.HasFlag(DiscoverySource.Desktop));
        Assert.Equal("Writer", withoutShortcut.DisplayName);

        start.Rows = [];
        var refreshed = Assert.Single((await Discover(service)).Applications);
        Assert.Equal(merged.AppId, refreshed.AppId);
        Assert.False(refreshed.DiscoverySource.HasFlag(DiscoverySource.StartMenu));
    }

    [Fact]
    public async Task AnIconOrAnExecutableNameAloneDoesNotIdentifyAnApplication()
    {
        var entries = new FixtureSource(
            new("uninstall:writer", "Writer", DiscoverySource.UserRegistry64,
                Icon: new(Writer), ExecutableHint: Writer, InstallDirectory: @"C:\Apps\Writer"),
            new("process:7", "Writer", DiscoverySource.Running, ExecutableName: "writer.exe"),
            new("start:missing", "Other app", DiscoverySource.StartMenu, @"relative.exe"));
        var catalog = await Discover(Create([entries]));
        Assert.Equal(3, catalog.Applications.Count);
        Assert.All(catalog.Applications, app =>
        {
            Assert.Equal(IdentificationStatus.Pending, app.IdentificationStatus);
            Assert.Null(app.ExecutablePath);
            Assert.False(app.GestureEnabled);
        });
    }

    [Fact]
    public async Task IdenticalNamesAndBasenamesAtDifferentPathsDoNotMerge()
    {
        var entries = new FixtureSource(new("a", "Writer", DiscoverySource.StartMenu, Writer),
            new("b", "Writer", DiscoverySource.StartMenu, Portable),
            new("c", "Writer", DiscoverySource.SystemRegistry64));
        var catalog = await Discover(Create([entries]));
        Assert.Equal(3, catalog.Applications.Count);
        Assert.Equal(3, catalog.Applications.Select(app => app.AppId).Distinct().Count());
    }

    [Fact]
    public async Task DistinctPackageAppsSharingAnExecutableRemainSeparate()
    {
        var aumid = Family + "!Editor";
        var entries = new FixtureSource(
            new("start:editor", "Editor", DiscoverySource.StartMenu, Aumid: aumid, PackageFamilyName: Family),
            new("appx:editor", "Editor", DiscoverySource.Appx, Writer, Aumid: aumid, PackageFamilyName: Family),
            new("appx:viewer", "Viewer", DiscoverySource.Appx, Writer, Aumid: Family + "!Viewer", PackageFamilyName: Family),
            new("run:writer", "Tools", DiscoverySource.Running, Writer));
        var catalog = await Discover(Create([entries]));
        Assert.Equal(3, catalog.Applications.Count);
        var editor = Assert.Single(catalog.Applications, app => app.AppId == ApplicationIdentity.ForAumid(aumid));
        Assert.Equal(DiscoverySource.StartMenu | DiscoverySource.Appx, editor.DiscoverySource);
        Assert.Equal(IdentificationStatus.Matched, editor.IdentificationStatus);
        Assert.Single(catalog.Applications, app => app.IdentificationStatus == IdentificationStatus.Pending);
        Assert.Null(ForegroundApplicationService.Match(Running(Writer), catalog.Applications));
        Assert.Same(editor, ForegroundApplicationService.Match(
            Running(null) with { Aumid = aumid, PackageFamilyName = Family, WindowKind = ForegroundWindowKind.PackagedApp },
            catalog.Applications));
        Assert.Null(ForegroundApplicationService.Match(Running(Writer) with { PackageFamilyName = Family }, catalog.Applications));
    }

    [Fact]
    public async Task RefreshDropsUninstalledAndMissingPortableAppsButRetainsAnUnresolvedInstallRecord()
    {
        var fileExists = true;
        var entries = new FixtureSource(new("uninstall:only", "Incomplete app", DiscoverySource.UserRegistry32),
            new("start:writer", "Writer", DiscoverySource.StartMenu, Writer));
        var service = Create([entries], _ => fileExists);
        var manual = new[] { new ManualApplication(Portable) };
        var initial = await Discover(service, manual);
        Assert.Equal(3, initial.Applications.Count);
        Assert.Contains(initial.Applications, app => app.DiscoverySource == DiscoverySource.Manual && app.ExecutablePath == Portable);

        entries.Rows = [entries.Rows[0]];
        fileExists = false;
        var refreshed = Assert.Single((await Discover(service, manual)).Applications);
        Assert.Equal(IdentificationStatus.Pending, refreshed.IdentificationStatus);
        Assert.Equal("Incomplete app", refreshed.DisplayName);
    }

    [Fact]
    public async Task ManuallyAssociatingAValidExeResolvesTheOriginalRecordAndUsesItsName()
    {
        var key = "uninstall:missing";
        var registry = new FixtureSource(new ApplicationObservation(key, "Installed label", DiscoverySource.UserRegistry64));
        var catalog = await Discover(Create([registry], _ => true), [new(Portable, ApplicationIdentity.ForRecord(key))]);
        var app = Assert.Single(catalog.Applications);
        Assert.Equal(ApplicationIdentity.ForPath(Portable), app.AppId);
        Assert.Equal("Installed label", app.DisplayName);
        Assert.Equal(DiscoverySource.Manual | DiscoverySource.UserRegistry64, app.DiscoverySource);
        Assert.Equal(IdentificationStatus.Matched, app.IdentificationStatus);
    }

    [Fact]
    public async Task ForegroundIdentityRequiresEvidenceBeyondTheProcessShortName()
    {
        var catalog = await Discover(Create([new FixtureSource(new ApplicationObservation("start:writer", "Writer", DiscoverySource.StartMenu, Writer))]));
        Assert.Null(ForegroundApplicationService.Match(Running(null), catalog.Applications));
        Assert.Null(ForegroundApplicationService.Match(Running(Portable), catalog.Applications));
        Assert.NotNull(ForegroundApplicationService.Match(Running(Writer), catalog.Applications));

        Assert.Equal(ForegroundWindowKind.ExplorerDesktop,
            ForegroundApplicationService.ClassifyWindow("explorer.exe", "WorkerW", false, null));
        Assert.Equal(ForegroundWindowKind.ExplorerFolder,
            ForegroundApplicationService.ClassifyWindow("explorer.exe", "CabinetWClass", false, null));
        Assert.Equal(ForegroundWindowKind.ApplicationHost,
            ForegroundApplicationService.ClassifyWindow("ApplicationFrameHost.exe", "ApplicationFrameWindow", true, null));
        Assert.Null(ForegroundApplicationService.Match(
            Running(Writer) with { WindowKind = ForegroundWindowKind.ApplicationHost }, catalog.Applications));
    }

    [Fact]
    public async Task RiskClassificationAloneNeverGrantsAuthorization()
    {
        var unknown = Writer;
        var high = Portable;
        const string compatible = @"C:\Apps\Validated\verified.exe";
        var entries = new FixtureSource(
            new("unknown", "Unknown", DiscoverySource.StartMenu, unknown),
            new("high", "High risk", DiscoverySource.StartMenu, high),
            new("verified", "Verified", DiscoverySource.StartMenu, compatible),
            new("pending", "Pending", DiscoverySource.UserRegistry32));
        var service = Create([entries], classifyRisk: (id, _, _) =>
            id == ApplicationIdentity.ForPath(high) ? RiskLevel.High : id == ApplicationIdentity.ForPath(compatible)
                || id == ApplicationIdentity.ForRecord("pending") ? RiskLevel.Compatible : RiskLevel.Unknown);
        var apps = (await Discover(service)).Applications;
        Assert.All(apps, app => Assert.False(app.GestureEnabled));
        Assert.Contains(apps, app => app.DisplayName == "Verified" && app.RiskLevel == RiskLevel.Compatible);
    }

    [Fact]
    public async Task SavedPoliciesAndManualEntriesReloadIndependentlyOfBindingsAndVirtualApps()
    {
        var temp = Path.Combine(Path.GetTempPath(), "GestureFlow-app-tests-" + Guid.NewGuid().ToString("N"));
        var store = new ApplicationStateStore(temp);
        try
        {
            var id = ApplicationIdentity.ForPath(Portable);
            var policy = new ApplicationOverride(true, RiskLevel.Unknown, GestureTrigger.ShiftRightButtonDrag, true, true);
            var risk = new AppRiskPolicyService();
            var target = new ProtectionTarget(id, Portable, risk.Assess("writer.exe"));
            var flow = new AuthorizationFlow(target);
            Assert.True(flow.AcknowledgeRisk());
            Assert.True(flow.Review(policy.Trigger));
            store.AddManualApplication(new(Portable));
            store.SaveConfirmedPolicy(target, flow.Confirm(target)!);
            var reopened = new ApplicationStateStore(temp);
            Assert.Equal(policy, reopened.LoadPolicies()[id]);
            var files = Directory.GetFiles(temp).Select(path => Path.GetFileName(path)!).Order().ToArray();
            Assert.Equal(["application-authorizations.dat", "application-policies.json", "manual-applications.json"], files);
            var authorizations = reopened.LoadAuthorizations();
            var discovery = new InstalledApplicationDiscoveryService([], _ => true, isAuthorized: app =>
                ApplicationGesturePolicy.VerifiedAuthorization(new(app.AppId, app.ExecutablePath, risk.Assess(app.ExecutableName)),
                    app.UserOverride, authorizations) is not null);
            var catalog = await discovery.DiscoverAsync(reopened.LoadManualApplications(), reopened.LoadPolicies());
            var actual = Assert.Single(catalog.Applications);
            Assert.True(actual.GestureEnabled);
            Assert.All(VirtualApplicationScenarios.All, scenario =>
            {
                Assert.StartsWith("virtual:", scenario.AppId);
                Assert.False(ApplicationIdentity.IsManageableId(scenario.AppId));
                Assert.Throws<JsonException>(() => store.SavePolicy(scenario.AppId, policy));
            });
            Assert.DoesNotContain(typeof(VirtualApplicationScenarios).Assembly.GetReferencedAssemblies(),
                assembly => assembly.Name == "GestureFlow.Desktop");

            var otherId = ApplicationIdentity.ForPath(Writer);
            reopened.SavePolicy(otherId, policy with { Allowed = false, HasUserAuthorization = false });
            Assert.Equal(2, store.LoadPolicies().Count);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void CanonicalPathsAreCaseInsensitiveAndRejectNonExecutableInputs()
    {
        Assert.Equal(ApplicationIdentity.ForPath(Writer), ApplicationIdentity.ForPath(@"c:\apps\Writer\..\Writer\WRITER.exe"));
        Assert.Null(ApplicationIdentity.NormalizeExecutable(@"Writer.exe"));
        Assert.Null(ApplicationIdentity.NormalizeExecutable(@"C:\Apps\Writer\writer.dll"));
        Assert.Null(ApplicationIdentity.NormalizeExecutable(@"C:\Apps\Writer\writer.exe:other.exe"));
    }

    private static InstalledApplicationDiscoveryService Create(IReadOnlyList<IApplicationDiscoverySource> sources,
        Func<string, bool>? exists = null, Func<string, string?, string?, RiskLevel>? classifyRisk = null) =>
        new(sources, exists ?? (_ => false), classifyRisk);

    private static Task<ApplicationCatalog> Discover(InstalledApplicationDiscoveryService service,
        IReadOnlyList<ManualApplication>? manual = null) =>
        service.DiscoverAsync(manual ?? [], new Dictionary<string, ApplicationOverride>());

    private static RunningApplication Running(string? path) =>
        new(0, 1, 1, "Writer", ForegroundWindowKind.DesktopProgram, "writer.exe", path, null, null);
}

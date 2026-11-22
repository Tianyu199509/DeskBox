using DeskBox.Services.DesktopOrganization;

namespace DeskBox.Services;

/// <summary>
/// Central zero-reflection composition root for all core DeskBox application-lifetime services.
/// Eliminates runtime <c>ServiceCollection</c> constructor reflection and allows Native AOT
/// to trim the entire <c>Microsoft.Extensions.DependencyInjection</c> graph while preserving
/// lazy singleton resolution and deterministic reverse-creation disposal order.
/// </summary>
public static class ServiceRegistry
{
    public static DeskBoxServiceContainer CreateContainer() => new();
}

/// <summary>
/// Strongly-typed, reflection-free application singleton container with thread-safe lazy
/// initialization and reverse-instantiation <see cref="IDisposable"/> teardown.
/// </summary>
public sealed class DeskBoxServiceContainer : IDisposable
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _disposables = new(14);
    private bool _disposed;

    private SettingsService? _settingsService;
    private DeskBoxDataBackupService? _dataBackupService;
    private ICredentialStore? _credentialStore;
    private CloudBackupService? _cloudBackupService;
    private DeskBoxAttachmentHealthService? _attachmentHealthService;
    private DeskBoxDiagnosticsBundleService? _diagnosticsBundleService;
    private LocalizationService? _localizationService;
    private ThemeService? _themeService;
    private FileService? _fileService;
    private ResizeGuideOverlayService? _resizeGuideOverlayService;
    private ManagedStorageDesktopShortcutService? _managedStorageDesktopShortcutService;
    private DesktopIconPositionService? _desktopIconPositionService;
    private OrganizerService? _organizerService;
    private QuickCaptureService? _quickCaptureService;
    private IAppUpdateService? _appUpdateService;

    public SettingsService SettingsService =>
        GetOrCreate(ref _settingsService, static _ => new SettingsService());

    public DeskBoxDataBackupService DataBackupService =>
        GetOrCreate(ref _dataBackupService, static _ => new DeskBoxDataBackupService());

    internal ICredentialStore CredentialStore =>
        GetOrCreate(ref _credentialStore, static _ => new PasswordVaultCredentialStore());

    internal CloudBackupService CloudBackupService =>
        GetOrCreate(
            ref _cloudBackupService,
            static c => new CloudBackupService(c.DataBackupService, c.SettingsService, c.CredentialStore));

    public DeskBoxAttachmentHealthService AttachmentHealthService =>
        GetOrCreate(ref _attachmentHealthService, static _ => new DeskBoxAttachmentHealthService());

    public DeskBoxDiagnosticsBundleService DiagnosticsBundleService =>
        GetOrCreate(ref _diagnosticsBundleService, static _ => new DeskBoxDiagnosticsBundleService());

    public LocalizationService LocalizationService =>
        GetOrCreate(ref _localizationService, static c => new LocalizationService(c.SettingsService));

    public ThemeService ThemeService =>
        GetOrCreate(ref _themeService, static c => new ThemeService(c.SettingsService));

    public FileService FileService =>
        GetOrCreate(ref _fileService, static c => new FileService(c.LocalizationService));

    public ResizeGuideOverlayService ResizeGuideOverlayService =>
        GetOrCreate(ref _resizeGuideOverlayService, static _ => new ResizeGuideOverlayService());

    public ManagedStorageDesktopShortcutService ManagedStorageDesktopShortcutService =>
        GetOrCreate(
            ref _managedStorageDesktopShortcutService,
            static c => new ManagedStorageDesktopShortcutService(c.SettingsService));

    public DesktopIconPositionService DesktopIconPositionService =>
        GetOrCreate(ref _desktopIconPositionService, static _ => new DesktopIconPositionService());

    public OrganizerService OrganizerService =>
        GetOrCreate(
            ref _organizerService,
            static c => new OrganizerService(
                c.SettingsService,
                c.FileService,
                desktopIconPositions: c.DesktopIconPositionService));

    public QuickCaptureService QuickCaptureService =>
        GetOrCreate(
            ref _quickCaptureService,
            static c => new QuickCaptureService(
                recycleAttachmentFileAsync: path =>
                    c.FileService.DeleteEntryAsync(path, recycle: true)));

    public IAppUpdateService AppUpdateService =>
        GetOrCreate(
            ref _appUpdateService,
            static _ => AppUpdateServiceFactory.Create(AppDistributionService.Current));

    private T GetOrCreate<T>(ref T? slot, Func<DeskBoxServiceContainer, T> factory)
        where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (slot is not null)
            {
                return slot;
            }

            T created = factory(this);
            slot = created;
            if (created is IDisposable disposable)
            {
                _disposables.Add(disposable);
            }

            return created;
        }
    }

    public void Dispose()
    {
        IDisposable[] toDispose;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDispose = _disposables.ToArray();
            _disposables.Clear();
        }

        List<Exception>? exceptions = null;
        for (int i = toDispose.Length - 1; i >= 0; i--)
        {
            try
            {
                toDispose[i].Dispose();
            }
            catch (Exception ex)
            {
                (exceptions ??= []).Add(ex);
            }
        }

        if (exceptions is { Count: > 0 })
        {
            throw new AggregateException(exceptions);
        }
    }
}

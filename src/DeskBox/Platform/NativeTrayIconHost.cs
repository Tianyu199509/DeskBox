// Copyright (c) DeskBox. All rights reserved.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeskBox.Platform;

internal enum TrayBalloonIconKind
{
    None = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

internal sealed record NativeTrayMenuEntry(
    int CommandId,
    string Text,
    string? Glyph,
    bool IsEnabled = true,
    bool IsSeparator = false,
    Func<Task>? Action = null)
{
    public static NativeTrayMenuEntry Separator { get; } =
        new(0, string.Empty, null, IsEnabled: false, IsSeparator: true, Action: null);

    public static NativeTrayMenuEntry Item(
        int commandId,
        string text,
        string? glyph,
        bool isEnabled,
        Func<Task> action) =>
        new(commandId, text, glyph, isEnabled, IsSeparator: false, action);
}

internal enum TrayMenuHorizontalAlignment
{
    Left = 0,
    Center = 1,
    Right = 2,
}

internal enum TrayMenuVerticalAlignment
{
    Top = 0,
    Center = 1,
    Bottom = 2,
}

/// <summary>
/// Pure Win32 notification-area host and native themed popup menu.
/// Replaces H.NotifyIcon.WinUI and eliminates both of its permanent hidden
/// WinUI 3 XAML windows (_trayWindow and SecondWindow) while preserving a
/// top-level message/owner HWND on the UI thread for hotkeys, display/session
/// broadcasts, picker ownership, and TrackPopupMenuEx.
/// </summary>
internal sealed class NativeTrayIconHost : IDisposable
{
    private const string WindowClassName = "DeskBoxTrayMessageWindow";
    private const uint TrayCallbackMessage = 0x8000 + 0x0042; // WM_APP + 0x42
    private const uint TrayIconNumericId = 1;

    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;

    private const uint WmNull = 0x0000;
    private const uint WmDestroy = 0x0002;
    private const uint WmContextMenu = 0x007B;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;

    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifGuid = 0x00000020;
    private const uint NifShowTip = 0x00000080;

    private const uint NiifNone = 0x00000000;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifWarning = 0x00000002;
    private const uint NiifError = 0x00000003;
    private const uint NiifNoSound = 0x00000010;
    private const uint NiifLargeIcon = 0x00000020;
    private const uint NiifRespectQuietTime = 0x00000080;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    private const uint MimStyle = 0x00000010;
    private const uint MnsCheckOrBmp = 0x04000000;

    private const uint MiimState = 0x00000001;
    private const uint MiimId = 0x00000002;
    private const uint MiimString = 0x00000040;
    private const uint MiimBitmap = 0x00000080;
    private const uint MiimFType = 0x00000100;

    private const uint MftSeparator = 0x00000800;
    private const uint MfsEnabled = 0x00000000;
    private const uint MfsDisabled = 0x00000003;

    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmCenterAlign = 0x0004;
    private const uint TpmRightAlign = 0x0008;
    private const uint TpmTopAlign = 0x0000;
    private const uint TpmVCenterAlign = 0x0010;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmVertical = 0x0040;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmLayoutRtl = 0x8000;

    private const int BiRgb = 0;
    private const int TransparentBkMode = 1;
    private const uint DtCenter = 0x00000001;
    private const uint DtVCenter = 0x00000004;
    private const uint DtSingleLine = 0x00000020;
    private const uint DtNoPrefix = 0x00000800;
    private const byte DefaultCharSet = 1;
    private const byte AntialiasedQuality = 4;
    private const int FwNormal = 400;
    private const uint GgiMarkNonexistingGlyphs = 0x0001;

    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const int UxthemeRefreshColorPolicyOrdinal = 104;
    private const int UxthemeAllowDarkModeForWindowOrdinal = 133;
    private const int UxthemeSetPreferredAppModeOrdinal = 135;
    private const int UxthemeFlushMenuThemesOrdinal = 136;
    private const int PreferredAppModeForceDark = 2;
    private const int PreferredAppModeForceLight = 3;

    private static readonly object s_classLock = new();
    private static bool s_windowClassRegistered;
    private static uint s_taskbarCreatedMessage;
    private static readonly ConcurrentDictionary<IntPtr, NativeTrayIconHost> s_hostsByWindow = new();
    private static int s_activeMenuDepth;

    private static readonly object s_uxthemeLock = new();
    private static bool s_uxthemeResolved;
    private static IntPtr s_fnRefreshColorPolicy;
    private static IntPtr s_fnAllowDarkModeForWindow;
    private static IntPtr s_fnSetPreferredAppMode;
    private static IntPtr s_fnFlushMenuThemes;
    private static int s_appliedMenuTheme = -1; // -1 unknown, 0 light, 1 dark

    private readonly Dictionary<GlyphBitmapKey, IntPtr> _glyphBitmapCache = new();
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private string? _iconPath;
    private uint _loadedIconDpi;
    private string _toolTipText = "DeskBox";
    private Guid _configuredGuid;
    private bool _usesGuidIdentity = true;
    private bool _isCreated;
    private bool _desiredVisible;
    private uint _notifyIconVersion;
    private bool _disposed;

    public NativeTrayIconHost(string windowTitle = "DeskBox Tray Host")
    {
        EnsureWindowClassRegistered();
        _configuredGuid = ComputeDeterministicIconGuid(Environment.ProcessPath);
        _windowHandle = CreateTopLevelHostWindow(windowTitle);
        if (_windowHandle != IntPtr.Zero)
        {
            s_hostsByWindow[_windowHandle] = this;
        }
    }

    public IntPtr WindowHandle => _windowHandle;

    public Guid IconId => _usesGuidIdentity ? _configuredGuid : Guid.Empty;

    public uint NumericId => TrayIconNumericId;

    public bool IsCreated => _isCreated;

    public bool DesiredVisible
    {
        get => _desiredVisible;
        set => _desiredVisible = value;
    }

    public event Action? LeftClickRequested;

    public event Action? RightClickRequested;

    internal static unsafe int SizeOfNotifyIconDataW => sizeof(NOTIFYICONDATAW);

    public static Guid ComputeDeterministicIconGuid(string? processPath)
    {
        string normalized = string.IsNullOrWhiteSpace(processPath)
            ? "DESKBOX-DEFAULT-TRAY"
            : Path.GetFullPath(processPath.Trim()).ToUpperInvariant();

        Span<byte> hash = stackalloc byte[32];
        byte[] input = Encoding.UTF8.GetBytes("DeskBox.TrayIcon.v1:" + normalized);
        SHA256.HashData(input, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        // RFC 4122 version 5 + variant 10xx bits (little-endian Data3 high nibble).
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    public static IntPtr CreateTransientOwnerWindow(string title = "DeskBox Picker Host")
    {
        EnsureWindowClassRegistered();
        return CreateTopLevelHostWindow(title);
    }

    public static void DestroyTransientOwnerWindow(ref IntPtr hwnd)
    {
        IntPtr target = Interlocked.Exchange(ref hwnd, IntPtr.Zero);
        if (target != IntPtr.Zero && IsWindow(target))
        {
            _ = DestroyWindow(target);
        }
    }

    public static void DismissActiveMenuIfOpen()
    {
        if (Volatile.Read(ref s_activeMenuDepth) > 0)
        {
            _ = EndMenu();
        }
    }

    public void SetIconPath(string? iconPath)
    {
        if (_disposed)
        {
            return;
        }

        uint dpi = GetEffectiveDpi(_windowHandle);
        if (string.Equals(_iconPath, iconPath, StringComparison.OrdinalIgnoreCase) &&
            _loadedIconDpi == dpi &&
            (_iconHandle != IntPtr.Zero || string.IsNullOrWhiteSpace(iconPath)))
        {
            return;
        }

        _iconPath = iconPath;
        ReloadIconHandle(dpi);

        if (_isCreated)
        {
            uint flags = NifIcon;
            if (_notifyIconVersion >= NotifyIconVersion4)
            {
                flags |= NifTip | NifShowTip;
            }

            _ = TryModifyNotifyIcon(flags);
        }
    }

    public void SetToolTipText(string? toolTipText)
    {
        if (_disposed)
        {
            return;
        }

        string normalized = TruncateUtf16Safe(toolTipText ?? string.Empty, 127);
        if (string.Equals(_toolTipText, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _toolTipText = normalized;
        if (_isCreated)
        {
            uint flags = _notifyIconVersion >= NotifyIconVersion4
                ? NifTip | NifShowTip
                : NifTip;
            _ = TryModifyNotifyIcon(flags);
        }
    }

    public unsafe bool TryCreateIcon()
    {
        if (_disposed || _windowHandle == IntPtr.Zero)
        {
            return false;
        }

        _desiredVisible = true;
        if (_isCreated)
        {
            return true;
        }

        if (_iconHandle == IntPtr.Zero && !string.IsNullOrWhiteSpace(_iconPath))
        {
            ReloadIconHandle(GetEffectiveDpi(_windowHandle));
        }

        if (_usesGuidIdentity)
        {
            if (TryAddNotifyIconCore(useGuid: true))
            {
                PromoteCreatedIconVersion();
                return true;
            }

            // Clean up any stale Explorer registration for this GUID and retry once.
            _ = TryDeleteNotifyIconCore(useGuid: true);
            if (TryAddNotifyIconCore(useGuid: true))
            {
                PromoteCreatedIconVersion();
                return true;
            }

            // If Explorer rejected the GUID registration (e.g. stale IconStreams
            // path mismatch), fall back to (hWnd, uID) identity so the tray icon
            // remains usable.
            _usesGuidIdentity = false;
        }

        if (TryAddNotifyIconCore(useGuid: false))
        {
            PromoteCreatedIconVersion();
            return true;
        }

        return false;
    }

    public bool TryRemoveIcon()
    {
        if (!_isCreated)
        {
            return true;
        }

        bool removed = TryDeleteNotifyIconCore(_usesGuidIdentity);
        _isCreated = false;
        _notifyIconVersion = 0;
        return removed;
    }

    public bool ShowNotification(
        string title,
        string message,
        TrayBalloonIconKind icon = TrayBalloonIconKind.None,
        IntPtr? customIconHandle = null,
        bool largeIcon = false,
        bool sound = true,
        bool respectQuietTime = true,
        bool realtime = false,
        TimeSpan? timeout = null)
    {
        _ = customIconHandle;
        _ = realtime;
        _ = timeout;
        return TryShowBalloonNotification(title, message, icon, largeIcon, sound, respectQuietTime);
    }

    public unsafe bool TryShowBalloonNotification(
        string title,
        string message,
        TrayBalloonIconKind iconKind,
        bool largeIcon = false,
        bool sound = false,
        bool respectQuietTime = true)
    {
        if (_disposed || !_isCreated || _windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NOTIFYICONDATAW data = default;
        PopulateIdentity(ref data, _usesGuidIdentity, NifInfo | NifShowTip);
        CopyFixedUtf16(data.szInfoTitle, 64, TruncateUtf16Safe(title ?? string.Empty, 63));
        CopyFixedUtf16(data.szInfo, 256, TruncateUtf16Safe(message ?? string.Empty, 255));

        uint infoFlags = iconKind switch
        {
            TrayBalloonIconKind.Info => NiifInfo,
            TrayBalloonIconKind.Warning => NiifWarning,
            TrayBalloonIconKind.Error => NiifError,
            _ => NiifNone,
        };
        if (!sound)
        {
            infoFlags |= NiifNoSound;
        }
        if (largeIcon)
        {
            infoFlags |= NiifLargeIcon;
        }
        if (respectQuietTime)
        {
            infoFlags |= NiifRespectQuietTime;
        }

        data.dwInfoFlags = infoFlags;
        return Shell_NotifyIconW(NimModify, &data) != 0;
    }

    public unsafe int ShowContextMenu(
        int screenX,
        int screenY,
        IReadOnlyList<NativeTrayMenuEntry> entries,
        bool isDarkTheme,
        bool isRightToLeft,
        TrayMenuHorizontalAlignment horizontalAlignment = TrayMenuHorizontalAlignment.Left,
        TrayMenuVerticalAlignment verticalAlignment = TrayMenuVerticalAlignment.Bottom)
    {
        if (_disposed || _windowHandle == IntPtr.Zero || entries.Count == 0)
        {
            return 0;
        }

        ApplyMenuTheme(_windowHandle, isDarkTheme);

        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            MENUINFO menuInfo = new()
            {
                cbSize = (uint)sizeof(MENUINFO),
                fMask = MimStyle,
                dwStyle = MnsCheckOrBmp,
            };
            _ = SetMenuInfo(hMenu, &menuInfo);

            uint dpi = GetEffectiveDpi(_windowHandle);
            uint position = 0;
            foreach (NativeTrayMenuEntry entry in entries)
            {
                if (entry.IsSeparator)
                {
                    MENUITEMINFOW sep = new()
                    {
                        cbSize = (uint)sizeof(MENUITEMINFOW),
                        fMask = MiimFType,
                        fType = MftSeparator,
                    };
                    _ = InsertMenuItemW(hMenu, position++, 1, &sep);
                    continue;
                }

                IntPtr glyphBitmap = IntPtr.Zero;
                if (!string.IsNullOrEmpty(entry.Glyph))
                {
                    glyphBitmap = GetOrCreateGlyphBitmap(
                        entry.Glyph[0],
                        dpi,
                        isDarkTheme,
                        entry.IsEnabled);
                }

                string itemText = entry.Text ?? string.Empty;
                fixed (char* textPtr = itemText)
                {
                    MENUITEMINFOW item = new()
                    {
                        cbSize = (uint)sizeof(MENUITEMINFOW),
                        fMask = MiimId | MiimState | MiimString | (glyphBitmap != IntPtr.Zero ? MiimBitmap : 0),
                        fState = entry.IsEnabled ? MfsEnabled : MfsDisabled,
                        wID = (uint)entry.CommandId,
                        dwTypeData = (IntPtr)textPtr,
                        cch = (uint)itemText.Length,
                        hbmpItem = glyphBitmap,
                    };
                    _ = InsertMenuItemW(hMenu, position++, 1, &item);
                }
            }

            uint flags = TpmReturnCmd | TpmRightButton | TpmVertical;
            flags |= horizontalAlignment switch
            {
                TrayMenuHorizontalAlignment.Center => TpmCenterAlign,
                TrayMenuHorizontalAlignment.Right => TpmRightAlign,
                _ => isRightToLeft ? TpmRightAlign : TpmLeftAlign,
            };
            flags |= verticalAlignment switch
            {
                TrayMenuVerticalAlignment.Top => TpmTopAlign,
                TrayMenuVerticalAlignment.Center => TpmVCenterAlign,
                _ => TpmBottomAlign,
            };
            if (isRightToLeft)
            {
                flags |= TpmLayoutRtl;
            }

            _ = Win32Helper.SetForegroundWindow(_windowHandle);
            Interlocked.Increment(ref s_activeMenuDepth);
            int selectedCommand;
            try
            {
                selectedCommand = TrackPopupMenuEx(
                    hMenu,
                    flags,
                    screenX,
                    screenY,
                    _windowHandle,
                    IntPtr.Zero);
            }
            finally
            {
                Interlocked.Decrement(ref s_activeMenuDepth);
                _ = PostMessageW(_windowHandle, WmNull, UIntPtr.Zero, IntPtr.Zero);
            }

            return selectedCommand;
        }
        finally
        {
            _ = DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _desiredVisible = false;
        DismissActiveMenuIfOpen();
        _ = TryRemoveIcon();

        if (_iconHandle != IntPtr.Zero)
        {
            _ = DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        ClearGlyphBitmapCache();

        IntPtr hwnd = Interlocked.Exchange(ref _windowHandle, IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
        {
            s_hostsByWindow.TryRemove(hwnd, out _);
            if (IsWindow(hwnd))
            {
                _ = DestroyWindow(hwnd);
            }
        }
    }

    internal static bool ProbeWin32EntryPoints()
    {
        _ = GetEffectiveDpi(IntPtr.Zero);
        ResolveUxthemeMenuApi();
        return s_fnSetPreferredAppMode != IntPtr.Zero;
    }

    internal static IntPtr CreateMenuGlyphBitmapForTesting(
        char glyph,
        uint dpi,
        bool isDarkTheme,
        bool isEnabled) =>
        RenderMenuGlyphBitmap(glyph, dpi == 0 ? 96u : dpi, isDarkTheme, isEnabled);

    internal static void DeleteBitmapForTesting(IntPtr hBitmap)
    {
        if (hBitmap != IntPtr.Zero)
        {
            _ = DeleteObject(hBitmap);
        }
    }

    private unsafe void PromoteCreatedIconVersion()
    {
        _isCreated = true;
        NOTIFYICONDATAW versionData = default;
        PopulateIdentity(ref versionData, _usesGuidIdentity, 0);
        versionData.uTimeoutOrVersion = NotifyIconVersion4;
        _notifyIconVersion = Shell_NotifyIconW(NimSetVersion, &versionData) != 0
            ? NotifyIconVersion4
            : 0u;
        if (_notifyIconVersion >= NotifyIconVersion4)
        {
            // NIF_SHOWTIP is valid only after NIM_SETVERSION promotes the icon
            // to NOTIFYICON_VERSION_4; re-apply tooltip visibility now.
            _ = TryModifyNotifyIcon(NifTip | NifShowTip);
        }
    }

    private unsafe bool TryAddNotifyIconCore(bool useGuid)
    {
        uint flags = NifMessage | NifTip;
        if (_iconHandle != IntPtr.Zero)
        {
            flags |= NifIcon;
        }

        NOTIFYICONDATAW data = default;
        PopulateIdentity(ref data, useGuid, flags);
        data.uCallbackMessage = TrayCallbackMessage;
        data.hIcon = _iconHandle;
        CopyFixedUtf16(data.szTip, 128, _toolTipText);
        return Shell_NotifyIconW(NimAdd, &data) != 0;
    }

    private unsafe bool TryModifyNotifyIcon(uint extraFlags)
    {
        if (!_isCreated || _windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NOTIFYICONDATAW data = default;
        PopulateIdentity(ref data, _usesGuidIdentity, extraFlags);
        data.uCallbackMessage = TrayCallbackMessage;
        data.hIcon = _iconHandle;
        CopyFixedUtf16(data.szTip, 128, _toolTipText);
        return Shell_NotifyIconW(NimModify, &data) != 0;
    }

    private unsafe bool TryDeleteNotifyIconCore(bool useGuid)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NOTIFYICONDATAW data = default;
        PopulateIdentity(ref data, useGuid, 0);
        return Shell_NotifyIconW(NimDelete, &data) != 0;
    }

    private unsafe void PopulateIdentity(ref NOTIFYICONDATAW data, bool useGuid, uint flags)
    {
        data.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        data.hWnd = _windowHandle;
        data.uID = TrayIconNumericId;
        if (useGuid && _configuredGuid != Guid.Empty)
        {
            data.uFlags = flags | NifGuid;
            data.guidItem = _configuredGuid;
        }
        else
        {
            data.uFlags = flags;
            data.guidItem = Guid.Empty;
        }
    }

    private void ReloadIconHandle(uint dpi)
    {
        IntPtr previous = _iconHandle;
        _iconHandle = IntPtr.Zero;
        _loadedIconDpi = dpi;

        if (!string.IsNullOrWhiteSpace(_iconPath) && File.Exists(_iconPath))
        {
            int cx = GetSmallIconMetric(SmCxSmIcon, dpi);
            int cy = GetSmallIconMetric(SmCySmIcon, dpi);
            _iconHandle = LoadImageW(
                IntPtr.Zero,
                _iconPath,
                ImageIcon,
                cx,
                cy,
                LrLoadFromFile);
        }

        if (previous != IntPtr.Zero)
        {
            _ = DestroyIcon(previous);
        }
    }

    private IntPtr GetOrCreateGlyphBitmap(char glyph, uint dpi, bool isDarkTheme, bool isEnabled)
    {
        var key = new GlyphBitmapKey(glyph, dpi, isDarkTheme, isEnabled);
        if (_glyphBitmapCache.TryGetValue(key, out IntPtr existing) && existing != IntPtr.Zero)
        {
            return existing;
        }

        if (_glyphBitmapCache.Count >= 96)
        {
            ClearGlyphBitmapCache();
        }

        IntPtr created = RenderMenuGlyphBitmap(glyph, dpi, isDarkTheme, isEnabled);
        if (created != IntPtr.Zero)
        {
            _glyphBitmapCache[key] = created;
        }

        return created;
    }

    private void ClearGlyphBitmapCache()
    {
        foreach (IntPtr hBitmap in _glyphBitmapCache.Values)
        {
            if (hBitmap != IntPtr.Zero)
            {
                _ = DeleteObject(hBitmap);
            }
        }

        _glyphBitmapCache.Clear();
    }

    private static unsafe IntPtr RenderMenuGlyphBitmap(
        char glyph,
        uint dpi,
        bool isDarkTheme,
        bool isEnabled)
    {
        int sizePx = Math.Max(16, MulDiv(16, (int)dpi, 96));
        int fontHeight = -Math.Max(12, MulDiv(12, (int)dpi, 96));

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr memDc = CreateCompatibleDC(screenDc);
        _ = ReleaseDC(IntPtr.Zero, screenDc);
        if (memDc == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            BITMAPINFOHEADER bmi = new()
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = sizePx,
                biHeight = -sizePx, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            };

            IntPtr bitsPtr;
            IntPtr hBitmap = CreateDIBSection(memDc, &bmi, 0, out bitsPtr, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero)
            {
                if (hBitmap != IntPtr.Zero)
                {
                    _ = DeleteObject(hBitmap);
                }

                return IntPtr.Zero;
            }

            int pixelCount = sizePx * sizePx;
            uint* pixels = (uint*)bitsPtr;
            for (int i = 0; i < pixelCount; i++)
            {
                pixels[i] = 0xFF000000u;
            }

            IntPtr oldBmp = SelectObject(memDc, hBitmap);
            IntPtr hFont = CreateIconFont(memDc, glyph, fontHeight);
            IntPtr oldFont = hFont != IntPtr.Zero ? SelectObject(memDc, hFont) : IntPtr.Zero;
            try
            {
                _ = SetBkMode(memDc, TransparentBkMode);
                _ = SetTextColor(memDc, 0x00FFFFFF);

                char glyphChar = glyph;
                RECT rect = new() { Left = 0, Top = 0, Right = sizePx, Bottom = sizePx };
                _ = DrawTextW(
                    memDc,
                    &glyphChar,
                    1,
                    &rect,
                    DtCenter | DtVCenter | DtSingleLine | DtNoPrefix);
                _ = GdiFlush();
            }
            finally
            {
                if (oldFont != IntPtr.Zero)
                {
                    _ = SelectObject(memDc, oldFont);
                }
                if (hFont != IntPtr.Zero)
                {
                    _ = DeleteObject(hFont);
                }
                if (oldBmp != IntPtr.Zero)
                {
                    _ = SelectObject(memDc, oldBmp);
                }
            }

            (byte fgR, byte fgG, byte fgB) = (isDarkTheme, isEnabled) switch
            {
                (true, true) => ((byte)245, (byte)245, (byte)245),
                (true, false) => ((byte)128, (byte)128, (byte)128),
                (false, true) => ((byte)28, (byte)28, (byte)28),
                (false, false) => ((byte)150, (byte)150, (byte)150),
            };

            for (int i = 0; i < pixelCount; i++)
            {
                uint raw = pixels[i];
                byte r = (byte)((raw >> 16) & 0xFF);
                byte g = (byte)((raw >> 8) & 0xFF);
                byte b = (byte)(raw & 0xFF);
                byte alpha = Math.Max(r, Math.Max(g, b));
                if (alpha == 0)
                {
                    pixels[i] = 0;
                    continue;
                }

                uint pr = (uint)((fgR * alpha + 127) / 255);
                uint pg = (uint)((fgG * alpha + 127) / 255);
                uint pb = (uint)((fgB * alpha + 127) / 255);
                pixels[i] = ((uint)alpha << 24) | (pr << 16) | (pg << 8) | pb;
            }

            return hBitmap;
        }
        finally
        {
            _ = DeleteDC(memDc);
        }
    }

    private static unsafe IntPtr CreateIconFont(IntPtr hdc, char glyph, int fontHeight)
    {
        IntPtr fluentFont = CreateFontByFace("Segoe Fluent Icons", fontHeight);
        if (fluentFont != IntPtr.Zero)
        {
            IntPtr prev = SelectObject(hdc, fluentFont);
            ushort glyphIndex = 0xFFFF;
            char ch = glyph;
            uint count = GetGlyphIndicesW(hdc, &ch, 1, &glyphIndex, GgiMarkNonexistingGlyphs);
            if (prev != IntPtr.Zero)
            {
                _ = SelectObject(hdc, prev);
            }

            if (count == 1 && glyphIndex != 0xFFFF && glyphIndex != 0)
            {
                return fluentFont;
            }

            _ = DeleteObject(fluentFont);
        }

        return CreateFontByFace("Segoe MDL2 Assets", fontHeight);
    }

    private static unsafe IntPtr CreateFontByFace(string faceName, int fontHeight)
    {
        LOGFONTW lf = new()
        {
            lfHeight = fontHeight,
            lfWeight = FwNormal,
            lfCharSet = DefaultCharSet,
            lfQuality = AntialiasedQuality,
        };
        CopyFixedUtf16(lf.lfFaceName, 32, faceName);
        return CreateFontIndirectW(&lf);
    }

    private static unsafe void EnsureWindowClassRegistered()
    {
        lock (s_classLock)
        {
            if (s_windowClassRegistered)
            {
                return;
            }

            s_taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
            IntPtr hInstance = GetModuleHandleW(null);
            fixed (char* classNamePtr = WindowClassName)
            {
                WNDCLASSEXW wc = new()
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr>)&TrayWindowProc,
                    hInstance = hInstance,
                    lpszClassName = (IntPtr)classNamePtr,
                };
                ushort atom = RegisterClassExW(&wc);
                // ERROR_CLASS_ALREADY_EXISTS (1410) is also acceptable.
                if (atom != 0 || Marshal.GetLastWin32Error() == 1410)
                {
                    s_windowClassRegistered = true;
                }
            }
        }
    }

    private static IntPtr CreateTopLevelHostWindow(string title)
    {
        IntPtr hInstance = GetModuleHandleW(null);
        return CreateWindowExW(
            WsExToolWindow,
            WindowClassName,
            title,
            WsPopup,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr TrayWindowProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (s_hostsByWindow.TryGetValue(hWnd, out NativeTrayIconHost? host))
        {
            try
            {
                if (msg == TrayCallbackMessage)
                {
                    host.OnTrayCallbackMessage(lParam);
                    return IntPtr.Zero;
                }

                if (s_taskbarCreatedMessage != 0 && msg == s_taskbarCreatedMessage)
                {
                    host.OnTaskbarCreated();
                    return IntPtr.Zero;
                }

                if (msg == WmDpiChanged)
                {
                    uint newDpi = (uint)((ulong)wParam & 0xFFFF);
                    host.OnDpiChanged(newDpi);
                    return IntPtr.Zero;
                }

                if (msg == WmDestroy)
                {
                    s_hostsByWindow.TryRemove(hWnd, out _);
                }
            }
            catch (Exception ex)
            {
                global::DeskBox.App.Log($"[Tray] NativeTrayIconHost WndProc error: {ex.Message}");
            }
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnTrayCallbackMessage(IntPtr lParam)
    {
        if (_disposed)
        {
            return;
        }

        uint eventCode = (uint)((ulong)lParam & 0xFFFF);
        bool isLeftActivate = _notifyIconVersion >= NotifyIconVersion4
            ? eventCode is NinSelect or NinKeySelect
            : eventCode is WmLButtonUp or NinSelect or NinKeySelect;
        bool isContextMenu = _notifyIconVersion >= NotifyIconVersion4
            ? eventCode == WmContextMenu
            : eventCode is WmContextMenu or WmRButtonUp;

        if (!isLeftActivate && !isContextMenu)
        {
            return;
        }

        CallbackDepth.Enter();
        try
        {
            if (isLeftActivate)
            {
                LeftClickRequested?.Invoke();
            }
            else
            {
                RightClickRequested?.Invoke();
            }
        }
        finally
        {
            CallbackDepth.Exit();
        }
    }

    private void OnTaskbarCreated()
    {
        if (_disposed)
        {
            return;
        }

        // Explorer restarted: the previous shell notification icon registration
        // died with the old taskbar. Only re-register when the user has not
        // hidden the tray icon.
        _isCreated = false;
        _notifyIconVersion = 0;
        if (!_desiredVisible)
        {
            return;
        }

        UiDispatch.PostAfterCallback(() =>
        {
            if (!_disposed && _desiredVisible && !_isCreated)
            {
                if (TryCreateIcon())
                {
                    global::DeskBox.App.Log("[Tray] Re-registered native tray icon after taskbar restart");
                }
            }
        });
    }

    private void OnDpiChanged(uint newDpi)
    {
        if (_disposed || newDpi == 0 || newDpi == _loadedIconDpi)
        {
            return;
        }

        UiDispatch.PostAfterCallback(() =>
        {
            if (_disposed || string.IsNullOrWhiteSpace(_iconPath))
            {
                return;
            }

            ReloadIconHandle(newDpi);
            if (_isCreated)
            {
                _ = TryModifyNotifyIcon(NifIcon | NifShowTip);
            }
        });
    }

    private static unsafe void ApplyMenuTheme(IntPtr ownerHwnd, bool isDarkTheme)
    {
        ResolveUxthemeMenuApi();
        int desired = isDarkTheme ? 1 : 0;
        if (s_fnSetPreferredAppMode != IntPtr.Zero &&
            Volatile.Read(ref s_appliedMenuTheme) != desired)
        {
            if (s_fnRefreshColorPolicy != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<void>)s_fnRefreshColorPolicy)();
            }

            int mode = isDarkTheme ? PreferredAppModeForceDark : PreferredAppModeForceLight;
            _ = ((delegate* unmanaged[Stdcall]<int, int>)s_fnSetPreferredAppMode)(mode);

            if (s_fnFlushMenuThemes != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<void>)s_fnFlushMenuThemes)();
            }

            Volatile.Write(ref s_appliedMenuTheme, desired);
        }

        if (ownerHwnd != IntPtr.Zero)
        {
            if (s_fnAllowDarkModeForWindow != IntPtr.Zero)
            {
                _ = ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)s_fnAllowDarkModeForWindow)(
                    ownerHwnd,
                    isDarkTheme ? 1 : 0);
            }

            _ = SetWindowTheme(
                ownerHwnd,
                isDarkTheme ? "DarkMode_Explorer" : "Explorer",
                null);
        }
    }

    private static void ResolveUxthemeMenuApi()
    {
        lock (s_uxthemeLock)
        {
            if (s_uxthemeResolved)
            {
                return;
            }

            s_uxthemeResolved = true;
            IntPtr module = LoadLibraryExW("uxtheme.dll", IntPtr.Zero, LoadLibrarySearchSystem32);
            if (module == IntPtr.Zero)
            {
                return;
            }

            s_fnRefreshColorPolicy = GetProcAddressOrdinal(module, (IntPtr)UxthemeRefreshColorPolicyOrdinal);
            s_fnAllowDarkModeForWindow = GetProcAddressOrdinal(module, (IntPtr)UxthemeAllowDarkModeForWindowOrdinal);
            s_fnSetPreferredAppMode = GetProcAddressOrdinal(module, (IntPtr)UxthemeSetPreferredAppModeOrdinal);
            s_fnFlushMenuThemes = GetProcAddressOrdinal(module, (IntPtr)UxthemeFlushMenuThemesOrdinal);
        }
    }

    private static uint GetEffectiveDpi(IntPtr hwnd)
    {
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                uint windowDpi = GetDpiForWindow(hwnd);
                if (windowDpi >= 96)
                {
                    return windowDpi;
                }
            }

            uint systemDpi = GetDpiForSystem();
            if (systemDpi >= 96)
            {
                return systemDpi;
            }
        }
        catch
        {
            // Fallback to 96 DPI on unexpected platform stubs.
        }

        return 96;
    }

    private static int GetSmallIconMetric(int metricIndex, uint dpi)
    {
        try
        {
            int scaled = GetSystemMetricsForDpi(metricIndex, dpi);
            if (scaled > 0)
            {
                return scaled;
            }
        }
        catch
        {
            // Fallback below.
        }

        int fallback = GetSystemMetrics(metricIndex);
        return fallback > 0 ? fallback : 16;
    }

    private static int MulDiv(int number, int numerator, int denominator) =>
        (int)(((long)number * numerator + (denominator / 2)) / denominator);

    private static string TruncateUtf16Safe(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value ?? string.Empty;
        }

        int length = maxChars;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return length <= 0 ? string.Empty : value[..length];
    }

    private static unsafe void CopyFixedUtf16(ushort* destination, int capacity, string value)
    {
        if (capacity <= 0)
        {
            return;
        }

        int copyLength = Math.Min(value.Length, capacity - 1);
        if (copyLength > 0 && char.IsHighSurrogate(value[copyLength - 1]))
        {
            copyLength--;
        }

        for (int i = 0; i < copyLength; i++)
        {
            destination[i] = value[i];
        }

        destination[copyLength] = 0;
    }

    private readonly record struct GlyphBitmapKey(
        char Glyph,
        uint Dpi,
        bool IsDarkTheme,
        bool IsEnabled);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed ushort szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed ushort szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed ushort szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint dwStyle;
        public uint cyMax;
        public IntPtr hbrBack;
        public uint dwContextHelpID;
        public UIntPtr dwMenuData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public UIntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        public fixed ushort lfFaceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddressOrdinal(IntPtr hModule, IntPtr lpProcName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "IsWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetDpiForSystem", ExactSpelling = true)]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", EntryPoint = "GetSystemMetricsForDpi", ExactSpelling = true)]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "DestroyMenu", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", EntryPoint = "SetMenuInfo", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool SetMenuInfo(IntPtr hMenu, MENUINFO* lpcmi);

    [DllImport("user32.dll", EntryPoint = "InsertMenuItemW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool InsertMenuItemW(IntPtr hMenu, uint item, int fByPosition, MENUITEMINFOW* lpmi);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx", ExactSpelling = true, SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        IntPtr hMenu,
        uint fuFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [DllImport("user32.dll", EntryPoint = "EndMenu", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndMenu();

    [DllImport("user32.dll", EntryPoint = "GetDC", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", EntryPoint = "DrawTextW", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe int DrawTextW(
        IntPtr hdc,
        char* lpchText,
        int cchText,
        RECT* lprc,
        uint format);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe int Shell_NotifyIconW(uint dwMessage, NOTIFYICONDATAW* lpData);

    [DllImport("uxtheme.dll", EntryPoint = "SetWindowTheme", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe IntPtr CreateDIBSection(
        IntPtr hdc,
        BITMAPINFOHEADER* pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontIndirectW", ExactSpelling = true)]
    private static extern unsafe IntPtr CreateFontIndirectW(LOGFONTW* lplf);

    [DllImport("gdi32.dll", EntryPoint = "SetBkMode", ExactSpelling = true)]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", EntryPoint = "SetTextColor", ExactSpelling = true)]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", EntryPoint = "GetGlyphIndicesW", ExactSpelling = true)]
    private static extern unsafe uint GetGlyphIndicesW(
        IntPtr hdc,
        char* lpstr,
        int c,
        ushort* pgi,
        uint fl);

    [DllImport("gdi32.dll", EntryPoint = "GdiFlush", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}

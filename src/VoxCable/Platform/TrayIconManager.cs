using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace VoxCable.Platform;

/// <summary>
/// Pure Win32 Shell_NotifyIconW System Tray manager (zero WinForms dependency or namespace clashes).
/// Shows the executable's embedded icon, updates status tooltip, restores on click, and hosts a styled WPF ContextMenu.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private const uint WmUser = 0x0400;
    private const uint WmTrayCallback = WmUser + 0x517;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint IconId = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIconW(IntPtr hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private HwndSource? _source;
    private ContextMenu? _contextMenu;
    private bool _added;
    private bool _disposed;

    public event Action? RestoreRequested;

    public void Attach(Window owner, ContextMenu contextMenu, string tooltip = "VoxCable — Virtual Audio FX Studio")
    {
        _hwnd = new WindowInteropHelper(owner).Handle;
        _contextMenu = contextMenu;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            ushort iconIdx = 0;
            _hIcon = ExtractAssociatedIconW(IntPtr.Zero, exePath, ref iconIdx);
        }

        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
            szTip = TruncateTip(tooltip)
        };

        _added = Shell_NotifyIconW(NimAdd, ref data);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added || _disposed || _hwnd == IntPtr.Zero) return;
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NifTip,
            szTip = TruncateTip(tooltip)
        };
        Shell_NotifyIconW(NimModify, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayCallback)
        {
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WmLButtonUp || mouseMsg == WmLButtonDblClk)
            {
                RestoreRequested?.Invoke();
                handled = true;
            }
            else if (mouseMsg == WmRButtonUp && _contextMenu != null)
            {
                _contextMenu.Placement = PlacementMode.MousePoint;
                _contextMenu.IsOpen = true;
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private static string TruncateTip(string tip) =>
        tip.Length > 120 ? tip[..120] : tip;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added && _hwnd != IntPtr.Zero)
        {
            var data = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = _hwnd,
                uID = IconId
            };
            Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }
}

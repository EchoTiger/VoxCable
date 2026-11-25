using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VoxCable.Platform;

public enum HotkeyAction
{
    ToggleMute = 1001,
    ToggleBypass = 1002,
    HoldCensorBleep = 1003,
    StopAllSfx = 1004,
    Pad1 = 1100,
    Pad2 = 1101,
    Pad3 = 1102,
    Pad4 = 1103,
    Pad5 = 1104,
    Pad6 = 1105,
    Pad7 = 1106,
    Pad8 = 1107
}

/// <summary>
/// Win32 global hotkey manager using RegisterHotKey (no intrusive low-level keyboard hook)
/// plus momentary key-state polling for Hold-to-Bleep.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkM = 0x4D;
    private const uint VkB = 0x42;
    private const uint VkX = 0x58;
    private const uint Vk0 = 0x30;
    private const uint Vk1 = 0x31;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private IntPtr _hwnd;
    private HwndSource? _source;
    private readonly List<int> _registeredIds = new();
    private bool _enabled;
    private bool _disposed;

    public int RegisteredCount => _registeredIds.Count;
    public event Action<HotkeyAction>? HotkeyPressed;

    public static bool IsBleepKeyCurrentlyDown => (GetAsyncKeyState((int)VkX) & 0x8000) != 0;

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;

        if (_hwnd == IntPtr.Zero) return;

        if (enabled)
            RegisterDefaults();
        else
            UnregisterAll();
    }

    private void RegisterDefaults()
    {
        UnregisterAll();
        uint mods = ModControl | ModAlt | ModNoRepeat;

        TryRegister((int)HotkeyAction.ToggleMute, mods, VkM);
        TryRegister((int)HotkeyAction.ToggleBypass, mods, VkB);
        TryRegister((int)HotkeyAction.HoldCensorBleep, mods, VkX);
        TryRegister((int)HotkeyAction.StopAllSfx, mods, Vk0);

        for (int i = 0; i < 8; i++)
        {
            TryRegister((int)HotkeyAction.Pad1 + i, mods, Vk1 + (uint)i);
        }
    }

    private void TryRegister(int id, uint mods, uint vk)
    {
        if (RegisterHotKey(_hwnd, id, mods, vk))
        {
            _registeredIds.Add(id);
        }
    }

    private void UnregisterAll()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (int id in _registeredIds)
            {
                UnregisterHotKey(_hwnd, id);
            }
        }
        _registeredIds.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (Enum.IsDefined(typeof(HotkeyAction), id))
            {
                HotkeyPressed?.Invoke((HotkeyAction)id);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}

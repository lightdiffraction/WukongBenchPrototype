using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using System.Threading.Tasks;

namespace WukongBench;

public static class UiAutomation
{
    const uint INPUT_MOUSE = 0;
    const uint INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const ushort VK_RETURN = 0x0D;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION U; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }


    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private static IntPtr FindVisibleWindow(int targetPid)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);

            if (pid != (uint)targetPid)
                return true;

            if (!IsWindowVisible(hWnd))
                return true;

            // Не берём окна без заголовка.
            if (GetWindowTextLength(hWnd) == 0)
                return true;

            found = hWnd;
            return false; // Нашли первое подходящее окно.
        }, IntPtr.Zero);

        return found;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }

    private static void DumpBenchmarkWindows()
    {
        Console.WriteLine("Видимые окна процессов Benchmark Tool:");

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);

            string processName;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                processName = p.ProcessName;
            }
            catch
            {
                processName = "?";
            }

            string title = GetWindowTitle(hWnd);

            if (processName.Contains("b1",
                    StringComparison.OrdinalIgnoreCase)
                || title.Contains("b1",
                    StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"PID={pid}, Process={processName}, " +
                    $"Title='{title}', HWND={hWnd}");
            }

            return true;
        }, IntPtr.Zero);
    }

    public static async Task WaitAndStartBenchmarkAsync(
        int launcherPid, AppConfig cfg)
    {
        Process? game = null;
        IntPtr hwnd = IntPtr.Zero;

        // Ищем процессы, пока у одного из них не появится видимое окно.
        for (int i = 0; i < 120; i++)
        {
            var processes = Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        return p.ProcessName.Equals(
                                   "b1-Win64-Shipping",
                                   StringComparison.OrdinalIgnoreCase)
                               || p.ProcessName.Equals(
                                   "b1-benchmark",
                                   StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();

            foreach (var process in processes)
            {
                hwnd = FindVisibleWindow(process.Id);

                if (hwnd != IntPtr.Zero)
                {
                    game = process;
                    break;
                }
            }

            if (hwnd != IntPtr.Zero)
                break;

            await Task.Delay(1000);
        }

        if (game == null || hwnd == IntPtr.Zero)
        {
            Console.WriteLine("Не удалось найти видимое окно.");
            DumpBenchmarkWindows();
            throw new InvalidOperationException(
                "Benchmark process found, but no visible top-level window was found.");
        }

        Console.WriteLine(
            $"Окно найдено: PID={game.Id}, " +
            $"Process={game.ProcessName}, " +
            $"Title='{GetWindowTitle(hwnd)}', HWND={hwnd}");

        SetForegroundWindow(hwnd);
        await Task.Delay(3000);

        // Поскольку GUI прогружается не сразу и обратной связи нет,
        // выполняем серию попыток (до 14) раз в 0.1 минуты, начиная сразу.
        const int maxAttempts = 14;
        var interval = TimeSpan.FromMinutes(0.1);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (game == null || game.HasExited)
                {
                    Console.WriteLine("Benchmark process exited before UI interaction.");
                    break;
                }

                Console.WriteLine($"UI attempt {attempt}/{maxAttempts}: sending wake key (Enter).");
                // Ставим окно на передний план и посылаем Enter, чтобы пробудить UI.
                SetForegroundWindow(hwnd);
                Key(0x0D);

                // Ждём, чтобы интерфейс отреагировал.
                await Task.Delay(5000);

                // Вычисляем координаты по формуле и кликаем на пиксель.
                int W = GetScreenWidth();
                int H = GetScreenHeight();
                double R = 16.0 / 9.0;
                double Wi = Math.Min(W, H * R);
                double Hi = Wi / R;
                double left = (W - Wi) / 2.0;
                double top = (H - Hi) / 2.0;
                double scale = Wi / 3120.0;

                int x1 = (int)Math.Round(left + 361 * scale);
                int y1 = (int)Math.Round(top + 794 * scale);
                int x2 = (int)Math.Round(left + 1243 * scale);
                int y2 = (int)Math.Round(top + 1040 * scale);

                Console.WriteLine($"UI attempt {attempt}/{maxAttempts}: clicking first button at ({x1},{y1}).");
                ClickAbsolute(x1, y1);

                await Task.Delay(2000);

                Console.WriteLine($"UI attempt {attempt}/{maxAttempts}: clicking second button at ({x2},{y2}).");
                ClickAbsolute(x2, y2);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UI attempt {attempt} failed: {ex.Message}");
            }

            if (attempt < maxAttempts)
            {
                Console.WriteLine($"Ожидание {interval.TotalMinutes} минуты перед следующей попыткой...");
                await Task.Delay(interval);
            }
        }

        Console.WriteLine("Отправлены команды запуска Benchmark.");
    }
    static void Key(ushort vk)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } }
        };
        var up = input;
        up.U.ki.dwFlags = 0x0002;
        SendInput(2, new[] { input, up }, Marshal.SizeOf<INPUT>());
    }

    static void ClickNormalized(IntPtr hwnd, double x, double y)
    {
        if (!GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("GetWindowRect failed.");

        int sx = Math.Clamp(r.Left + (int)((r.Right - r.Left) * x), r.Left, r.Right - 1);
        int sy = Math.Clamp(r.Top + (int)((r.Bottom - r.Top) * y), r.Top, r.Bottom - 1);

        int vx = (int)Math.Round(sx * 65535.0 / (GetScreenWidth() - 1));
        int vy = (int)Math.Round(sy * 65535.0 / (GetScreenHeight() - 1));

        var down = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION { mi = new MOUSEINPUT { dx = vx, dy = vy, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN } }
        };
        var up = down;
        up.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTUP;

        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    static void ClickAbsolute(int sx, int sy)
    {
        // sx, sy — экранные координаты в пикселях
        int vx = (int)Math.Round(sx * 65535.0 / (GetScreenWidth() - 1));
        int vy = (int)Math.Round(sy * 65535.0 / (GetScreenHeight() - 1));

        var down = new INPUT
        {
            type = INPUT_MOUSE,
            U = new INPUTUNION { mi = new MOUSEINPUT { dx = vx, dy = vy, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN } }
        };
        var up = down;
        up.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTUP;

        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    static int GetScreenWidth() => GetSystemMetrics(0);
    static int GetScreenHeight() => GetSystemMetrics(1);
}

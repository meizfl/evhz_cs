using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace InputPollRateMonitor
{
    public class Program
    {
        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        public static void Main(string[] args)
        {
            bool verbose = true;

            foreach (var arg in args)
            {
                switch (arg.ToLower())
                {
                    case "-n":
                    case "--nonverbose":
                        verbose = false;
                        break;
                    case "-h":
                    case "--help":
                        ShowHelp();
                        return;
                }
            }

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\nStopping...");
                _cancellationTokenSource.Cancel();
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                RunLinux(verbose);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                RunWindows(verbose);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Console.WriteLine("macOS support not yet implemented.");
                Console.WriteLine("Consider using third-party tools or contributing to this project.");
            }
            else
            {
                Console.WriteLine("Unsupported platform.");
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine("Input Device Polling Rate Monitor");
            Console.WriteLine("\nUsage: InputPollRateMonitor [options]");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  -n, --nonverbose    Disable verbose output");
            Console.WriteLine("  -h, --help          Show this help message");
            Console.WriteLine("\nPress CTRL-C to exit and see average polling rates.");
        }

        private static void RunLinux(bool verbose)
        {
            Console.WriteLine("Press CTRL-C to exit.\n");

            var devices = new List<LinuxInputDevice>();

            for (int i = 0; i < 32; i++)
            {
                string devicePath = $"/dev/input/event{i}";
                if (File.Exists(devicePath))
                {
                    try
                    {
                        var device = new LinuxInputDevice(devicePath, i, verbose);
                        devices.Add(device);

                        if (verbose)
                        {
                            Console.WriteLine($"event{i}: {device.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (verbose)
                        {
                            Console.WriteLine($"Could not open {devicePath}: {ex.Message}");
                        }
                    }
                }
            }

            if (devices.Count == 0)
            {
                Console.WriteLine("No input devices found.");
                return;
            }

            Console.WriteLine($"\nMonitoring {devices.Count} device(s). Move your mouse or press keys...\n");

            var tasks = devices.Select(device =>
            Task.Run(() => device.Monitor(_cancellationTokenSource.Token))
            ).ToList();

            _cancellationTokenSource.Token.WaitHandle.WaitOne();

            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(2));

            Console.WriteLine("\n=== Final Averages ===");
            foreach (var device in devices.Where(d => d.AverageHz > 0).OrderBy(d => d.DeviceId))
            {
                Console.WriteLine($"Average for {device.Name}: {device.AverageHz,5} Hz");
            }

            foreach (var device in devices)
            {
                device.Dispose();
            }
        }

        private static void RunWindows(bool verbose)
        {
            Console.WriteLine("Press CTRL-C to exit.\n");
            Console.WriteLine("Monitoring mouse movement. Please move your mouse...\n");

            // FIX: pass frequency into device so it's available before base ctor runs
            WindowsInputDevice.QueryPerformanceFrequency(out long frequency);
            var device = new WindowsInputDevice(verbose, frequency);

            var task = Task.Run(() => device.Monitor(_cancellationTokenSource.Token));

            _cancellationTokenSource.Token.WaitHandle.WaitOne();

            task.Wait(TimeSpan.FromSeconds(1));

            if (device.AverageHz > 0)
            {
                Console.WriteLine($"\n=== Final Average ===");
                Console.WriteLine($"Average polling rate: {device.AverageHz,5} Hz");
            }
            else
            {
                Console.WriteLine("\nNo mouse movement detected.");
            }

            device.Dispose();
        }
    }

    public abstract class InputDevice : IDisposable
    {
        protected const int HZ_LIST_SIZE = 64;
        protected readonly int[] _hzList = new int[HZ_LIST_SIZE];
        protected int _count = 0;
        protected int _averageHz = 0;

        // FIX: _prevTime starts at 0; lazy-initialized on first event to avoid
        // calling the abstract GetCurrentMicroseconds() from the base constructor
        // before derived fields (e.g. _frequency) are set.
        protected long _prevTime = 0;

        protected readonly bool _verbose;
        protected readonly object _lock = new object();

        public string Name { get; protected set; }
        public int DeviceId { get; protected set; }
        public int AverageHz
        {
            get
            {
                lock (_lock)
                {
                    return _averageHz;
                }
            }
        }

        protected InputDevice(bool verbose)
        {
            _verbose = verbose;
            Name = "Unknown Device";
            // FIX: do NOT call GetCurrentMicroseconds() here — derived class fields
            // are not yet initialized at this point, which caused DivideByZeroException
            // in WindowsInputDevice when _frequency was still 0.
        }

        public abstract void Monitor(CancellationToken cancellationToken);
        public abstract void Dispose();

        protected void UpdatePollingRate(long currentTimeMicros)
        {
            lock (_lock)
            {
                // FIX: first call just records the baseline timestamp and returns
                if (_prevTime == 0)
                {
                    _prevTime = currentTimeMicros;
                    return;
                }

                long timeDiff = currentTimeMicros - _prevTime;

                if (timeDiff <= 0 || timeDiff > 10_000_000) // ignore gaps > 10 s or invalid
                {
                    _prevTime = currentTimeMicros;
                    return;
                }

                int hz = (int)(1_000_000L / timeDiff);

                if (hz > 0 && hz < 10000) // sanity check
                {
                    _count++;
                    _hzList[_count & (HZ_LIST_SIZE - 1)] = hz;

                    int maxAvg = Math.Min(_count, HZ_LIST_SIZE);
                    int sum = 0;

                    for (int i = 0; i < maxAvg; i++)
                    {
                        sum += _hzList[i];
                    }

                    _averageHz = sum / maxAvg;

                    if (_verbose)
                    {
                        Console.WriteLine($"{Name}: Latest {hz,5} Hz, Average {_averageHz,5} Hz");
                    }
                }

                _prevTime = currentTimeMicros;
            }
        }
    }

    // Linux implementation using /dev/input/eventX
    public class LinuxInputDevice : InputDevice
    {
        private FileStream _fileStream;
        private readonly string _devicePath;

        public LinuxInputDevice(string devicePath, int deviceId, bool verbose) : base(verbose)
        {
            _devicePath = devicePath;
            DeviceId = deviceId;

            _fileStream = new FileStream(devicePath, FileMode.Open, FileAccess.Read,
                                         FileShare.ReadWrite, 4096, FileOptions.Asynchronous);

            try
            {
                Name = GetDeviceName(_fileStream.SafeFileHandle.DangerousGetHandle());
                if (string.IsNullOrEmpty(Name))
                    Name = $"event{deviceId}";
            }
            catch
            {
                Name = $"event{deviceId}";
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int ioctl(IntPtr fd, uint request, byte[] buffer);

        private const uint EVIOCGNAME = 0x80FF4506;

        private string GetDeviceName(IntPtr fd)
        {
            byte[] buffer = new byte[256];
            int result = ioctl(fd, EVIOCGNAME, buffer);

            if (result > 0)
                return System.Text.Encoding.UTF8.GetString(buffer, 0, result).TrimEnd('\0');

            return null;
        }

        public override void Monitor(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[24]; // sizeof(struct input_event)

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var readTask = _fileStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    readTask.Wait(cancellationToken);
                    int bytesRead = readTask.Result;

                    if (bytesRead == buffer.Length)
                    {
                        long tv_sec  = BitConverter.ToInt64(buffer, 0);
                        long tv_usec = BitConverter.ToInt64(buffer, 8);
                        ushort type  = BitConverter.ToUInt16(buffer, 16);

                        // EV_REL = 2 (relative), EV_ABS = 3 (absolute)
                        if (type == 2 || type == 3)
                        {
                            long timeMicros = tv_sec * 1_000_000L + tv_usec;
                            UpdatePollingRate(timeMicros);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    break;
                }
            }
        }

        public override void Dispose()
        {
            _fileStream?.Dispose();
            _fileStream = null;
        }
    }

    // Windows implementation using GetCursorPos + QueryPerformanceCounter
    public class WindowsInputDevice : InputDevice
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("kernel32.dll")]
        public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

        // FIX: made public so Program.RunWindows can call it before constructing this object
        [DllImport("kernel32.dll")]
        public static extern bool QueryPerformanceFrequency(out long lpFrequency);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private readonly long _frequency;

        // FIX: frequency is passed in from outside so it's set before the base ctor
        // calls any virtual/abstract methods that depend on it.
        public WindowsInputDevice(bool verbose, long frequency) : base(verbose)
        {
            Name = "Mouse";
            DeviceId = 0;
            _frequency = frequency;
        }

        private long GetCurrentMicroseconds()
        {
            QueryPerformanceCounter(out long counter);
            return (counter * 1_000_000L) / _frequency;
        }

        public override void Monitor(CancellationToken cancellationToken)
        {
            if (!GetCursorPos(out POINT prevPos))
            {
                Console.WriteLine("Failed to get cursor position");
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if (GetCursorPos(out POINT currentPos))
                {
                    if (prevPos.X != currentPos.X || prevPos.Y != currentPos.Y)
                    {
                        // FIX: use the helper method instead of inline math
                        long timeMicros = GetCurrentMicroseconds();
                        UpdatePollingRate(timeMicros);
                        prevPos = currentPos;
                    }
                }

                Thread.Sleep(1);
            }
        }

        public override void Dispose()
        {
            // No unmanaged resources
        }
    }
}

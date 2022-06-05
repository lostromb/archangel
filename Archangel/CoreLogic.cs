using Durandal.Common.File;
using Durandal.Common.Logger;
using Durandal.Common.Tasks;
using Durandal.Common.Time;
using Durandal.Common.Utils.Json;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Archangel
{
    public class CoreLogic
    {
        private const int TIME_RESETS_AT_X_AM = 5;
        private static readonly TimeSpan DEFAULT_ALLOTMENT_TIME = TimeSpan.FromHours(2);
        private static readonly TimeSpan MINIMUM_TIME_BETWEEN_READOUTS = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan COMPUTER_SLEEP_MINIMUM_TIME = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan THREAD_INTERVAL_TIME = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan[] READOUT_TIMES = new TimeSpan[]
        {
            TimeSpan.FromHours(5),
            TimeSpan.FromHours(4.5),
            TimeSpan.FromHours(4),
            TimeSpan.FromHours(3.5),
            TimeSpan.FromHours(3),
            TimeSpan.FromHours(2.5),
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(1.5),
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(45),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(1),
        };

        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly VirtualPath _stateFile;
        private readonly Func<TimeSpan, Task> _notifyTimeDelegate;
        private readonly Func<Task> _restrictDelegate;
        private CancellationTokenSource _cancelizer;
        private MonitorState _lastMonitorState = null;
        private Task _backgroundTask = null;

        public CoreLogic(
            ILogger logger,
            IFileSystem fileSystem,
            VirtualPath stateFile,
            Func<TimeSpan, Task> notifyRemainingTimeDelegate,
            Func<Task> restrictDelegate)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _stateFile = stateFile;
            _notifyTimeDelegate = notifyRemainingTimeDelegate;
            _restrictDelegate = restrictDelegate;
        }

        public MonitorState CurrentMonitorState
        {
            get
            {
                return _lastMonitorState;
            }
        }

        public void Start(IRealTimeProvider realTime)
        {
            if (_backgroundTask != null)
            {
                throw new InvalidOperationException();
            }

            _cancelizer = new CancellationTokenSource();
            IRealTimeProvider backgroundThreadTime = realTime.Fork();
            _backgroundTask = DurandalTaskExtensions.LongRunningTaskFactory.StartNew(async () =>
            {
                try
                {
                    await RunBackgroundLoop(backgroundThreadTime, _cancelizer.Token);
                }
                finally
                {
                    _backgroundTask = null;
                }
            });
        }

        public async Task Stop()
        {
            if (_backgroundTask == null)
            {
                throw new InvalidOperationException();
            }

            _cancelizer.Cancel();
            await _backgroundTask;
        }

        private async Task<MonitorState> ReadFile()
        {
            if (await _fileSystem.ExistsAsync(_stateFile))
            {
                using (Stream fileIn = await _fileSystem.OpenStreamAsync(_stateFile, FileOpenMode.Open, FileAccessMode.Read))
                using (MemoryStream bucket = new MemoryStream())
                {
                    await fileIn.CopyToAsync(bucket);
                    if (bucket.Length > 0)
                    {
                        string json = Encoding.UTF8.GetString(bucket.ToArray());
                        return JsonConvert.DeserializeObject<MonitorState>(json);
                    }

                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        private async Task WriteFile(MonitorState state)
        {
            using (Stream fileOut = await _fileSystem.OpenStreamAsync(_stateFile, FileOpenMode.Create, FileAccessMode.Write))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state));
                await fileOut.WriteAsync(bytes, 0, bytes.Length);
                await fileOut.FlushAsync();
            }
        }

        private async Task RunBackgroundLoop(
            IRealTimeProvider realTime,
            CancellationToken cancelizer)
        {
            try
            {
                while (!cancelizer.IsCancellationRequested)
                {
                    try
                    {
                        DateTimeOffset currentLocalTime = realTime.Time.ToLocalTime();
                        MonitorState currentMonitorState = await ReadFile();
                        if (currentMonitorState == null)
                        {
                            currentMonitorState = new MonitorState()
                            {
                                TimeAllotmentPerDay = DEFAULT_ALLOTMENT_TIME,
                                TimeRemainingToday = DEFAULT_ALLOTMENT_TIME,
                                LastUpdateTime = currentLocalTime,
                                Enabled = true
                            };
                        }

                        _lastMonitorState = currentMonitorState;

                        if (currentMonitorState.Enabled)
                        {
                            // Has the day changed since the last update?
                            // don't reset at midnight; reset at 5 AM
                            if (currentLocalTime.Date != currentMonitorState.LastUpdateTime.Date)
                            {
                                // Reset the time allocation for the day
                                _logger.Log("Detected day change. Resetting time allotment to " + currentMonitorState.TimeAllotmentPerDay.ToString());
                                currentMonitorState.TimeRemainingToday = currentMonitorState.TimeAllotmentPerDay;
                            }
                            else if (currentLocalTime.Date == currentMonitorState.LastUpdateTime.Date &&
                                currentLocalTime.Hour >= TIME_RESETS_AT_X_AM &&
                                currentMonitorState.LastUpdateTime.Hour < TIME_RESETS_AT_X_AM)
                            {
                                _logger.Log("Detected 5 AM change. Resetting time allotment to " + currentMonitorState.TimeAllotmentPerDay.ToString());
                                currentMonitorState.TimeRemainingToday = currentMonitorState.TimeAllotmentPerDay;
                            }

                            // Do we need to restrict immediately?
                            if (currentMonitorState.TimeRemainingToday <= TimeSpan.Zero)
                            {
                                _logger.Log("Time has expired. Enforcing restrictions (local time " + currentLocalTime.ToString() + ")");
                                currentMonitorState.LastUpdateTime = currentLocalTime;
                                await WriteFile(currentMonitorState);
                                await _restrictDelegate();
                            }
                            else
                            {
                                TimeSpan timeSinceLastUpdate = currentLocalTime - currentMonitorState.LastUpdateTime;

                                // Have we been asleep for a while?
                                if (timeSinceLastUpdate > COMPUTER_SLEEP_MINIMUM_TIME)
                                {
                                    // If so, read out the time again
                                    _logger.Log("Detected machine sleep of " + timeSinceLastUpdate);
                                    _logger.Log("Triggering readout of " + currentMonitorState.TimeRemainingToday);
                                    await _notifyTimeDelegate(currentMonitorState.TimeRemainingToday);
                                    currentMonitorState.LastReadoutTime = currentLocalTime;

                                    // And write back our state file. Make sure we don't subtract any time from the allotment in this case.
                                    currentMonitorState.LastUpdateTime = currentLocalTime;
                                    await WriteFile(currentMonitorState);
                                }
                                else
                                {
                                    // Is it about time to do a readout?
                                    TimeSpan? timeSinceLastReadout = null;
                                    if (currentMonitorState.LastReadoutTime.HasValue)
                                    {
                                        timeSinceLastReadout = currentLocalTime - currentMonitorState.LastReadoutTime.Value;
                                    }

                                    double secondsToNearestReadout = double.MaxValue;
                                    foreach (TimeSpan readoutTime in READOUT_TIMES)
                                    {
                                        secondsToNearestReadout = Math.Min(secondsToNearestReadout, Math.Abs(currentMonitorState.TimeRemainingToday.TotalSeconds - readoutTime.TotalSeconds));
                                    }

                                    //_logger.Log("SecondsToNearestReadout " + secondsToNearestReadout);
                                    if (secondsToNearestReadout < 30 &&
                                        (timeSinceLastReadout == null || timeSinceLastReadout.Value > MINIMUM_TIME_BETWEEN_READOUTS))
                                    {
                                        _logger.Log("Triggering readout of " + currentMonitorState.TimeRemainingToday);
                                        await _notifyTimeDelegate(currentMonitorState.TimeRemainingToday);
                                        currentMonitorState.LastReadoutTime = currentLocalTime;
                                    }

                                    // Now write back our state file
                                    currentLocalTime = realTime.Time.ToLocalTime();
                                    currentMonitorState.TimeRemainingToday -= (currentLocalTime - currentMonitorState.LastUpdateTime);
                                    if (currentMonitorState.TimeRemainingToday < TimeSpan.Zero)
                                    {
                                        currentMonitorState.TimeRemainingToday = TimeSpan.Zero;
                                    }

                                    currentMonitorState.LastUpdateTime = currentLocalTime;
                                    await WriteFile(currentMonitorState);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Log(e, LogLevel.Err);
                    }

                    // And sleep for a while
                    await realTime.WaitAsync(THREAD_INTERVAL_TIME, cancelizer);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception e)
            {
                _logger.Log(e, LogLevel.Err);
            }
            finally
            {
                realTime.Merge();
            }
        }
    }
}

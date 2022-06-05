using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Durandal.Common.Logger;
using Durandal.Common.File;
using System.Threading;
using Durandal.Common.Tasks;
using Durandal.Common.Time;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Archangel.UnitTests
{
    [TestClass]
    public class CoreLogicTests
    {
        private static readonly VirtualPath FAKE_STATE_FILE = new VirtualPath("state.json");

        [TestMethod]
        public async Task TestFreshStartup()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });

            logic.Start(lockStepTime);
            try
            {
                lockStepTime.Step(TimeSpan.FromSeconds(30), 5000);
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsTrue(lastReadoutTime.HasValue);
                Assert.AreEqual(120, lastReadoutTime.Value.TotalMinutes, 0.5);
            }
            finally
            {
                await logic.Stop();
            }
        }

        [TestMethod]
        public async Task TestReadoutsNearExpiry()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });

            await SetState(fileSystem, new MonitorState()
            {
                LastReadoutTime = null,
                LastUpdateTime = lockStepTime.Time.ToLocalTime(),
                TimeAllotmentPerDay = TimeSpan.FromHours(2),
                TimeRemainingToday = TimeSpan.FromMinutes(15),
                Enabled = true
            });

            logic.Start(lockStepTime);
            try
            {
                // 15 minutes
                lockStepTime.Step(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsTrue(lastReadoutTime.HasValue);
                Assert.AreEqual(15, lastReadoutTime.Value.TotalMinutes, 0.5);

                // 14 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                 
                // 13 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                
                // 12 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 11 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 10 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(2, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 9 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(2, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 8 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(2, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 7 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(2, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 6 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(2, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 5 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(3, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 4 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(3, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 3 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(3, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 2 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(3, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 1 minute
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(4, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);

                // 0 minutes
                lockStepTime.Step(TimeSpan.FromMinutes(1), 10000);
                Assert.AreEqual(4, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);


                lockStepTime.Step(TimeSpan.FromSeconds(10), 1000);
                Assert.AreEqual(4, readoutTriggers);
                Assert.AreEqual(1, restrictTriggers);
            }
            finally
            {
                await logic.Stop();
            }
        }

        [TestMethod]
        public async Task TestReadoutAfterComputerLongSuspend()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });

            logic.Start(lockStepTime);
            try
            {
                lockStepTime.Step(TimeSpan.FromSeconds(30), 5000);
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsTrue(lastReadoutTime.HasValue);
                Assert.AreEqual(120, lastReadoutTime.Value.TotalMinutes, 0.5);
            }
            finally
            {
                await logic.Stop();
            }

            // Simulate putting the computer to sleep for 17 minutes
            lockStepTime.Step(TimeSpan.FromMinutes(17));

            // Then restart the process
            logic.Start(lockStepTime);
            try
            {
                lockStepTime.Step(TimeSpan.FromSeconds(30), 5000);
                Assert.AreEqual(2, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsTrue(lastReadoutTime.HasValue);
                Assert.AreEqual(119.5, lastReadoutTime.Value.TotalMinutes, 0.5);
            }
            finally
            {
                await logic.Stop();
            }
        }

        [TestMethod]
        public async Task TestNoReadoutAfterComputerShortSuspend()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });

            logic.Start(lockStepTime);
            try
            {
                lockStepTime.Step(TimeSpan.FromSeconds(30), 5000);
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsTrue(lastReadoutTime.HasValue);
                Assert.AreEqual(120, lastReadoutTime.Value.TotalMinutes, 0.5);
            }
            finally
            {
                await logic.Stop();
            }

            // Simulate putting the computer to sleep for 2 minutes
            lockStepTime.Step(TimeSpan.FromMinutes(2));

            // Then restart the process
            logic.Start(lockStepTime);
            try
            {
                lockStepTime.Step(TimeSpan.FromSeconds(30), 5000);
                Assert.AreEqual(1, readoutTriggers); // should not have triggered a new readout
                Assert.AreEqual(0, restrictTriggers);
            }
            finally
            {
                await logic.Stop();
            }
        }

        [TestMethod]
        public async Task TestTimeResetAfter5AMActive()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });

            // Fast-forward the lock step time to 4:55 AM
            SetLockStepTimeTo(lockStepTime, 4, 55);

            await SetState(fileSystem, new MonitorState()
            {
                LastReadoutTime = lockStepTime.Time.ToLocalTime(),
                LastUpdateTime = lockStepTime.Time.ToLocalTime(),
                TimeAllotmentPerDay = TimeSpan.FromHours(2),
                TimeRemainingToday = TimeSpan.Zero,
                Enabled = true
            });

            logic.Start(lockStepTime);
            try
            {
                // Assert that we are in restrict mode
                lockStepTime.Step(TimeSpan.FromMinutes(1), 5000);
                Assert.AreNotEqual(0, restrictTriggers);
                lockStepTime.Step(TimeSpan.FromMinutes(4), 5000);

                // Time should reset now
                restrictTriggers = 0;
                lockStepTime.Step(TimeSpan.FromMinutes(1), 5000);

                // Should have triggered a new readout of 2 hours remaining
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsTrue(lastReadoutTime.HasValue);
                Assert.AreEqual(120, lastReadoutTime.Value.TotalMinutes, 0.5);
            }
            finally
            {
                await logic.Stop();
            }
        }

        [TestMethod]
        public async Task TestTimeResetAfter5AMPassive()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });

            // Fast-forward the lock step time to 4:55 AM
            SetLockStepTimeTo(lockStepTime, 4, 55);

            await SetState(fileSystem, new MonitorState()
            {
                LastReadoutTime = lockStepTime.Time.ToLocalTime(),
                LastUpdateTime = lockStepTime.Time.ToLocalTime(),
                TimeAllotmentPerDay = TimeSpan.FromHours(2),
                TimeRemainingToday = TimeSpan.Zero,
                Enabled = true
            });

            logic.Start(lockStepTime);
            try
            {
                // Assert that we are in restrict mode
                lockStepTime.Step(TimeSpan.FromMinutes(1), 5000);
                Assert.AreNotEqual(0, restrictTriggers);
            }
            finally
            {
                await logic.Stop();
            }

            // Put the machine to sleep for 8 hours
            lockStepTime.Step(TimeSpan.FromHours(8));
            restrictTriggers = 0;

            logic.Start(lockStepTime);
            try
            {
                lockStepTime.Step(TimeSpan.FromSeconds(5), 1000);
                // Should have triggered a new readout of 2 hours remaining
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsTrue(lastReadoutTime.HasValue);
                Assert.AreEqual(120, lastReadoutTime.Value.TotalMinutes, 0.5);

                // Also assert that no restrict events happen in the next 30 seconds
                lockStepTime.Step(TimeSpan.FromSeconds(30), 1000);
                Assert.AreEqual(0, restrictTriggers);
            }
            finally
            {
                await logic.Stop();
            }
        }

        [TestMethod]
        public async Task TestTimeResetAfter23Hours()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });

            // Fast-forward the lock step time to 9:00 PM
            SetLockStepTimeTo(lockStepTime, 21, 00);

            await SetState(fileSystem, new MonitorState()
            {
                LastReadoutTime = lockStepTime.Time.ToLocalTime(),
                LastUpdateTime = lockStepTime.Time.ToLocalTime(),
                TimeAllotmentPerDay = TimeSpan.FromHours(2),
                TimeRemainingToday = TimeSpan.Zero,
                Enabled = true
            });

            logic.Start(lockStepTime);
            try
            {
                // Assert that we are in restrict mode
                lockStepTime.Step(TimeSpan.FromMinutes(1), 5000);
                Assert.AreNotEqual(0, restrictTriggers);
            }
            finally
            {
                await logic.Stop();
            }

            // Put the machine to sleep for 23 hours
            lockStepTime.Step(TimeSpan.FromHours(23));
            restrictTriggers = 0;

            logic.Start(lockStepTime);
            try
            {
                lockStepTime.Step(TimeSpan.FromSeconds(5), 1000);
                // Should have triggered a new readout of 2 hours remaining
                Assert.AreEqual(1, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsTrue(lastReadoutTime.HasValue);
                Assert.AreEqual(120, lastReadoutTime.Value.TotalMinutes, 0.5);
            }
            finally
            {
                await logic.Stop();
            }
        }

        [TestMethod]
        public async Task TestMonitorNotEnabled()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });

            await SetState(fileSystem, new MonitorState()
            {
                LastReadoutTime = null,
                LastUpdateTime = lockStepTime.Time.ToLocalTime(),
                TimeAllotmentPerDay = TimeSpan.FromHours(2),
                TimeRemainingToday = TimeSpan.FromMinutes(15),
                Enabled = false
            });

            logic.Start(lockStepTime);
            try
            {
                lockStepTime.Step(TimeSpan.FromMinutes(5), 10000);
                Assert.AreEqual(0, readoutTriggers);
                Assert.AreEqual(0, restrictTriggers);
                Assert.IsNotNull(logic.CurrentMonitorState);
                Assert.IsFalse(logic.CurrentMonitorState.Enabled);
                Assert.AreEqual(15, logic.CurrentMonitorState.TimeRemainingToday.TotalMinutes, 0.1);
            }
            finally
            {
                await logic.Stop();
            }
        }

        [TestMethod]
        public async Task TestRestrictionIfTimeRemainingIsNegative()
        {
            ILogger logger = new ConsoleLogger();
            InMemoryFileSystem fileSystem = new InMemoryFileSystem();
            LockStepRealTimeProvider lockStepTime = new LockStepRealTimeProvider(logger.Clone("LockStepTime"));
            int readoutTriggers = 0;
            int restrictTriggers = 0;
            TimeSpan? lastReadoutTime = null;
            CoreLogic logic = new CoreLogic(
                logger,
                fileSystem,
                FAKE_STATE_FILE,
                async (timeRemaining) =>
                {
                    lastReadoutTime = timeRemaining;
                    Interlocked.Increment(ref readoutTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                },
                async () =>
                {
                    Interlocked.Increment(ref restrictTriggers);
                    await DurandalTaskExtensions.NoOpTask;
                });
            
            await SetState(fileSystem, new MonitorState()
            {
                LastReadoutTime = lockStepTime.Time.ToLocalTime(),
                LastUpdateTime = lockStepTime.Time.ToLocalTime(),
                TimeAllotmentPerDay = TimeSpan.FromHours(2),
                TimeRemainingToday = TimeSpan.FromHours(1).Negate(),
                Enabled = true
            });

            logic.Start(lockStepTime);
            try
            {
                // Assert that we are in restrict mode
                lockStepTime.Step(TimeSpan.FromMinutes(1), 5000);
                Assert.AreNotEqual(0, restrictTriggers);
            }
            finally
            {
                await logic.Stop();
            }
        }

        private static void SetLockStepTimeTo(LockStepRealTimeProvider lockStepTime, int hour, int minute)
        {
            lockStepTime.Step(TimeSpan.FromSeconds(60 - lockStepTime.Time.ToLocalTime().Second));
            lockStepTime.Step(TimeSpan.FromMinutes(60 - lockStepTime.Time.ToLocalTime().Minute));
            lockStepTime.Step(TimeSpan.FromHours(24 - lockStepTime.Time.ToLocalTime().Hour));
            lockStepTime.Step(TimeSpan.FromHours(hour));
            lockStepTime.Step(TimeSpan.FromMinutes(minute));
        }

        private static async Task SetState(
            IFileSystem fileSystem,
            MonitorState state)
        {
            using (Stream fileOut = await fileSystem.OpenStreamAsync(FAKE_STATE_FILE, FileOpenMode.Create, FileAccessMode.Write))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state));
                await fileOut.WriteAsync(bytes, 0, bytes.Length);
                await fileOut.FlushAsync();
            }
        }
    }
}

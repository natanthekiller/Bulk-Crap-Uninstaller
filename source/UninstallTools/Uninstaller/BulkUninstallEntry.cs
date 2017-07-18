/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Klocman.Extensions;
using Klocman.IO;
using Klocman.Tools;
using UninstallTools.Factory;
using UninstallTools.Properties;

namespace UninstallTools.Uninstaller
{
    public class BulkUninstallEntry
    {
        private static readonly string[] NamesOfIgnoredProcesses =
            WindowsTools.GetInstalledWebBrowsers().Select(s =>
            {
                try
                {
                    return Path.GetFileNameWithoutExtension(s);
                }
                catch (ArgumentException)
                {
                    try
                    {
                        var dash = s.LastIndexOf('\\');
                        return s.Substring(dash + 1, s.LastIndexOf('.') - dash - 1);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }).Where(x => !string.IsNullOrEmpty(x)).Concat(new[] {"explorer"}).Distinct().ToArray();

        private readonly object _operationLock = new object();

        private readonly Dictionary<string, PerfCounterEntry> _perfCounterBuffer =
            new Dictionary<string, PerfCounterEntry>();

        private bool _canRetry = true;
        private SkipCurrentLevel _skipLevel = SkipCurrentLevel.None;
        private Thread _worker;

        public BulkUninstallEntry(ApplicationUninstallerEntry uninstallerEntry, bool isSilent,
            UninstallStatus startingStatus)
        {
            CurrentStatus = startingStatus;
            IsSilent = isSilent;
            UninstallerEntry = uninstallerEntry;
        }

        public Exception CurrentError { get; private set; }

        public UninstallStatus CurrentStatus { get; private set; }

        public bool Finished { get; private set; }

        public int Id { get; internal set; }

        public bool IsRunning
        {
            get
            {
                lock (_operationLock)
                    return _worker != null && _worker.IsAlive;
            }
        }

        public bool IsSilent { get; set; }

        public ApplicationUninstallerEntry UninstallerEntry { get; }

        private static void KillProcesses(IEnumerable<Process> processes)
        {
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            }
        }

        public void Reset()
        {
            //bug handle already running
            CurrentError = null;
            CurrentStatus = UninstallStatus.Waiting;
            Finished = false;
        }

        /// <summary>
        ///     Run the uninstaller on a new thread.
        /// </summary>
        internal void RunUninstaller(RunUninstallerOptions options)
        {
            lock (_operationLock)
            {
                if (Finished || IsRunning || CurrentStatus != UninstallStatus.Waiting)
                    return;

                if (UninstallerEntry.IsRegistered && !UninstallerEntry.RegKeyStillExists())
                {
                    CurrentStatus = UninstallStatus.Completed;
                    Finished = true;
                    return;
                }

                if (UninstallerEntry.UninstallerKind == UninstallerType.Msiexec)
                {
                    var uninstallString = IsSilent && UninstallerEntry.QuietUninstallPossible
                        ? UninstallerEntry.QuietUninstallString
                        : UninstallerEntry.UninstallString;

                    // Always reenumerate products in case any were uninstalled
                    if (ApplicationUninstallerFactory.PathPointsToMsiExec(uninstallString) && 
                        MsiTools.MsiEnumProducts().All(g => !g.Equals(UninstallerEntry.BundleProviderKey)))
                    {
                        CurrentStatus = UninstallStatus.Completed;
                        Finished = true;
                        return;
                    }
                }

                CurrentStatus = UninstallStatus.Uninstalling;

                try
                {
                    _worker = new Thread(UninstallThread) {Name = "RunBulkUninstall_Worker", IsBackground = false};
                    _worker.Start(options);
                }
                catch
                {
                    CurrentStatus = UninstallStatus.Failed;
                    Finished = true;
                    throw;
                }
            }
        }

        public void SkipWaiting(bool terminate)
        {
            lock (_operationLock)
            {
                if (Finished)
                    return;

                if (!IsRunning && CurrentStatus == UninstallStatus.Waiting)
                    CurrentStatus = UninstallStatus.Skipped;

                // Do not allow skipping of Msiexec uninstallers because they will hold up the rest of Msiexec uninstallers in the task
                if (CurrentStatus == UninstallStatus.Uninstalling &&
                    UninstallerEntry.UninstallerKind == UninstallerType.Msiexec &&
                    !terminate)
                    return;

                _skipLevel = terminate ? SkipCurrentLevel.Terminate : SkipCurrentLevel.Skip;
            }
        }

        /// <summary>
        ///     Returns true if uninstaller appears to be stalled. Blocks for 1000ms to gather data.
        /// </summary>
        private bool TestUninstallerForStalls(IEnumerable<string> childProcesses)
        {
            var childProcessNames = childProcesses as IList<string> ?? childProcesses.ToList();

            foreach (var perfCounterEntry in _perfCounterBuffer.ToList())
            {
                if (!childProcessNames.Contains(perfCounterEntry.Key))
                {
                    _perfCounterBuffer.Remove(perfCounterEntry.Key);
                    perfCounterEntry.Value.Dispose();
                }
            }

            foreach (var childProcessName in childProcessNames)
            {
                PerformanceCounter[] perfCounters = null;
                try
                {
                    perfCounters = new[]
                    {
                        new PerformanceCounter("Process", "% Processor Time", childProcessName, true),
                        new PerformanceCounter("Process", "IO Data Bytes/sec", childProcessName, true)
                    };
                    // Important to NextSample now, they will collect data when we sleep
                    _perfCounterBuffer.Add(childProcessName, new PerfCounterEntry(
                        perfCounters, new[] {perfCounters[0].NextSample(), perfCounters[1].NextSample()}));
                }
                catch
                {
                    // Ignore errors caused by counters derping
                    if (perfCounters != null && perfCounters.Length == 2)
                    {
                        perfCounters[0].Dispose();
                        perfCounters[1].Dispose();
                    }
                }
            }

            // Let the counters gather some data
            Thread.Sleep(1100);

            bool? anyWorking = null;

            foreach (var perfCounterEntry in _perfCounterBuffer.ToList())
            {
                try
                {
                    var new0 = perfCounterEntry.Value.Counter[0].NextSample();
                    var new1 = perfCounterEntry.Value.Counter[1].NextSample();
                    var c0 = CounterSample.Calculate(perfCounterEntry.Value.Sample[0], new0);
                    var c1 = CounterSample.Calculate(perfCounterEntry.Value.Sample[1], new1);
                    perfCounterEntry.Value.Sample[0] = new0;
                    perfCounterEntry.Value.Sample[1] = new1;

                    Debug.WriteLine("CPU " + c0 + "%, IO " + c1 + "B");

                    // Check if process seems to be doing anything. Use 1% for CPU and 10KB for I/O
                    if (c0 <= 1 && c1 <= 10240)
                    {
                        anyWorking = false;
                    }
                    else
                    {
                        anyWorking = true;
                        break;
                    }
                }
                catch
                {
                    perfCounterEntry.Value.Dispose();
                    _perfCounterBuffer.Remove(perfCounterEntry.Key);
                }
            }

            // Only return true if we had at least one process to test and it tested negatively
            return anyWorking.HasValue && !anyWorking.Value;
        }

        private void UninstallThread(object parameters)
        {
            var options = parameters as RunUninstallerOptions;
            Debug.Assert(options != null, "options != null");

            Exception error = null;
            var retry = false;
            try
            {
                var processSnapshot = Process.GetProcesses().Select(x => x.Id).ToArray();

                using (var uninstaller = UninstallerEntry.RunUninstaller(options.PreferQuiet, options.Simulate))
                {
                    // Can be null during simulation
                    if (uninstaller != null)
                    {
                        if (options.PreferQuiet && UninstallerEntry.QuietUninstallPossible)
                            uninstaller.PriorityClass = ProcessPriorityClass.BelowNormal;

                        var checkCounters = options.PreferQuiet && options.AutoKillStuckQuiet &&
                                            UninstallerEntry.QuietUninstallPossible;
                        var watchedProcesses = new List<Process> {uninstaller};
                        var idleCounter = 0;

                        while (true)
                        {
                            if (_skipLevel == SkipCurrentLevel.Skip)
                                break;

                            foreach (var watchedProcess in watchedProcesses.ToList())
                                watchedProcesses.AddRange(watchedProcess.GetChildProcesses());

                            // Msiexec service can start processes, but we don't want to watch the service
                            if (UninstallerEntry.UninstallerKind == UninstallerType.Msiexec)
                            {
                                foreach (var watchedProcess in Process.GetProcessesByName("msiexec"))
                                    watchedProcesses.AddRange(watchedProcess.GetChildProcesses());
                            }

                            // Remove duplicate, dead, and blacklisted processes
                            watchedProcesses = watchedProcesses.DistinctBy(x => x.Id).Where(p =>
                            {
                                try
                                {
                                    if (p.HasExited)
                                    return false;

                                    var pName = p.ProcessName;
                                    if (NamesOfIgnoredProcesses.Any(n =>
                                        pName.Equals(n, StringComparison.InvariantCultureIgnoreCase)))
                                        return false;
                                }
                                catch (Win32Exception)
                                {
                                    return false;
                                }
                                catch (InvalidOperationException)
                                {
                                    return false;
                                }

                                return !processSnapshot.Contains(p.Id);
                            }).ToList();

                            // Check if we are done, or if there are some proceses left that we missed
                            if (watchedProcesses.Count == 0)
                            {
                                if (string.IsNullOrEmpty(UninstallerEntry.InstallLocation))
                                    break;

                                var candidates = Process.GetProcesses().Where(x => !processSnapshot.Contains(x.Id));
                                foreach (var process in candidates)
                                {
                                    try
                                    {
                                        if (process.MainModule.FileName.Contains(UninstallerEntry.InstallLocation,
                                            StringComparison.InvariantCultureIgnoreCase)
                                            ||
                                            process.GetCommandLine()
                                                .Contains(UninstallerEntry.InstallLocation,
                                                    StringComparison.InvariantCultureIgnoreCase))
                                            watchedProcesses.Add(process);
                                    }
                                    catch
                                    {
                                        // Ignore permission and access errors
                                    }
                                }

                                if (watchedProcesses.Count == 0)
                                    break;
                            }

                            // Check for deadlocks during silent uninstall
                            if (checkCounters)
                            {
                                var processNames = watchedProcesses.Select(x =>
                                {
                                    try
                                    {
                                        return x.ProcessName;
                                    }
                                    catch
                                    {
                                        // Ignore errors caused by processes that exited
                                        return null;
                                    }
                                }).Where(x => !string.IsNullOrEmpty(x));

                                if (TestUninstallerForStalls(processNames))
                                    idleCounter++;
                                else
                                    idleCounter = 0;

                                // Kill the uninstaller (and children) if they were idle/stalled for too long
                                if (idleCounter > 30)
                                {
                                    KillProcesses(watchedProcesses);
                                    throw new IOException(Localisation.UninstallError_UninstallerTimedOut);
                                }
                            }
                            else Thread.Sleep(1000);

                            // Kill the uninstaller (and children) if user told us to or if it was idle for too long
                            if (_skipLevel == SkipCurrentLevel.Terminate)
                            {
                                if (UninstallerEntry.UninstallerKind == UninstallerType.Msiexec)
                                    watchedProcesses.AddRange(Process.GetProcessesByName("Msiexec"));

                                KillProcesses(watchedProcesses);
                                break;
                            }
                        }

                        if (_skipLevel == SkipCurrentLevel.None)
                        {
                            var exitVar = uninstaller.ExitCode;
                            if (exitVar != 0)
                            {
                                if (UninstallerEntry.UninstallerKind == UninstallerType.Msiexec && exitVar == 1602)
                                {
                                    // 1602 ERROR_INSTALL_USEREXIT - The user has cancelled the installation.
                                    _skipLevel = SkipCurrentLevel.Skip;
                                }
                                else if (UninstallerEntry.UninstallerKind == UninstallerType.Nsis &&
                                         (exitVar == 1 || exitVar == 2))
                                {
                                    // 1 - Installation aborted by user (cancel button)
                                    // 2 - Installation aborted by script (often after user clicks cancel)
                                    _skipLevel = SkipCurrentLevel.Skip;
                                }
                                else if (exitVar == -1073741510)
                                {
                                    /* 3221225786 / 0xC000013A / -1073741510 
                                    The application terminated as a result of a CTRL+C. 
                                    Indicates that the application has been terminated either by user's 
                                    keyboard input CTRL+C or CTRL+Break or closing command prompt window. */
                                    _skipLevel = SkipCurrentLevel.Terminate;
                                }
                                else
                                {
                                    switch (exitVar)
                                    {
                                        // The system cannot find the file specified. Indicates that the file can not be found in specified location.
                                        case 2:
                                        // The system cannot find the path specified. Indicates that the specified path can not be found.
                                        case 3:
                                        // Access is denied. Indicates that user has no access right to specified resource.
                                        case 5:
                                        // Program is not recognized as an internal or external command, operable program or batch file. 
                                        case 9009:
                                            break;
                                        default:
                                            if (options.RetryFailedQuiet)
                                                retry = true;
                                            break;
                                    }
                                    throw new IOException(Localisation.UninstallError_UninstallerReturnedCode +
                                                          exitVar);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                try
                {
                    _perfCounterBuffer.ForEach(x => x.Value.Dispose());
                }
                catch
                {
                    // Ignore any errors to make sure rest of this code runs
                }
                _perfCounterBuffer.Clear();

                // Take care of the aftermath
                if (_skipLevel != SkipCurrentLevel.None)
                {
                    _skipLevel = SkipCurrentLevel.None;

                    CurrentStatus = UninstallStatus.Skipped;
                    CurrentError = new OperationCanceledException(Localisation.ManagerError_Skipped);
                }
                else if (error != null)
                {
                    //Localisation.ManagerError_PrematureWorkerStop is unused
                    CurrentStatus = UninstallStatus.Failed;
                    CurrentError = error;
                }
                else
                {
                    CurrentStatus = UninstallStatus.Completed;
                }

                if (retry && _canRetry)
                {
                    CurrentStatus = UninstallStatus.Waiting;
                    _canRetry = false;
                }
                else
                {
                    Finished = true;
                }
            }
        }

        private sealed class PerfCounterEntry : IDisposable
        {
            public PerfCounterEntry(PerformanceCounter[] counter, CounterSample[] sample)
            {
                Counter = counter;
                Sample = sample;
            }

            public PerformanceCounter[] Counter { get; }

            public CounterSample[] Sample { get; }

            public void Dispose()
            {
                foreach (var performanceCounter in Counter)
                {
                    performanceCounter?.Dispose();
                }
            }
        }

        internal sealed class RunUninstallerOptions
        {
            public RunUninstallerOptions(bool autoKillStuckQuiet, bool retryFailedQuiet, bool preferQuiet, bool simulate)
            {
                AutoKillStuckQuiet = autoKillStuckQuiet;
                RetryFailedQuiet = retryFailedQuiet;
                PreferQuiet = preferQuiet;
                Simulate = simulate;
            }

            public bool AutoKillStuckQuiet { get; }

            public bool PreferQuiet { get; }

            public bool RetryFailedQuiet { get; }

            public bool Simulate { get; }
        }

        internal enum SkipCurrentLevel
        {
            None = 0,
            Terminate,
            Skip
        }
    }
}
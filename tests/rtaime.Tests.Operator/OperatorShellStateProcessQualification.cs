// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed partial class OperatorXamlStartupTests
{
    [Fact]
    public Task Shell_states_load_real_MainWindow_in_isolated_STA_process() => RunShellQualificationAsync("states");

    [Fact]
    public Task Shell_dock_stress_load_real_MainWindow_in_isolated_STA_process() => RunShellQualificationAsync("stress");

    private static async Task RunShellQualificationAsync(string phase)
    {
        var elapsed = Stopwatch.StartNew();
        // Child output is buffered until exit, so retain the actual phase times in each line.
        void ReportPhase(string detail) => Console.WriteLine(
            $"SHELL PROCESS PHASE [{phase}] {detail}; utc={DateTime.UtcNow:O}; elapsedMs={elapsed.ElapsedMilliseconds}; pid={Environment.ProcessId}");

        const string childVariable = "RTAIME_SHELL_QUALIFICATION_CHILD";
        var method = phase == "stress"
            ? nameof(Shell_dock_stress_load_real_MainWindow_in_isolated_STA_process)
            : nameof(Shell_states_load_real_MainWindow_in_isolated_STA_process);
        if (Environment.GetEnvironmentVariable(childVariable) != phase)
        {
            // WPF permits one Application per process. Keep the existing MainWindow journey's
            // 45-second bound and all its assertions; each additive phase has its own same bound.
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(OperatorXamlStartupTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=rtaime.Tests.Operator.OperatorXamlStartupTests." + method);
            start.ArgumentList.Add("--logger:console;verbosity=detailed");
            start.Environment[childVariable] = phase;
            ReportPhase("Child launch BEGIN");
            using var process = Process.Start(start)!;
            ReportPhase($"Child launch PASS; childPid={process.Id}");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                ReportPhase("Child exit wait BEGIN");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                ReportPhase($"Child exit wait PASS; exitCode={process.ExitCode}");
                var report = await stdout;
                Assert.True(process.ExitCode == 0, report + Environment.NewLine + await stderr);
                Assert.Contains(phase == "stress" ? "SHELL CYCLES PASS: 100" : "SHELL STATES PASS: 80", report);
            }
            catch (TimeoutException)
            {
                ReportPhase("Child process watchdog EXPIRED");
                throw;
            }
            finally
            {
                if (!process.HasExited)
                {
                    ReportPhase("Child process tree termination BEGIN");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    ReportPhase($"Child process tree termination PASS; exitCode={process.ExitCode}");
                }
                // Retain phase output even when the unchanged process watchdog expires.
                try
                {
                    await Task.WhenAll(stdout,stderr).WaitAsync(TimeSpan.FromSeconds(5));
                    Console.WriteLine(await stdout);
                    Console.WriteLine(await stderr);
                }
                catch (TimeoutException) { Console.WriteLine("Shell diagnostic pipes did not close within five seconds."); }
            }
            return;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var failures = new List<Exception>();
            OperatorResourceApplication? app = null;
            dispatcher.UnhandledException += (_, args) => { failures.Add(args.Exception); args.Handled = true; };
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    ReportPhase("Application initialization BEGIN");
                    app = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                    ReportPhase("Application initialization PASS");
                    ReportPhase("Window qualification BEGIN");
                    await OperatorCombinedVisualQualification.VerifyShellStatesAsync(null, dispatcher, phase == "stress");
                    ReportPhase("Window qualification PASS");
                }
                catch (Exception exception) { failures.Add(exception); }
                finally
                {
                    try
                    {
                        ReportPhase("Application Shutdown BEGIN");
                        app?.Shutdown();
                        ReportPhase("Application Shutdown PASS");
                    }
                    catch (Exception exception) { failures.Add(exception); }
                    ReportPhase("Dispatcher shutdown request BEGIN");
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    ReportPhase("Dispatcher shutdown request PASS");
                }
            }));
            ReportPhase("Dispatcher Run BEGIN");
            try { Dispatcher.Run(); } catch (Exception exception) { failures.Add(exception); }
            finally
            {
                ReportPhase($"Dispatcher Run EXIT; failures={failures.Count}");
                if (failures.Count == 0) completed.TrySetResult();
                else completed.TrySetException(new AggregateException(failures));
            }
        }) { IsBackground = true, Name = "Operator shell state qualification" };
        thread.SetApartmentState(ApartmentState.STA);
        ReportPhase("STA thread start BEGIN");
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally
        {
            ReportPhase("STA thread join BEGIN");
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Shell state STA did not terminate.");
            ReportPhase("STA thread join PASS");
        }
    }
}

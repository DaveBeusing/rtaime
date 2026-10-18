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
    public async Task Shell_states_load_real_MainWindow_in_isolated_STA_process()
    {
        const string childVariable = "RTAIME_SHELL_STATE_CHILD";
        if (Environment.GetEnvironmentVariable(childVariable) != "1")
        {
            // WPF permits one Application per process. Keep the existing MainWindow journey's
            // 45-second bound and all its assertions; this additive matrix has its own same bound.
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(OperatorXamlStartupTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=rtaime.Tests.Operator.OperatorXamlStartupTests.Shell_states_load_real_MainWindow_in_isolated_STA_process");
            start.ArgumentList.Add("--logger:console;verbosity=detailed");
            start.Environment[childVariable] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                var report = await stdout;
                Console.WriteLine(report);
                Assert.True(process.ExitCode == 0, report + Environment.NewLine + await stderr);
                Assert.Contains("SHELL CYCLES PASS: 100", report);
                Assert.Contains("SHELL STATES PASS: 80", report);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
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
                    app = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                    await OperatorCombinedVisualQualification.VerifyShellStatesAsync(null, dispatcher);
                }
                catch (Exception exception) { failures.Add(exception); }
                finally
                {
                    try { app?.Shutdown(); } catch (Exception exception) { failures.Add(exception); }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            }));
            try { Dispatcher.Run(); } catch (Exception exception) { failures.Add(exception); }
            finally
            {
                if (failures.Count == 0) completed.TrySetResult();
                else completed.TrySetException(new AggregateException(failures));
            }
        }) { IsBackground = true, Name = "Operator shell state qualification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Shell state STA did not terminate."); }
    }
}

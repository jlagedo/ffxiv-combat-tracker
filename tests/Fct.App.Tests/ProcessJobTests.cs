using System;
using System.Diagnostics;
using Fct.Host;
using Xunit;

namespace Fct.App.Tests
{
    // ProcessJob ties child processes to the host via a Windows kill-on-close job. These tests run
    // only on Windows (the job API is Win32); elsewhere they no-op.
    public class ProcessJobTests
    {
        [Fact]
        public void TryCreate_succeeds_on_windows()
        {
            if (!OperatingSystem.IsWindows()) return;
            using var job = ProcessJob.TryCreate();
            Assert.NotNull(job);
            Assert.True(job!.IsValid);
        }

        [Fact]
        public void Disposing_the_job_kills_an_enrolled_process()
        {
            if (!OperatingSystem.IsWindows()) return;

            var job = ProcessJob.TryCreate();
            Assert.NotNull(job);

            // A child that blocks forever until terminated.
            var child = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c pause",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            });
            Assert.NotNull(child);

            try
            {
                job!.AddProcess(child!);
                Assert.False(child!.HasExited);

                // Closing the last job handle makes the OS terminate every member.
                job.Dispose();

                Assert.True(child.WaitForExit(5000), "enrolled process was not killed when the job closed");
            }
            finally
            {
                if (child != null && !child.HasExited) child.Kill();
                child?.Dispose();
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace StrmTool.Tests
{
    public sealed class ExtractProgressTests
    {
        [Fact]
        public async Task ConcurrentCompletionsReportProgressInOrder()
        {
            using var task = new ExtractHarness();
            using var firstReportEntered = new ManualResetEventSlim();
            using var secondReportEntered = new ManualResetEventSlim();
            var reports = new ConcurrentQueue<double>();
            var progress = new InlineProgress(value =>
            {
                if (value == 50)
                {
                    firstReportEntered.Set();
                    // A concurrent second report would overtake this slow observer.
                    secondReportEntered.Wait(TimeSpan.FromSeconds(1));
                }
                else
                {
                    secondReportEntered.Set();
                }

                reports.Enqueue(value);
            });

            int started = 0;
            var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int succeeded = await task.RunAsync(async (_, cancellationToken) =>
            {
                int index = Interlocked.Increment(ref started);
                if (index == 2)
                    bothStarted.SetResult();
                await bothStarted.Task.WaitAsync(cancellationToken);
                if (index == 2)
                    Assert.True(firstReportEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
                return true;
            }, progress);

            Assert.Equal(2, succeeded);
            Assert.Equal(new[] { 50.0, 100.0, 100.0 }, reports.ToArray());
        }

        private sealed class InlineProgress(Action<double> report) : IProgress<double>
        {
            public void Report(double value) => report(value);
        }

        private sealed class ExtractHarness : ExtractTask
        {
            public ExtractHarness()
                : base(DispatchProxy.Create<ILibraryManager, TaskRegressionTests.NoOpLibraryProxy>(),
                    null, null, null, NullLogger<ExtractTask>.Instance)
            {
                _config = new PluginConfiguration { MaxConcurrentExtract = 2 };
            }

            public Task<int> RunAsync(Func<BaseItem, CancellationToken, Task<bool>> action, IProgress<double> progress)
                => ProcessStrmItemsAsync(new List<BaseItem> { new Video(), new Video() },
                    action, progress, CancellationToken.None);
        }
    }
}

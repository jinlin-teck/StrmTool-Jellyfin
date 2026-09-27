using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace StrmTool.Tests
{
    public sealed class TaskRegressionTests
    {
        [Fact]
        public void FilterStopsWhenCancelledInsidePredicate()
        {
            using var cts = new CancellationTokenSource();
            int visited = 0;
            Assert.ThrowsAny<OperationCanceledException>(() => Filter(Enumerable.Range(0, 10000).ToArray(), _ =>
            {
                Interlocked.Increment(ref visited);
                cts.Cancel();
                return true;
            }, cts.Token));
            Assert.InRange(visited, 1, 8);
        }

        [Fact]
        public void EmptyFilterStillObservesCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Filter(Array.Empty<int>(), _ => true, cts.Token));
        }

        [Fact]
        public void FilterPreservesAllMatches()
        {
            var result = Filter(Enumerable.Range(0, 100).ToArray(), n => n % 2 == 0, CancellationToken.None);
            Assert.Equal(Enumerable.Range(0, 50).Select(n => n * 2), result.OrderBy(n => n));
        }

        [Fact]
        public void LibraryQueryChecksCancellationBeforeCallingManager()
        {
            // null manager would fail if accessed; cancellation must precede the query.
            var service = new StrmMediaInfoService(null, null, null, null, NullLogger.Instance);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => service.GetAllStrmItems(cts.Token));
        }

        [Fact]
        public async Task OnlyOuterEvaluationOwnsRestoreCompletion()
        {
            var library = DispatchProxy.Create<ILibraryManager, NoOpLibraryProxy>();
            using var listener = new ItemUpdateListener(library, NullLogger.Instance, new PluginConfiguration());
            listener.Dispose();

            var type = typeof(ItemUpdateListener);
            var tasks = (ConcurrentDictionary<Guid, TaskCompletionSource<bool>>)type
                .GetField("_runningTasks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(listener);
            var id = Guid.NewGuid();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(tasks.TryAdd(id, completion));

            // 内层即使提前返回也不能释放外层的生命周期/去重记录。
            var restore = type.GetMethod("RestoreItemMetadataAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            await (Task)restore.Invoke(listener, new object[] { id, "movie" });
            Assert.Same(completion, tasks[id]);
            Assert.False(completion.Task.IsCompleted);

            var evaluate = type.GetMethod("EvaluateRestoreAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            await (Task)evaluate.Invoke(listener, new object[] { id, "movie", completion });
            Assert.False(tasks.ContainsKey(id));
            Assert.True(completion.Task.IsCompletedSuccessfully);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AutoExtractQueuesExistingStreamsWithoutReadingThemOnEventThread(bool forceRefresh)
        {
            var library = DispatchProxy.Create<ILibraryManager, NoOpLibraryProxy>();
            using var task = new ExtractHarness(library, forceRefresh);
            var item = new LibraryVideo { Id = Guid.NewGuid(), Path = Path.Combine(Path.GetTempPath(), "movie.strm") };
            var type = typeof(ExtractTask);
            type.GetMethod("OnStrmFileDetected", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(task, new object[] { null, item });

            var queued = (ConcurrentDictionary<Guid, byte>)type
                .GetField("_queuedAutoExtractItems", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(task);
            Assert.True(queued.ContainsKey(item.Id));
            Assert.Equal(0, item.StreamReads);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExportTaskPreservesExistingCacheIncludingChangedSource(bool changeSource)
        {
            string directory = Path.Combine(Path.GetTempPath(), "strmtool-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var item = new LibraryVideo { Id = Guid.NewGuid(), Path = Path.Combine(directory, "movie.strm") };
                File.WriteAllText(item.Path, "https://example.invalid/a.mkv");
                var cache = new MediaInfoCache(NullLogger.Instance);
                Assert.True(await cache.SaveFullCacheAsync(item.Path, ((IHasMediaSources)item).GetMediaStreams(), 100, 1000, "mkv"));
                var cachePath = Path.Combine(directory, "movie.strmtool.json");
                string original = File.ReadAllText(cachePath);
                if (changeSource)
                    File.WriteAllText(item.Path, "https://example.invalid/b.mkv");

                var task = CreateExportTask(item);
                await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
                Assert.Equal(original, File.ReadAllText(cachePath));
                Assert.Equal(!changeSource, cache.TryGetFullCache(item.Path, out _, verifyContentHash: true));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public async Task ExportSingleItemReportsActualWriteFailure()
        {
            string directory = Path.Combine(Path.GetTempPath(), "strmtool-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var item = new LibraryVideo { Id = Guid.NewGuid(), Path = Path.Combine(directory, "movie.strm") };
                File.WriteAllText(item.Path, "https://example.invalid/a.mkv");
                Directory.CreateDirectory(Path.Combine(directory, "movie.strmtool.json"));
                var task = CreateExportTask(item);
                var service = new StrmMediaInfoService(null, null, null, null, NullLogger.Instance);
                var method = typeof(ExportStrmInfoTask).GetMethod("ExportSingleItemAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                bool saved = await (Task<bool>)method.Invoke(task, new object[]
                {
                    service, new MediaInfoCache(NullLogger.Instance), item, CancellationToken.None
                });
                Assert.False(saved);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static ExportStrmInfoTask CreateExportTask(BaseItem item)
        {
            var library = DispatchProxy.Create<ILibraryManager, NoOpLibraryProxy>();
            ((NoOpLibraryProxy)(object)library).Items = new List<BaseItem> { item };
            return new ExportStrmInfoTask(library,
                DispatchProxy.Create<IMediaEncoder, NoOpLibraryProxy>(),
                DispatchProxy.Create<IMediaStreamRepository, NoOpLibraryProxy>(),
                DispatchProxy.Create<IItemRepository, NoOpLibraryProxy>(),
                NullLogger<ExportStrmInfoTask>.Instance);
        }

        private sealed class LibraryVideo : Video, IHasMediaSources
        {
            public int StreamReads { get; private set; }
            IReadOnlyList<MediaStream> IHasMediaSources.GetMediaStreams()
            {
                StreamReads++;
                return new List<MediaStream> { new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" } };
            }
        }

        private sealed class ExtractHarness : ExtractTask
        {
            public ExtractHarness(ILibraryManager library, bool forceRefresh)
                : base(library, null, null, null, NullLogger<ExtractTask>.Instance)
            {
                _config = new PluginConfiguration
                {
                    EnableAutoExtract = true,
                    ForceRefreshIgnoreExisting = forceRefresh,
                    RefreshDelayMs = 20000
                };
            }
        }

        private static List<int> Filter(IReadOnlyList<int> items, Func<int, bool> predicate, CancellationToken token)
        {
            var method = typeof(StrmLibraryTaskBase)
                .GetMethod("FilterItemsInParallel", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(typeof(int));
            try
            {
                return (List<int>)method.Invoke(null, new object[] { items, predicate, token });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        public class NoOpLibraryProxy : DispatchProxy
        {
            public List<BaseItem> Items { get; set; }
            protected override object Invoke(MethodInfo targetMethod, object[] args) =>
                targetMethod.Name == "GetItemList" ? Items : null;
        }
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2025 Vadim Belov <https://belov.us>

using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Octockup.Server.Collections
{
    public class LazyLoader<T> : IEnumerable<T>, IDisposable
    {
        private readonly IEnumerable<T> _lazyCollection;
        private readonly BlockingCollection<T> _buffer;
        private readonly CancellationTokenSource _disposeCancellation = new();
        private readonly object _completionSync = new();

        private int _loadingStarted;
        private int _isDisposed;
        private int _totalLoaded;
        private ExceptionDispatchInfo? _loadingException;

        public LazyLoader(IEnumerable<T> lazyCollection, int maxBufferedItems)
        {
            ArgumentNullException.ThrowIfNull(lazyCollection);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBufferedItems, 1);

            _lazyCollection = lazyCollection;
            _buffer = new BlockingCollection<T>(maxBufferedItems);
        }

        public bool IsEnumerationCompleted => _buffer.IsAddingCompleted;

        public int Total => Volatile.Read(ref _totalLoaded);

        public IEnumerator<T> GetEnumerator()
        {
            StartBackgroundLoadingIfNeeded();

            while (TryTakeNext(out T item))
            {
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerable<T[]> GetBatches(
            int maxBatchSize,
            TimeSpan flushAfterIdle,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flushAfterIdle, TimeSpan.Zero);

            StartBackgroundLoadingIfNeeded();

            while (TryTakeNext(out T first, cancellationToken: cancellationToken))
            {
                List<T> batch = [first];
                while (batch.Count < maxBatchSize &&
                    TryTakeNext(out T item, flushAfterIdle, cancellationToken))
                {
                    batch.Add(item);
                }

                yield return batch.ToArray();
            }
        }

        private bool TryTakeNext(
            out T item,
            TimeSpan? waitTimeout = null,
            CancellationToken cancellationToken = default)
        {
            T? loadedItem;
            int timeout = waitTimeout.HasValue
                ? (int)waitTimeout.Value.TotalMilliseconds
                : Timeout.Infinite;
            bool hasItem = _buffer.TryTake(out loadedItem, timeout, cancellationToken);

            if (hasItem)
            {
                item = loadedItem!;
                return true;
            }

            Volatile.Read(ref _loadingException)?.Throw();
            item = default!;
            return false;
        }

        private void StartBackgroundLoadingIfNeeded()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            if (Interlocked.CompareExchange(ref _loadingStarted, 1, 0) == 0)
            {
                _ = Task.Run(Load);
            }
        }

        private void Load()
        {
            try
            {
                foreach (T item in _lazyCollection)
                {
                    _buffer.Add(item, _disposeCancellation.Token);
                    Interlocked.Increment(ref _totalLoaded);
                }
            }
            catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException) when (Volatile.Read(ref _isDisposed) != 0)
            {
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _loadingException, ExceptionDispatchInfo.Capture(ex));
            }
            finally
            {
                CompleteAdding();
            }
        }

        private void CompleteAdding()
        {
            lock (_completionSync)
            {
                if (!_buffer.IsAddingCompleted)
                {
                    _buffer.CompleteAdding();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            _disposeCancellation.Cancel();
            CompleteAdding();
            while (_buffer.TryTake(out _))
            {
            }

            GC.SuppressFinalize(this);
        }
    }
}

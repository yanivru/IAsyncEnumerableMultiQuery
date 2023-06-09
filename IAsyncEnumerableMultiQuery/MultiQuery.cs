﻿using Microsoft.VisualStudio.Threading;

namespace IAsyncEnumerableMultiQuery
{
    public static class EnumerateAndExtensions
    {
        public static (IAsyncEnumerable<T>, Func<IAsyncEnumerable<T>, ValueTask>) EnumerateAnd<T>(this IAsyncEnumerable<T> source, Func<IAsyncEnumerable<T>, ValueTask> action)
        {
            return (source, action);
        }

        public static async ValueTask<TQueryResult> QueryAsync<T, TQueryResult>(this (IAsyncEnumerable<T> source, Func<IAsyncEnumerable<T>, ValueTask> action) prev, Func<IAsyncEnumerable<T>, ValueTask<TQueryResult>> query)
        {
            var lazyEnumerable = new EnumerateAndRunner<T>(prev.source);

            var t = prev.action(lazyEnumerable);
            var t2 = query(lazyEnumerable);

            await lazyEnumerable.RunAsync();

            await t;
            return await t2;
        }

        //public static (IAsyncEnumerable<T>, Task<TQueryResult>, Task<TQueryResult2>) Query()
    }

    internal class EnumerateAndRunner<T> : IAsyncEnumerable<T>
    {
        private IAsyncEnumerable<T> _source;
        private readonly OneToManyEnumerator<T> _oneToManyEnumerator;
        private readonly OneToManyEnumerator<T> _oneToManyEnumerator2;
        private int _enumeratorIndex;

        public EnumerateAndRunner(IAsyncEnumerable<T> source)
        {
            _source = source;
            _oneToManyEnumerator = new OneToManyEnumerator<T>();
            _oneToManyEnumerator2 = new OneToManyEnumerator<T>();
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (_enumeratorIndex == 0)
            {
                _enumeratorIndex++;
                return _oneToManyEnumerator; 
            }
            else
                return _oneToManyEnumerator2;
        }

        public async Task RunAsync()
        {
            await foreach (var item in _source)
            {
                bool isMoreNeeded1 = await _oneToManyEnumerator.IsReadyToRecieveMoreAsync();
                bool isMoreNeeded2 = await _oneToManyEnumerator2.IsReadyToRecieveMoreAsync();

                if(isMoreNeeded1)
                    _oneToManyEnumerator.SetResult(item);
                if(isMoreNeeded2)
                    _oneToManyEnumerator2.SetResult(item);

                if (!isMoreNeeded1 && !isMoreNeeded2)
                    break;
            }

            _oneToManyEnumerator.SetNoMoreResults();
            _oneToManyEnumerator2.SetNoMoreResults();
        }
    }

    public static class MultiQuery
    {
        public static async Task<(T1, T2)> Query<T, T1, T2>(this IAsyncEnumerable<T> source, Func<IAsyncEnumerable<T>, ValueTask<T1>> query1, Func<IAsyncEnumerable<T>, ValueTask<T2>> query2)
        {
            var e = new SingleEnumerable<T>(source, 2);
            var r1 = query1(e);
            var r2 = query2(e);
            return (await r1, await r2);
        }
    }

    class SingleEnumerable<T> : IAsyncEnumerable<T>
    {
        private readonly SingleEnumerator<T> _singleEnumerator;

        public SingleEnumerable(IAsyncEnumerable<T> inner, int queryCount)
        {
            _singleEnumerator = new SingleEnumerator<T>(inner, queryCount);
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = new())
        {
            return _singleEnumerator;
        }
    }

    class OneToManyEnumerator<T> : IAsyncEnumerator<T>
    {
        private TaskCompletionSource<bool> _isCurrentItemReady = new();
        private TaskCompletionSource<bool> _isReadyToReceiveMore = new();

        public void SetResult(T result)
        {
            Current = result;
            var prevRead = _isCurrentItemReady;
            _isCurrentItemReady = new TaskCompletionSource<bool>();
            prevRead.SetResult(true);
        }
         
        public ValueTask DisposeAsync()
        {
            _isReadyToReceiveMore.TrySetResult(false);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            _isReadyToReceiveMore.SetResult(true);
            return await _isCurrentItemReady.Task;
        }

        public async ValueTask<bool> IsReadyToRecieveMoreAsync()
        {
            var isStillActive = await _isReadyToReceiveMore.Task;
            _isReadyToReceiveMore = new TaskCompletionSource<bool>();
            return isStillActive;
        }

        internal void SetNoMoreResults()
        {
            _isCurrentItemReady.TrySetResult(false);
        }

        public T Current { get; private set; }
    }

    class SingleEnumerator<T> : IAsyncEnumerator<T>
    {
        private int _queryCount;
        private int _waitingCount;
        private int _disposedCount = 0;
        private TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>();
        private IAsyncEnumerator<T> _innerEnumerator;

        public SingleEnumerator(IAsyncEnumerable<T> inner, int queryCount)
        {
            _queryCount = queryCount;
            _innerEnumerator = inner.GetAsyncEnumerator();
        }

        public ValueTask DisposeAsync()
        {
            var newQueryCount = Interlocked.Decrement(ref _queryCount);

            if (newQueryCount == 0)
            {
                _innerEnumerator.DisposeAsync();
                return ValueTask.CompletedTask;
            }

            if (newQueryCount == _waitingCount)
            {
                _ = MoveToNextElement();
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            if (Interlocked.Increment(ref _waitingCount) == _queryCount)
            {
                return await MoveToNextElement();
            }
            return await _ready.Task;
        }

        private async ValueTask<bool> MoveToNextElement()
        {
            Interlocked.Exchange(ref _waitingCount, 0);
            var ready = _ready;
            _ready = new TaskCompletionSource<bool>();
            ready.SetResult(await _innerEnumerator.MoveNextAsync());
            return await ready.Task;
        }

        public T Current => _innerEnumerator.Current;
    }
}
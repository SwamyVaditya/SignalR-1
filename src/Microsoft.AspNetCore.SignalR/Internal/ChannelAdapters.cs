using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks.Channels;

namespace Microsoft.AspNetCore.SignalR.Internal
{
    public static class ChannelAdapters
    {
        private static readonly MethodInfo _fromObservableMethod = typeof(ChannelAdapters)
            .GetRuntimeMethods()
            .Single(m => m.Name.Equals("FromObservable") && m.IsGenericMethod);

        public static ReadableChannel<object> FromObservable(object observable, Type observableInterface)
        {
            // TODO: Cache expressions by observable.GetType()?
            return (ReadableChannel<object>)_fromObservableMethod
                .MakeGenericMethod(observableInterface.GetGenericArguments())
                .Invoke(null, new[] { observable });
        }

        public static ReadableChannel<object> FromObservable<T>(IObservable<T> observable)
        {
            // TODO: Allow bounding and optimizations?
            var channel = Channel.CreateUnbounded<object>();
            var cancellationTokenSource = new CancellationTokenSource();

            var subscription = observable.Subscribe(new ChannelObserver<T>(channel.Out, cancellationTokenSource.Token));

            return channel.In;
        }

        private class ChannelObserver<T> : IObserver<T>
        {
            private WritableChannel<object> _output;
            private CancellationToken _cancellationToken;

            public ChannelObserver(WritableChannel<object> output, CancellationToken cancellationToken)
            {
                _output = output;
                _cancellationToken = cancellationToken;
            }

            public void OnCompleted()
            {
                _output.TryComplete();
            }

            public void OnError(Exception error)
            {
                _output.TryComplete(error);
            }

            public void OnNext(T value)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                // This will block the thread emitting the object if the channel is bounded and full
                // I think this is OK, since we want to push the backpressure up. However, we may need
                // to find a way to force the subscription off to a dedicated thread in order to
                // ensure we don't block other tasks

                // Right now however, we use unbounded channels, so all of the above is moot because TryWrite will always succeed
                while(!_output.TryWrite(value))
                {
                    // Wait for a spot
                    if(!_output.WaitToWriteAsync(_cancellationToken).Result)
                    {
                        // Channel was closed.
                        throw new InvalidOperationException("Output channel was closed");
                    }
                }
            }
        }
    }
}

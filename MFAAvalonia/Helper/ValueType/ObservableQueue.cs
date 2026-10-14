using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MFAAvalonia.Helper.ValueType;

public partial class ObservableQueue<T> : ObservableObject
{
    private readonly Queue<T> _queue = new();
    private readonly Lock _lock = new();

    [ObservableProperty] private int _count;

    public EventHandler<CountChangedEventArgs>? CountChanged;

    public ObservableQueue()
    {
        Count = _queue.Count;
    }
    partial void OnCountChanged(int oldValue, int newValue)
    {
        CountChanged?.Invoke(this, new CountChangedEventArgs(oldValue, newValue));
    }
    
    public void Enqueue(T task)
    {
        lock (_lock)
        {
            _queue.Enqueue(task);
            Count = _queue.Count;
        }
    }

    public T Dequeue()
    {
        lock (_lock)
        {
            var task = _queue.Dequeue();
            Count = _queue.Count;
            return task;
        }
    }

    public bool TryDequeue(out T? task)
    {
        lock (_lock)
        {
            if (!_queue.TryDequeue(out task))
                return false;

            Count = _queue.Count;
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            Count = _queue.Count;
        }
    }
    public bool Any()
    {
        lock (_lock)
        {
            return _queue.Count > 0;
        }
    }
    
    public bool Any(Func<T, bool> predicate)
    {
        lock (_lock)
        {
            return _queue.Any(predicate);
        }
    }
    
    public class CountChangedEventArgs(int oldValue, int newValue) : EventArgs
    {
        public int OldValue => oldValue;
        public int NewValue => newValue;
    }
}

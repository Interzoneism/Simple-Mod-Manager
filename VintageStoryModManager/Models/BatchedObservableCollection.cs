using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace VintageStoryModManager.Models;

/// <summary>
/// Observable collection that supports batch operations with suspended notifications.
/// Reduces UI overhead when adding/removing many items at once.
/// </summary>
public sealed class BatchedObservableCollection<T> : ObservableCollection<T>
{
    private int _suspendCount;
    private bool _hasChangedWhileSuspended;

    /// <summary>
    /// Suspends collection change notifications. Returns a disposable that will resume notifications when disposed.
    /// Multiple suspend scopes can be nested.
    /// </summary>
    public IDisposable SuspendNotifications()
    {
        return new SuspensionScope(this);
    }

    /// <summary>
    /// Adds a range of items in a single batch operation.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        using (SuspendNotifications())
        {
            foreach (var item in items)
            {
                Add(item);
            }
        }
    }

    /// <summary>
    /// Removes all items and adds new items in a single batch operation.
    /// </summary>
    public void Reset(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        using (SuspendNotifications())
        {
            Clear();
            foreach (var item in items)
            {
                Add(item);
            }
        }
    }

    /// <summary>
    /// Replaces items at specified indices in a batch operation.
    /// Indices that are out of range are silently ignored to allow partial updates.
    /// </summary>
    public void ReplaceRange(IReadOnlyList<(int Index, T Item)> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);

        using (SuspendNotifications())
        {
            foreach (var (index, item) in replacements)
            {
                if (index >= 0 && index < Count)
                {
                    this[index] = item;
                }
                // Note: Invalid indices are silently ignored to support partial/filtered updates
            }
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suspendCount > 0)
        {
            _hasChangedWhileSuspended = true;
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suspendCount > 0)
        {
            return;
        }

        base.OnPropertyChanged(e);
    }

    private void BeginSuspension()
    {
        _suspendCount++;
    }

    private void EndSuspension()
    {
        if (_suspendCount <= 0)
        {
            throw new InvalidOperationException("Cannot end suspension that was not started.");
        }

        _suspendCount--;

        if (_suspendCount == 0 && _hasChangedWhileSuspended)
        {
            _hasChangedWhileSuspended = false;
            // Fire a Reset notification to indicate the collection changed
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        }
    }

    private sealed class SuspensionScope : IDisposable
    {
        private readonly BatchedObservableCollection<T> _collection;
        private bool _disposed;

        public SuspensionScope(BatchedObservableCollection<T> collection)
        {
            _collection = collection;
            _collection.BeginSuspension();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _collection.EndSuspension();
        }
    }
}

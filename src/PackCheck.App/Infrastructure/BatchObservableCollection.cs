using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PackCheck.App.Infrastructure;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can add or remove many items with one change notification.
/// WPF item controls reject multi-item Add/Remove events, so a range change is announced as a Reset.
/// A single-item change still raises the normal Add/Remove event.
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    /// <summary>Appends all items and raises one notification.</summary>
    public void AddRange(IReadOnlyCollection<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        if (items.Count == 1)
        {
            Add(items.First());
            return;
        }

        CheckReentrancy();
        ((List<T>)Items).AddRange(items);
        RaiseReset();
    }

    /// <summary>Removes <paramref name="count"/> items starting at <paramref name="index"/> and raises one notification.</summary>
    public void RemoveRange(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + count, Count);
        if (count == 0)
        {
            return;
        }

        if (count == 1)
        {
            RemoveAt(index);
            return;
        }

        CheckReentrancy();
        ((List<T>)Items).RemoveRange(index, count);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(ResetArgs);
    }
}

using System;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Reactive.Linq;

namespace SLSKDONET.Services;

/// <summary>
/// Unified event bus for application-wide event communication.
/// Replaces fragmented event patterns (custom events, Subject<> instances) with a centralized, type-safe system.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Subscribe to events of type T.
    /// </summary>
    IObservable<T> GetEvent<T>();
    
    /// <summary>
    /// Publish an event of type T to all subscribers.
    /// </summary>
    void Publish<T>(T eventData);
}

public class EventBusService : IEventBus, IDisposable
{
    private readonly ConcurrentDictionary<Type, object> _subjects = new();
    
    public IObservable<T> GetEvent<T>()
    {
        var subject = (Subject<T>)_subjects.GetOrAdd(typeof(T), _ => new Subject<T>());
        return subject.AsObservable();
    }
    
    public void Publish<T>(T eventData)
    {
        if (_subjects.TryGetValue(typeof(T), out var subjectObj))
        {
            ((Subject<T>)subjectObj).OnNext(eventData);
        }
    }
    
    public void Dispose()
    {
        foreach (var subject in _subjects.Values)
        {
            if (subject is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _subjects.Clear();
    }
}

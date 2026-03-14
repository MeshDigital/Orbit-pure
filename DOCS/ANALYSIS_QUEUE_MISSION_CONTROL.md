# Analysis Queue "Mission Control" - Master Implementation Plan

## Vision
Transform the basic analysis queue into a comprehensive Mission Control dashboard that provides full transparency and control over the parallel analysis engine.

## Current State Assessment

### ✅ What We Have
- `AnalysisQueueService` with pause/resume
- `AnalysisWorker` with parallel processing (just implemented)
- Basic status tracking (QueuedCount, ProcessedCount, CurrentTrackHash)
- Channel-based queuing system

### ❌ What's Missing
- No visibility into individual thread activity
- No per-track progress details
- No resource usage monitoring
- No granular control (cancel individual tracks, prioritization)
- No performance analytics or history
- Basic UI with minimal interactivity

---

## Phase 1: Enhanced Real-Time UI (1-2 Days)

### Goal
Make parallel processing **visible** and **understandable** to users.

### 1.1 Live Thread Activity Grid

**Backend Changes:**
```csharp
// AnalysisWorker.cs - Add thread tracking
private readonly ConcurrentDictionary<int, ThreadStatus> _threadStatus = new();

public class ThreadStatus
{
    public int ThreadId { get; set; }
    public string State { get; set; } // Idle, Processing, Writing
    public string? CurrentTrackHash { get; set; }
    public string? CurrentTrackName { get; set; }
    public DateTime? StartedAt { get; set; }
}

public IReadOnlyDictionary<int, ThreadStatus> GetThreadStatus() => _threadStatus;
```

**UI Changes:**
```xml
<!-- AnalysisQueuePage.axaml - Thread Activity Widget -->
<Border Classes="card">
    <StackPanel>
        <TextBlock Text="🧵 Thread Activity" FontSize="16" FontWeight="Bold"/>
        <ItemsControl ItemsSource="{Binding ThreadStatuses}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Grid ColumnDefinitions="Auto,*,Auto" Margin="0,4">
                        <TextBlock Grid.Column="0" Text="{Binding ThreadId, StringFormat='Worker #{0}'}"/>
                        <TextBlock Grid.Column="1" Text="{Binding CurrentTrackName}" Margin="12,0"/>
                        <TextBlock Grid.Column="2" Text="{Binding State}" 
                                   Foreground="{Binding State, Converter={StaticResource StateColorConverter}}"/>
                    </Grid>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </StackPanel>
</Border>
```

### 1.2 System Resource Monitor

**Backend Changes:**
```csharp
// New: ResourceMonitor.cs
public class ResourceMonitor
{
    private PerformanceCounter _cpuCounter;
    private PerformanceCounter _ramCounter;
    
    public ResourceMetrics GetCurrentMetrics()
    {
        return new ResourceMetrics
        {
            CpuUsagePercent = _cpuCounter.NextValue(),
            RamUsageMB = GetProcessMemoryMB(),
            ThreadCount = Process.GetCurrentProcess().Threads.Count
        };
    }
}
```

**UI Changes:**
```xml
<!-- Resource Usage Widget -->
<Border Classes="card">
    <StackPanel>
        <TextBlock Text="📊 Resource Usage" FontSize="16" FontWeight="Bold"/>
        <Grid ColumnDefinitions="*,Auto" RowDefinitions="Auto,Auto,Auto">
            <TextBlock Grid.Row="0" Grid.Column="0" Text="CPU"/>
            <ProgressBar Grid.Row="0" Grid.Column="1" Value="{Binding CpuUsage}" Maximum="100" Width="200"/>
            
            <TextBlock Grid.Row="1" Grid.Column="0" Text="RAM"/>
            <TextBlock Grid.Row="1" Grid.Column="1" Text="{Binding RamUsageMB, StringFormat='{}{0:F0} MB'}"/>
            
            <TextBlock Grid.Row="2" Grid.Column="0" Text="Threads"/>
            <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding ThreadCount}"/>
        </Grid>
    </StackPanel>
</Border>
```

### 1.3 Per-Track Progress with Details

**Backend Changes:**
```csharp
// Enhance TrackAnalysisProgressEvent
public record TrackAnalysisProgressEvent(
    string TrackHash,
    string Stage, // "Decoding", "WaveformGen", "MusicalAnalysis", "TechAnalysis", "Saving"
    int ProgressPercent,
    string StatusMessage,
    TimeSpan? EstimatedTimeRemaining
);

// In AnalysisWorker.ProcessRequestAsync - publish detailed progress
PublishThrottled(new TrackAnalysisProgressEvent(
    trackHash, 
    "WaveformGen", 
    30, 
    "Generating waveform...", 
    EstimateTimeRemaining(trackHash)
));
```

**UI Changes:**
```xml
<!-- Enhanced Queue Item Template -->
<DataTemplate DataType="vm:QueueTrackViewModel">
    <Border Classes="queue-item">
        <Grid ColumnDefinitions="Auto,*,Auto,Auto">
            <!-- Track Info -->
            <StackPanel Grid.Column="1">
                <TextBlock Text="{Binding TrackName}" FontWeight="SemiBold"/>
                <TextBlock Text="{Binding CurrentStage}" FontSize="11" Foreground="#888"/>
            </StackPanel>
            
            <!-- Progress -->
            <ProgressBar Grid.Column="2" Value="{Binding Progress}" Width="120"/>
            
            <!-- ETA -->
            <TextBlock Grid.Column="3" Text="{Binding ETA, StringFormat='ETA: {0}'}"/>
        </Grid>
    </Border>
</DataTemplate>
```

---

## Phase 2: Control & Transparency (1-2 Days)

### Goal
Add user control and deeper insight into the analysis process.

### 2.1 Granular Track Controls

**Backend Changes:**
```csharp
// AnalysisQueueService.cs - Add control methods
public void CancelTrack(string trackHash)
{
    // Remove from queue if not started
    // Signal cancellation if processing
}

public void PauseTrack(string trackHash)
{
    // Move to paused queue
}

public void ResumeTrack(string trackHash)
{
    // Re-queue
}
```

**UI Changes:**
```xml
<!-- Per-Track Action Buttons -->
<StackPanel Grid.Column="4" Orientation="Horizontal" Spacing="8">
    <Button Command="{Binding PauseCommand}" ToolTip.Tip="Pause">⏸</Button>
    <Button Command="{Binding CancelCommand}" ToolTip.Tip="Cancel">✖</Button>
</StackPanel>
```

### 2.2 Integrated Analysis Log

**Backend Changes:**
```csharp
// New: AnalysisLogService.cs
public class AnalysisLogService
{
    private ObservableCollection<AnalysisLogEntry> _logs = new();
    
    public void LogEvent(string trackHash, LogLevel level, string message)
    {
        var entry = new AnalysisLogEntry
        {
            Timestamp = DateTime.Now,
            TrackHash = trackHash,
            Level = level,
            Message = message
        };
        
        Dispatcher.UIThread.Post(() => _logs.Insert(0, entry));
    }
}
```

**UI Changes:**
```xml
<!-- Analysis Log Panel -->
<Expander Header="📜 Analysis Log" IsExpanded="False">
    <ListBox ItemsSource="{Binding AnalysisLogs}" MaxHeight="300">
        <ListBox.ItemTemplate>
            <DataTemplate>
                <Grid ColumnDefinitions="Auto,Auto,*">
                    <TextBlock Grid.Column="0" Text="{Binding Timestamp, StringFormat='{}{0:HH:mm:ss}'}"/>
                    <TextBlock Grid.Column="1" Text="{Binding Level}" 
                               Foreground="{Binding Level, Converter={StaticResource LogLevelColorConverter}}"/>
                    <TextBlock Grid.Column="2" Text="{Binding Message}" TextWrapping="Wrap"/>
                </Grid>
            </DataTemplate>
        </ListBox.ItemTemplate>
    </ListBox>
</Expander>
```

### 2.3 Priority Management

**Backend Changes:**
```csharp
// AnalysisQueueService.cs - Add priority system
public enum AnalysisPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}

public void SetTrackPriority(string trackHash, AnalysisPriority priority)
{
    // Re-order queue based on priority
}
```

**UI Changes:**
```xml
<!-- Priority Column -->
<ComboBox SelectedItem="{Binding Priority}" Width="80">
    <ComboBoxItem>Low</ComboBoxItem>
    <ComboBoxItem>Normal</ComboBoxItem>
    <ComboBoxItem>High</ComboBoxItem>
    <ComboBoxItem>Urgent</ComboBoxItem>
</ComboBox>
```

---

## Phase 3: Performance & Persistence (2-3 Days)

### Goal
Add analytics, history, and ensure resilience.

### 3.1 Performance Analytics Panel

**Backend Changes:**
```csharp
// New: AnalyticsService.cs
public class AnalysisAnalytics
{
    public int ProcessedToday { get; set; }
    public double AverageTimePerTrack { get; set; }
    public List<DataPoint> ThroughputHistory { get; set; } // Last 60 minutes
    public Dictionary<string, int> FailureReasons { get; set; }
}
```

**UI Changes:**
```xml
<!-- Analytics Panel -->
<Border Classes="card">
    <StackPanel>
        <TextBlock Text="📈 Performance Analytics" FontSize="16" FontWeight="Bold"/>
        
        <Grid ColumnDefinitions="*,*,*" Margin="0,12,0,0">
            <StackPanel Grid.Column="0">
                <TextBlock Text="Processed Today" FontSize="11" Foreground="#888"/>
                <TextBlock Text="{Binding ProcessedToday}" FontSize="20" FontWeight="Bold"/>
            </StackPanel>
            
            <StackPanel Grid.Column="1">
                <TextBlock Text="Avg Time" FontSize="11" Foreground="#888"/>
                <TextBlock Text="{Binding AvgTimePerTrack, StringFormat='{}{0:F1}s'}" 
                           FontSize="20" FontWeight="Bold"/>
            </StackPanel>
            
            <StackPanel Grid.Column="2">
                <TextBlock Text="Current Rate" FontSize="11" Foreground="#888"/>
                <TextBlock Text="{Binding TracksPerMinute, StringFormat='{}{0:F1}/min'}" 
                           FontSize="20" FontWeight="Bold"/>
            </StackPanel>
        </Grid>
        
        <!-- Throughput Chart -->
        <lvc:CartesianChart Series="{Binding ThroughputSeries}" Height="200" Margin="0,12,0,0"/>
    </StackPanel>
</Border>
```

### 3.2 Persistent Queue State

**Backend Changes:**
```csharp
// New: QueuePersistenceService.cs
public class QueuePersistenceService
{
    private readonly string _queueStatePath = "Data/queue_state.json";
    
    public async Task SaveQueueStateAsync(QueueState state)
    {
        var json = JsonSerializer.Serialize(state);
        await File.WriteAllTextAsync(_queueStatePath, json);
    }
    
    public async Task<QueueState?> LoadQueueStateAsync()
    {
        if (!File.Exists(_queueStatePath)) return null;
        
        var json = await File.ReadAllTextAsync(_queueStatePath);
        return JsonSerializer.Deserialize<QueueState>(json);
    }
}
```

### 3.3 UI/UX Polish

**Features:**
- Compact/Detailed view toggle
- Smooth animations for state changes
- Adaptive color scheme (light/dark mode support)
- Keyboard shortcuts (Space = pause/resume, Del = cancel)
- Export analytics to CSV

---

## Implementation Order

### Week 1 (Days 1-2)
1. ✅ Implement thread tracking in `AnalysisWorker`
2. ✅ Create `ResourceMonitor` service
3. ✅ Build basic thread activity widget UI
4. ✅ Enhance progress events with detailed stages

### Week 2 (Days 3-4)
5. ✅ Implement per-track controls (pause/cancel)
6. ✅ Create `AnalysisLogService`
7. ✅ Build integrated log panel UI
8. ✅ Add priority queue system

### Week 3 (Days 5-7)
9. ✅ Create `AnalysicsService` with metrics tracking
10. ✅ Build performance analytics panel UI
11. ✅ Implement queue state persistence
12. ✅ Add UI polish (animations, themes, shortcuts)

---

## Success Metrics

### User Experience
- Users can see exactly what each thread is doing at any moment
- Resource usage is transparent and controllable
- Failed tracks are clearly identified with actionable logs
- Queue state survives app restarts

### Performance
- Dashboard updates don't impact analysis throughput
- UI remains responsive during heavy processing
- Memory usage stays predictable

### Developer Experience
- Clean separation of concerns (services vs UI)
- Easy to add new metrics or controls
- Well-documented event flow

---

## Files to Create/Modify

### New Files
- `Services/ResourceMonitor.cs`
- `Services/AnalysisLogService.cs`
- `Services/AnalyticsService.cs`
- `Services/QueuePersistenceService.cs`
- `ViewModels/AnalysisQueueViewModel.cs` (major refactor)
- `Models/ThreadStatus.cs`
- `Models/AnalysisLogEntry.cs`
- `Models/QueueState.cs`

### Modified Files
- `Services/AnalysisQueueService.cs` - Add detailed tracking
- `Services/AnalysisWorker.cs` - Emit granular progress events
- `Views/AnalysisQueuePage.axaml` - Complete UI overhaul
- `App.axaml.cs` - Register new services

---

## Next Steps

**Ready to proceed?** Choose your starting point:

**Option A:** Start with Phase 1.1 (Thread Activity Grid) - Most visible impact  
**Option B:** Start with Phase 2.2 (Analysis Log) - Easiest to implement  
**Option C:** Build all backend services first, then UI in one pass  

**Which approach would you prefer?**

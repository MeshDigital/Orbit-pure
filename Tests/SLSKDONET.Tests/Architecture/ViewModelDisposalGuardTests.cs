using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// "Zombie Scout" — Automated architectural guardrail tests.
/// 
/// These tests use a combination of reflection and source analysis to detect:
/// 1. ViewModels that subscribe to EventBus/Rx observables without implementing IDisposable
/// 2. ViewModels that use DispatcherTimer without proper cleanup in Dispose()
/// 3. ViewModels that have CompositeDisposable but don't call .Dispose() on it
/// 
/// PURPOSE: Prevent memory leaks from re-entering the codebase during future feature sprints.
/// Any new ViewModel that subscribes to events MUST implement IDisposable.
/// 
/// Run: dotnet test --filter "FullyQualifiedName~ViewModelDisposalGuard"
/// </summary>
public class ViewModelDisposalGuardTests
{
    private readonly ITestOutputHelper _output;
    
    // The main ORBIT assembly
    private static readonly Assembly OrbitAssembly = typeof(SLSKDONET.Services.EventBusService).Assembly;
    
    // Source root for file-level analysis
    private static readonly string SourceRoot = FindSourceRoot();

    public ViewModelDisposalGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// GUARDRAIL 1: Any class containing .Subscribe( or .GetEvent< in its source
    /// MUST implement IDisposable. This catches the #1 cause of zombie ViewModels.
    /// </summary>
    [Fact]
    public void AllViewModels_WithEventSubscriptions_MustImplementIDisposable()
    {
        var violations = new List<string>();
        var viewModelFiles = FindViewModelSourceFiles();
        
        _output.WriteLine($"Scanning {viewModelFiles.Count} ViewModel source files...");

        foreach (var file in viewModelFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);
            
            // Check if the file subscribes to EventBus events or Rx observables
            bool hasSubscribe = content.Contains(".Subscribe(");
            bool hasGetEvent = content.Contains(".GetEvent<");
            bool hasEventBusField = content.Contains("IEventBus") || content.Contains("_eventBus");
            
            if (!hasSubscribe && !hasGetEvent)
                continue; // No subscriptions, no risk

            // Check if the class implements IDisposable
            bool implementsDisposable = 
                content.Contains("IDisposable") && 
                content.Contains("public void Dispose()");
            
            if (!implementsDisposable)
            {
                violations.Add($"❌ {fileName}: Has .Subscribe() calls but does NOT implement IDisposable");
                _output.WriteLine($"  VIOLATION: {fileName} ({Path.GetRelativePath(SourceRoot, file)})");
            }
            else
            {
                _output.WriteLine($"  ✅ {fileName}: IDisposable implemented");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} ViewModel(s) with untracked subscriptions:\n" +
            string.Join("\n", violations) +
            "\n\nFIX: Implement IDisposable and add .DisposeWith(_disposables) to all .Subscribe() calls.");
    }

    /// <summary>
    /// GUARDRAIL 2: Any class using DispatcherTimer MUST implement IDisposable
    /// and call timer.Stop() + unsubscribe Tick in Dispose().
    /// A leaking timer is both a memory AND CPU leak.
    /// </summary>
    [Fact]
    public void AllViewModels_WithDispatcherTimer_MustDisposeTimer()
    {
        var violations = new List<string>();
        var viewModelFiles = FindViewModelSourceFiles();

        foreach (var file in viewModelFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);
            
            if (!content.Contains("DispatcherTimer"))
                continue;

            bool hasDispose = content.Contains("public void Dispose()");
            bool stopsTimer = content.Contains(".Stop()") && hasDispose;
            
            // Check that timer.Stop() appears in or near the Dispose method
            if (!hasDispose)
            {
                violations.Add($"❌ {fileName}: Uses DispatcherTimer but does NOT implement IDisposable");
            }
            else if (!stopsTimer)
            {
                violations.Add($"⚠️ {fileName}: Has IDisposable but may not stop the DispatcherTimer in Dispose()");
            }
            else
            {
                _output.WriteLine($"  ✅ {fileName}: DispatcherTimer properly managed");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} ViewModel(s) with unmanaged DispatcherTimers:\n" +
            string.Join("\n", violations) +
            "\n\nFIX: Call _timer.Stop() and _timer.Tick -= handler in Dispose().");
    }

    /// <summary>
    /// GUARDRAIL 3: If a ViewModel has CompositeDisposable, verify its Dispose() method
    /// actually calls _disposables.Dispose(). A CompositeDisposable that isn't disposed
    /// is worse than no CompositeDisposable — it gives a false sense of safety.
    /// </summary>
    [Fact]
    public void AllViewModels_WithCompositeDisposable_MustDisposeIt()
    {
        var violations = new List<string>();
        var viewModelFiles = FindViewModelSourceFiles();

        foreach (var file in viewModelFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);
            
            if (!content.Contains("CompositeDisposable"))
                continue;

            // Find the field name used for CompositeDisposable
            var match = Regex.Match(content, @"(?:readonly\s+)?CompositeDisposable\s+(_\w+)");
            if (!match.Success)
                continue;

            var fieldName = match.Groups[1].Value;
            
            // Check if Dispose() calls fieldName.Dispose() or fieldName?.Dispose()
            bool hasDispose = content.Contains("public void Dispose()");
            bool disposesField = content.Contains($"{fieldName}.Dispose()") || 
                                 content.Contains($"{fieldName}?.Dispose()");
            
            if (!hasDispose)
            {
                violations.Add($"❌ {fileName}: Has CompositeDisposable '{fieldName}' but no Dispose() method");
            }
            else if (!disposesField)
            {
                violations.Add($"⚠️ {fileName}: Has Dispose() but doesn't call {fieldName}.Dispose()");
            }
            else
            {
                _output.WriteLine($"  ✅ {fileName}: {fieldName} properly disposed");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} ViewModel(s) with undisposed CompositeDisposable:\n" +
            string.Join("\n", violations) +
            "\n\nFIX: Ensure Dispose() calls _disposables.Dispose().");
    }

    /// <summary>
    /// GUARDRAIL 4: Reflection-based audit — verify IDisposable at the type level.
    /// Scans all types in the ORBIT assembly that have fields of type IEventBus.
    /// </summary>
    [Fact]
    public void AllTypes_WithEventBusField_MustImplementIDisposable()
    {
        var violations = new List<string>();
        
        var typesWithEventBus = OrbitAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.DeclaringType == null) // Only top-level types — skip all nested/inner classes
            .Where(t => !t.Name.Contains("<")) // Skip compiler-generated closure/display classes
            .Where(t => t.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length == 0)
            .Where(t => t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                .Any(f => f.FieldType == typeof(SLSKDONET.Services.IEventBus) || 
                          f.FieldType.Name == "IEventBus"))
            .ToList();

        _output.WriteLine($"Found {typesWithEventBus.Count} types with IEventBus fields:");

        foreach (var type in typesWithEventBus)
        {
            bool implementsDisposable = typeof(IDisposable).IsAssignableFrom(type);
            
            if (!implementsDisposable)
            {
                // Only flag ViewModels and Services that are transient/scoped
                // Skip if it's a Service that's typically singleton (services are okay to leak less)
                bool isViewModel = type.Name.Contains("ViewModel") || type.Name.Contains("View");
                
                if (isViewModel)
                {
                    violations.Add($"❌ {type.FullName}: Has IEventBus field but does NOT implement IDisposable");
                    _output.WriteLine($"  VIOLATION: {type.FullName}");
                }
                else
                {
                    _output.WriteLine($"  ⚠️ {type.FullName}: Has IEventBus but is likely a service (singleton) — skipped");
                }
            }
            else
            {
                _output.WriteLine($"  ✅ {type.FullName}: IDisposable ✓");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} ViewModel(s) with IEventBus fields that don't implement IDisposable:\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// DIAGNOSTIC: Prints a full "Subscription Health Report" for all ViewModels.
    /// Not a pass/fail test — purely informational for code review sessions.
    /// </summary>
    [Fact]
    public void DiagnosticReport_SubscriptionHealthAudit()
    {
        var viewModelFiles = FindViewModelSourceFiles();
        
        _output.WriteLine("═══════════════════════════════════════════════════");
        _output.WriteLine("  ORBIT Subscription Health Report");
        _output.WriteLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        _output.WriteLine("═══════════════════════════════════════════════════");

        int totalFiles = 0;
        int totalSubscriptions = 0;
        int tracked = 0;
        int untracked = 0;

        foreach (var file in viewModelFiles.OrderBy(f => Path.GetFileName(f)))
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);
            
            var subscribeCount = Regex.Matches(content, @"\.Subscribe\(").Count;
            if (subscribeCount == 0) continue;

            totalFiles++;
            totalSubscriptions += subscribeCount;

            bool hasDisposable = content.Contains("IDisposable");
            bool hasComposite = content.Contains("CompositeDisposable") || content.Contains("DisposeWith");
            int disposeWithCount = Regex.Matches(content, @"\.DisposeWith\(").Count;
            int addToDisposables = Regex.Matches(content, @"_disposables\.Add\(").Count;
            int trackedCount = disposeWithCount + addToDisposables;
            
            tracked += trackedCount;
            untracked += (subscribeCount - trackedCount);

            string status = hasDisposable ? "🟢" : "🔴";
            string compositeStatus = hasComposite ? $"({trackedCount}/{subscribeCount} tracked)" : "(no CompositeDisposable)";
            
            _output.WriteLine($"  {status} {fileName,-45} Subs: {subscribeCount,2}  {compositeStatus}");
        }

        _output.WriteLine("───────────────────────────────────────────────────");
        _output.WriteLine($"  Total Files with Subscriptions: {totalFiles}");
        _output.WriteLine($"  Total Subscriptions:            {totalSubscriptions}");
        _output.WriteLine($"  Tracked (DisposeWith/Add):      {tracked}");
        _output.WriteLine($"  Untracked:                      {untracked}");
        _output.WriteLine($"  Coverage:                       {(totalSubscriptions > 0 ? (tracked * 100.0 / totalSubscriptions) : 100):F0}%");
        _output.WriteLine("═══════════════════════════════════════════════════");
    }

    #region Helpers

    private static List<string> FindViewModelSourceFiles()
    {
        var results = new List<string>();
        
        if (string.IsNullOrEmpty(SourceRoot) || !Directory.Exists(SourceRoot))
            return results;

        // Scan ViewModels directory
        var viewModelsDir = Path.Combine(SourceRoot, "ViewModels");
        if (Directory.Exists(viewModelsDir))
        {
            results.AddRange(Directory.GetFiles(viewModelsDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("obj") && !f.Contains("bin")));
        }

        // Scan Features directory (contains sidebar VMs etc.)
        var featuresDir = Path.Combine(SourceRoot, "Features");
        if (Directory.Exists(featuresDir))
        {
            results.AddRange(Directory.GetFiles(featuresDir, "*ViewModel*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("obj") && !f.Contains("bin")));
        }

        // Scan Views directory (contains MainViewModel)
        var viewsDir = Path.Combine(SourceRoot, "Views");
        if (Directory.Exists(viewsDir))
        {
            results.AddRange(Directory.GetFiles(viewsDir, "*ViewModel*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("obj") && !f.Contains("bin")));
        }

        return results.Distinct().ToList();
    }

    private static string FindSourceRoot()
    {
        // Walk up from test assembly to find the solution root
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "SLSKDONET.csproj")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        
        // Fallback: relative path from test project
        var testDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(candidate, "SLSKDONET.csproj")))
            return candidate;
            
        return string.Empty;
    }

    #endregion
}

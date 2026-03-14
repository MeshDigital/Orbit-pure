# TreeDataGrid Integration (Non-Commercial / Source Integration)

**Component**: `External/TreeDataGrid`  
**Status**: ✅ Source-Integrated (Direct Project Reference)  
**Critical Rule**: **NEVER** install the `Avalonia.Controls.TreeDataGrid` NuGet package.

---

## Why Source Integration?

In this project, we use a customized/specific version of the Avalonia `TreeDataGrid` that has been integrated directly from source code into the `External/TreeDataGrid` directory. This is done for several reasons:

1.  **Licensing & versioning consistency**: Ensures we are using the exact version compatible with our Avalonia 11.x setup without NuGet dependency conflicts.
2.  **Customizations**: Source integration allows us to apply specific patches or performance optimizations directly if needed.
3.  **Non-Commercial Usage**: We use the source version to ensure internal project consistency without relying on commercial NuGet distribution logic that might trigger licensing prompts in certain environments.

---

## How it works

The project is referenced directly in `SLSKDONET.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="External\TreeDataGrid\src\Avalonia.Controls.TreeDataGrid\Avalonia.Controls.TreeDataGrid.csproj" />
</ItemGroup>
```

We also have a block to prevent the generic compiler from trying to "double-compile" the items in `/External`:

```xml
<ItemGroup>
  <Compile Remove="External\**" />
  <EmbeddedResource Remove="External\**" />
  <None Remove="External\**" />
  <AdditionalFiles Remove="External\**" />
  <AvaloniaResource Remove="External\**" />
  <AvaloniaXaml Remove="External\**" />
</ItemGroup>
```

---

## Developer / AI Guardrails

> [!CAUTION]
> **DO NOT** run `dotnet add package Avalonia.Controls.TreeDataGrid`.

If you see compilation errors related to `TreeDataGrid`, **do not try to "fix" them by installing the NuGet package**. This will cause:
*   **Duplicate Type Errors**: The compiler will see the same classes (e.g., `HierarchicalTreeDataGridSource`) in both the source project and the NuGet dll.
*   **Build Failures**: Namespace collisions will break the entire UI layer.

### How to Fix Issues
1.  Verify the `ProjectReference` exists in the `.csproj`.
2.  Ensure the source files are present in `External/TreeDataGrid`.
3.  Check that the `using` statements are correct: `using Avalonia.Controls;` or `using Avalonia.Controls.Models.TreeDataGrid;`.

---

## Related Page Views
*   [SearchPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/SearchPage.axaml) - Uses TreeDataGrid for search results.
*   [LibraryPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/LibraryPage.axaml) - Uses standard DataGrid but may transition to TreeDataGrid later.

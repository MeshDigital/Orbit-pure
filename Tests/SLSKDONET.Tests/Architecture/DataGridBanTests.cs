using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

public class DataGridBanTests
{
    [Fact]
    public void AxamlFiles_DoNotContainDataGridOrDgDataGrid()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot), "Source root directory could not be resolved.");

        var viewsDir = Path.Combine(sourceRoot, "Views");
        Assert.True(Directory.Exists(viewsDir), $"Expected Views directory at {viewsDir}");

        var axamlFiles = Directory.GetFiles(viewsDir, "*.axaml", SearchOption.AllDirectories);
        Assert.NotEmpty(axamlFiles);

        foreach (var filePath in axamlFiles)
        {
            var content = File.ReadAllText(filePath);
            
            // Assert that the file does not contain legacy DataGrid elements
            Assert.False(content.Contains("<DataGrid"), $"File '{filePath}' contains a legacy <DataGrid> element, which is banned.");
            Assert.False(content.Contains("<dg:DataGrid"), $"File '{filePath}' contains a legacy <dg:DataGrid> element, which is banned.");
        }
    }

    private static string FindSourceRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "SLSKDONET.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        var candidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(candidate, "SLSKDONET.csproj")))
        {
            return candidate;
        }

        return string.Empty;
    }
}

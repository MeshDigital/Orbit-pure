using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface IImportOrchestrationService
{
    Task StartImportWithPreviewAsync(IImportProvider provider, string input);
    Task SilentImportAsync(IImportProvider provider, string input);
}

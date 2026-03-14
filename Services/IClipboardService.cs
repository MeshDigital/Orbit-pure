using System.Threading.Tasks;

namespace SLSKDONET.Services;

public interface IClipboardService
{
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}

using System.Collections.Generic;
using System.Threading.Tasks;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Library;

public interface ISavedDoublesService
{
    Task<IReadOnlyList<SavedDouble>> LoadAsync();
    Task SaveAsync(IReadOnlyList<SavedDouble> doubles);
    Task AddOrUpdateAsync(SavedDouble pair);
    Task RemoveAsync(SavedDouble pair);
    bool Exists(string trackAId, string trackBId);
}

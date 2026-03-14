using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services
{
    public class StyleLabPersistenceVerifier
    {
        private readonly ILogger<StyleLabPersistenceVerifier> _logger;

        public StyleLabPersistenceVerifier(ILogger<StyleLabPersistenceVerifier> logger)
        {
            _logger = logger;
        }

        public async Task VerifyPersistenceAsync()
        {
            _logger.LogInformation("🧪 STARTING STYLE LAB PERSISTENCE VERIFICATION...");
            Console.WriteLine("🧪 STARTING STYLE LAB PERSISTENCE VERIFICATION...");

            var testStyleName = "VERIFY_PERSISTENCE_" + Guid.NewGuid().ToString().Substring(0, 8);
            var testTrackHash = "TEST_HASH_" + Guid.NewGuid();
            Guid styleId;

            // STEP 1: CREATE AND SAVE
            using (var context = new AppDbContext())
            {
                _logger.LogInformation("Step 1: Creating new style '{StyleName}'...", testStyleName);
                var style = new StyleDefinitionEntity
                {
                    Name = testStyleName,
                    ColorHex = "#FF00FF",
                    ReferenceTrackHashes = new List<string> { testTrackHash } // Set via Property/JSON
                };
                
                context.StyleDefinitions.Add(style);
                await context.SaveChangesAsync();
                styleId = style.Id;
                _logger.LogInformation("Step 1 Complete. Style ID: {Id}", styleId);
            }

            // STEP 2: RELOAD FROM NEW CONTEXT
            using (var context = new AppDbContext())
            {
                _logger.LogInformation("Step 2: Reloading from fresh context...");
                var style = await context.StyleDefinitions.FirstOrDefaultAsync(s => s.Id == styleId);

                if (style == null)
                {
                    _logger.LogError("❌ VERIFICATION FAILED: Style not found in database.");
                    return;
                }

                _logger.LogInformation("Style found. Verifying properties...");

                if (style.Name != testStyleName)
                {
                    _logger.LogError("❌ VERIFICATION FAILED: Name mismatch. Expected '{Exp}', Got '{Act}'", testStyleName, style.Name);
                    return;
                }

                if (style.ReferenceTrackHashes == null || !style.ReferenceTrackHashes.Contains(testTrackHash))
                {
                    _logger.LogError("❌ VERIFICATION FAILED: Track hash list mismatch. Count: {Count}", style.ReferenceTrackHashes?.Count ?? 0);
                    _logger.LogError("Raw JSON: {Json}", style.ReferenceTrackHashesJson);
                    return;
                }

                _logger.LogInformation("✅ PERSISTENCE VERIFIED: Name and Track List match.");
                
                // Cleanup
                context.StyleDefinitions.Remove(style);
                await context.SaveChangesAsync();
                _logger.LogInformation("Cleanup complete.");
            }
        }
    }
}

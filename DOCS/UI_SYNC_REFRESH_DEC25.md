# UI Synchronization Refresh - December 25

This update synchronizes several key frontend components with the backend logic, improving data accuracy and feature visibility.

## 📈 Home Page: Library Health (Purity) Metrics
- **Dynamic Counters**: The Library Health widget now displays real-time counts from the database instead of placeholders.
- **Purity Levels**: Tracks are classified into Silver, Gold, and Diamond based on bitrate and metadata quality.
- **Service Integration**: Connected `HomeViewModel` to `DashboardService` for live data retrieval.

## 🛠️ Settings Page: Ranking Strategy Management
- **Scoring Profiles**: Users can now select between "Balanced", "Quality First", and "DJ Mode" ranking strategies.
- **Live Weight Updates**: Selecting a profile instantly updates the scoring weights used for search result prioritization.
- **Persistence**: The chosen profile is saved in `AppConfig` and automatically reapplied when the application starts.
- **UI Interaction**: Added a new ComboBox in Settings for easy strategy swapping.

## 🔍 Track Inspector: Spotify Musical Intelligence
- **Audio Features**: Added visual indicators (sliders) for Spotify Energy, Danceability, and Valence (Mood).
- **Deep enrichment**: Updated `SpotifyMetadataService` to fetch these features during the enrichment process.
- **Data Propagation**: Ensured these features are correctly stored in `TrackEntity` and propagated through the `ImportPreviewViewModel` to the Library.
- **Schema Patching**: Automatic database schema updates were added to handle the new musical feature columns.

## ⚙️ Technical Improvements
- **Startup Loading**: Restored the logic in `App.axaml.cs` to correctly initialize the `ResultSorter` with the user's preferred strategy and weights on launch.
- **Batch Enrichment Support**: Updated `ImportPreviewViewModel` and `DatabaseService` to handle batch musical intelligence enrichment.

---
**Status**: Implemented & Verified ✅

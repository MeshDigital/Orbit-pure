# Essentia TensorFlow Models

This folder contains TensorFlow models used by Essentia for audio analysis.

## Included Models (< 50MB)

These models are included in the repository and ready to use:
- `danceability-msd-musicnn-1.pb` - Danceability score
- `discogs-effnet-bs64-1.pb` - Genre classification (519 classes)
- `emomusic-msd-musicnn-2.pb` - Arousal/Valence emotion mapping
- `genre_electronic-musicnn-msd-2.pb` - Electronic subgenre detection
- `mood_*-discogs-effnet-1.pb` - Mood classifiers
- `tonal_atonal-musicnn-msd-2.pb` - DJ Tool detection
- `voice_instrumental-msd-musicnn-1.pb` - Vocal detection
- `genre_discogs400-discogs-effnet-1.pb` - Detailed genre (400 classes)
- `mtg_jamendo_genre-discogs-effnet-1.pb` - Jamendo genre tags
- `deepsquare-k16-3.pb` - Audio fingerprinting

## Large Models (Download Separately)

The following models exceed GitHub's 50MB file size limit and must be downloaded separately:

### audioset-vggish-3.pb (~289MB)
General audio event embeddings.
```
Download: https://essentia.upf.edu/models/audio-event-recognition/audioset-vggish/audioset-vggish-3.pb
```

### crepe-full-1.pb (~89MB)
High-precision pitch detection.
```
Download: https://essentia.upf.edu/models/pitch/crepe/crepe-full-1.pb
Also get: https://essentia.upf.edu/models/pitch/crepe/crepe-full-1.json
```

### spleeter-5s-3.pb (~197MB)
Source separation (vocals, drums, bass, etc.)
```
Download: https://essentia.upf.edu/models/source-separation/spleeter/spleeter-5s-3.pb
Also get: https://essentia.upf.edu/models/source-separation/spleeter/spleeter-5s-3.json
```

## Download Instructions

1. Download the model `.pb` and corresponding `.json` files from the URLs above
2. Place them in this folder: `Tools/Essentia/models/`
3. Restart ORBIT to use the new models

## Official Model Repository

All models are available at: https://essentia.upf.edu/models/

Browse the catalog to find additional models for:
- Genre classification
- Mood/emotion detection
- Instrument recognition
- Tempo estimation
- And more...

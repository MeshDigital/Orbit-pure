# 🧠 The Cortex: ML.NET Audio Classification Engine

> **"From Vector Math to Gradient Boosting."**

As of **Phase 15.5**, ORBIT has transitioned its recommendation and classification engine from simple Euclidean distance (Vector Math) to a robust **Machine Learning** pipeline powered by **Microsoft ML.NET**.

## 1. High-Level Architecture

The core of the system is the **`PersonalClassifierService`**, which acts as the user's personal musicologist. It learns from user-defined "Style Buckets" to predict the vibe of new tracks.

```mermaid
graph TD
    A[Audio File] -->|FFmpeg + Essentia| B(Feature Extraction)
    B -->|EffNet-b0 Model| C{128-d Embedding}
    C -->|Input| D[PersonalClassifierService]
    
    subgraph "The Brain (ML.NET)"
        D -->|Load| E[LightGBM Model]
        E -->|Predict| F(Vibe Prediction)
        F -->|Confidence Score| G{Confidence Cliff}
    end
    
    G -->|High Confidence (>0.6)| H[Specific Vibe]
    G -->|Low Confidence (<0.6)| I[Unknown / Mixed]
```

## 2. The Technology Stack

| Component | Technology | Role |
| :--- | :--- | :--- |
| **Framework** | **ML.NET 3.0** | Core machine learning pipeline. |
| **Algorithm** | **LightGBM** | Gradient Boosting Decision Tree. Chosen for speed and accuracy with dense embedding data. |
| **Input Data** | **EffNet-b0 Embeddings** | 128-float vectors representing "texture" and "style" (extracted via Essentia). |
| **Training** | **On-Device (Local)** | The model trains 100% locally on your machine. No cloud data. |

## 3. The "Style Lab" Workflow

The system is designed to be **User-Supervised**. We do not ship a generic "Techno" classifier. You define what "Techno" is.

1.  **Bucket Creation**: User creates a Style Bucket (e.g., "Hypnotic Techno", "Liquid DnB").
2.  **Seed Tracks**: User drags "Gold Standard" tracks into the bucket.
3.  **Feature Extraction**: The system extracts the 128-dimensional embedding for these seeds.
4.  **Training Trigger**: The `StyleLabViewModel` flags the model as "Outdated".
5.  **Global Retraining**: When the user clicks "Save & Train", the `PersonalClassifierService`:
    *   Aggregates all seed data from all buckets.
    *   Maps labels (Style Names) to Integers.
    *   Trains a **Multiclass LightGBM** model.
    *   Saves the model to `Data/Models/personal_vibe_model.zip`.

## 4. Why ML.NET over Vector Math?

Previous versions used **Centroid-based classification** (calculating the average center point of a generic cluster).

| Feature | Old Vector Math | New ML.NET (LightGBM) |
| :--- | :--- | :--- |
| **Boundary Handling** | Linear (Circles). Struggles with complex overlaps. | Non-Linear (Decision Trees). Can handle complex, "swirly" boundaries. |
| **Feature Importance** | All 128 dimensions treated equally. | Learns which of the 128 dimensions matter for *your* specific genres. |
| **Scalability** | Gets slower as you add more reference tracks. | Prediction speed is constant (O(1)) regardless of training data size. |
| **Confidence** | Simple distance metric (hard to tune). | Robust Probability Calibration (0.0 - 1.0). |

## 5. The "Confidence Cliff"

We implemented a strict **Reliability Gate**:

```csharp
// Inside PersonalClassifierService.Predict()
if (maxConfidence < 0.6f) 
{
    return ("Unknown / Mixed", 0.0f);
}
```

If the AI isn't at least **60% sure**, it won't guess. This prevents the "Hallucination" problem where everything gets labeled *something* even if it sounds like nothing else in your library.

## 6. Data Persistence

To support high-speed retraining without re-analyzing audio:

*   **`AudioFeaturesEntity.AiEmbeddingJson`**:
    *   Stores the 128-float vector.
    *   serialized as a JSON string for SQLite storage.
    *   *Note: This allows instantaneous retraining when you change bucket definitions.*

## 7. Future Capabilities

Moving to ML.NET opens the door for:
*   **Anomaly Detection**: "This track is labeled 'Deep House' but sounds like 'Dubstep'."
*   **Active Learning**: The AI asking *you* to label a confusing track to improve itself.
*   **ONNX Export**: Using the trained model in other applications.

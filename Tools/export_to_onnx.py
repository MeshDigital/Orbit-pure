import tensorflow as tf
import tf2onnx
import os
import sys

# Path to the .pb model
pb_path = r"Tools\Essentia\models\spleeter-5s-3.pb"
output_path = r"Tools\Essentia\models\spleeter-5stems.onnx"

print(f"Loading model from: {pb_path}")
if not os.path.exists(pb_path):
    print("Error: Model file not found.")
    sys.exit(1)

try:
    with tf.compat.v1.io.gfile.GFile(pb_path, 'rb') as f:
        graph_def = tf.compat.v1.GraphDef()
        graph_def.ParseFromString(f.read())

    print("Model loaded successfully. Converting to ONNX...")
    
    # Using tf.compat.v1 to handle the frozen graph
    # Verified Nodes:
    # Input: "waveform" [Samples, 2]
    # Outputs: "waveform_vocals", "waveform_piano", "waveform_drums", "waveform_bass", "waveform_other"
    
    tf2onnx.convert.from_graph_def(
        graph_def,
        input_names=["waveform:0"],
        output_names=[
            "waveform_vocals:0", 
            "waveform_piano:0", 
            "waveform_drums:0", 
            "waveform_bass:0", 
            "waveform_other:0"
        ],
        output_path=output_path,
        opset=15
    )
    
    print(f"Conversion complete! ONNX model saved to: {output_path}")

except Exception as e:
    print(f"Conversion failed: {e}")
    sys.exit(1)

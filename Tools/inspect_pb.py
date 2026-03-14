import tensorflow as tf
import os

pb_path = r"Tools\Essentia\models\spleeter-5s-3.pb"

with tf.compat.v1.io.gfile.GFile(pb_path, 'rb') as f:
    graph_def = tf.compat.v1.GraphDef()
    graph_def.ParseFromString(f.read())

for node in graph_def.node:
    if node.name == "waveform":
        print(f"Node: {node.name}")
        print(f"Op: {node.op}")
        # Attributes
        if "shape" in node.attr:
            print(f"Shape: {node.attr['shape'].shape}")
        if "dtype" in node.attr:
            print(f"Dtype: {node.attr['dtype'].type}")

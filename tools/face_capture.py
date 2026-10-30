"""YuzuPet face capture sidecar.

Runs MediaPipe FaceLandmarker on the webcam (or a --demo sine generator) and
streams compact JSON frames over UDP for the Godot pet to consume:

    {"t": 12.34, "x": 0.12, "y": -0.05, "roll": -3.2, "pitch": 0.08,
     "blinkL": 0.0, "blinkR": 0.0, "mouth": 0.0, "smile": 0.1}

x/y are nose-tip offsets normalized to [-1,1] around the frame center,
roll is in degrees, the rest are blendshape scores in [0,1].
The model file is the original app's Face Landmarker .task (recovered from
the APK), so nothing is downloaded at runtime.

Usage:
    python face_capture.py --model ../assets/face_landmarker.task --port 7901
    python face_capture.py --demo --port 7901
"""
import argparse
import json
import math
import socket
import sys
import time


def build_demo_stream():
    def sample(t):
        blink = 1.0 if (t % 4.0) > 3.86 else 0.0
        smile = max(0.0, math.sin(t * 0.55))
        mouth = max(0.0, math.sin(t * 1.7 - 0.4)) * (1 if (t % 9.0) > 6.5 else 0.25)
        return {
            "t": round(t, 3),
            "x": round(math.sin(t * 0.7) * 0.35, 4),
            "y": round(math.sin(t * 1.1) * 0.12, 4),
            "roll": round(math.sin(t * 0.5) * 9.0, 3),
            "pitch": round(math.sin(t * 0.9) * 0.18, 4),
            "blinkL": blink,
            "blinkR": blink,
            "mouth": round(mouth, 3),
            "smile": round(smile, 3),
        }
    return sample


def build_camera_stream(model_path):
    import cv2
    import mediapipe as mp
    from mediapipe.tasks import python as mp_python
    from mediapipe.tasks.python import vision

    options = vision.FaceLandmarkerOptions(
        base_options=mp_python.BaseOptions(model_asset_path=model_path),
        running_mode=vision.RunningMode.VIDEO,
        output_face_blendshapes=True,
    )
    landmarker = vision.FaceLandmarker.create_from_options(options)
    cap = cv2.VideoCapture(0)
    if not cap.isOpened():
        raise RuntimeError("camera 0 not available")

    nose = 1
    eye_outer_l, eye_outer_r = 33, 263
    cheek_l, cheek_r, forehead, chin = 234, 454, 10, 152

    def score(blendshapes, name):
        for b in blendshapes:
            if b.category_name == name:
                return b.score
        return 0.0

    def sample(t):
        ok, frame = cap.read()
        if not ok:
            return None
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
        result = landmarker.detect_for_video(mp_image, int(t * 1000))
        if not result.face_landmarks:
            return None
        lm = result.face_landmarks[0]
        x = (lm[nose].x - 0.5) * 2.0
        y = (lm[nose].y - 0.5) * 2.0
        dx = lm[eye_outer_r].x - lm[eye_outer_l].x
        dy = lm[eye_outer_r].y - lm[eye_outer_l].y
        roll = math.degrees(math.atan2(dy, dx))
        face_h = abs(lm[chin].y - lm[forehead].y) or 1e-6
        center_y = (lm[cheek_l].y + lm[cheek_r].y) * 0.5
        pitch = max(-1.0, min(1.0, (lm[nose].y - center_y) / face_h * 2.0))
        bs = result.face_blendshapes[0].category if result.face_blendshapes else []
        return {
            "t": round(t, 3),
            "x": round(max(-1, min(1, x)), 4),
            "y": round(max(-1, min(1, y)), 4),
            "roll": round(max(-12, min(12, roll)), 3),
            "pitch": round(pitch, 4),
            "blinkL": round(score(bs, "eyeBlinkLeft"), 3),
            "blinkR": round(score(bs, "eyeBlinkRight"), 3),
            "mouth": round(score(bs, "jawOpen"), 3),
            "smile": round((score(bs, "mouthSmileLeft") + score(bs, "mouthSmileRight")) * 0.5, 3),
        }

    return sample


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=None)
    ap.add_argument("--port", type=int, default=7901)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--demo", action="store_true")
    ap.add_argument("--hz", type=float, default=30.0)
    args = ap.parse_args()

    sample = None
    if not args.demo:
        try:
            sample = build_camera_stream(args.model)
            print("camera stream ready", flush=True)
        except Exception as e:
            print(f"camera failed ({e}); falling back to demo stream", file=sys.stderr, flush=True)
    if sample is None:
        sample = build_demo_stream()
        print("demo stream ready", flush=True)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    period = 1.0 / max(1.0, args.hz)
    start = time.time()
    next_tick = start
    while True:
        now = time.time()
        frame = sample(now - start)
        if frame is not None:
            sock.sendto(json.dumps(frame, separators=(",", ":")).encode(),
                        (args.host, args.port))
        next_tick += period
        sleep = next_tick - time.time()
        if sleep > 0:
            time.sleep(sleep)
        else:
            next_tick = time.time()


if __name__ == "__main__":
    main()
